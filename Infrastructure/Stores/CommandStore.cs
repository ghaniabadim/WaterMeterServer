using System.Collections.Concurrent;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Domain.Models;

namespace WaterMeterServer.Infrastructure.Stores
{
    public class CommandStore : ICommandStore
    {
        private readonly ConcurrentDictionary<string, List<MeterCommand>> _activeCommands = new();

        public IReadOnlyList<MeterCommand> GetPendingCommands(string meterId, int maxCount)
        {
            if (!_activeSessions.TryGetValue(meterId, out var list)) return new List<MeterCommand>();
            lock (list)
            {
                return list.Where(c => c.Status == CommandStatus.Pending).Take(maxCount).ToList();
            }
        }

        public void MarkInProgress(MeterCommand command) => command.Status = CommandStatus.InProgress;

        public void MarkSucceeded(MeterCommand command)
        {
            command.Status = CommandStatus.Succeeded;
            // در اینجا می‌توانید کد آپدیت دیتابیس (DeviceCommandLog) را نیز فراخوانی کنید
        }

        public void MarkFailed(MeterCommand command, string reason)
        {
            command.Status = CommandStatus.Failed;
            // ثبت دلیل خطا در لاگ
        }
    }
}