namespace WaterMeterServer.Domain.Entities
{

    public class CommunicationLog
    {
        public long Id { get; set; }
        public string? MeterId { get; set; }
        public string ConnectionId { get; set; } = null!;

        public string Direction { get; set; } = null!; // "Inbound" or "Outbound"
        public byte[] RawData { get; set; } = null!; // دیتای خام دریافتی یا ارسالی

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    
}