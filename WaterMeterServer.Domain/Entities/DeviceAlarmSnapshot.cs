using NodaTime;
using System;
using System.Collections.Generic;
using System.Text;

namespace WaterMeterServer.Domain.Entities
{
    public class DeviceAlarmSnapshot
    {
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;
        public Instant UpdatedAt { get; set; }

        // بایت 0 - فیزیکی
        public bool PumpOff { get; set; }
        public bool MeterRemoved { get; set; }
        public bool MagneticInterference { get; set; }
        public bool RelayFault { get; set; }

        // بایت 1 - هیدرولیک
        public bool EmptyPipe { get; set; }
        public bool ExcitationAlarm { get; set; }
        public bool LowSignal { get; set; }
        public bool MeasurementError { get; set; }
        public bool Backflow { get; set; }
        public bool AbnormallyHighFlow { get; set; }

        // بایت 2 - سیستمی
        public bool StorageError { get; set; }
        public bool TrafficCollectionError { get; set; }
        public bool LowBattery { get; set; }
        public bool PowerLockout { get; set; }
        public bool ExternalPowerConnected { get; set; }
    }
}
