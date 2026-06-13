using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Text;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Application.BackgroundWorkers
{
    public class CommunicationLogWorker : BackgroundService
    {
        private readonly LogQueue _logQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private DateTime _lastCleanupTime = DateTime.MinValue;

        public CommunicationLogWorker(LogQueue logQueue, IServiceScopeFactory scopeFactory)
        {
            _logQueue = logQueue;
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // ۱. پردازش لاگ‌های ورودی
                if (await _logQueue.Reader.WaitToReadAsync(stoppingToken))
                {
                    while (_logQueue.Reader.TryRead(out var log))
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                            db.CommunicationLogs.Add(log);
                            await db.SaveChangesAsync(stoppingToken);
                        }
                        catch (Exception ex) { /* Log error */ }
                    }
                }

                // ۲. اجرای پاکسازی (مثلاً هر ۲۴ ساعت یک‌بار)
                if (DateTime.UtcNow - _lastCleanupTime > TimeSpan.FromHours(24))
                {
                    await PerformCleanupAsync();
                    _lastCleanupTime = DateTime.UtcNow;
                }
            }
        }

        private async Task PerformCleanupAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                // حذف لاگ‌های قدیمی‌تر از ۳۰ روز
                var threshold = DateTime.UtcNow.AddDays(-30);
                var oldLogs = db.CommunicationLogs.Where(l => l.Timestamp < threshold);

                db.CommunicationLogs.RemoveRange(oldLogs);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Log cleanup error
            }
        }
    }

}
