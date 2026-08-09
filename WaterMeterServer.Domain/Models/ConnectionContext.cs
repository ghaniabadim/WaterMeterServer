using System.IO.Pipelines;
using System.Net;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Domain.Models
{
    public class ConnectionContext
    {
        public enum TransportState
        {
            WaitForReporting,
            ReportingComplete,
            SendingCommand,
            FirmwareUpgrading,
            EndConnection
        }

        public string ConnectionId { get; } = Guid.NewGuid().ToString();
        public IPEndPoint? RemoteEndPoint { get; set; }
        public string? MeterId { get; set; }
        public uint? SessionId { get; set; }

        public TransportState CurrentState { get; set; } = TransportState.WaitForReporting;

        public PipeReader Reader { get; set; } = null!;
        public PipeWriter Writer { get; set; } = null!;

        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public Device Device { get; set; }
        public ushort SequenceNumber { get; set; }
        public ushort ServerFrameNumber { get; private set; }

        public ushort NextServerFrameNumber()
        {
            if (ServerFrameNumber == ushort.MaxValue)
            {
                throw new InvalidOperationException("Server frame number exhausted; a new session handshake is required.");
            }

            return ++ServerFrameNumber;
        }
    }
}
