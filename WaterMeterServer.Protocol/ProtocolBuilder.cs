using System.Buffers.Binary;
using System.Text;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Domain.Models;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
            DateTime now = DateTime.UtcNow;

            business[offset++] = Utils.ByteToBcd(now.Year % 100);
            business[offset++] = Utils.ByteToBcd(now.Month);
            business[offset++] = Utils.ByteToBcd(now.Day);
            business[offset++] = Utils.ByteToBcd(now.Hour);
            business[offset++] = Utils.ByteToBcd(now.Minute);
            business[offset++] = Utils.ByteToBcd(now.Second);

            var encrypted = _cryptoService.Encrypt(business);
            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x05, encrypted);
        }

        public byte[] BuildReadCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, List<DeviceCommandLog> commands)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId, frameNumber);
            var dataLenPos = ms.Position;
            ushort dataLenAfterReqSeq = (ushort)(1 + (commands.Count * 2));
            WriteBigEndian(ms, dataLenAfterReqSeq);
            ms.WriteByte(ProtocolConstants.FunCodeReadData); // Function Code
            WriteBigEndian(ms, requestNumber); // SequenceNumber / REQID
            ms.WriteByte((byte)commands.Count); // Number of objects requested

            foreach (var command in commands)
            {
                WriteBigEndian(ms, (ushort)command.CommandId); // Object ID
            }

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        // 2. Write Data Object (05H)
        public byte[] BuildWriteCommandRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, List<DeviceCommandLog> commands)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId, frameNumber);

            long pos = ms.Position;
            ushort dataLenAfterReqSeq = (ushort)(1 + (commands.Count * 2));
            WriteBigEndian(ms, dataLenAfterReqSeq);

            ms.WriteByte(ProtocolConstants.FunCodeWriteData); // Function Code
            WriteBigEndian(ms, requestNumber); // SequenceNumber / REQID
            ms.WriteByte((byte)commands.Count); // Number of objects

            foreach (var command in commands)
            {
                WriteBigEndian(ms, (ushort)command.CommandId); // Object ID
                if (command.RequestPayload != null && command.RequestPayload.Length > 0)
                {
                    ms.Write(command.RequestPayload);
                    dataLenAfterReqSeq = (ushort)(dataLenAfterReqSeq + command.RequestPayload.Length);
                }
            }

            ms.Position = pos;
            WriteBigEndian(ms, dataLenAfterReqSeq);
            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        // 3. Read Records by Start Time (07H)
        public byte[] BuildReadRecordsByTimeRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, ushort recordObjectId, byte[] bcdStartTime, byte recordLimit)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId, frameNumber);

            // طول بایت‌های بعد از شماره توالی ثابت و برابر با 9 بایت است
            WriteBigEndian(ms, (ushort)9);
            ms.WriteByte(0x07); // Function Code
            WriteBigEndian(ms, requestNumber); // SequenceNumber / REQID

            WriteBigEndian(ms, recordObjectId); // Data Object ID
            ms.Write(bcdStartTime); // Record start time (6 Bytes BCD)
            ms.WriteByte(recordLimit); // Record count limit

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        // 4. Read Recent Records (08H)
        public byte[] BuildReadRecentRecordsRequest(uint sessionId, byte mid, ushort frameNumber, ushort requestNumber, ushort recordObjectId, byte recordCount)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId, frameNumber);

            // طول بایت‌های بعد از شماره توالی ثابت و برابر با 9 بایت است
            WriteBigEndian(ms, (ushort)9);
            ms.WriteByte(0x08); // Function Code
            WriteBigEndian(ms, requestNumber); // SequenceNumber / REQID

            WriteBigEndian(ms, recordObjectId); // Record file number
            ms.WriteByte(recordCount); // Number of records read

            byte[] padding = new byte[6]; // پر کردن بایت‌های رزرو جهت حفظ ساختار فریم
            ms.Write(padding);

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        public byte[] BuildWriteFirmwareRequest(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber, string currentVersion, string targetVersion)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId ?? 0, frameNumber);

            WriteBigEndian(ms, (ushort)0x000C); // Length after REQID
            ms.WriteByte(ProtocolConstants.FunCodeDataDistribution);
            WriteBigEndian(ms, requestNumber);

            ms.WriteByte(0x01); // Object Count
            WriteBigEndian(ms, (ushort)0x430C); // Firmware upgrade request ID

            ms.WriteByte(0x01); // Upgrade Mode / Flag

            byte[] targetVersionBytes = Utils.StringToBcd(targetVersion.Replace("V", "").Replace(".", "").PadLeft(8, '0'));
            byte[] currentVersionBytes = Utils.StringToBcd(currentVersion.Replace("V", "").Replace(".", "").PadLeft(8, '0'));

            ms.Write(targetVersionBytes, 0, 4);
            ms.Write(currentVersionBytes, 0, 4);

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        public byte[] BuildFirmwareInfo(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber, string targetVersion, int fileSize, uint fileCrc32)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId ?? 0, frameNumber);

            WriteBigEndian(ms, (ushort)0x000F); // Length after REQID
            ms.WriteByte(ProtocolConstants.FunCodeDataDistribution);
            WriteBigEndian(ms, requestNumber);

            ms.WriteByte(0x01);
            WriteBigEndian(ms, (ushort)0x4305);

            byte[] versionBytes = Utils.StringToBcd(targetVersion.Replace("V", "").Replace(".", "").PadLeft(8, '0'));
            ms.Write(versionBytes, 0, 4);

            byte[] sizeBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(sizeBytes, fileSize);
            ms.Write(sizeBytes);

            byte[] crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, fileCrc32);
            ms.Write(crcBytes);

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        public byte[] BuildFirmwareChunkResponse(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber, int currentOffset, byte[] chunkData)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId ?? 0, frameNumber);

            // 1B Function + 2B ReqNumber + 1B ObjCount + 2B ObjID + 4B Offset + 4B ChunkLen + Data + 2B CRC16
            ushort dataLen = (ushort)(13 + chunkData.Length);
            WriteBigEndian(ms, dataLen);

            ms.WriteByte(ProtocolConstants.FunCodeDataDistribution);
            WriteBigEndian(ms, requestNumber);
            ms.WriteByte(0x01);
            WriteBigEndian(ms, (ushort)0x4307);

            byte[] offsetBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(offsetBytes, currentOffset);
            ms.Write(offsetBytes);

            byte[] lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lenBytes, chunkData.Length);
            ms.Write(lenBytes);

            // کپی کردن محتوای باینری
            ms.Write(chunkData, 0, chunkData.Length);

            // محاسبه CRC16 مربوط به خود چانک داده باینری
            ushort chunkCrc16 = Crc16.Calculate(chunkData);
            byte[] crc16Bytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(crc16Bytes, chunkCrc16);
            ms.Write(crc16Bytes);

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

        public byte[] BuildUpgradeStatusAcknowledgement(uint? sessionId, byte mid, ushort frameNumber, ushort requestNumber)
        {
            using var ms = new MemoryStream();
            WriteStandardHeader(ms, sessionId ?? 0, frameNumber);

            WriteBigEndian(ms, (ushort)0x0004);
            ms.WriteByte(ProtocolConstants.FunCodeDataDistribution);
            WriteBigEndian(ms, requestNumber);

            ms.WriteByte(0x01);
            WriteBigEndian(ms, FirmwareObjectIds.UpgradeStatusAcknowledgement);
            ms.WriteByte(0x01);

            return WrapPacket(ProtocolConstants.TypeTransport, mid, 0x02, _cryptoService.Encrypt(ms.ToArray()));
        }

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

        private void WriteBigEndian(MemoryStream ms, ushort value)
        {
            byte[] buffer = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            ms.Write(buffer, 0, 2);

        }
    }
}
