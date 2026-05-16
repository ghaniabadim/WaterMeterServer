using System.IO.Pipelines;
using System.Net;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Networking
{
    public class ConnectionContext
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString();
        public IPEndPoint? RemoteEndPoint { get; set; }
        public string? MeterId { get; set; }
        public uint? SessionId { get; set; }

        // ابزارهای خواندن و نوشتن در لوله (Pipe)
        public PipeReader Reader { get; set; } = null!;
        public PipeWriter Writer { get; set; } = null!;

        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }
}