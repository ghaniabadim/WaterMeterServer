using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Intrinsics.Arm;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Protocol
{
    public sealed class FrameParser
    {
        private readonly ICryptoService _cryptoService;

        public FrameParser(ICryptoService cryptoService) => _cryptoService = cryptoService;

        public bool TryParse(ref ReadOnlySequence<byte> buffer, out MeterFrame? frame)
        {
            frame = null;
            var reader = new SequenceReader<byte>(buffer);

            // ۱. جستجوی HEAD (0x68) 
            if (!reader.TryAdvanceTo(ProtocolConstants.Head, false))
            {
                buffer = buffer.Slice(buffer.End);
                return false;
            }

            var startPos = reader.Position;
            if (reader.Remaining < 10) return false; // حداقل طول فریم 

            reader.Advance(3); // عبور از Type و Version
            if (!reader.TryReadBigEndian(out short totalLen)) return false;

            if (buffer.Length < totalLen) return false; // فریم هنوز کامل نشده است

            var frameSeq = buffer.Slice(startPos, totalLen);

            // ۲. بررسی انتهای فریم (0x16) 
            if (Utils.ReadByteAt(frameSeq, totalLen - 1) != ProtocolConstants.Tail)
            {
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            // ۳. بررسی CRC16 مطابق Appendix C [cite: 131, 274]
            ushort receivedCrc = Utils.ReadUInt16BigEndianAt(frameSeq, totalLen - 3);
            ushort computedCrc = Crc16.Calculate(frameSeq.Slice(5, totalLen - 8));

            if (receivedCrc != computedCrc)
            {
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            // ۴. دکریپت کردن بخش داده (Data Domain) [cite: 94, 95]
            var encryptedPayload = frameSeq.Slice(7, totalLen - 10);
            byte[] decryptedData = _cryptoService.Decrypt(encryptedPayload);

            frame = new MeterFrame(
                type: Utils.ReadByteAt(frameSeq, 1),
                version: Utils.ReadByteAt(frameSeq, 2),
                length: (ushort)totalLen,
                mid: Utils.ReadByteAt(frameSeq, 5),
                control: Utils.ReadByteAt(frameSeq, 6),
                decryptedData: decryptedData
            );

            buffer = buffer.Slice(frameSeq.End);
            return true;
        }
    }
}