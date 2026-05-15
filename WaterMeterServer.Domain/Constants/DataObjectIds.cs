namespace WaterMeterServer.Domain.Constants
{
    public static class DataObjectIds
    {
        public const ushort Clock = 0x0002;                // ساعت کنتور
        public const ushort FirmwareVersion = 0x4211;     // نسخه میان‌افزار
        public const ushort ScheduledUpload = 0xB061;     // تنظیمات آپلود زمان‌بندی شده
        public const ushort ServerAddress = 0x2007;       // آدرس IP و پورت سرور
        public const ushort DailyFrozenData = 0xB05C;     // داده‌های منجمد روزانه
    }
}