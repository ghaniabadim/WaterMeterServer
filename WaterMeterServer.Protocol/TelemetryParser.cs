using System.Buffers.Binary;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Protocol
{
    public static class TelemetryParser
    {
        public static TelemetryRecord Parse70F2(ReadOnlySpan<byte> data, long deviceId)
        {
            // کل طول طبق جدول شما: 49 بایت
            if (data.Length < 49) throw new ArgumentException("Data length must be at least 49 bytes.");

            var record = new TelemetryRecord { DeviceId = deviceId, RecordedAt = Utils.DateTimeToInstant( DateTime.UtcNow) };

            // 1. Terminal clock (6 Bytes - YYMMDDhhmmss BCD)
            record.TerminalTime = Utils.DateTimeToInstant(Utils.ParseBcdDateTime(data.Slice(0, 6)));

            // 2. Main voltage (2 Bytes - Unit 0.001V) - Offset 6
            record.MainVoltage = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2)) / 1000.0;

            // 3. Backup power voltage (2 Bytes - 0.001V) - Offset 8
            record.BackupVoltage = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2)) / 1000.0;

            // 4. CSQ-GPRS (1 Byte) - Offset 10
            record.SignalStrength = data[10];

            // 5. Positive cumulative amount (5 Bytes - Unit 10L) - Offset 11
            record.PositiveCumulative = Utils.ReadUint40BigEndian(data.Slice(11, 5)) * 10.0 / 1000.0;

            // 6. Reverse cumulative amount (5 Bytes - Unit 10L) - Offset 16
            record.ReverseCumulative = Utils.ReadUint40BigEndian(data.Slice(16, 5)) * 10.0 / 1000.0;

            // 7. Instantaneous flow rate (3 Bytes - Unit 10 L/h) - Offset 21
            // این فیلد Signed (علامت‌دار) است
            record.InstantaneousFlow = Utils.ReadInt24BigEndian(data.Slice(21, 3)) * 10.0 / 1000.0;

            // 8. Remaining amount (4 Bytes) - Offset 24
            record.RemainingAmount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(24, 4));

            // 9. Total pump operating time (4 Bytes) - Offset 28
            record.PumpRunningTime = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(28, 4));

            // 10. Remaining allowance (5 Bytes - Unit 10L) - Offset 32
            record.RemainingAllowance = Utils.ReadUint40BigEndian(data.Slice(32, 5)) * 10.0 / 1000.0;

            // 11. Additional usage (5 Bytes - Unit 10L) - Offset 37
            record.AdditionalUsage = Utils.ReadUint40BigEndian(data.Slice(37, 5)) * 10.0 / 1000.0;

            // 12. Maximum daily traffic (3 Bytes - Unit 10L/h) - Offset 42
            record.MaxDailyTraffic = Utils.ReadInt24BigEndian(data.Slice(42, 3)) * 10.0 / 1000.0;

            // 13. Average daily flow rate (4 Bytes - Unit L/h) - Offset 45
            record.AvgDailyFlowRate = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(45, 4)) / 100.0; // بر اساس مثال 1600 = 16.00

            return record;
        }
    }
}