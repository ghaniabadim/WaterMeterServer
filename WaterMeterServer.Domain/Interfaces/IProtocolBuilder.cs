using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Models;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface IProtocolBuilder
    {
        // فریم پاسخ هندشیک (بخش ۶.۲ سند)
        byte[] BuildHandshakeResponse(byte incomingMid, uint sessionId, Span<byte>composite,ushort handshakeSataus);

        // فریم‌های تاییدیه یا ادامه (بخش ۷.۴ سند)
        byte[] BuildContinueFrameResponse(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber);

        // فریم پایان ارتباط به همراه همگام‌سازی ساعت (بخش ۷.۳ سند)
        byte[] BuildEndFrameResponse(uint sessionId, byte mid, ushort frameNumber, byte termination);

        // ساخت فریم برای دستورات خواندن و نوشتن (بخش ۷.۵ و ۷.۶)
        public byte[] BuildWriteCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, List<DeviceCommandLog> commands);
        public byte[] BuildReadCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, List<DeviceCommandLog> commands);

        public byte[] BuildReadRecordsByTimeRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, ushort recordObjectId, byte[] bcdStartTime, byte recordLimit);
        public byte[] BuildReadRecentRecordsRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, ushort recordObjectId, byte recordCount);


        // فریم‌های مربوط به آپدیت فریمور (بخش ۸ سند)
        public byte[] BuildWriteFirmwareRequest(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber, string currentVersion, string targetVersion);
        public byte[] BuildFirmwareInfo(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber, string targetVersion, int fileSize, uint fileCrc32);
        public byte[] BuildFirmwareChunkResponse(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber, int currentOffset, byte[] chunkData);
        public byte[] BuildUpgradeStatusAcknowledgement(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber);

    }
}
