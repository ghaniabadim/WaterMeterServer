using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Protocol
{
    public static class AlarmParser
    {
        public static List<Alarm> ParseB070(ReadOnlySpan<byte> data, int deviceId)
        {
            var alarms = new List<Alarm>();

            //// بایت اول: وضعیت‌های فیزیکی
            //if ((data[0] & 0x01) != 0) alarms.Add(new Alarm { DeviceId = deviceId, AlarmType = AlarmType. /*Type = "Pump Off"*/ });
            //if ((data[0] & 0x02) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Meter Removed" });
            //if ((data[0] & 0x04) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Magnetic Interference" });
            //if ((data[0] & 0x08) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Relay Fault" });

            //// بایت دوم: آلارم‌های جریان و سیگنال
            //if ((data[1] & 0x01) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Empty Pipe" });
            //if ((data[1] & 0x08) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Low Signal" });
            //if ((data[1] & 0x20) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Backflow" });
            //if ((data[1] & 0x40) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "High Flow" });

            //// بایت سوم: آلارم‌های سیستم و باتری
            //if ((data[2] & 0x01) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Storage Error" });
            //if ((data[2] & 0x08) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "Low Battery" });
            //if ((data[2] & 0x80) != 0) alarms.Add(new Alarm { DeviceId = deviceId, Type = "External Power Connected" });

            return alarms;
        }
    }
}