namespace WaterMeterServer.Domain.Constants
{
    public static class FirmwareObjectIds
    {
        public const ushort UpgradeRequest = 0x430C; // درخواست ارتقا (شامل نسخه هدف) 
        public const ushort FirmwareInfo = 0x4305;   // اطلاعات فریمور (سایز و CRC) 
        public const ushort Segmentation = 0x4306;    // پارامترهای قطعه‌بندی (Offset) 
        public const ushort DataStructure = 0x4307;  // ساختار داده‌های فریمور (Binary Data) 
        public const ushort UpgradeStatus = 0x4304;   // وضعیت نهایی ارتقا 
    }
}