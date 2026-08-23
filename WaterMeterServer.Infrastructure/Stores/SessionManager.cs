using System.Collections.Concurrent;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Infrastructure.Stores
{
    public class SessionManager : ISessionManager
    {
        private readonly ConcurrentDictionary<string, uint> _activeSessions = new();
        private readonly ConcurrentDictionary<uint, string> _sessionToMeterMap = new();
        private readonly ConcurrentDictionary<string, byte> _lastMids = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActivity = new();
        private readonly TimeSpan _sessionTtl;
        private readonly Func<DateTimeOffset> _now;

        public SessionManager(TimeSpan? sessionTtl = null, Func<DateTimeOffset>? now = null)
        {
            _sessionTtl = sessionTtl ?? TimeSpan.FromMinutes(30);
            if (_sessionTtl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(sessionTtl));
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public uint GenerateSessionId(string meterSerialNumber)
        {
            if (_activeSessions.TryRemove(meterSerialNumber, out var previousSid))
            {
                _sessionToMeterMap.TryRemove(previousSid, out _);
                _lastMids.TryRemove(meterSerialNumber, out _);
                _lastActivity.TryRemove(meterSerialNumber, out _);
            }

            uint sid;
            do
            {
                sid = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(
                    0x10000000,
                    int.MaxValue);
            }
            while (!_sessionToMeterMap.TryAdd(sid, meterSerialNumber));

            _activeSessions[meterSerialNumber] = sid;
            _lastActivity[meterSerialNumber] = _now();
            return sid;
        }

        public bool ValidateSession(string meterId, uint sessionId)
        {
            if (!_activeSessions.TryGetValue(meterId, out var activeSid) || activeSid != sessionId)
                return false;

            if (!_lastActivity.TryGetValue(meterId, out var lastActivity) ||
                _now() - lastActivity > _sessionTtl)
            {
                RemoveSession(meterId);
                return false;
            }

            _lastActivity[meterId] = _now();
            return true;
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
                _lastActivity.TryRemove(meterId, out _);
            }
        }

        public uint? GetSession(string meterId)
        {
            return _activeSessions.TryGetValue(meterId, out var sessionId) &&
                   ValidateSession(meterId, sessionId)
                ? sessionId
                : null;
        }

        public string? GetMeterIdBySession(uint sessionId)
        {
            // حل خطای CS0161 با پیاده‌سازی مسیر بازگشت
            return _sessionToMeterMap.TryGetValue(sessionId, out var meterId) ? meterId : null;
        }
    }
}
