using WaterMeterServer.Domain.Models;

namespace WaterMeterServer.Domain.Entities
{
    public class DeviceCommandLog
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;

        public ushort CommandId { get; set; } // مثل 0x0002
        public byte[] RequestPayload { get; set; } = null!;
        public byte[]? ResponsePayload { get; set; }

        public CommandStatus Status { get; set; }
        public ushort SequenceNumber { get; set; } // REQID

        public DateTime SentAt { get; set; } = DateTime.UtcNow;
        public DateTime? RespondedAt { get; set; }
        public string? ExecutionResult { get; set; } // شرح خطا یا موفقیت
    }
}