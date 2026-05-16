using Microsoft.Extensions.Hosting;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Application.BackgroundWorkers
{
    public class TelemetryBatchProcessor : BackgroundService
    {
        private readonly ITelemetryBuffer _buffer;
        private readonly IServiceProvider _serviceProvider;

        public TelemetryBatchProcessor(ITelemetryBuffer buffer, IServiceProvider serviceProvider)
        {
            _buffer = buffer;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // منطق Batch Insert که قبلاً طراحی کردیم در اینجا قرار می‌گیرد
            // داده‌ها را از بافر می‌خواند و هر 100 رکورد را یکجا ذخیره می‌کند
        }
    }
}