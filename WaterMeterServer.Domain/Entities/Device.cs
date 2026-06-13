using NodaTime;

namespace WaterMeterServer.Domain.Entities
{
    public enum DeviceStatus : short
    {
        Inactive = 0,
        Active = 1,
        Suspended = 2,
        Faulty = 3
    }

    public class Device
    {
        public long Id { get; set; }
        public string SerialNumber { get; set; } = null!;
        public string? DeviceUid { get; set; }
        public short Status { get; set; }
        public Instant? LastSeenAt { get; set; }
        public string? FirmwareVersion { get; set; }
        public long LastSessionId { get; set; }
        public string? HardwareVersion { get; set; }
        public string? CommunicationIccid { get; set; }
        public Instant? LastFirmwareUpdateAt { get; set; }
        public bool IsUpgradePending { get; set; }
        public bool HasPendingCommands { get; set; }
        public long? TenantId { get; set; }
        public Tenant? Tenant { get; set; }

        // --- فیلدهای آخرین وضعیت تلمتری زنده (Real-time Snapshot) ---
        public double? LastMainVoltage { get; set; }
        public double? LastBackupVoltage { get; set; }
        public int? LastSignalStrength { get; set; }
        public double? LastPositiveCumulative { get; set; }
        public double? LastReverseCumulative { get; set; }
        public double? LastInstantaneousFlow { get; set; }
        public double? LastRemainingAmount { get; set; }
        public long? LastPumpRunningTime { get; set; }

        // روابط معکوس دیتابیس
        public ICollection<TelemetryRecord> TelemetryRecords { get; set; } = new List<TelemetryRecord>();
        public DeviceAlarmSnapshot? AlarmSnapshot { get; set; }
        public DeviceDailyFrozenSnapshot? DailyFrozenSnapshot { get; set; }
        public ICollection<DailyFrozenLog> DailyFrozenLogs { get; set; } = new List<DailyFrozenLog>();
        public ICollection<AlarmStatusLog> AlarmStatusLogs { get; set; } = new List<AlarmStatusLog>();
    }
}