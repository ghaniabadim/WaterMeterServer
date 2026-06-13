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
        public DbSet<CommunicationLog> CommunicationLogs { get; set; }
        public DbSet<SystemEventLog> SystemEventLogs { get; set; }

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
            });

            modelBuilder.Entity<FirmwareVersion>(entity =>
            {
                entity.ToTable("firmware_versions");
                entity.Property(x => x.BinaryData).HasColumnType("bytea");
            });
        }
    }
}