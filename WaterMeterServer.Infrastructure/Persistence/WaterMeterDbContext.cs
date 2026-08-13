using Microsoft.EntityFrameworkCore;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Infrastructure.Persistence
{
    public class WaterMeterDbContext : DbContext
    {
        public WaterMeterDbContext(DbContextOptions<WaterMeterDbContext> options) : base(options)
        {
        }

        public DbSet<Device> Devices { get; set; }
        public DbSet<TelemetryRecord> TelemetryRecords { get; set; }
        public DbSet<DeviceCommandLog> DeviceCommandLogs { get; set; }
        public DbSet<DeviceAlarmSnapshot> DeviceAlarmSnapshots { get; set; }
        public DbSet<AlarmStatusLog> AlarmStatusLogs { get; set; }
        public DbSet<DeviceDailyFrozenSnapshot> DeviceDailyFrozenSnapshots { get; set; }
        public DbSet<DailyFrozenLog> DailyFrozenLogs { get; set; }
        public DbSet<Tenant> Tenants { get; set; }
        public DbSet<FirmwareUpgradeRequest> FirmwareUpgradeRequests { get; set; }
        public DbSet<FirmwareVersion> FirmwareVersions { get; set; }
        public DbSet<FirmwareUpgradeLog> FirmwareUpgradeLogs { get; set; }
        public DbSet<FirmwareChunkLog> FirmwareChunkLogs { get; set; }
        public DbSet<CommunicationLog> CommunicationLogs { get; set; }
        public DbSet<SystemEventLog> SystemEventLogs { get; set; }
        public DbSet<WaterUsageRecord> WaterUsageRecords { get; set; }
        public DbSet<HourlyWaterUsage> HourlyWaterUsages { get; set; }
        public DbSet<DailyWaterUsage> DailyWaterUsages { get; set; }
        public DbSet<MonthlyWaterUsage> MonthlyWaterUsages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ۱. پیکربندی دستگاه‌ها
            modelBuilder.Entity<Device>(entity =>
            {
                entity.ToTable("devices");
                entity.HasIndex(x => x.SerialNumber).IsUnique();
                entity.Property(x => x.HasPendingCommands).HasDefaultValue(false);
            });

            // ۲. پیکربندی تلمتری به صورت تاریخی (Time-series)
            modelBuilder.Entity<TelemetryRecord>(entity =>
            {
                entity.ToTable("telemetry_records");
                entity.HasKey(x => new { x.DeviceId, x.RecordedAt });
                entity.HasIndex(x => x.RecordedAt);
                entity.HasOne(x => x.Device)
                      .WithMany(d => d.TelemetryRecords)
                      .HasForeignKey(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ۳. پیکربندی اسنپ‌شات آلارم‌ها (رابطه ۱ به ۱ با دستگاه)
            modelBuilder.Entity<DeviceAlarmSnapshot>(entity =>
            {
                entity.ToTable("device_alarm_snapshots");
                entity.HasKey(x => x.DeviceId);
                entity.HasOne(x => x.Device)
                      .WithOne(d => d.AlarmSnapshot)
                      .HasForeignKey<DeviceAlarmSnapshot>(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ۴. پیکربندی لاگ تاریخی آلارم‌ها و وقایع
            modelBuilder.Entity<AlarmStatusLog>(entity =>
            {
                entity.ToTable("alarm_status_logs");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => x.Timestamp);
                entity.HasOne(x => x.Device)
                      .WithMany(d => d.AlarmStatusLogs)
                      .HasForeignKey(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ۵. پیکربندی اسنپ‌شات فریز روزانه (رابطه ۱ به ۱ با دستگاه)
            modelBuilder.Entity<DeviceDailyFrozenSnapshot>(entity =>
            {
                entity.ToTable("device_daily_frozen_snapshots");
                entity.HasKey(x => x.DeviceId);
                entity.HasOne(x => x.Device)
                      .WithOne(d => d.DailyFrozenSnapshot)
                      .HasForeignKey<DeviceDailyFrozenSnapshot>(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ۶. پیکربندی لاگ تاریخی فریز روزانه
            modelBuilder.Entity<DailyFrozenLog>(entity =>
            {
                entity.ToTable("daily_frozen_logs");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => x.FrozenAt);
                entity.HasOne(x => x.Device)
                      .WithMany(d => d.DailyFrozenLogs)
                      .HasForeignKey(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // سایر ساختارهای پایه فریمورک شما بدون تغییر
            modelBuilder.Entity<CommunicationLog>(entity =>
            {
                entity.ToTable("communication_logs");
                entity.Property(x => x.RawData).HasColumnType("bytea");
                entity.HasIndex(x => x.Timestamp);
                entity.HasIndex(x => new { x.MeterId, x.Timestamp });
                entity.Property(x => x.Result).HasMaxLength(32);
                entity.Property(x => x.ErrorReason).HasMaxLength(512);
            });

            modelBuilder.Entity<SystemEventLog>(entity =>
            {
                entity.ToTable("system_event_logs");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => x.Timestamp);
                entity.HasIndex(x => new { x.Category, x.Level, x.Timestamp });
                entity.Property(x => x.Category).HasMaxLength(64);
                entity.Property(x => x.Message).HasMaxLength(2048);
            });

            modelBuilder.Entity<DeviceCommandLog>(entity =>
            {
                entity.ToTable("device_command_logs");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.RequestPayload).HasColumnType("bytea");
                entity.Property(x => x.ResponsePayload).HasColumnType("bytea");
                entity.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<FirmwareUpgradeLog>(entity =>
            {
                entity.ToTable("firmware_upgrade_logs");
                entity.HasOne(x => x.FirmwareVersion)
                    .WithMany()
                    .HasForeignKey(x => x.FirmwareVersionId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<FirmwareChunkLog>(entity =>
            {
                entity.ToTable("firmware_chunk_logs");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => new { x.FirmwareUpgradeRequestId, x.Offset });
                entity.Property(x => x.Sha256).HasMaxLength(64);
                entity.HasOne(x => x.FirmwareUpgradeRequest)
                    .WithMany()
                    .HasForeignKey(x => x.FirmwareUpgradeRequestId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.Device)
                    .WithMany()
                    .HasForeignKey(x => x.DeviceId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<FirmwareVersion>(entity =>
            {
                entity.ToTable("firmware_versions");
                entity.Property(x => x.BinaryData).HasColumnType("bytea");
            });

            modelBuilder.Entity<WaterUsageRecord>(entity =>
            {
                entity.ToTable("water_usage_records");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.RawData).HasColumnType("bytea");
                entity.HasIndex(x => new { x.DeviceId, x.RecordObjectId, x.RecordTime });
                entity.HasOne(x => x.Device)
                      .WithMany()
                      .HasForeignKey(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<HourlyWaterUsage>(entity =>
            {
                entity.ToTable("hourly_water_usages");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => new { x.DeviceId, x.HourStart }).IsUnique();
                entity.HasOne(x => x.Device)
                      .WithMany()
                      .HasForeignKey(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<DailyWaterUsage>(entity =>
            {
                entity.ToTable("daily_water_usages");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => new { x.DeviceId, x.UsageDate }).IsUnique();
                entity.HasOne(x => x.Device)
                      .WithMany()
                      .HasForeignKey(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MonthlyWaterUsage>(entity =>
            {
                entity.ToTable("monthly_water_usages");
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => new { x.DeviceId, x.UsageYear, x.UsageMonth }).IsUnique();
                entity.HasOne(x => x.Device)
                      .WithMany()
                      .HasForeignKey(x => x.DeviceId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
