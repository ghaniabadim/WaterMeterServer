using System.Threading.Channels;
using System.Threading.Tasks;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface ITelemetryBuffer
    {
        ValueTask PushRecordAsync(TelemetryRecord record);
        Task<TelemetryRecord[]> PopRecordsAsync(int v, CancellationToken stoppingToken);

        ChannelReader<TelemetryRecord> Reader { get; }
    }
}