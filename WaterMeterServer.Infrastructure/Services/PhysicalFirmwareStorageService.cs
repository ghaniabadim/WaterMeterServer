using System;
using System.IO;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Infrastructure.Services
{
    public class PhysicalFirmwareStorageService : IFirmwareStorageService
    {
        private readonly ILogger<PhysicalFirmwareStorageService> _logger;

        
        private static readonly uint[] JamCrc32Table = InitializeJamCrcTable();

        public PhysicalFirmwareStorageService(ILogger<PhysicalFirmwareStorageService> logger)
        {
            _logger = logger;
        }

        private static uint[] InitializeJamCrcTable()
        {
            const uint polynomial = 0xEDB88320;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                {
                    entry = (entry & 1) == 1 ? (entry >> 1) ^ polynomial : entry >> 1;
                }
                table[i] = entry;
            }
            return table;
        }

        public async Task<byte[]> ReadChunkAsync(string filePath, int offset, int length, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError("[FOTA Storage] Firmware file not found at path: {FilePath}", filePath);
                throw new FileNotFoundException("Firmware binary file missing on server disk.", filePath);
            }

            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            if (offset >= fileStream.Length)
            {
                _logger.LogWarning("[FOTA Storage] Requested offset {Offset} exceeds file length {Length}", offset, fileStream.Length);
                return Array.Empty<byte>();
            }

            fileStream.Seek(offset, SeekOrigin.Begin);

            int bytesToRead = (int)Math.Min(length, fileStream.Length - offset);
            byte[] buffer = new byte[bytesToRead];

            int totalBytesRead = 0;
            while (totalBytesRead < bytesToRead)
            {
                int read = await fileStream.ReadAsync(
                    buffer.AsMemory(totalBytesRead, bytesToRead - totalBytesRead),
                    cancellationToken);

                if (read == 0) break;
                totalBytesRead += read;
            }

            return buffer;
        }

        public async Task<int> GetFileSizeAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                var info = new FileInfo(filePath);
                return info.Exists ? (int)info.Length : 0;
            });
        }

        /// <summary>
        /// محاسبه مستقیم CRC-32/JAMCRC استریمی بدون اشغال حافظه RAM
        /// </summary>
        public async Task<uint> CalculateCrc32Async(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogError("[FOTA Storage] File for JAMCRC calculation not found: {FilePath}", filePath);
                throw new FileNotFoundException("Firmware file missing for CRC calculation.", filePath);
            }

            // مقدار اولیه طبق استانداردهای JAMCRC برابر با 0xFFFFFFFF است
            uint crc = 0xFFFFFFFF;

            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true);

            byte[] buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];
                    crc = (crc >> 8) ^ JamCrc32Table[(crc ^ b) & 0xFF];
                }
            }

            return crc;
        }
    }
}