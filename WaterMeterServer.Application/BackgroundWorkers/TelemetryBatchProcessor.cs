using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Persistence;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Application.Services;

namespace WaterMeterServer.Application.BackgroundWorkers
{
    public class TelemetryBatchProcessor : BackgroundService
    {
        private readonly ITelemetryBuffer _telemetryBuffer;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TelemetryBatchProcessor> _logger;
        private readonly SystemEventLogger _systemEventLogger;
        private readonly WaterUsageAggregationService _usageAggregationService;

        public TelemetryBatchProcessor(
            ITelemetryBuffer telemetryBuffer,
            IServiceScopeFactory scopeFactory,
            ILogger<TelemetryBatchProcessor> logger,
            SystemEventLogger systemEventLogger,
            WaterUsageAggregationService usageAggregationService)
        {
            _telemetryBuffer = telemetryBuffer;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _systemEventLogger = systemEventLogger;
            _usageAggregationService = usageAggregationService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var records = await _telemetryBuffer.PopRecordsAsync(100, stoppingToken);

                    if (records != null && records.Length > 0)
                    {
                        Exception? lastException = null;
                        var persisted = false;
                        for (var attempt = 1; attempt <= 3 && !persisted; attempt++)
                        {
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var dbContext = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                                await dbContext.TelemetryRecords.AddRangeAsync(records, stoppingToken);
                                await _usageAggregationService.AggregateAsync(dbContext, records, stoppingToken);
                                await dbContext.SaveChangesAsync(stoppingToken);
                                persisted = true;
                                _logger.LogInformation(
                                    "{Count} telemetry records written to remote database via SSH on attempt {Attempt}.",
                                    records.Length, attempt);
                            }
                            catch (Exception ex) when (attempt < 3)
                            {
                                lastException = ex;
                                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                lastException = ex;
                            }
                        }

                        if (!persisted && lastException != null)
                        {
                            _logger.LogError(lastException, "Telemetry batch of {Count} could not be persisted after retries.", records.Length);
                            await _systemEventLogger.LogEventAsync(
                                WaterMeterServer.Domain.Entities.LogLevel.Error,
                                "Telemetry",
                                $"Telemetry batch of {records.Length} records was not persisted after 3 attempts.",
                                lastException.ToString());
                        }
                    }

                    await Task.Delay(5000, stoppingToken); 
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during telemetry batch insertion to Database.");
                }
            }
        }
    }
}
