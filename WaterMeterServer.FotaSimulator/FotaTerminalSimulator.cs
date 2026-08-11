using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Infrastructure.Security;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.FotaSimulator
{
    public sealed class FotaTerminalSimulator
    {
        private readonly SimulatorOptions _options;
        private readonly AesCryptoService _crypto;
        private readonly Action<string> _log;
        private byte _mid;
        private ushort _terminalFrameNumber;
        private uint _sessionId;

        public FotaTerminalSimulator(
            SimulatorOptions options,
            Action<string>? log = null)
        {
            _options = options;
            _crypto = new AesCryptoService(options.AesKey);
            _log = log ?? Console.WriteLine;
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            try
            {
                if (_options.Scenario is SimulationScenario.InstallFailure)
                {
                    await RunTransferAsync(timeout.Token);
                    await ReportInstallationResultAsync(0x06, timeout.Token);
                    return;
                }

                if (_options.Scenario is SimulationScenario.DownloadFailure)
                {
                    await RunDownloadFailureAsync(timeout.Token);
                    return;
                }

                await RunTransferAsync(timeout.Token);
                await ReportInstallationResultAsync(0x05, timeout.Token);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"FOTA simulation exceeded the {_options.TimeoutSeconds}-second timeout.");
            }
        }

        private async Task RunTransferAsync(CancellationToken cancellationToken)
        {
            using TcpClient client = await ConnectAndHandshakeAsync(cancellationToken);
            NetworkStream stream = client.GetStream();

            byte initialMid = NextMid();
            await SendAsync(
                stream,
                BuildReport(initialMid, Array.Empty<ReportedObject>()),
                cancellationToken);
            ReceivedFrame upgradeRequestFrame = await ReadFrameAsync(stream, cancellationToken);
            ValidateResponseMid(initialMid, upgradeRequestFrame);
            byte[] upgradeRequest = ReadSingleDistributedObject(
                upgradeRequestFrame,
                FirmwareObjectIds.UpgradeRequest);
            EnsureLength(upgradeRequest, 9, FirmwareObjectIds.UpgradeRequest);

            _log(
                $"430C received. Target={Convert.ToHexString(upgradeRequest.AsSpan(1, 4))}, Current={Convert.ToHexString(upgradeRequest.AsSpan(5, 4))}");

            byte acceptanceStatus = _options.Scenario == SimulationScenario.Resume
                ? (byte)0x02
                : (byte)0x01;
            byte statusMid = NextMid();
            await SendAsync(
                stream,
                BuildReport(
                    statusMid,
                    new ReportedObject(
                        FirmwareObjectIds.UpgradeStatus,
                        new byte[] { acceptanceStatus, 0x00, 0x00, 0x00 })),
                cancellationToken);

            ReceivedFrame firmwareInfoFrame = await ReadFrameAsync(stream, cancellationToken);
            ValidateResponseMid(statusMid, firmwareInfoFrame);
            byte[] firmwareInfo = ReadSingleDistributedObject(
                firmwareInfoFrame,
                FirmwareObjectIds.FirmwareInfo);
            EnsureLength(firmwareInfo, 12, FirmwareObjectIds.FirmwareInfo);

            int firmwareSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                firmwareInfo.AsSpan(4, 4)));
            uint firmwareCrc32 = BinaryPrimitives.ReadUInt32BigEndian(
                firmwareInfo.AsSpan(8, 4));
            _log(
                $"4305 received. Size={firmwareSize}, CRC32=0x{firmwareCrc32:X8}");

            int offset = 0;
            bool retriedFirstChunk = false;
            while (offset < firmwareSize)
            {
                int requestedLength = Math.Min(_options.ChunkSize, firmwareSize - offset);
                byte segmentMid = NextMid();
                await SendAsync(
                    stream,
                    BuildSegmentRequest(segmentMid, firmwareInfo, offset, requestedLength),
                    cancellationToken);

                ReceivedFrame chunkFrame = await ReadFrameAsync(stream, cancellationToken);
                ValidateResponseMid(segmentMid, chunkFrame);
                byte[] chunkObject = ReadSingleDistributedObject(
                    chunkFrame,
                    FirmwareObjectIds.DataStructure);
                ValidateChunk(chunkObject, offset, requestedLength);

                _log($"4307 received. Offset={offset}, Length={requestedLength}");

                if (_options.Scenario == SimulationScenario.CrcRetry && !retriedFirstChunk)
                {
                    retriedFirstChunk = true;
                    _log($"Retrying offset {offset} to simulate terminal CRC rejection.");
                    continue;
                }

                offset += requestedLength;
            }

            byte completedMid = NextMid();
            await SendAsync(
                stream,
                BuildReport(
                    completedMid,
                    new ReportedObject(
                        FirmwareObjectIds.UpgradeStatus,
                        new byte[] { 0x03, 0x00, 0x00, 0x00 })),
                cancellationToken);
            ReceivedFrame endFrame = await ReadFrameAsync(stream, cancellationToken);
            ValidateResponseMid(completedMid, endFrame);
            ValidateEndFrame(endFrame);
            _log("Download complete status accepted; server issued End Frame.");
        }

        private async Task RunDownloadFailureAsync(CancellationToken cancellationToken)
        {
            using TcpClient client = await ConnectAndHandshakeAsync(cancellationToken);
            NetworkStream stream = client.GetStream();

            byte initialMid = NextMid();
            await SendAsync(
                stream,
                BuildReport(initialMid, Array.Empty<ReportedObject>()),
                cancellationToken);
            ReceivedFrame upgradeRequestFrame = await ReadFrameAsync(stream, cancellationToken);
            ValidateResponseMid(initialMid, upgradeRequestFrame);
            ReadSingleDistributedObject(upgradeRequestFrame, FirmwareObjectIds.UpgradeRequest);

            byte failureMid = NextMid();
            await SendAsync(
                stream,
                BuildReport(
                    failureMid,
                    new ReportedObject(
                        FirmwareObjectIds.UpgradeStatus,
                        new byte[] { 0x04, 0x03, 0x00, 0x00 })),
                cancellationToken);
            ReceivedFrame endFrame = await ReadFrameAsync(stream, cancellationToken);
            ValidateResponseMid(failureMid, endFrame);
            ValidateEndFrame(endFrame);
            _log("Download failure status accepted; server issued End Frame.");
        }

        private async Task ReportInstallationResultAsync(
            byte status,
            CancellationToken cancellationToken)
        {
            ResetConnectionCounters();
            using TcpClient client = await ConnectAndHandshakeAsync(cancellationToken);
            NetworkStream stream = client.GetStream();

            byte statusMid = NextMid();
            await SendAsync(
                stream,
                BuildReport(
                    statusMid,
                    new ReportedObject(
                        FirmwareObjectIds.UpgradeStatus,
                        new byte[] { status, 0x00, 0x00, 0x00 })),
                cancellationToken);
            ReceivedFrame endFrame = await ReadFrameAsync(stream, cancellationToken);
            ValidateResponseMid(statusMid, endFrame);
            ValidateEndFrame(endFrame);

            string result = status == 0x05 ? "installation success" : "installation failure";
            _log($"Final {result} status accepted; simulation finished.");
        }

        private async Task<TcpClient> ConnectAndHandshakeAsync(
            CancellationToken cancellationToken)
        {
            var client = new TcpClient();
            await client.ConnectAsync(_options.Host, _options.Port, cancellationToken);
            NetworkStream stream = client.GetStream();

            byte handshakeMid = NextMid();
            await SendAsync(
                stream,
                BuildHandshakeRequest(handshakeMid),
                cancellationToken);
            ReceivedFrame response = await ReadFrameAsync(stream, cancellationToken);
            ValidateResponseMid(handshakeMid, response);

            if (response.Type != ProtocolConstants.TypeHandshake ||
                response.Control != 0x02 ||
                response.Plaintext.Length < 26)
            {
                throw new InvalidDataException("Invalid handshake response.");
            }

            ushort status = BinaryPrimitives.ReadUInt16BigEndian(
                response.Plaintext.AsSpan(20, 2));
            if (status != ProtocolConstants.HandshakeSuccess)
            {
                throw new InvalidOperationException(
                    $"Handshake rejected with status 0x{status:X4}. Ensure the meter is registered.");
            }

            _sessionId = BinaryPrimitives.ReadUInt32BigEndian(
                response.Plaintext.AsSpan(22, 4));
            _log($"Handshake accepted. Session=0x{_sessionId:X8}");
            return client;
        }

        private byte[] BuildHandshakeRequest(byte mid)
        {
            byte[] business = new byte[41];
            BinaryPrimitives.WriteUInt16BigEndian(business, 0x0902);
            business[2] = (byte)_options.MeterId.Length;
            byte[] meterBytes = Utils.StringToBcd(_options.MeterId);
            meterBytes.CopyTo(business, 3);
            business[20] = 0x01;

            DateTime now = DateTime.UtcNow;
            business[21] = Utils.ByteToBcd(now.Year % 100);
            business[22] = Utils.ByteToBcd(now.Month);
            business[23] = Utils.ByteToBcd(now.Day);
            business[24] = Utils.ByteToBcd(now.Hour);
            business[25] = Utils.ByteToBcd(now.Minute);
            business[26] = Utils.ByteToBcd(now.Second);
            business[27] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(business.AsSpan(32, 2), 0x0002);
            business[34] = 0x01;
            business[35] = 0x05;

            return WrapPacket(
                ProtocolConstants.TypeHandshake,
                mid,
                0x01,
                _crypto.Encrypt(business));
        }

        private byte[] BuildSegmentRequest(
            byte mid,
            byte[] firmwareInfo,
            int offset,
            int length)
        {
            byte[] segmentation = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(
                segmentation,
                checked((uint)offset));
            BinaryPrimitives.WriteUInt32BigEndian(
                segmentation.AsSpan(4, 4),
                checked((uint)length));

            return BuildReport(
                mid,
                new ReportedObject(FirmwareObjectIds.FirmwareInfo, firmwareInfo),
                new ReportedObject(FirmwareObjectIds.Segmentation, segmentation));
        }

        private byte[] BuildReport(byte mid, params ReportedObject[] objects)
        {
            using var business = new MemoryStream();
            WriteUInt32(business, _sessionId);
            WriteUInt16(business, NextTerminalFrameNumber());

            int objectsLength = objects.Sum(item => 3 + item.Content.Length);
            WriteUInt16(business, checked((ushort)(1 + objectsLength)));
            business.WriteByte(ProtocolConstants.ControlReporting);
            WriteUInt16(business, 0x0000);
            business.WriteByte(checked((byte)objects.Length));

            foreach (ReportedObject item in objects)
            {
                if (item.Content.Length > byte.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Reported object 0x{item.Id:X4} exceeds 255 bytes.");
                }

                WriteUInt16(business, item.Id);
                business.WriteByte((byte)item.Content.Length);
                business.Write(item.Content);
            }

            return WrapPacket(
                ProtocolConstants.TypeTransport,
                mid,
                ProtocolConstants.ControlReporting,
                _crypto.Encrypt(business.ToArray()));
        }

        private byte[] ReadSingleDistributedObject(
            ReceivedFrame frame,
            ushort expectedObjectId)
        {
            if (frame.Type == ProtocolConstants.TypeTransport &&
                frame.Control == ProtocolConstants.ControlEndFrame)
            {
                string reason = expectedObjectId == FirmwareObjectIds.UpgradeRequest
                    ? "Ensure an active FirmwareUpgradeRequest exists for the meter."
                    : "Inspect the WorkerService FOTA log and FirmwareUpgradeRequest.LastErrorMessage.";
                throw new InvalidOperationException(
                    $"Server ended the connection while waiting for object 0x{expectedObjectId:X4}. {reason}");
            }

            if (frame.Type != ProtocolConstants.TypeTransport ||
                frame.Control != ProtocolConstants.ControlDistribution ||
                frame.Plaintext.Length < 14)
            {
                throw new InvalidDataException(
                    $"Invalid data distribution frame. Type=0x{frame.Type:X2}, " +
                    $"Control=0x{frame.Control:X2}, PlaintextLength={frame.Plaintext.Length}, " +
                    $"Plaintext={Convert.ToHexString(frame.Plaintext)}");
            }

            uint sessionId = BinaryPrimitives.ReadUInt32BigEndian(frame.Plaintext);
            if (sessionId != _sessionId)
            {
                throw new InvalidDataException("Session ID mismatch in server response.");
            }

            byte functionCode = frame.Plaintext[8];
            if (functionCode != ProtocolConstants.FunCodeDataDistribution)
            {
                throw new InvalidDataException(
                    $"Unexpected function code 0x{functionCode:X2}.");
            }

            if (frame.Plaintext[11] != 0x01)
            {
                throw new InvalidDataException("Expected exactly one distributed object.");
            }

            ushort objectId = BinaryPrimitives.ReadUInt16BigEndian(
                frame.Plaintext.AsSpan(12, 2));
            if (objectId != expectedObjectId)
            {
                throw new InvalidDataException(
                    $"Expected object 0x{expectedObjectId:X4}, received 0x{objectId:X4}.");
            }

            int dataLength = BinaryPrimitives.ReadUInt16BigEndian(
                frame.Plaintext.AsSpan(6, 2));
            int contentLength = dataLength - 3;
            if (contentLength < 0 || 14 + contentLength > frame.Plaintext.Length)
            {
                throw new InvalidDataException("Invalid distributed object length.");
            }

            return frame.Plaintext.AsSpan(14, contentLength).ToArray();
        }

        private static void ValidateChunk(
            byte[] chunkObject,
            int expectedOffset,
            int expectedLength)
        {
            if (chunkObject.Length < 10)
            {
                throw new InvalidDataException("4307H content is too short.");
            }

            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkObject));
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                chunkObject.AsSpan(4, 4)));
            if (offset != expectedOffset || length != expectedLength)
            {
                throw new InvalidDataException(
                    $"4307H mismatch. Expected offset/length {expectedOffset}/{expectedLength}, received {offset}/{length}.");
            }

            if (chunkObject.Length != 10 + length)
            {
                throw new InvalidDataException("4307H content length is inconsistent.");
            }

            ReadOnlySpan<byte> data = chunkObject.AsSpan(8, length);
            ushort receivedCrc = BinaryPrimitives.ReadUInt16BigEndian(
                chunkObject.AsSpan(8 + length, 2));
            ushort computedCrc = Crc16.Calculate(data);
            if (receivedCrc != computedCrc)
            {
                throw new InvalidDataException(
                    $"4307H CRC mismatch. Received 0x{receivedCrc:X4}, computed 0x{computedCrc:X4}.");
            }
        }

        private static void ValidateEndFrame(ReceivedFrame frame)
        {
            if (frame.Type != ProtocolConstants.TypeTransport ||
                frame.Control != ProtocolConstants.ControlEndFrame ||
                frame.Plaintext.Length < 12 ||
                frame.Plaintext[6] != 0x00 ||
                frame.Plaintext[9] != ProtocolConstants.FunCodeEndCommunication)
            {
                throw new InvalidDataException("Expected a normal communication End Frame.");
            }
        }

        private static void EnsureLength(byte[] content, int expected, ushort objectId)
        {
            if (content.Length != expected)
            {
                throw new InvalidDataException(
                    $"Object 0x{objectId:X4} length is {content.Length}; expected {expected}.");
            }
        }

        private static void ValidateResponseMid(byte sentMid, ReceivedFrame response)
        {
            if (response.Mid != sentMid)
            {
                throw new InvalidDataException(
                    $"MID mismatch. Sent 0x{sentMid:X2}, received 0x{response.Mid:X2}.");
            }
        }

        private async Task<ReceivedFrame> ReadFrameAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] prefix = new byte[5];
            await stream.ReadExactlyAsync(prefix, cancellationToken);

            if (prefix[0] != ProtocolConstants.Head)
            {
                throw new InvalidDataException("Invalid frame header.");
            }

            int totalLength = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(3, 2));
            if (totalLength < 10 || totalLength > 4096)
            {
                throw new InvalidDataException($"Invalid frame length {totalLength}.");
            }

            byte[] packet = new byte[totalLength];
            prefix.CopyTo(packet, 0);
            await stream.ReadExactlyAsync(
                packet.AsMemory(5, totalLength - 5),
                cancellationToken);

            if (packet[^1] != ProtocolConstants.Tail)
            {
                throw new InvalidDataException("Invalid frame tail.");
            }

            ushort receivedCrc = BinaryPrimitives.ReadUInt16BigEndian(
                packet.AsSpan(totalLength - 3, 2));
            ushort computedCrc = Crc16.Calculate(
                packet.AsSpan(5, totalLength - 8));
            if (receivedCrc != computedCrc)
            {
                throw new InvalidDataException(
                    $"Frame CRC mismatch. Received 0x{receivedCrc:X4}, computed 0x{computedCrc:X4}.");
            }

            var encrypted = new ReadOnlySequence<byte>(
                packet.AsMemory(7, totalLength - 10));
            byte[] plaintext = _crypto.Decrypt(encrypted);
            return new ReceivedFrame(
                packet[1],
                packet[5],
                packet[6],
                plaintext);
        }

        private static async Task SendAsync(
            NetworkStream stream,
            byte[] packet,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(packet, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private byte[] WrapPacket(byte type, byte mid, byte control, byte[] encryptedData)
        {
            int totalLength = 10 + encryptedData.Length;
            byte[] packet = new byte[totalLength];
            packet[0] = ProtocolConstants.Head;
            packet[1] = type;
            packet[2] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(
                packet.AsSpan(3, 2),
                checked((ushort)totalLength));
            packet[5] = mid;
            packet[6] = control;
            encryptedData.CopyTo(packet, 7);

            ushort crc = Crc16.Calculate(packet.AsSpan(5, totalLength - 8));
            BinaryPrimitives.WriteUInt16BigEndian(
                packet.AsSpan(totalLength - 3, 2),
                crc);
            packet[^1] = ProtocolConstants.Tail;
            return packet;
        }

        private byte NextMid()
        {
            if (_mid == byte.MaxValue)
            {
                throw new InvalidOperationException(
                    "MID exhausted; start a new simulated session.");
            }

            return ++_mid;
        }

        private ushort NextTerminalFrameNumber()
        {
            if (_terminalFrameNumber == ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    "Terminal frame number exhausted; start a new session.");
            }

            return ++_terminalFrameNumber;
        }

        private void ResetConnectionCounters()
        {
            _mid = 0;
            _terminalFrameNumber = 0;
            _sessionId = 0;
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            stream.Write(buffer);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            stream.Write(buffer);
        }

        private sealed record ReportedObject(ushort Id, byte[] Content);

        private sealed record ReceivedFrame(
            byte Type,
            byte Mid,
            byte Control,
            byte[] Plaintext);
    }
}
