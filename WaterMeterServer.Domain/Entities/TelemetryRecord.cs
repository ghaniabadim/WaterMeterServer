namespace WaterMeterServer.Domain.Entities
{
    public class TelemetryRecord
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;

        public DateTime RecordedAt { get; set; } // زمان واقعی کنتور (از شیء 70F2H)
        public DateTime ServerReceivedAt { get; set; } = DateTime.UtcNow;

        public decimal WaterUsage { get; set; } // حجم مصرفی (واحد 10L)
        public decimal FlowRate { get; set; }   // دبی لحظه‌ای (واحد 10L/h)
        public decimal BatteryVoltage { get; set; } // ولتاژ (واحد 0.001V)
        public int SignalStrength { get; set; } // CSQ
    }
}