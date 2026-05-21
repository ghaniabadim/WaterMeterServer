using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using WaterMeterServer.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace WaterMeterServer.Infrastructure.Stores
{
    /// <summary>
    /// مدیریت جلسات (Sessions) برای 5000 دستگاه همزمان
    /// شامل Timeout خودکار و بهداشت حافظه
    /// </summary>
    public class SessionInfo
    {
        public uint SessionId { get; set; }
        public string MeterId { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class SessionManager : ISessionManager
    {
        private readonly ConcurrentDictionary<string, SessionInfo> _activeSessions = new();
        private readonly ILogger<SessionManager> _logger;
        private readonly TimeSpan _sessionTimeout = TimeSpan.FromHours(1); // توقیت سشن

        public SessionManager(ILogger<SessionManager> logger)
        {
            _logger = logger;
        }

        public uint GenerateSessionId(string meterSerialNumber)
        {
            // ✅ برای تست: مقدار ثابت (بعداً به dynamic تغییر می‌یابد)
            uint sessionId = 0x6001F438;

            var sessionInfo = new SessionInfo
            {
                SessionId = sessionId,
                MeterId = meterSerialNumber,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
                IsActive = true
            };

            _activeSessions[meterSerialNumber] = sessionInfo;
            _logger.LogInformation("Session created for Meter: {MeterId}, SessionId: {SessionId:X8}", 
                meterSerialNumber, sessionId);

            return sessionId;
        }

        public bool ValidateSession(string meterId, uint sessionId)
        {
            if (_activeSessions.TryGetValue(meterId, out var sessionInfo))
            {
                // ✅ بررسی timeout
                if (DateTime.UtcNow - sessionInfo.LastActivityAt > _sessionTimeout)
                {
                    _logger.LogWarning("Session expired for Meter: {MeterId}", meterId);
                    _activeSessions.TryRemove(meterId, out _);
                    return false;
                }

                // ✅ بروزرسانی آخرین فعالیت
                sessionInfo.LastActivityAt = DateTime.UtcNow;
                return sessionInfo.IsActive && sessionInfo.SessionId == sessionId;
            }

            return false;
        }

        public void RemoveSession(string meterId)
        {
            if (_activeSessions.TryRemove(meterId, out var sessionInfo))
            {
                _logger.LogInformation("Session removed for Meter: {MeterId}", meterId);
            }
        }

        /// <summary>
        /// ✅ تمیز‌کردن خودکار سشن‌های منقضی
        /// این متد باید در Background Worker فراخوانی شود
        /// </summary>
        public List<string> CleanupExpiredSessions()
        {
            var expiredMeters = new List<string>();
            var now = DateTime.UtcNow;

            foreach (var kvp in _activeSessions)
            {
                if (now - kvp.Value.LastActivityAt > _sessionTimeout)
                {
                    if (_activeSessions.TryRemove(kvp.Key, out var removed))
                    {
                        expiredMeters.Add(kvp.Key);
                        _logger.LogWarning(
                            "Session cleanup: Removed expired session for Meter: {MeterId} (inactive for {Minutes} minutes)",
                            kvp.Key,
                            (now - removed.LastActivityAt).TotalMinutes);
                    }
                }
            }

            return expiredMeters;
        }

        /// <summary>
        /// ✅ دریافت آمار سشن‌ها برای Monitoring
        /// </summary>
        public (int ActiveSessions, int ExpiredButActive, DateTime OldestSession) GetSessionStatistics()
        {
            var now = DateTime.UtcNow;
            int activeSessions = 0;
            int expiredButStillActive = 0;
            DateTime oldestSession = DateTime.UtcNow;

            foreach (var session in _activeSessions.Values)
            {
                if (session.IsActive)
                {
                    activeSessions++;
                    if (now - session.CreatedAt < oldestSession.Ticks)
                        oldestSession = session.CreatedAt;

                    if (now - session.LastActivityAt > _sessionTimeout)
                        expiredButStillActive++;
                }
            }

            return (activeSessions, expiredButStillActive, oldestSession);
        }

        /// <summary>
        /// ✅ دریافت GetSession اضافی (برای compatibility)
        /// </summary>
        public uint? GetSession(string meterId)
        {
            return _activeSessions.TryGetValue(meterId, out var session) ? session.SessionId : null;
        }
    }
}
