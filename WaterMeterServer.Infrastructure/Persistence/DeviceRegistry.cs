using Microsoft.EntityFrameworkCore;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.Infrastructure.Persistence
{
    public class DeviceRegistry : IDeviceRegistry
    {
        private readonly WaterMeterDbContext _db;
        private readonly ILogger<DeviceRegistry> _logger;

        public DeviceRegistry(WaterMeterDbContext db, ILogger<DeviceRegistry> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<Device> EnsureDeviceExistsAsync(string meterId)
        {
            var device = await _db.Devices.FirstOrDefaultAsync(d => d.SerialNumber == meterId);

            if (device == null)
            {
                try
                {
                    device = new Device
                    {
                        SerialNumber = meterId,
                    };

                    _db.Devices.Add(device);
                    await _db.SaveChangesAsync();
                    _logger.LogInformation("New device registered: {SerialNumber}", meterId);
                }
                catch (DbUpdateException)
                {
                    _db.Entry(device!).State = EntityState.Detached;
                    device = await _db.Devices.AsNoTracking()
                     .FirstOrDefaultAsync(d => d.SerialNumber == meterId);
                    if (device == null) throw;
                }
            }

            return device!;
        }

        public async Task UpdateDeviceAtivityAsync(Device device)
        {
            // متصل کردن انتیتی به کانتکست جاری دیتابیس این اسکوپ
            if (_db.Entry(device).State == EntityState.Detached)
            {
                _db.Devices.Attach(device);
            }

            // علامت‌گذاری فیلدهای پایه‌ای برای تغییر
            device.LastSeenAt = Utils.DateTimeToInstant( DateTime.UtcNow);
            _db.Entry(device).Property(x => x.LastSeenAt).IsModified = true;
            _db.Entry(device).Property(x => x.LastSessionId).IsModified = true;

            // علامت‌گذاری فیلدهای مربوط به اسنپ‌شات آخرین وضعیت تلمتری (Snapshot)
            _db.Entry(device).Property(x => x.LastMainVoltage).IsModified = true;
            _db.Entry(device).Property(x => x.LastBackupVoltage).IsModified = true;
            _db.Entry(device).Property(x => x.LastSignalStrength).IsModified = true;
            _db.Entry(device).Property(x => x.LastPositiveCumulative).IsModified = true;
            _db.Entry(device).Property(x => x.LastReverseCumulative).IsModified = true;
            _db.Entry(device).Property(x => x.LastInstantaneousFlow).IsModified = true;
            _db.Entry(device).Property(x => x.LastRemainingAmount).IsModified = true;
            _db.Entry(device).Property(x => x.LastPumpRunningTime).IsModified = true;

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning("Concurrency issue updating activity/snapshot for {SerialNumber}: {Message}",
                    device.SerialNumber, ex.Message);
            }
        }
    }
}
