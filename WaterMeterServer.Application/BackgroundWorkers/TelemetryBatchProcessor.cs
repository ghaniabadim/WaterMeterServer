using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Application.BackgroundWorkers
{
    public class TelemetryBatchProcessor : BackgroundService
    {
        private readonly ITelemetryBuffer _telemetryBuffer;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TelemetryBatchProcessor> _logger;

        public TelemetryBatchProcessor(
            ITelemetryBuffer telemetryBuffer,
            IServiceScopeFactory scopeFactory,
            ILogger<TelemetryBatchProcessor> logger)
        {
            _telemetryBuffer = telemetryBuffer;
            _scopeFactory = scopeFactory;
            _logger = logger;
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
                        using (var scope = _scopeFactory.CreateScope())
                        {
                            var dbContext = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                            await dbContext.TelemetryRecords.AddRangeAsync(records, stoppingToken);
                            await dbContext.SaveChangesAsync(stoppingToken);

                            _logger.LogInformation("{Count} telemetry records written to remote database via SSH.", records.Length);
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