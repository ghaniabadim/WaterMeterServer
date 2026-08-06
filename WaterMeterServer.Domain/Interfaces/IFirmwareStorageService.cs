using System;
using System.Collections.Generic;
using System.Text;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface IFirmwareStorageService
    {
        Task<byte[]> ReadChunkAsync(string filePath, int offset, int length, CancellationToken cancellationToken = default);
        Task<uint> CalculateCrc32Async(string filePath, CancellationToken cancellationToken = default);
        Task<int> GetFileSizeAsync(string filePath);
    }
}
