using Microsoft.Extensions.DependencyInjection;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Infrastructure.Logging
{
    public sealed class SystemEventLogger
    {
        private readonly IServiceProvider _serviceProvider;

        public SystemEventLogger(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task LogEventAsync(LogLevel level, string category, string message, string? exception = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

            db.SystemEventLogs.Add(new SystemEventLog
            {
                Level = level,
                Category = category,
                Message = message,
                ExceptionDetails = exception,
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }
    }
}
