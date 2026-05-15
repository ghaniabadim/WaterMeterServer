using Microsoft.Extensions.DependencyInjection;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Infrastructure.Logging
{
    public class CommunicationLogger
    {
        private readonly IServiceProvider _serviceProvider;

        public CommunicationLogger(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task LogCommunicationAsync(string meterId, string connectionId, string direction, byte[] data)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

            var log = new CommunicationLog
            {
                MeterId = meterId,
                ConnectionId = connectionId,
                Direction = direction,
                RawData = data,
                Timestamp = DateTime.UtcNow
            };

            db.CommunicationLogs.Add(log);
            await db.SaveChangesAsync();
        }
    }
}