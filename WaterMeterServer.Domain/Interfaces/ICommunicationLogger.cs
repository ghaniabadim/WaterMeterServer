using System;
using System.Collections.Generic;
using System.Text;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface ICommunicationLogger
    {
        Task LogCommunicationAsync(string meterId, string connectionId, string direction, byte[] data);
    }
}
