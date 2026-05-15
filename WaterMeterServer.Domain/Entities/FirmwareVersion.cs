using System;
using System.Collections.Generic;
using System.Text;

namespace WaterMeterServer.Domain.Entities
{
    public class FirmwareVersion
    {
        public long Id { get; set; }
        public string VersionString { get; set; } = null!; // مثلاً V1.4
        public byte[] BinaryData { get; set; } = null!;
        public int FileSize { get; set; }
        public uint Crc32 { get; set; } // برای کنترل سلامت فایل
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
