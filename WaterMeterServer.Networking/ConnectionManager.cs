using System.Collections.Concurrent;
using WaterMeterServer.Domain.Models;

namespace WaterMeterServer.Networking
{
    public class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, ConnectionContext> _meters = new();

        public void AddOrUpdate(string meterId, ConnectionContext context)
        {
            _meters[meterId] = context;
        }

        public async Task SendPacketAsync(string meterId, byte[] data)
        {
            if (_meters.TryGetValue(meterId, out var context))
            {
                await context.Writer.WriteAsync(data);
                await context.Writer.FlushAsync();
            }
        }
    }
}