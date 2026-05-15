using System.Collections.Concurrent;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Infrastructure.Stores
{
    public class SessionManager : ISessionManager
    {
        // استفاده از ConcurrentDictionary برای مدیریت همزمان ۵۰۰۰ نشست
        private readonly ConcurrentDictionary<string, uint> _activeSessions = new();

        public uint GenerateSessionId(string meterSerialNumber)
        {
            // طبق پیشنهاد پروتکل: استفاده از ۹ رقم آخر شماره سریال 
            string last9 = meterSerialNumber.Length > 9
                ? meterSerialNumber.Substring(meterSerialNumber.Length - 9)
                : meterSerialNumber;

            if (uint.TryParse(last9, out uint sid))
            {
                _activeSessions[meterSerialNumber] = sid;
                return sid;
            }

            // مقدار پیش‌فرض در صورت خطا
            uint defaultSid = 0x6001F438;
            _activeSessions[meterSerialNumber] = defaultSid;
            return defaultSid;
        }

        public bool ValidateSession(string meterId, uint sessionId)
        {
            return _activeSessions.TryGetValue(meterId, out var activeSid) && activeSid == sessionId;
        }

        public void RemoveSession(string meterId)
        {
            _activeSessions.TryRemove(meterId, out _);
        }
    }
}