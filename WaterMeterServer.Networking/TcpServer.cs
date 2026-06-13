using Microsoft.Extensions.Logging;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using WaterMeterServer.Application.Dispatchers;
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
        public TcpServer(int port, FrameParser parser, FrameDispatcher dispatcher, ILogger<TcpServer> logger,LogQueue logQueue)
        {
            _port = port;
            _parser = parser;
            _dispatcher = dispatcher;
            _logger = logger;
            _logQueue = logQueue;
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
            }
            finally
            {
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
                    while (_parser.TryParse(ref buffer, out var frame,out var data))
                    {
                        if (frame != null)
                        {
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
                    if (result.IsCompleted) break;
                }
                finally
                {
                    context.Reader.AdvanceTo(buffer.Start, buffer.End);
                }
            }
        }
    }
}