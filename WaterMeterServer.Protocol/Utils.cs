using NodaTime;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace WaterMeterServer.Protocol
{
    public static class Utils
    {
        public static byte ByteToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));
        public static byte BcdToByte(byte bcd) => (byte)(((bcd >> 4) * 10) + (bcd & 0x0F));

        public static byte ReadByteAt(ReadOnlySequence<byte> seq, int index)
        {
            return seq.Slice(index, 1).FirstSpan[0];
        }

        public static ushort ReadUInt16BigEndianAt(ReadOnlySequence<byte> seq, int index)
        {
            Span<byte> tmp = stackalloc byte[2];
            seq.Slice(index, 2).CopyTo(tmp);
            return BinaryPrimitives.ReadUInt16BigEndian(tmp);
        }

        public static string ByteArrayToHexString(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        public static long BcdToInt(ReadOnlySpan<byte> data)
        {
            long result = 0;
            foreach (byte b in data)
            {
                result = result * 100 + (b >> 4) * 10 + (b & 0x0F);
            }
            return result;
        }

        public static int LittleEndianToInt(ReadOnlySpan<byte> data)
        {
            if (data.Length == 2) return BinaryPrimitives.ReadInt16LittleEndian(data);
            if (data.Length == 4) return BinaryPrimitives.ReadInt32LittleEndian(data);
            return 0;
        }

        public static DateTime ParseBcdDateTime(ReadOnlySpan<byte> bcd)
        {
            try
            {
                int year = 2000 + BcdToByte(bcd[0]);
                int month = BcdToByte(bcd[1]);
                int day = BcdToByte(bcd[2]);
                int hour = BcdToByte(bcd[3]);
                int minute = BcdToByte(bcd[4]);
                int second = BcdToByte(bcd[5]);
                var dateTime = new DateTime(year, month, day, hour, minute, second);
                dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);

                return dateTime;
            }
            catch { return DateTime.UtcNow; }
        }

        public static Instant DateTimeToInstant(DateTime dateTime)
        {
            DateTime utcDateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);

            return NodaTime.Instant.FromDateTimeUtc(utcDateTime);
        }

        public static long ReadUint40BigEndian(ReadOnlySpan<byte> data)
        {
            return ((long)data[0] << 32) | ((long)data[1] << 24) |
                   ((long)data[2] << 16) | ((long)data[3] << 8) | data[4];
        }

        public static int ReadInt24BigEndian(ReadOnlySpan<byte> data)
        {
            int val = (data[0] << 16) | (data[1] << 8) | data[2];
            if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000); // Sign extension
            return val;
        }

        public static string BcdToString(ReadOnlySpan<byte> bcd)
        {
            var sb = new StringBuilder(bcd.Length * 2);
            foreach (var b in bcd)
            {
                sb.Append((b >> 4).ToString("X")); // نیبل بالا
                sb.Append((b & 0x0F).ToString("X")); // نیبل پایین
            }
            return sb.ToString();
        }
    }
}