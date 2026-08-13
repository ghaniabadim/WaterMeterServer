using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Application.BackgroundWorkers
{
    public sealed class CommunicationLogWorker : BackgroundService
    {
        private readonly LogQueue _logQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CommunicationLogWorker> _logger;

        public CommunicationLogWorker(
            LogQueue logQueue,
            IServiceScopeFactory scopeFactory,
            ILogger<CommunicationLogWorker> logger)
        {
            _logQueue = logQueue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            var readTask = _logQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var cleanupTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(readTask, cleanupTask);
                    if (completed == cleanupTask)
                    {
                        if (await cleanupTask)
                            await PerformCleanupAsync(stoppingToken);
                        cleanupTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                        continue;
                    }

                    if (!await readTask)
                        break;

                    var batch = new List<CommunicationLog>(250);
                    while (batch.Count < 250 && _logQueue.Reader.TryRead(out var log))
                        batch.Add(log);
                    if (batch.Count > 0)
                        await PersistBatchAsync(batch, stoppingToken);

                    readTask = _logQueue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        private async Task PersistBatchAsync(List<CommunicationLog> batch, CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                await db.CommunicationLogs.AddRangeAsync(batch, stoppingToken);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to persist communication log batch of {Count}.", batch.Count);
                    await TryLogSystemEventAsync(
                    WaterMeterServer.Domain.Entities.LogLevel.Error,
                    "CommunicationLog",
                    $"Persisting a batch of {batch.Count} communication logs failed.",
                    ex,
                    stoppingToken);
            }
        }

        private async Task PerformCleanupAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                var threshold = DateTime.UtcNow.AddDays(-30);
                var oldLogs = db.CommunicationLogs.Where(l => l.Timestamp < threshold);
                db.CommunicationLogs.RemoveRange(oldLogs);
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Communication log cleanup completed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to clean up expired communication logs.");
                await TryLogSystemEventAsync(WaterMeterServer.Domain.Entities.LogLevel.Error, "CommunicationLog", "Cleanup failed.", ex, stoppingToken);
            }
        }

        private async Task TryLogSystemEventAsync(
            WaterMeterServer.Domain.Entities.LogLevel level,
            string category,
            string message,
            Exception exception,
            CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                db.SystemEventLogs.Add(new SystemEventLog
                {
                    Level = level,
                    Category = category,
                    Message = message,
                    ExceptionDetails = exception.ToString(),
                    Timestamp = DateTime.UtcNow
                });
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception loggingException)
            {
                _logger.LogCritical(loggingException, "Unable to persist system event after logging failure.");
            }
        }
    }
}
