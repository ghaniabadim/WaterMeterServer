using Microsoft.Extensions.DependencyInjection;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Infrastructure.Logging
{
    public class CommunicationLogger: ICommunicationLogger
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public CommunicationLogger(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }


        public async Task LogCommunicationAsync(string meterId, string connectionId, string direction, byte[] data)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

            var log = new CommunicationLog
            {
                MeterId = meterId,
                ConnectionId = connectionId,
                Direction = direction,
                RawData = data,
                Timestamp = DateTime.UtcNow
            };

            context.CommunicationLogs.Add(log);
            await context.SaveChangesAsync();
        }
    }
}