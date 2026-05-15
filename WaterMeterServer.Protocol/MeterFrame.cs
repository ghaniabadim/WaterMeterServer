namespace WaterMeterServer.Protocol
{
    public sealed class MeterFrame
    {
        public byte Type { get; }
        public byte Version { get; }
        public ushort Length { get; }
        public byte Mid { get; }
        public byte ControlCode { get; }
        public byte[] DecryptedData { get; } // داده‌های بیزنس پس از رمزگشایی

        public MeterFrame(byte type, byte version, ushort length, byte mid, byte control, byte[] decryptedData)
        {
            Type = type;
            Version = version;
            Length = length;
            Mid = mid;
            ControlCode = control;
            DecryptedData = decryptedData;
        }
    }
}