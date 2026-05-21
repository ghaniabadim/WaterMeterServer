// مسیر فیزیکی: WaterMeterServer.Application/Dispatchers/FrameDispatcher.cs
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Domain.Models;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.Application.Dispatchers
{
    public class FrameDispatcher
    {
        private readonly ILogger<FrameDispatcher> _logger;
        private readonly IProtocolBuilder _protocolBuilder;
        private readonly ISessionManager _sessionManager;
        private readonly ITelemetryBuffer _telemetryBuffer;
        private readonly ICommandStore _commandStore;

        public FrameDispatcher(
            ILogger<FrameDispatcher> logger,
            IProtocolBuilder protocolBuilder,
            ISessionManager sessionManager,
            ITelemetryBuffer telemetryBuffer,
            ICommandStore commandStore)
        {
            _logger = logger;
            _protocolBuilder = protocolBuilder;
            _sessionManager = sessionManager;
            _telemetryBuffer = telemetryBuffer;
            _commandStore = commandStore;
        }

        public async Task<byte[]?> DispatchAsync(MeterFrame frame, ConnectionContext context)
        {
            if (frame.Type == ProtocolConstants.TypeTransport && string.IsNullOrEmpty(context.MeterId))
            {
                _logger.LogWarning("Unauthorized Transport received before handshake. Session ID: {SessionId}", frame.SessionId);
                return _protocolBuilder.BuildEndFrameResponse(frame.SessionId, frame.Mid, (ushort)(frame.FrameNo + 1), 0x01);
            }

            if (frame.Type == ProtocolConstants.TypeHandshake)
            {
                return await HandleHandshakeAsync(frame, context);
            }

            if (frame.Type == ProtocolConstants.TypeTransport)
            {
                if (context.SessionId != frame.SessionId)
                {
                    _logger.LogWarning("Session ID mismatch. Expected: {Expected}, Received: {Received}", context.SessionId, frame.SessionId);
                    return _protocolBuilder.BuildEndFrameResponse(frame.SessionId, frame.Mid, (ushort)(frame.FrameNo + 1), 0x01);
                }

                return await HandleTransportAsync(frame, context);
            }

            return null;
        }

        private async Task<byte[]?> HandleHandshakeAsync(MeterFrame frame, ConnectionContext context)
        {
            _logger.LogInformation("Processing handshake for Mid: {Mid}", frame.Mid);

            if (frame.DecryptedData.Length > 23)
            {
                int meterIdLength = frame.DecryptedData[2];
                if (meterIdLength > 0 && meterIdLength <= 34)
                {
                    var meterIdBytes = frame.DecryptedData.AsSpan(3, meterIdLength / 2);
                    var meterId = Utils.BcdToString(meterIdBytes);
                    frame.MeterId = meterId;

                    if (_sessionManager.GetSession(meterId) != null)
                    {
                        _sessionManager.RemoveSession(meterId);
                        _logger.LogInformation("Previous session cleared for Meter: {MeterId}", meterId);
                    }

                    var sessionId = _sessionManager.GenerateSessionId(meterId);
                    context.MeterId = meterId;
                    context.SessionId = sessionId;

                    return _protocolBuilder.BuildHandshakeResponse(frame.Mid, sessionId, frame.DecryptedData, ProtocolConstants.HandshakeSuccess);
                }
            }

            return _protocolBuilder.BuildHandshakeResponse(frame.Mid, 0, frame.DecryptedData, ProtocolConstants.HandshakeFailure);
        }

        private async Task<byte[]?> HandleTransportAsync(MeterFrame frame, ConnectionContext context)
        {
            ushort nextFrameNo = (ushort)(frame.FrameNo + 1);
            ushort requestSeq = BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(9, 2));

            switch (context.CurrentState)
            {
                case ConnectionContext.TransportState.WaitForReporting:
                    await ProcessTelemetryObject(frame);

                    bool hasMoreData = (frame.ControlCode & 0x80) != 0;
                    if (hasMoreData)
                    {
                        return _protocolBuilder.BuildContinueFrameResponse(frame.SessionId, frame.Mid, nextFrameNo, requestSeq);
                    }

                    context.CurrentState = ConnectionContext.TransportState.ReportingComplete;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.ReportingComplete:
                    var pendingCommands = _commandStore.GetPendingCommands(context.MeterId!, 5);
                    if (pendingCommands != null && pendingCommands.Count > 0)
                    {
                        context.CurrentState = ConnectionContext.TransportState.SendingCommand;
                        return _protocolBuilder.BuildWriteCommandRequest(frame.SessionId, frame.Mid, nextFrameNo, requestSeq, pendingCommands);
                    }

                    context.CurrentState = ConnectionContext.TransportState.FirmwareUpgrading;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.SendingCommand:
                    context.CurrentState = ConnectionContext.TransportState.FirmwareUpgrading;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.FirmwareUpgrading:
                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.EndConnection:

                    uint sessionIdToByte = context.SessionId ?? frame.SessionId;
                    return _protocolBuilder.BuildEndFrameResponse(sessionIdToByte, frame.Mid, nextFrameNo, 0x00);

                default:
                    return null;
            }
        }

        private async Task ProcessTelemetryObject(MeterFrame frame)
        {
            try
            {
                int objCount = frame.DecryptedData[11];
                int currentOffset = 12;

                _logger.LogInformation("Processing packet from Mid {Mid} containing {Count} objects.", frame.Mid, objCount);

                for (int i = 0; i < objCount; i++)
                {
                    ushort objId = BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(currentOffset, 2));
                    currentOffset += 2;
                    int len = frame.DecryptedData[currentOffset++];

                    switch (objId)
                    {
                        case 0x70F2:
                            if (currentOffset + 49 <= frame.DecryptedData.Length)
                            {
                                var telemetryData = frame.DecryptedData.AsSpan(currentOffset, 49);
                                var record = TelemetryParser.Parse70F2(telemetryData, frame.Mid);

                                await _telemetryBuffer.PushRecordAsync(record);
                                currentOffset += 49;
                            }
                            break;

                        case 0xB070:
                            currentOffset += 3;
                            break;

                        case 0xB064:
                            currentOffset += 6;
                            break;

                        case 0xB05C:
                            currentOffset += 10;
                            break;

                        case 0x70EE:
                            currentOffset += 90;
                            break;

                        default:
                            _logger.LogWarning("Unknown Object ID 0x{Id:X4}. Stopping parse.", objId);
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error parsing multi-object packet for Mid {Mid}", frame.Mid);
            }
        }
    }
}