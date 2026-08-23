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
            => TryParse(ref buffer, out frame, out data, out _);

        public bool TryParse(
            ref ReadOnlySequence<byte> buffer,
            out MeterFrame? frame,
            out byte[]? data,
            out string? failureReason)
        {
            frame = null;
            data = null;
            failureReason = null;
            string? rejectedReason = null;
            byte[]? rejectedData = null;
            void Reject(string reason, ReadOnlySequence<byte>? candidate = null)
            {
                rejectedReason = reason;
                rejectedData = candidate?.ToArray();
            }
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
                Reject($"Invalid frame length: {totalLen}.");
                failureReason = rejectedReason;
                data = rejectedData;
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            if (buffer.Length < totalLen) return false; // فریم هنوز کامل نشده است

            var frameSeq = buffer.Slice(startPos, totalLen);

            byte type = Utils.ReadByteAt(frameSeq, 1);
            byte version = Utils.ReadByteAt(frameSeq, 2);
            byte control = Utils.ReadByteAt(frameSeq, 6);
            if (version != 0 ||
                (type != ProtocolConstants.TypeTransport &&
                 type != ProtocolConstants.TypeHandshake) ||
                (type == ProtocolConstants.TypeHandshake && control is not (0x01 or 0x02)) ||
                (type == ProtocolConstants.TypeTransport &&
                 control is not (ProtocolConstants.ControlReporting
                     or ProtocolConstants.ControlDistribution
                     or ProtocolConstants.ControlEndFrame)))
            {
                Reject($"Invalid header fields. Type=0x{type:X2}, Version=0x{version:X2}, Control=0x{control:X2}.", frameSeq);
                failureReason = rejectedReason;
                data = rejectedData;
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            // ۲. بررسی انتهای فریم (0x16) 
            if (Utils.ReadByteAt(frameSeq, totalLen - 1) != ProtocolConstants.Tail)
            {
                Reject("Invalid frame tail.", frameSeq);
                failureReason = rejectedReason;
                data = rejectedData;
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            // ۳. بررسی CRC16 مطابق Appendix C 
            ushort receivedCrc = Utils.ReadUInt16BigEndianAt(frameSeq, totalLen - 3);
            ushort computedCrc = Crc16.Calculate(frameSeq.Slice(5, totalLen - 8));

            if (receivedCrc != computedCrc)
            {
                Reject($"CRC mismatch. Received=0x{receivedCrc:X4}, Computed=0x{computedCrc:X4}.", frameSeq);
                failureReason = rejectedReason;
                data = rejectedData;
                buffer = buffer.Slice(buffer.GetPosition(1, startPos));
                return false;
            }

            // ۴. دکریپت کردن بخش داده (Data Domain) 
            var encryptedPayload = frameSeq.Slice(7, totalLen - MinimumFrameLength);
            if (encryptedPayload.Length == 0 || encryptedPayload.Length % 16 != 0)
            {
                Reject("Encrypted payload length is invalid.", frameSeq);
                failureReason = rejectedReason;
                data = rejectedData;
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
                Reject("AES payload decryption failed.", frameSeq);
                failureReason = rejectedReason;
                data = rejectedData;
                buffer = buffer.Slice(frameSeq.End);
                return false;
            }

            if (type == ProtocolConstants.TypeHandshake)
            {
                if (control == 0x02)
                {
                    if (decryptedData.Length < 26)
                    {
                        Reject("Handshake response payload is too short.", frameSeq);
                        failureReason = rejectedReason;
                        data = rejectedData;
                        buffer = buffer.Slice(frameSeq.End);
                        return false;
                    }
                }
                else if (decryptedData.Length < 41)
                {
                    Reject("Handshake payload is too short.", frameSeq);
                    failureReason = rejectedReason;
                    data = rejectedData;
                    buffer = buffer.Slice(frameSeq.End);
                    return false;
                }

                else
                {
                    int meterDigits = decryptedData[2];
                    int meterBytes = (meterDigits + 1) / 2;
                    if (decryptedData.Length < 3 + meterBytes ||
                        meterDigits is < 1 or > 34)
                    {
                        Reject("Handshake meter identifier length is invalid.", frameSeq);
                        failureReason = rejectedReason;
                        data = rejectedData;
                        buffer = buffer.Slice(frameSeq.End);
                        return false;
                    }
                }
            }
            else
            {
                if (decryptedData.Length < 11)
                {
                    Reject("Transport payload is too short.", frameSeq);
                    failureReason = rejectedReason;
                    data = rejectedData;
                    buffer = buffer.Slice(frameSeq.End);
                    return false;
                }

                int dataLengthOffset = control == ProtocolConstants.ControlEndFrame ? 7 : 6;
                ushort declaredDataLength =
                    BinaryPrimitives.ReadUInt16BigEndian(
                        decryptedData.AsSpan(dataLengthOffset, 2));
                int actualDataLength = control == ProtocolConstants.ControlEndFrame
                    ? decryptedData.Length - 12
                    : decryptedData.Length - 11;
                if (declaredDataLength != actualDataLength)
                {
                    Reject($"Transport payload length mismatch. Declared={declaredDataLength}, Actual={actualDataLength}.", frameSeq);
                    failureReason = rejectedReason;
                    data = rejectedData;
                    buffer = buffer.Slice(frameSeq.End);
                    return false;
                }
            }

            frame = new MeterFrame(
                type: type,
                version: version,
                length: (ushort)totalLen,
                mid: Utils.ReadByteAt(frameSeq, 5),
                control: control,
                decryptedData: decryptedData
            );

            if (type == ProtocolConstants.TypeTransport)
            {
                frame.SessionId = (uint)BinaryPrimitives.ReadInt32BigEndian(decryptedData.AsSpan(0, 4));
                frame.FrameNo = BinaryPrimitives.ReadUInt16BigEndian(decryptedData.AsSpan(4, 2));
            }
            
            data = frameSeq.ToArray();
            buffer = buffer.Slice(frameSeq.End);
            return true;
        }
    }
}
