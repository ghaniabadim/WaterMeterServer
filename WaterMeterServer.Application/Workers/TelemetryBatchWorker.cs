using Microsoft.Extensions.Hosting;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Application.Workers
{
    public class TelemetryBatchWorker : BackgroundService
    {
        private readonly ITelemetryBuffer _buffer;
        private readonly IServiceProvider _serviceProvider;

        public TelemetryBatchWorker(ITelemetryBuffer buffer, IServiceProvider serviceProvider)
        {
            _buffer = buffer;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var batch = new List<TelemetryRecord>();

            while (!stoppingToken.IsCancellationRequested)
            {
                // خواندن از کانال (Channel) طراحی شده در Infrastructure
                // اگر ۱۰۰ تا جمع شد یا ۱۰ ثانیه گذشت، ذخیره در دیتابیس
                // کد عملیاتی ذخیره دسته‌جمعی در اینجا قرار می‌گیرد...
            }
        }
    }
}