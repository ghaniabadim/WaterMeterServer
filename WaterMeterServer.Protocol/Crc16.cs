using System.Buffers;

namespace WaterMeterServer.Protocol
{
    public static class Crc16
    {
        public static ushort Calculate(ReadOnlySpan<byte> data)
        {
            ushort crc = 0x0000; // مقدار اولیه 
            const ushort poly = 0x1021; // چندجمله‌ای استاندارد 

            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ poly);
                    else
                        crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }
        public static ushort Calculate(ReadOnlySequence<byte> sequence)
        {
            ushort crc = 0x0000;
            foreach (var segment in sequence)
            {
                crc = CalculateIncremental(segment.Span, crc);
            }
            return crc;
        }

        private static ushort CalculateIncremental(ReadOnlySpan<byte> data, ushort seed)
        {
            ushort crc = seed;
            const ushort poly = 0x1021;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ poly);
                    else
                        crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }
    }
}