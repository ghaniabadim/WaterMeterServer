using System.Threading.Channels;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Infrastructure.Buffering
{
    public class TelemetryBuffer : ITelemetryBuffer
    {
        private readonly Channel<TelemetryRecord> _channel;

        public TelemetryBuffer()
        {
            _channel = Channel.CreateBounded<TelemetryRecord>(new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public ValueTask PushRecordAsync(TelemetryRecord record) => _channel.Writer.WriteAsync(record);

        public async Task<TelemetryRecord[]> PopRecordsAsync(int maxCount, CancellationToken stoppingToken)
        {
            var records = new List<TelemetryRecord>();

            if (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                while (records.Count < maxCount && _channel.Reader.TryRead(out var record))
                {
                    records.Add(record);
                }
            }

            return records.ToArray();
        }

        public ChannelReader<TelemetryRecord> Reader => _channel.Reader;
    }
}