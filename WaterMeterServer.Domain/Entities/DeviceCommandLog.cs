using NodaTime;
using WaterMeterServer.Domain.Models;

namespace WaterMeterServer.Domain.Entities
{
   public enum DeviceCommandStatus : short
    {
        Pending,
        Succeeded,
        Failed,
        Timeout,
        Disconnected,
        Cancelled,
        DuplicateResponse,
        SequenceMismatch,
    };
    public class DeviceCommandLog
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;
        public byte FunctionCode { get; set; }
        public int CommandId { get; set; }
        public byte[]? RequestPayload { get; set; }
        public byte[]? ResponsePayload { get; set; }
        public DeviceCommandStatus Status { get; set; }
        public string? ExecutionResult { get; set; }
        public ushort SequenceNumber { get; set; }
        public Instant CreatedAt { get; set; } 
        public Instant SentAt { get; set; }
        public Instant? RespondedAt { get; set; }
    }
}
