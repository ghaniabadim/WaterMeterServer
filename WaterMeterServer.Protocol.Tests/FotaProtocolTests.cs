using System.Buffers;
using System.Buffers.Binary;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Infrastructure.Security;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.Protocol.Tests
{
    public class FotaProtocolTests
    {
        private const string AesKey = "676F6C64636172643030323530323533";

        [Fact]
        public void BuildUpgradeRequest_MatchesOfficialProtocolVector()
        {
            var builder = CreateBuilder();

            byte[] packet = builder.BuildWriteFirmwareRequest(
                0x9FD1A793,
                0x02,
                0x0001,
                0x0000,
                "02605301",
                "02605302");

            Assert.Equal(
                "680400002A0202BB09B702A689EB9C11CC392CFFE7AD035C6F543A65342F8E7AF47EF0E5A10335ACEC16",
                Convert.ToHexString(packet));
        }

        [Fact]
        public void BuildFirmwareInfo_MatchesOfficialProtocolVector()
        {
            var builder = CreateBuilder();

            byte[] packet = builder.BuildFirmwareInfo(
                0x9FD1A793,
                0x03,
                0x0002,
                0x0000,
                "02605302",
                0x00000F00,
                0xEE2FCC70);

            Assert.Equal(
                "680400002A030265E79BD568196B0D35CB9EE68ED692D74610A3FAD868BFFE89AF7609A02CFA27BBD216",
                Convert.ToHexString(packet));
        }

        [Fact]
        public void FrameParser_ParsesOfficialUpgradeStatusFrame()
        {
            var crypto = new AesCryptoService(AesKey);
            var parser = new FrameParser(crypto);
            byte[] rawFrame = Convert.FromHexString(
                "680400002A030111FD9AB856F2570A134F1B74E32857B4ABF31D22577BCC83AE8C64A776B9C87D4D7E16");
            var buffer = new ReadOnlySequence<byte>(rawFrame);

            bool parsed = parser.TryParse(ref buffer, out MeterFrame? frame, out _);

            Assert.True(parsed);
            Assert.NotNull(frame);
            Assert.Equal(0x9FD1A793u, frame.SessionId);
            Assert.Equal((ushort)0x0002, frame.FrameNo);
            Assert.Equal((byte)0x01, frame.DecryptedData[8]);
            Assert.Equal((ushort)0x0000, BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(9, 2)));
        }

        [Fact]
        public void FrameParser_ParsesOfficialEndFrame()
        {
            var crypto = new AesCryptoService(AesKey);
            var parser = new FrameParser(crypto);
            byte[] rawFrame = Convert.FromHexString(
                "680400003A03053063050268248A4B73D2CD24F83F55834E929EEDA88AF8F235582BC8E42C8E06ABF31D22577BCC83AE8C64A776B9C87D670D16");
            var buffer = new ReadOnlySequence<byte>(rawFrame);

            bool parsed = parser.TryParse(ref buffer, out MeterFrame? frame, out _);

            Assert.True(parsed);
            Assert.NotNull(frame);
            Assert.Equal(0x2DE9E43Du, frame.SessionId);
            Assert.Equal((ushort)0x0002, frame.FrameNo);
            Assert.Equal((byte)0x05, frame.ControlCode);
            Assert.Equal((byte)0x00, frame.DecryptedData[6]);
            Assert.Equal((ushort)0x21B8, BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(13, 2)));
        }

        [Fact]
        public void FrameParser_RejectsUnsupportedTransportControl()
        {
            var crypto = new AesCryptoService(AesKey);
            var parser = new FrameParser(crypto);
            byte[] rawFrame = Convert.FromHexString(
                "680400002A030111FD9AB856F2570A134F1B74E32857B4ABF31D22577BCC83AE8C64A776B9C87D4D7E16");
            rawFrame[6] = 0x06;
            var buffer = new ReadOnlySequence<byte>(rawFrame);

            Assert.False(parser.TryParse(ref buffer, out _, out _));
        }

        [Fact]
        public void FrameParser_RejectsBadTransportDataLength()
        {
            var crypto = new AesCryptoService(AesKey);
            var parser = new FrameParser(crypto);
            byte[] rawFrame = Convert.FromHexString(
                "680400002A030111FD9AB856F2570A134F1B74E32857B4ABF31D22577BCC83AE8C64A776B9C87D4D7E16");
            rawFrame[7 + 6] = 0x00;
            rawFrame[7 + 7] = 0x01;
            var buffer = new ReadOnlySequence<byte>(rawFrame);

            Assert.False(parser.TryParse(ref buffer, out _, out _));
        }

        [Fact]
        public void ReportedObjectParser_ReadsFourByteUpgradeStatus()
        {
            byte[] plaintext = Convert.FromHexString(
                "9FD1A793000200080100000143040401000000");

            bool found = FotaPayloadParser.TryFindReportedObject(
                plaintext,
                FirmwareObjectIds.UpgradeStatus,
                out byte[] content);

            Assert.True(found);
            Assert.Equal("01000000", Convert.ToHexString(content));
        }

        [Fact]
        public void ReportedObjectParser_ReadsSegmentationFromCombined4305And4306()
        {
            byte[] plaintext = Convert.FromHexString(
                "9FD1A7930003001B0100000243050C0260530200000F00EE2FCC704306080000000000000100");

            bool foundInfo = FotaPayloadParser.TryFindReportedObject(
                plaintext,
                FirmwareObjectIds.FirmwareInfo,
                out byte[] firmwareInfo);
            bool foundSegment = FotaPayloadParser.TryFindReportedObject(
                plaintext,
                FirmwareObjectIds.Segmentation,
                out byte[] segment);

            Assert.True(foundInfo);
            Assert.Equal("0260530200000F00EE2FCC70", Convert.ToHexString(firmwareInfo));
            Assert.True(foundSegment);
            Assert.Equal("0000000000000100", Convert.ToHexString(segment));
        }

        [Fact]
        public void BuildFirmwareChunk_ContainsRequestedOffsetLengthDataAndCrc()
        {
            var crypto = new AesCryptoService(AesKey);
            var builder = new ProtocolBuilder(crypto);
            byte[] chunk = Convert.FromHexString("0102030405060708");

            byte[] packet = builder.BuildFirmwareChunkResponse(
                0x9FD1A793,
                0x04,
                0x0003,
                0x0000,
                0x00000100,
                chunk);

            byte[] plaintext = DecryptPacket(packet, crypto);
            Assert.Equal(0x9FD1A793u, BinaryPrimitives.ReadUInt32BigEndian(plaintext));
            Assert.Equal((ushort)0x0003, BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(4, 2)));
            Assert.Equal((byte)0x02, plaintext[8]);
            Assert.Equal(FirmwareObjectIds.DataStructure, BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(12, 2)));
            Assert.Equal(0x00000100u, BinaryPrimitives.ReadUInt32BigEndian(plaintext.AsSpan(14, 4)));
            Assert.Equal((uint)chunk.Length, BinaryPrimitives.ReadUInt32BigEndian(plaintext.AsSpan(18, 4)));
            Assert.Equal(chunk, plaintext.AsSpan(22, chunk.Length).ToArray());
            Assert.Equal(
                Crc16.Calculate(chunk),
                BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(22 + chunk.Length, 2)));
        }

        private static ProtocolBuilder CreateBuilder()
        {
            return new ProtocolBuilder(new AesCryptoService(AesKey));
        }

        private static byte[] DecryptPacket(byte[] packet, AesCryptoService crypto)
        {
            var encrypted = new ReadOnlySequence<byte>(
                packet.AsMemory(7, packet.Length - 10));
            return crypto.Decrypt(encrypted);
        }
    }
}
