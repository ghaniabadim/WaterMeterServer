using System;
using System.Collections.Generic;
using System.Text;

namespace WaterMeterServer.Domain.Entities
{
    public enum LogLevel { Info, Warning, Error, Critical }

    public class SystemEventLog
    {
        public long Id { get; set; }
        public LogLevel Level { get; set; }
        public string Category { get; set; } = null!; // مثلاً "Networking" یا "Database"
        public string Message { get; set; } = null!;
        public string? ExceptionDetails { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
