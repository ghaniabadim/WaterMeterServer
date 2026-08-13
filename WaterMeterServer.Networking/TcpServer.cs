using Microsoft.Extensions.Logging;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Buffers.Binary;
using WaterMeterServer.Application.Dispatchers;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Models;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.Networking
{
    public class TcpServer
    {
        private readonly int                _port;
        private readonly FrameParser        _parser;
        private readonly ILogger<TcpServer> _logger;
        private readonly FrameDispatcher    _dispatcher;
        private readonly LogQueue           _logQueue;
        private readonly SystemEventLogger _systemEventLogger;
        public TcpServer(int port, FrameParser parser, FrameDispatcher dispatcher, ILogger<TcpServer> logger, LogQueue logQueue, SystemEventLogger systemEventLogger)
        {
            _port = port;
            _parser = parser;
            _dispatcher = dispatcher;
            _logger = logger;
            _logQueue = logQueue;
            _systemEventLogger = systemEventLogger;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();
            _logger.LogInformation("Server started on port {Port}", _port);

            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptSocketAsync(ct);
                _ = HandleConnectionAsync(socket);
            }
        }

        private async Task HandleConnectionAsync(Socket socket)
        {
            var context = new ConnectionContext
            {
                RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint
            };

            using var stream = new NetworkStream(socket);
            var reader = PipeReader.Create(stream);
            var writer = PipeWriter.Create(stream);

            context.Reader = reader;
            context.Writer = writer;

            try
            {
                _logger.LogInformation("New client connected: {Ip}", context.RemoteEndPoint);
                await ProcessLinesAsync(context, _dispatcher);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection error for {Ip}", context.RemoteEndPoint);
                await _systemEventLogger.LogEventAsync(
                    WaterMeterServer.Domain.Entities.LogLevel.Error,
                    "Networking",
                    $"Connection error for {context.RemoteEndPoint}.",
                    ex.ToString());
            }
            finally
            {
                _logger.LogInformation("Client disconnected: {Ip}, ConnectionId={ConnectionId}, MeterId={MeterId}",
                    context.RemoteEndPoint, context.ConnectionId, context.MeterId);
                socket.Close();
            }
        }

        private async Task ProcessLinesAsync(ConnectionContext context, FrameDispatcher dispatcher)
        {
            while (true)
            {
                ReadResult result = await context.Reader.ReadAsync();
                var buffer = result.Buffer;

                try
                {
                    MeterFrame? frame;
                    byte[]? data;
                    string? failureReason;
                    while (_parser.TryParse(ref buffer, out frame, out data, out failureReason))
                    {
                        if (frame != null)
                        {
                            var startedAt = Stopwatch.GetTimestamp();
                            // ارسال فریم به لایه اپلیکیشن و دریافت پاسخ احتمالی
                            var response = await dispatcher.DispatchAsync(frame, context);

                            var meterId = context.MeterId ?? "UNKNOWN";
                            var connectionId = context.ConnectionId ?? "UNKNOWN_CONNECTION";

                            await _logQueue.Writer.WriteAsync(new CommunicationLog
                            {
                                MeterId = meterId,
                                ConnectionId = connectionId,
                                Direction = "Inbound",
                                RawData = data!,
                                FrameType = frame.Type,
                                ProtocolVersion = frame.Version,
                                ControlCode = frame.ControlCode,
                                Mid = frame.Mid,
                                SessionId = frame.Type == ProtocolConstants.TypeTransport ? frame.SessionId : null,
                                FrameNumber = frame.Type == ProtocolConstants.TypeTransport ? frame.FrameNo : null,
                                RequestSequence = TryGetRequestSequence(frame),
                                FunctionCode = TryGetFunctionCode(frame),
                                Result = "Processed",
                                ProcessingDurationMs = (long)(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
                                Timestamp = DateTime.UtcNow
                            });

                            if (response != null)
                            {
                                await context.Writer.WriteAsync(response);
                                await context.Writer.FlushAsync();

                                await _logQueue.Writer.WriteAsync(new CommunicationLog
                                {
                                    MeterId = meterId,
                                    ConnectionId = connectionId,
                                    Direction = "Outbound",
                                    RawData = response!,
                                    FrameType = ProtocolConstants.TypeTransport,
                                    ProtocolVersion = 0,
                                    ControlCode = response.Length > 6 ? response[6] : null,
                                    Mid = response.Length > 5 ? response[5] : null,
                                    SessionId = frame.Type == ProtocolConstants.TypeTransport ? frame.SessionId : null,
                                    FrameNumber = frame.Type == ProtocolConstants.TypeTransport ? frame.FrameNo : null,
                                    Result = "Sent",
                                    Timestamp = DateTime.UtcNow
                                });

                                if (context.CurrentState == ConnectionContext.TransportState.EndConnection)
                                {
                                    _logger.LogInformation("Communication ended for {Mid}. Closing socket.", context.MeterId);
                                    return;
                                }
                            }
                        }
                    }
                    if (failureReason != null)
                    {
                        await _logQueue.Writer.WriteAsync(new CommunicationLog
                        {
                            MeterId = context.MeterId ?? "UNKNOWN",
                            ConnectionId = context.ConnectionId ?? "UNKNOWN_CONNECTION",
                            Direction = "Inbound",
                            RawData = data ?? Array.Empty<byte>(),
                            Result = "Rejected",
                            ErrorReason = failureReason,
                            Timestamp = DateTime.UtcNow
                        });
                        await _systemEventLogger.LogEventAsync(
                            WaterMeterServer.Domain.Entities.LogLevel.Warning,
                            "Protocol",
                            $"Rejected frame on connection {context.ConnectionId ?? "UNKNOWN_CONNECTION"}: {failureReason}");
                    }
                    if (result.IsCompleted) break;
                }
                finally
                {
                    context.Reader.AdvanceTo(buffer.Start, buffer.End);
                }
            }
        }

        private static ushort? TryGetRequestSequence(MeterFrame frame)
        {
            if (frame.Type != ProtocolConstants.TypeTransport || frame.DecryptedData.Length < 11)
                return null;
            return BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(9, 2));
        }

        private static byte? TryGetFunctionCode(MeterFrame frame)
        {
            if (frame.Type != ProtocolConstants.TypeTransport || frame.DecryptedData.Length < 9)
                return null;
            return (byte)(frame.DecryptedData[8] & 0x1F);
        }
    }
}
