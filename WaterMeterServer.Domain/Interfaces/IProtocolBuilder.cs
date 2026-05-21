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
        byte[] BuildReadCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, IReadOnlyList<MeterCommand> commands);
        byte[] BuildWriteCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, IReadOnlyList<MeterCommand> commands);

        // فریم‌های مربوط به آپدیت فریمور (بخش ۸ سند)
        byte[] BuildWriteFirmwareRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, ushort objectId, byte[] payload);
    }
}