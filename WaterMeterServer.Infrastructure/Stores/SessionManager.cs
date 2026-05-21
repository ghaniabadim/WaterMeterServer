using System.Collections.Concurrent;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Infrastructure.Stores
{
    public class SessionManager : ISessionManager
    {
        private readonly ConcurrentDictionary<string, uint> _activeSessions = new();
        private readonly ConcurrentDictionary<uint, string> _sessionToMeterMap = new();
        private readonly ConcurrentDictionary<string, byte> _lastMids = new();

        public uint GenerateSessionId(string meterSerialNumber)
        {
            // استفاده از یک مقدار رندوم یا بخشی از سریال برای امنیت بیشتر
            //uint sid = (uint)new Random().Next(0x10000000, 0x7FFFFFFF);

            uint sid = 0x6001f438;
            _activeSessions[meterSerialNumber] = sid;
            _sessionToMeterMap[sid] = meterSerialNumber;
            return sid;
        }

        public bool ValidateSession(string meterId, uint sessionId)
        {
            return _activeSessions.TryGetValue(meterId, out var activeSid) && activeSid == sessionId;
        }

        public bool ValidateSessionAndSequence(string meterId, uint sessionId, byte incomingMid)
        {
            // ۱. بررسی SessionId
            if (!ValidateSession(meterId, sessionId)) return false;

            // ۲. بررسی توالی MID (باید مقدار قبلی + 1 باشد)
            if (_lastMids.TryGetValue(meterId, out var lastMid))
            {
                byte expectedMid = (byte)((lastMid + 1) % 256);
                if (incomingMid != expectedMid) return false;
            }

            _lastMids[meterId] = incomingMid;
            return true;
        }

        public void RemoveSession(string meterId)
        {
            if (_activeSessions.TryRemove(meterId, out var sid))
            {
                _sessionToMeterMap.TryRemove(sid, out _);
                _lastMids.TryRemove(meterId, out _);
            }
        }

        public uint? GetSession(string meterId)
        {
            return _activeSessions.TryGetValue(meterId, out var sessionId) ? sessionId : null;
        }

        public string? GetMeterIdBySession(uint sessionId)
        {
            // حل خطای CS0161 با پیاده‌سازی مسیر بازگشت
            return _sessionToMeterMap.TryGetValue(sessionId, out var meterId) ? meterId : null;
        }
    }
}