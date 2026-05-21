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
        public DbSet<Alarm> Alarms { get; set; }
        public DbSet<Tenant> Tenants { get; set; }
        public DbSet<FirmwareUpgradeRequest> FirmwareUpgradeRequests { get; set; }
        public DbSet<DeviceCommandLog> DeviceCommandLogs { get; set; }
        public DbSet<FirmwareVersion> FirmwareVersions { get; set; }
        public DbSet<FirmwareUpgradeLog> FirmwareUpgradeLogs { get; set; }
        public DbSet<CommunicationLog> CommunicationLogs { get; set; }
        public DbSet<SystemEventLog> SystemEventLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // پیکربندی دستگاه‌ها و ایندکس گذاری برای جستجوی سریع SerialNumber
            modelBuilder.Entity<Device>(entity =>
            {
                entity.ToTable("devices");
                entity.HasIndex(x => x.SerialNumber).IsUnique();
            });

            // پیکربندی تلمتری به صورت Time-series
            modelBuilder.Entity<TelemetryRecord>(entity =>
            {
                entity.ToTable("telemetry_records");
                entity.HasKey(x => new { x.DeviceId, x.RecordedAt });
                entity.HasIndex(x => x.RecordedAt);
            });

            // رفع خطای bytea با استفاده از متد HasColumnType (نیازمند پکیج Npgsql)
            modelBuilder.Entity<CommunicationLog>(entity =>
            {
                entity.ToTable("communication_logs");
                entity.Property(x => x.RawData).HasColumnType("bytea");
                entity.HasIndex(x => x.Timestamp);
            });

            modelBuilder.Entity<DeviceCommandLog>(entity =>
            {
                entity.ToTable("device_command_logs");
                entity.Property(x => x.RequestPayload).HasColumnType("bytea");
                entity.Property(x => x.ResponsePayload).HasColumnType("bytea");
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