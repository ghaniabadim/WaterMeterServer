using WaterMeterServer.Domain.Models;
using WaterMeterServer.Infrastructure.Stores;
using WaterMeterServer.Infrastructure.Security;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Protocol;
using System.Buffers;
using System.Buffers.Binary;

namespace WaterMeterServer.Protocol.Tests;

public sealed class RemoteProtocolPhase1Tests
{
    private const string AesKey = "676F6C64636172643030323530323533";

    [Fact]
    public void FrameParser_ParsesOfficialRemoteHandshakeRequestVector()
    {
        var parser = new FrameParser(
            new WaterMeterServer.Infrastructure.Security.AesCryptoService(AesKey));
        byte[] raw = Convert.FromHexString(
            "680500003A01017EDFFC051800D1BA253D8199ABB21EDC11CD2B2D8E03FBB8F45B7815CA66C7077E28404F0B9FA99503CEE6EE31696BDAE34416");
        var buffer = new ReadOnlySequence<byte>(raw);

        Assert.True(parser.TryParse(ref buffer, out var frame, out _));
        Assert.NotNull(frame);
        Assert.Equal((byte)0x05, frame!.Type);
        Assert.Equal((byte)0x01, frame.ControlCode);
        Assert.Equal("09020C", Convert.ToHexString(frame.DecryptedData.AsSpan(0, 3)));
        Assert.Equal(0x0C, frame.DecryptedData[2]);
        Assert.Equal(0x00, frame.DecryptedData[^1]);
        Assert.InRange(frame.DecryptedData.Length, 40, 64);
    }

    [Fact]
    public void RemoteCatalog_ContainsOnlyCurrentRemoteElectromagneticObjects()
    {
        Assert.Contains(RemoteDataObjectCatalog.Current, x => x.Id == 0x70F2);
        Assert.Contains(RemoteDataObjectCatalog.Current, x => x.Id == 0xB05C);
        Assert.Contains(RemoteDataObjectCatalog.Current, x => x.Id == 0xB070);
        Assert.Contains(RemoteDataObjectCatalog.Current, x => x.Id == 0x70EE);
        Assert.Contains(RemoteDataObjectCatalog.Current, x => x.Id == 0x430C);

        Assert.DoesNotContain(
            RemoteDataObjectCatalog.Current,
            x => x.Name.Contains("Local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SessionManager_Reconnect_InvalidatesPreviousSession()
    {
        var sessions = new SessionManager();
        const string meterId = "1234567890";

        uint first = sessions.GenerateSessionId(meterId);
        sessions.RemoveSession(meterId);
        uint second = sessions.GenerateSessionId(meterId);

        Assert.NotEqual(first, second);
        Assert.False(sessions.ValidateSession(meterId, first));
        Assert.True(sessions.ValidateSession(meterId, second));
        Assert.Null(sessions.GetMeterIdBySession(first));
        Assert.Equal(meterId, sessions.GetMeterIdBySession(second));
    }

    [Fact]
    public void SessionManager_ExpiresInactiveSessionAfterTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = new SessionManager(
            TimeSpan.FromMinutes(5),
            () => now);
        uint sessionId = sessions.GenerateSessionId("1234567890");

        now = now.AddMinutes(6);

        Assert.False(sessions.ValidateSession("1234567890", sessionId));
        Assert.Null(sessions.GetSession("1234567890"));
        Assert.Null(sessions.GetMeterIdBySession(sessionId));
    }

    [Fact]
    public void ConnectionContext_AcceptsDuplicateAsReplayAndRejectsRollback()
    {
        var context = new ConnectionContext();

        Assert.Equal(
            TerminalFrameDisposition.New,
            context.AcceptTerminalFrame(mid: 1, frameNumber: 10));
        Assert.Equal(
            TerminalFrameDisposition.Duplicate,
            context.AcceptTerminalFrame(mid: 1, frameNumber: 10));
        Assert.Equal(
            TerminalFrameDisposition.Invalid,
            context.AcceptTerminalFrame(mid: 1, frameNumber: 9));
    }

    [Fact]
    public void NegativeResponses_UseProtocolNegativeMarkers()
    {
        var builder = new ProtocolBuilder(new AesCryptoService(
            "676F6C64636172643030323530323533"));

        byte[] read = builder.BuildNegativeReadResponse(0x9FD1A793, 1, 2, 3);
        byte[] write = builder.BuildNegativeWriteResponse(0x9FD1A793, 1, 2, 3);
        byte[] byTime = builder.BuildNegativeReadRecordsByTimeResponse(
            0x9FD1A793, 1, 2, 3, 0x70F2);
        byte[] recent = builder.BuildNegativeReadRecentRecordsResponse(
            0x9FD1A793, 1, 2, 3, 0x70F2);

        Assert.Equal(0x02, read[6]);
        Assert.Equal(0x02, write[6]);
        Assert.Equal(0x02, byTime[6]);
        Assert.Equal(0x02, recent[6]);
        Assert.All(new[] { read, write, byTime, recent }, packet =>
            Assert.Equal(0x16, packet[^1]));
    }

    [Fact]
    public void FrameParser_ParsesOfficialRemoteHandshakeResponseVector()
    {
        var parser = new FrameParser(new AesCryptoService(
            "676F6C64636172643030323530323533"));
        byte[] raw = Convert.FromHexString(
            "680500002A01027EDFFC051800D1BA253D8199ABB21EDC7A843C9425A40A5744777D82BA7EAA7BA02116");
        var buffer = new ReadOnlySequence<byte>(raw);

        Assert.True(parser.TryParse(ref buffer, out var frame, out _));
        Assert.NotNull(frame);
        Assert.Equal((byte)0x02, frame!.ControlCode);
        Assert.Equal(26, frame.DecryptedData.Length);
    }

    [Fact]
    public void FrameParser_ParsesOfficialPeriodicReportingFirstFrameVector()
    {
        var parser = new FrameParser(new AesCryptoService(
            "676F6C64636172643030323530323533"));
        const string hex =
            "68040000CA0201" +
            "BA516C55E4F9304C324FF43C053614051BF841F4843F7B885E050FDABDC79E4835AC818AA83B8901C7307612BAF02263" +
            "251E6AC8BA6408EEE4F5D7BA6F5E332F351CBFCFF13B150D92FEF3411EEF105EEAE2854CFB8EF5C48A3BA5C7FA919BA5" +
            "E46ECE255A8A7F5A3414AE8B1AE03F89286A7970E585C58F31FE10A17CBF2F218168B7A12010D96F8AC159AD931AD0F3" +
            "E7B0D6B271E8404826DFFFD458EC175CD6CE70E0FFC61F0BA142CC1E9B8FC78F5ED9AA7BF6746AA68C97995CA7C85C6C" +
            "209516";
        var buffer = new ReadOnlySequence<byte>(Convert.FromHexString(hex));

        Assert.True(parser.TryParse(ref buffer, out var frame, out _));
        Assert.NotNull(frame);
        Assert.Equal((byte)0x04, frame!.Type);
        Assert.Equal((byte)0x01, frame.ControlCode);
        Assert.Equal(0x6001F438u, frame.SessionId);
        Assert.Equal((ushort)0x0001, frame.FrameNo);
        Assert.Equal((byte)0x01, frame.DecryptedData[8]);
        Assert.Equal((byte)0x05, frame.DecryptedData[11]);
        Assert.Equal(0xB064, BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(12, 2)));
    }
}
