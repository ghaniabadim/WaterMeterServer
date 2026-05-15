using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface IFirmwareManager
    {
        Task<bool> StartUpgradeAsync(string meterId, string firmwarePath, string version);
        Task ProcessMeterRequestAsync(string meterId, ushort objectId, byte[] payload);
        Task<FirmwareUpgradeRequest?> GetActiveRequestAsync(string meterId);
    }
}