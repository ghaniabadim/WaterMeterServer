namespace WaterMeterServer.Domain.Entities
{
    public enum FirmwareUpgradeStatus
    {
        Idle,
        RequestSent,      // 430CH ارسال شده
        Transferring,     // در حال انتقال قطعات (4307H)
        Verifying,        // در حال تایید توسط کنتور
        Completed,        // آپدیت موفق
        Failed            // خطا در آپدیت
    }


    public class FirmwareUpgradeLog
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;

        public long FirmwareVersionId { get; set; }
        public FirmwareVersion FirmwareVersion { get; set; } = null!;

        public FirmwareUpgradeStatus Status { get; set; }
        public int LastOffsetSent { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public bool IsSuccess { get; set; }
        public string? Description { get; set; } // ثبت جزئیات خطا در صورت شکست
        public DateTime ExecutionDate { get; set; } = DateTime.UtcNow;
    }
}