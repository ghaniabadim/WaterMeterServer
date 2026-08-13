using NodaTime;

namespace WaterMeterServer.Domain.Entities
{
    public sealed class MonthlyWaterUsage
    {
        public long Id { get; set; }
        public long DeviceId { get; set; }
        public Device Device { get; set; } = null!;
        public int UsageYear { get; set; }
        public int UsageMonth { get; set; }
        public Instant FirstReadingAt { get; set; }
        public Instant LastReadingAt { get; set; }
        public double InitialPositiveCumulative { get; set; }
        public double FinalPositiveCumulative { get; set; }
        public double InitialReverseCumulative { get; set; }
        public double FinalReverseCumulative { get; set; }
        public double PositiveUsage { get; set; }
        public double ReverseUsage { get; set; }
        public double NetUsage { get; set; }
        public int SampleCount { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
