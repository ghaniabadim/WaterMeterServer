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
    }
}