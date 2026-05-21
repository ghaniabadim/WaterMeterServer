using System.Collections.Concurrent;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Domain.Models;

namespace WaterMeterServer.Infrastructure.Stores
{
    public class CommandStore : ICommandStore
    {
        // استفاده از ConcurrentDictionary برای مدیریت دستورات معلق هر کنتور به صورت Thread-safe
        private readonly ConcurrentDictionary<string, List<MeterCommand>> _activeCommands = new();

        public IReadOnlyList<MeterCommand> GetPendingCommands(string meterId, int maxCount)
        {
            // اصلاح خطا: استفاده از _activeCommands به جای نام اشتباه قبلی
            if (!_activeCommands.TryGetValue(meterId, out var list))
                return new List<MeterCommand>();

            lock (list)
            {
                return list.Where(c => c.Status == CommandStatus.Pending)
                           .Take(maxCount)
                           .ToList();
            }
        }

        public void MarkInProgress(MeterCommand command)
        {
            command.Status = CommandStatus.InProgress;
        }

        public void MarkSucceeded(MeterCommand command)
        {
            command.Status = CommandStatus.Succeeded;
            // در اینجا می‌توانید منطق حذف از لیست فعال را هم اضافه کنید
        }

        public void MarkFailed(MeterCommand command, string reason)
        {
            command.Status = CommandStatus.Failed;
            // ذخیره علت خطا در لاگ (در صورت نیاز)
        }
    }
}