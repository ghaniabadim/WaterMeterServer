namespace WaterMeterServer.Domain.Entities
{
    public enum TenantStatus : byte
    {
        Inactive = 0,
        Active = 1,
        Suspended = 2
    }

    public class Tenant
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string Code { get; set; } = null!; // کد اشتراک یا کد شناسایی مالک
        public TenantStatus Status { get; set; }
        public string TimeZone { get; set; } = "Asia/Tehran";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // روابط
        public ICollection<Device> Devices { get; set; } = new List<Device>();
    }
}