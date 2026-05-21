using System.Buffers.Binary;
using System.Text;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Domain.Models;

namespace WaterMeterServer.Protocol
{
    public class ProtocolBuilder : IProtocolBuilder
    {
        private readonly ICryptoService _cryptoService;

        public ProtocolBuilder(ICryptoService cryptoService)
        {
            _cryptoService = cryptoService;
        }

        // 1. پیاده‌سازی پاسخ هندشیک (بخش 6.2 سند)
        byte[] IProtocolBuilder.BuildHandshakeResponse(byte incomingMid, uint sessionId, Span<byte> composite, ushort handshakeSataus)
        {
            // طول بدنه: 20 بایت سریال + 2 بایت نتیجه + 4 بایت SessionId = 26 بایت 
            byte[] business = new byte[26];

            // کپی کردن سریال (20 بایت اول) از دیتای دریافتی
            composite.Slice(0, 20).CopyTo(business);

            int offset = 20;
            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(offset), handshakeSataus); 
            offset += 2;
            BinaryPrimitives.WriteUInt32BigEndian(business.AsSpan(offset), sessionId);

            var encrypted = _cryptoService.Encrypt(business);
            return WrapPacket(ProtocolConstants.TypeHandshake, incomingMid, 0x02, encrypted);
        }

        // 2. پیاده‌سازی فریم ادامه (بخش 7.4 سند - Sequel)
        public byte[] BuildContinueFrameResponse(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber)
        {
            byte[] business = new byte[11];
            int offset = 0;
            BinaryPrimitives.WriteUInt32BigEndian(business.AsSpan(offset), sessionId); offset += 4;
            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(offset), frameNumber); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(offset), 0); offset += 2; // Business Data Len: 0 

            business[offset++] = 0x03; // Function: Resume (Sequel)
            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(offset), requestNumber);

            var encrypted = _cryptoService.Encrypt(business);
            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, encrypted);
        }

        // 3. پیاده‌سازی فریم پایان (بخش 7.3 سند)
        public byte[] BuildEndFrameResponse(uint sessionId, byte mid, ushort frameNumber, byte termination)
        {
            // ساختار طبق جدول 1.2 مستند:
            // [SID 4B][FNo 2B][Termination 1B][DataLen 2B][Fun 1B][ReqSeq 2B][ObjCount 1B][ObjID 2B][Clock 6B][Reserved 14B]
            // کل طول بیزنس دیتا: 7 + 23 = 30 بایت (یا طبق کد شما 26 بایت بدون رزروهای کامل)
            byte[] business = new byte[35];
            int offset = 0;

            BinaryPrimitives.WriteUInt32BigEndian(business.AsSpan(offset), sessionId); offset += 4;
            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(offset), frameNumber); offset += 2;
            business[offset++] = termination; // 00 Normal, 01 Re-handshake 

            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(offset), 23); offset += 2; // Data Length (Fixed at 23)
            business[offset++] = 0x02; // Function Code: End Communication
            offset += 2; // Request Sequence (Skip/Zero if not needed)
            business[offset++] = 0x01; // Number of Objects: 1
            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(offset), 0x21B8); offset += 2; // Object ID: System Clock 

            // درج زمان سیستم به صورت BCD برای هماهنگ‌سازی کنتور
            DateTime now = DateTime.Now;
            business[offset++] = Utils.ByteToBcd(now.Year % 100);
            business[offset++] = Utils.ByteToBcd(now.Month);
            business[offset++] = Utils.ByteToBcd(now.Day);
            business[offset++] = Utils.ByteToBcd(now.Hour);
            business[offset++] = Utils.ByteToBcd(now.Minute);
            business[offset++] = Utils.ByteToBcd(now.Second);

            var encrypted = _cryptoService.Encrypt(business);
            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x05, encrypted);
        }

        // 4. ساخت درخواست خواندن اشیاء (بخش 7.5)
        public byte[] BuildReadCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, IReadOnlyList<MeterCommand> commands)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId, frameNumber);

            ushort dataLen = (ushort)(1 + (commands.Count * 2) + 3); // DataLen logic
            WriteBigEndian(ms, (ushort)(1 + (commands.Count * 2))); // Length of data after ReqSeq
            ms.WriteByte(0x04); // Function: Read 
            WriteBigEndian(ms, requestNumber);
            ms.WriteByte((byte)commands.Count);

            foreach (var cmd in commands)
                WriteBigEndian(ms, (ushort)cmd.CommandId);

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        // 5. ساخت درخواست نوشتن اشیاء (بخش 7.6)
        public byte[] BuildWriteCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, IReadOnlyList<MeterCommand> commands)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId, frameNumber);

            int payloadLen = 1; // ObjCount
            foreach (var c in commands) payloadLen += 2 + (c.Payload?.Length ?? 0);

            WriteBigEndian(ms, (ushort)payloadLen);
            ms.WriteByte(0x05); // Function: Write 
            WriteBigEndian(ms, requestNumber);
            ms.WriteByte((byte)commands.Count);

            foreach (var c in commands)
            {
                WriteBigEndian(ms, (ushort)c.CommandId);
                if (c.Payload != null) ms.Write(c.Payload);
            }

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        // 6. ساخت درخواست فریمور (بخش 8)
        public byte[] BuildWriteFirmwareRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, ushort objectId, byte[] payload)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId, frameNumber);

            ushort dataLen = (ushort)(2 + payload.Length + 1); // Content length
            WriteBigEndian(ms, dataLen);
            ms.WriteByte(0x02); // Distribution (Function Code for Firmware)
            WriteBigEndian(ms, requestNumber);
            ms.WriteByte(0x01); // Obj Count
            WriteBigEndian(ms, objectId);
            ms.Write(payload);

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        // --- متدهای کمکی خصوصی ---

        private byte[] WrapPacket(byte type, byte mid, byte ctrl, byte[] encryptedData)
        {
            int totalLen = 10 + encryptedData.Length;
            byte[] packet = new byte[totalLen];
            packet[0] = ProtocolConstants.Head;
            packet[1] = type;
            packet[2] = 0; // Version
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), (ushort)totalLen);
            packet[5] = mid;
            packet[6] = ctrl;

            Array.Copy(encryptedData, 0, packet, 7, encryptedData.Length);

            // محاسبه CRC بر اساس بایت 5 تا قبل از فیلد CRC (طول پکت منهای 3 بایت آخر)
            ushort crc = Crc16.Calculate(packet.AsSpan(5, totalLen - 8));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(totalLen - 3), crc);
            packet[totalLen - 1] = ProtocolConstants.Tail;

            return packet;
        }

        private void WriteStandardHeader(Stream s, uint sid, ushort fno)
        {
            WriteBigEndian(s, sid);
            WriteBigEndian(s, fno);
        }

        private void WriteBigEndian(Stream s, ushort val)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, val);
            s.Write(b);
        }

        private void WriteBigEndian(Stream s, uint val)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, val);
            s.Write(b);
        }

        
    }
}