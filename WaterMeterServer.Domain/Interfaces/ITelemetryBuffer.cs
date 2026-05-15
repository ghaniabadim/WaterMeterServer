using System.Threading.Tasks;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface ITelemetryBuffer
    {
        ValueTask PushRecordAsync(TelemetryRecord record);
    }
}