namespace WaterMeterServer.Domain.Entities
{
    public class TelemetryRecord
    {
        public int DeviceId { get; set; }
        public DateTime RecordedAt { get; set; } // زمان ثبت در سرور
        public DateTime TerminalTime { get; set; } // زمان ساعت داخلی کنتور

        public double MainVoltage { get; set; }
        public double BackupVoltage { get; set; }
        public int SignalStrength { get; set; }

        public double PositiveCumulative { get; set; } // حجم تجمعی مثبت (m3)
        public double ReverseCumulative { get; set; }  // حجم تجمعی معکوس (m3)
        public double InstantaneousFlow { get; set; }  // دبی لحظه‌ای (m3/h)

        public double RemainingAmount { get; set; }    // مقدار باقی‌مانده شارژ
        public uint PumpRunningTime { get; set; }      // زمان کارکرد پمپ (ساعت)

        public double RemainingAllowance { get; set; } // سهمیه باقی‌مانده دوره
        public double AdditionalUsage { get; set; }    // مصرف اضافی دوره

        public double MaxDailyTraffic { get; set; }    // حداکثر ترافیک ۲۴ ساعت گذشته
        public double AvgDailyFlowRate { get; set; }   // میانگین دبی روزانه
    }
}