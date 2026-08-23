namespace WaterMeterServer.Domain.Entities
{
    public enum UpgradeState
    {
        Idle = 0,
        RequestInitiated = 1,    // ارسال درخواست 430CH
        InfoSent = 2,            // ارسال اطلاعات فایل 4305H
        SegmentRequested = 3,    // دریافت درخواست قطعه 4306H
        DataTransferring = 4,    // ارسال دیتای باینری 4307H
        WaitingForStatus = 5,    // منتظر تایید نهایی 4304H
        Completed = 6,
        Failed = 7
    }

    public class FirmwareUpgradeRequest
    {
        public long Id { get; set; }
        public string MeterId { get; set; } = null!; // شماره کنتور
        public string TargetVersion { get; set; } = null!; // نسخه فریمور هدف
        public string FilePath { get; set; } = null!; // آدرس فیزیکی فایل در سرور
        public int FileSize { get; set; } // سایز فایل
        public uint FileCrc32 { get; set; } // CRC32 فایل برای تایید سلامت

        // وضعیت چرخه بروزرسانی
        public UpgradeState State { get; set; } = UpgradeState.Idle;
        public int CurrentOffset { get; set; } // آفست فعلی (برای قابلیت Resume)
        public int ChunkSize { get; set; } = 256; // سایز هر پکت ارسالی (معمولاً 256 یا 512)
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; } = 3;

        public string? LastErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastUpdatedAt { get; set; }
    }
}
