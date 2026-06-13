using NodaTime;
using System;
using System.Collections.Generic;
using System.Text;

namespace WaterMeterServer.Domain.Entities
{
    public class DeviceDailyFrozenSnapshot
    {
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;
        public Instant FrozenAt { get; set; }
        public double PositiveCumulative { get; set; }
        public double ReverseCumulative { get; set; }
    }
}
