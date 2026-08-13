using NodaTime;

namespace WaterMeterServer.Domain.Entities
{
    public class WaterUsageRecord
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;
        public ushort RecordObjectId { get; set; }
        public int RecordIndex { get; set; }
        public Instant ReceivedAt { get; set; }
        public Instant? RecordTime { get; set; }
        public byte[] RawData { get; set; } = Array.Empty<byte>();
    }
}
