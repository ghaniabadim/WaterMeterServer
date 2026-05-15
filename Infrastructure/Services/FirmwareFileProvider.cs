namespace WaterMeterServer.Infrastructure.Services
{
    public class FirmwareFileProvider
    {
        public async Task<byte[]> GetFirmwareSegmentAsync(string path, int offset, int length)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Firmware file not found.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            stream.Seek(offset, SeekOrigin.Begin);

            byte[] buffer = new byte[length];
            int read = await stream.ReadAsync(buffer, 0, length);

            if (read < length) Array.Resize(ref buffer, read);
            return buffer;
        }
    }
}