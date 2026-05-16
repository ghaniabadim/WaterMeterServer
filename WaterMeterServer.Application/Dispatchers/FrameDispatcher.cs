using Microsoft.Extensions.Logging;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.Application.Dispatchers
{
    public class FrameDispatcher
    {
        private readonly ISessionManager _sessionManager;
        private readonly ITelemetryBuffer _telemetryBuffer;
        private readonly IProtocolBuilder _protocolBuilder;
        private readonly ILogger<FrameDispatcher> _logger;

        public FrameDispatcher(
            ISessionManager sessionManager,
            ITelemetryBuffer telemetryBuffer,
            IProtocolBuilder protocolBuilder,
            ILogger<FrameDispatcher> logger)
        {
            _sessionManager = sessionManager;
            _telemetryBuffer = telemetryBuffer;
            _protocolBuilder = protocolBuilder;
            _logger = logger;
        }

        public async Task<byte[]?> DispatchAsync(MeterFrame frame, string connectionId)
        {
            // ۱. فریم هندشیک (بخش ۶.۱ سند)
            if (frame.Type == ProtocolConstants.TypeHandshake)
            {
                return await HandleHandshakeAsync(frame);
            }

            // ۲. فریم‌های انتقال داده (بحث ۷ سند)
            if (frame.Type == ProtocolConstants.TypeTransport)
            {
                return await HandleTransportAsync(frame);
            }

            return null;
        }

        private async Task<byte[]> HandleHandshakeAsync(MeterFrame frame)
        {
            // استخراج سریال ۲۰ بایت اول داده
            var serial_Len = (int)(frame.DecryptedData[2]) /2;
            var serial = Utils.ByteArrayToHexString(frame.DecryptedData.AsSpan(3, serial_Len).ToArray());
            _logger.LogInformation("Handshake received for Serial: {Serial}", serial);

            var sessionId = _sessionManager.GenerateSessionId(serial);
            return _protocolBuilder.BuildHandshakeResponse(frame.Mid, sessionId, frame.DecryptedData.AsSpan(0,20));
        }

        private async Task<byte[]?> HandleTransportAsync(MeterFrame frame)
        {
            // بایت ۹ام در دیتای دکریپت شده معمولاً Function Code است (بسته به پکت)
            byte functionCode = frame.DecryptedData[8];

            switch (functionCode)
            {
                case ProtocolConstants.FunCodePeriodicReporting:
                    _logger.LogInformation("Periodic report received from Mid: {Mid}", frame.Mid);
                    // اینجا داده‌ها پارس شده و به TelemetryBuffer فرستاده می‌شوند
                    return null; // معمولاً برای گزارش دوره‌ای سرور بلافاصله پاسخ نمی‌دهد مگر فریم تایید

                default:
                    _logger.LogWarning("Unknown Function Code: 0x{Code:X2}", functionCode);
                    return null;
            }
        }
    }
}