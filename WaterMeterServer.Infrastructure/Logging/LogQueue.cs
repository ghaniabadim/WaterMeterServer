using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Channels;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Infrastructure.Logging
{
    public class LogQueue
    {
        private readonly Channel<CommunicationLog> _channel = Channel.CreateUnbounded<CommunicationLog>();
        public ChannelWriter<CommunicationLog> Writer => _channel.Writer;
        public ChannelReader<CommunicationLog> Reader => _channel.Reader;
    }

}
