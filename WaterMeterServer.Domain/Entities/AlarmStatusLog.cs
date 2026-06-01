using NodaTime;
using System;
using System.Collections.Generic;
using System.Text;

namespace WaterMeterServer.Domain.Entities
{
    public class AlarmStatusLog
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;
        public Instant Timestamp { get; set; }
        public string LogType { get; set; } = null!; // "Alarm" یا "Event"
        public string Code { get; set; } = null!;    // مثلاً "LowBattery" یا کد واقعه "0x70EE"
        public bool IsActive { get; set; }           // فعال شدن (True) یا رفع شدن (False)
        public string? Description { get; set; }
    }
}
