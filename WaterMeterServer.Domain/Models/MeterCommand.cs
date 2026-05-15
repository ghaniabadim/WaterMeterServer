namespace WaterMeterServer.Domain.Models
{
    public enum MeterCommandType
    {
        ReadingCommand = 0x04,
        WritingCommand = 0x05,
        ReadRecentRecords = 0x08
    }

    public enum CommandStatus
    {
        Pending,
        InProgress,
        Succeeded,
        Failed,
        TimedOut
    }

    public class MeterCommand
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string MeterId { get; set; } = null!;
        public MeterCommandType Type { get; set; }
        public CommandStatus Status { get; set; } = CommandStatus.Pending;

        public ushort CommandId { get; set; } // Object ID (e.g., 0x0002 for Clock)
        public byte[]? Payload { get; set; }
        public ushort Sequence { get; set; } // REQID برای تطبیق پاسخ

        public int RetryCount { get; set; }
        public int MaxRetry { get; set; } = 3;
    }
}