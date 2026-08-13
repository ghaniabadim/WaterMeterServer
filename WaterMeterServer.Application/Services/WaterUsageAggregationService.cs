using Microsoft.EntityFrameworkCore;
using NodaTime;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Infrastructure.Persistence;

namespace WaterMeterServer.Application.Services
{
    public sealed class WaterUsageAggregationService
    {
        public async Task AggregateAsync(
            WaterMeterDbContext db,
            IReadOnlyCollection<TelemetryRecord> records,
            CancellationToken cancellationToken = default)
        {
            foreach (var group in records
                .Where(x => x.PositiveCumulative >= 0 && x.ReverseCumulative >= 0)
                .GroupBy(x => x.DeviceId))
            {
                await AggregateHourlyAsync(db, group, cancellationToken);
                await AggregateDailyAsync(db, group, cancellationToken);
                await AggregateMonthlyAsync(db, group, cancellationToken);
            }
        }

        private static async Task AggregateHourlyAsync(
            WaterMeterDbContext db,
            IEnumerable<TelemetryRecord> records,
            CancellationToken cancellationToken)
        {
            foreach (var group in records.GroupBy(x => TruncateToHour(x.RecordedAt)))
            {
                var deviceId = group.First().DeviceId;
                var row = await db.HourlyWaterUsages
                    .SingleOrDefaultAsync(
                        x => x.DeviceId == deviceId && x.HourStart == group.Key,
                        cancellationToken);
                if (row == null)
                {
                    row = new HourlyWaterUsage
                    {
                        DeviceId = deviceId,
                        HourStart = group.Key
                    };
                    db.HourlyWaterUsages.Add(row);
                }

                Apply(row, group);
            }
        }

        private static async Task AggregateDailyAsync(
            WaterMeterDbContext db,
            IEnumerable<TelemetryRecord> records,
            CancellationToken cancellationToken)
        {
            foreach (var group in records.GroupBy(x => ToUtcDate(x.RecordedAt)))
            {
                var deviceId = group.First().DeviceId;
                var row = await db.DailyWaterUsages
                    .SingleOrDefaultAsync(
                        x => x.DeviceId == deviceId && x.UsageDate == group.Key,
                        cancellationToken);
                if (row == null)
                {
                    row = new DailyWaterUsage { DeviceId = deviceId, UsageDate = group.Key };
                    db.DailyWaterUsages.Add(row);
                }

                Apply(row, group);
            }
        }

        private static async Task AggregateMonthlyAsync(
            WaterMeterDbContext db,
            IEnumerable<TelemetryRecord> records,
            CancellationToken cancellationToken)
        {
            foreach (var group in records.GroupBy(x =>
                new { x.RecordedAt.InUtc().Year, Month = x.RecordedAt.InUtc().Month }))
            {
                var deviceId = group.First().DeviceId;
                var row = await db.MonthlyWaterUsages
                    .SingleOrDefaultAsync(
                        x => x.DeviceId == deviceId &&
                             x.UsageYear == group.Key.Year &&
                             x.UsageMonth == group.Key.Month,
                        cancellationToken);
                if (row == null)
                {
                    row = new MonthlyWaterUsage
                    {
                        DeviceId = deviceId,
                        UsageYear = group.Key.Year,
                        UsageMonth = group.Key.Month
                    };
                    db.MonthlyWaterUsages.Add(row);
                }

                Apply(row, group);
            }
        }

