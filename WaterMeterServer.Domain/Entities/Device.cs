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
        public string SerialNumber { get; set; } = null!; // شماره سریال 12 رقمی BCD
        public string? DeviceUid { get; set; } // IMEI یا شناسه یکتا
        public DeviceStatus Status { get; set; } = DeviceStatus.Active;
        public DateTime? LastSeenAt { get; set; }
        public string? FirmwareVersion { get; set; }
        public uint LastSessionId { get; set; }
        public string? HardwareVersion { get; set; }
        public string? CommunicationIccid { get; set; } // برای مدیریت سیم‌کارت
        public DateTime? LastFirmwareUpdateAt { get; set; }
        public bool IsUpgradePending { get; set; } // آیا دستوری برای آپدیت در صف هست؟

        // ناوبری (Navigation)
        public ICollection<TelemetryRecord> TelemetryRecords { get; set; } = new List<TelemetryRecord>();
    }
}