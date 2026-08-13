using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Channels;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Infrastructure.Logging
{
    public class LogQueue
    {
        private const int Capacity = 10_000;
        private readonly Channel<CommunicationLog> _channel = Channel.CreateBounded<CommunicationLog>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });
        public ChannelWriter<CommunicationLog> Writer => _channel.Writer;
        public ChannelReader<CommunicationLog> Reader => _channel.Reader;
    }

}