        private static void Apply<T>(T row, IEnumerable<TelemetryRecord> source)
            where T : class
        {
            var records = source.OrderBy(x => x.RecordedAt).ToList();
            var first = records[0];
            var last = records[^1];

            switch (row)
            {
                case HourlyWaterUsage hourly:
                    ApplyValues(
                        records, hourly.FirstReadingAt, hourly.LastReadingAt,
                        hourly.InitialPositiveCumulative, hourly.FinalPositiveCumulative,
                        hourly.InitialReverseCumulative, hourly.FinalReverseCumulative,
                        (firstAt, lastAt, initialPositive, finalPositive, initialReverse, finalReverse, count) =>
                        {
                            hourly.FirstReadingAt = firstAt;
                            hourly.LastReadingAt = lastAt;
                            hourly.InitialPositiveCumulative = initialPositive;
                            hourly.FinalPositiveCumulative = finalPositive;
                            hourly.InitialReverseCumulative = initialReverse;
                            hourly.FinalReverseCumulative = finalReverse;
                            hourly.SampleCount += count;
                            hourly.PositiveUsage = Math.Max(0, finalPositive - initialPositive);
                            hourly.ReverseUsage = Math.Max(0, finalReverse - initialReverse);
                            hourly.NetUsage = Math.Max(0, hourly.PositiveUsage - hourly.ReverseUsage);
                            hourly.UpdatedAt = DateTime.UtcNow;
                        });
                    break;
                case DailyWaterUsage daily:
                    ApplyValues(
                        records, daily.FirstReadingAt, daily.LastReadingAt,
                        daily.InitialPositiveCumulative, daily.FinalPositiveCumulative,
                        daily.InitialReverseCumulative, daily.FinalReverseCumulative,
                        (firstAt, lastAt, initialPositive, finalPositive, initialReverse, finalReverse, count) =>
                        {
                            daily.FirstReadingAt = firstAt;
                            daily.LastReadingAt = lastAt;
                            daily.InitialPositiveCumulative = initialPositive;
                            daily.FinalPositiveCumulative = finalPositive;
                            daily.InitialReverseCumulative = initialReverse;
                            daily.FinalReverseCumulative = finalReverse;
                            daily.SampleCount += count;
                            daily.PositiveUsage = Math.Max(0, finalPositive - initialPositive);
                            daily.ReverseUsage = Math.Max(0, finalReverse - initialReverse);
                            daily.NetUsage = Math.Max(0, daily.PositiveUsage - daily.ReverseUsage);
                            daily.UpdatedAt = DateTime.UtcNow;
                        });
                    break;
                case MonthlyWaterUsage monthly:
                    ApplyValues(
                        records, monthly.FirstReadingAt, monthly.LastReadingAt,
                        monthly.InitialPositiveCumulative, monthly.FinalPositiveCumulative,
                        monthly.InitialReverseCumulative, monthly.FinalReverseCumulative,
                        (firstAt, lastAt, initialPositive, finalPositive, initialReverse, finalReverse, count) =>
                        {
                            monthly.FirstReadingAt = firstAt;
                            monthly.LastReadingAt = lastAt;
                            monthly.InitialPositiveCumulative = initialPositive;
                            monthly.FinalPositiveCumulative = finalPositive;
                            monthly.InitialReverseCumulative = initialReverse;
                            monthly.FinalReverseCumulative = finalReverse;
                            monthly.SampleCount += count;
                            monthly.PositiveUsage = Math.Max(0, finalPositive - initialPositive);
                            monthly.ReverseUsage = Math.Max(0, finalReverse - initialReverse);
                            monthly.NetUsage = Math.Max(0, monthly.PositiveUsage - monthly.ReverseUsage);
                            monthly.UpdatedAt = DateTime.UtcNow;
                        });
                    break;
            }
        }

        private static void ApplyValues(
            IReadOnlyList<TelemetryRecord> records,
            Instant existingFirstAt,
            Instant existingLastAt,
            double existingInitialPositive,
            double existingFinalPositive,
            double existingInitialReverse,
            double existingFinalReverse,
            Action<Instant, Instant, double, double, double, double, int> apply)
        {
            var first = records[0];
            var last = records[^1];
            var hasExisting = existingLastAt != default;
            var firstAt = hasExisting && existingFirstAt < first.RecordedAt ? existingFirstAt : first.RecordedAt;
            var lastAt = hasExisting && existingLastAt > last.RecordedAt ? existingLastAt : last.RecordedAt;
            var initialPositive = hasExisting
                ? Math.Min(existingInitialPositive, first.PositiveCumulative)
                : first.PositiveCumulative;
            var finalPositive = hasExisting
                ? Math.Max(existingFinalPositive, last.PositiveCumulative)
                : last.PositiveCumulative;
            var initialReverse = hasExisting
                ? Math.Min(existingInitialReverse, first.ReverseCumulative)
                : first.ReverseCumulative;
            var finalReverse = hasExisting
                ? Math.Max(existingFinalReverse, last.ReverseCumulative)
                : last.ReverseCumulative;
            apply(firstAt, lastAt, initialPositive, finalPositive, initialReverse, finalReverse, records.Count);
        }

        private static Instant TruncateToHour(Instant value)
        {
            var utc = value.ToDateTimeUtc();
            return Instant.FromDateTimeUtc(new DateTime(
                utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc));
        }

        private static LocalDate ToUtcDate(Instant value) => value.InUtc().Date;
    }
}
