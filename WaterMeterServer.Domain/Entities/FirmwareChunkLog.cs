namespace WaterMeterServer.Domain.Entities
{
    public enum FirmwareChunkResult : short
    {
        Sent = 0,
        Failed = 1
    }

    public sealed class FirmwareChunkLog
    {
        public long Id { get; set; }
        public long FirmwareUpgradeRequestId { get; set; }
        public FirmwareUpgradeRequest FirmwareUpgradeRequest { get; set; } = null!;
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;
        public uint SessionId { get; set; }
        public byte Mid { get; set; }
        public ushort RequestSequence { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; }
        public string? Sha256 { get; set; }
        public int RetryCount { get; set; }
        public FirmwareChunkResult Result { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
    }
}
