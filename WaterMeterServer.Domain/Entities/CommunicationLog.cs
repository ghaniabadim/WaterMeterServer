namespace WaterMeterServer.Domain.Entities
{

    public class CommunicationLog
    {
        public long Id { get; set; }
        public string? MeterId { get; set; }
        public string ConnectionId { get; set; } = null!;

        public string Direction { get; set; } = null!; // "Inbound" or "Outbound"
        public byte[] RawData { get; set; } = null!; // دیتای خام دریافتی یا ارسالی

        public byte? FrameType { get; set; }
        public byte? ProtocolVersion { get; set; }
        public byte? ControlCode { get; set; }
        public byte? Mid { get; set; }
        public uint? SessionId { get; set; }
        public ushort? FrameNumber { get; set; }
        public ushort? RequestSequence { get; set; }
        public byte? FunctionCode { get; set; }
        public string Result { get; set; } = "Received";
        public string? ErrorReason { get; set; }
        public long? ProcessingDurationMs { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    
}
