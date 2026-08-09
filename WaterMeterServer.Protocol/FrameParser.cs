using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Protocol
{
    public sealed class FrameParser
    {
        private const int MinimumFrameLength = 10;
        private const int MaximumFrameLength = 4_096;
        private readonly ICryptoService _cryptoService;

        public FrameParser(ICryptoService cryptoService) => _cryptoService = cryptoService;

        public bool TryParse(ref ReadOnlySequence<byte> buffer, out MeterFrame? frame, out byte[]? data)
        {
            frame = null;
            data = null;
            var reader = new SequenceReader<byte>(buffer);

            // ۱. جستجوی HEAD (0x68) 
            if (!reader.TryAdvanceTo(ProtocolConstants.Head, false))
            {
                buffer = buffer.Slice(buffer.End);
                return false;
            }
            
            var startPos = reader.Position;
            if (reader.Remaining < MinimumFrameLength) return false; // حداقل طول فریم

            reader.Advance(3); // عبور از Type و Version
            if (!reader.TryReadBigEndian(out short rawLength)) return false;

            int totalLen = unchecked((ushort)rawLength);
            if (totalLen < MinimumFrameLength || totalLen > MaximumFrameLength)
            {
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            if (buffer.Length < totalLen) return false; // فریم هنوز کامل نشده است

            var frameSeq = buffer.Slice(startPos, totalLen);

            // ۲. بررسی انتهای فریم (0x16) 
            if (Utils.ReadByteAt(frameSeq, totalLen - 1) != ProtocolConstants.Tail)
            {
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            // ۳. بررسی CRC16 مطابق Appendix C 
            ushort receivedCrc = Utils.ReadUInt16BigEndianAt(frameSeq, totalLen - 3);
            ushort computedCrc = Crc16.Calculate(frameSeq.Slice(5, totalLen - 8));

            if (receivedCrc != computedCrc)
            {
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            // ۴. دکریپت کردن بخش داده (Data Domain) 
            var encryptedPayload = frameSeq.Slice(7, totalLen - MinimumFrameLength);
            if (encryptedPayload.Length == 0 || encryptedPayload.Length % 16 != 0)
            {
                buffer = buffer.Slice(frameSeq.End);
                return false;
            }

            byte[] decryptedData;
            try
            {
                decryptedData = _cryptoService.Decrypt(encryptedPayload);
            }
            catch (CryptographicException)
            {
                buffer = buffer.Slice(frameSeq.End);
                return false;
            }

            

            frame = new MeterFrame(
                type: Utils.ReadByteAt(frameSeq, 1),
                version: Utils.ReadByteAt(frameSeq, 2),
                length: (ushort)totalLen,
                mid: Utils.ReadByteAt(frameSeq, 5),
                control: Utils.ReadByteAt(frameSeq, 6),
                decryptedData: decryptedData
            );

            if (frame.Type == ProtocolConstants.TypeTransport)
            {
                if (decryptedData.Length < 11)
                {
                    buffer = buffer.Slice(frameSeq.End);
                    return false;
                }

                frame.SessionId = (uint)BinaryPrimitives.ReadInt32BigEndian(decryptedData.AsSpan(0, 4));
                frame.FrameNo = BinaryPrimitives.ReadUInt16BigEndian(decryptedData.AsSpan(4, 2));
            }
            
            data = frameSeq.ToArray();
            buffer = buffer.Slice(frameSeq.End);
            return true;
        }
    }
}
