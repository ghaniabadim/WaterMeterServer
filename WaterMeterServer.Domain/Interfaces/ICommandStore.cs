using WaterMeterServer.Domain.Models; // برای MeterCommand

namespace WaterMeterServer.Domain.Interfaces
{
    public interface ICommandStore
    {
        IReadOnlyList<MeterCommand> GetPendingCommands(string meterId, int maxCount);
        void MarkInProgress(MeterCommand command);
        void MarkSucceeded(MeterCommand command);
        void MarkFailed(MeterCommand command, string reason);
    }
}