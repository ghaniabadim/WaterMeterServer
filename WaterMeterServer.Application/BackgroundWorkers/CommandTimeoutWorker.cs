using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Application.BackgroundWorkers
{
    public sealed class CommandTimeoutWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SystemEventLogger _systemEventLogger;
        private readonly ILogger<CommandTimeoutWorker> _logger;

        public CommandTimeoutWorker(
            IServiceScopeFactory scopeFactory,
            SystemEventLogger systemEventLogger,
            ILogger<CommandTimeoutWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _systemEventLogger = systemEventLogger;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                    var cutoff = SystemClock.Instance.GetCurrentInstant() - Duration.FromMinutes(2);
                    var timedOut = await db.DeviceCommandLogs
                        .Where(x => x.Status == DeviceCommandStatus.Pending && x.SentAt < cutoff)
                        .ToListAsync(stoppingToken);
                    if (timedOut.Count == 0)
                        continue;

                    foreach (var command in timedOut)
                    {
                        command.Status = DeviceCommandStatus.Timeout;
                        command.ExecutionResult = "Timeout: no response received within 2 minutes.";
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogWarning("Marked {Count} pending commands as timed out.", timedOut.Count);
                    await _systemEventLogger.LogEventAsync(
                        WaterMeterServer.Domain.Entities.LogLevel.Warning,
                        "Command",
                        $"{timedOut.Count} command(s) timed out.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }
}
