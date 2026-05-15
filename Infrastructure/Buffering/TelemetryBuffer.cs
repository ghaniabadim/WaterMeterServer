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
            // ظرفیت 10,000 رکورد برای مدیریت پیک‌های ترافیکی
            _channel = Channel.CreateBounded<TelemetryRecord>(new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public ValueTask PushRecordAsync(TelemetryRecord record) => _channel.Writer.WriteAsync(record);
        public ChannelReader<TelemetryRecord> Reader => _channel.Reader;
    }
}