namespace WaterMeterServer.Domain.Entities
{
    public enum AlarmType : short
    {
        Unknown = 0,
        Leak = 1,                 // نشت
        Tamper = 2,               // جدا شدن کنتور یا تداخل مغناطیسی
        LowBattery = 3,           // باتری ضعیف
        ReverseFlow = 4,          // جریان معکوس
        OverFlow = 6              // دبی غیرمجاز
    }

    public enum AlarmSeverity : short
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    public class Alarm
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;

        public AlarmType AlarmType { get; set; }
        public AlarmSeverity Severity { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
    }
}