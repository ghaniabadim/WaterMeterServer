using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Domain.Models;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Infrastructure.Persistence;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.Application.Dispatchers
{
    public sealed class FrameDispatcher
    {
        private readonly ILogger<FrameDispatcher> _logger;
        private readonly IProtocolBuilder _protocolBuilder;
        private readonly ISessionManager _sessionManager;
        private readonly ITelemetryBuffer _telemetryBuffer;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LogQueue _logQueue;

        public FrameDispatcher(
            ILogger<FrameDispatcher> logger,
            IProtocolBuilder protocolBuilder,
            ISessionManager sessionManager,
            ITelemetryBuffer telemetryBuffer,
            IServiceScopeFactory scopeFactory,
            LogQueue logQueue)
        {
            _logger = logger;
            _protocolBuilder = protocolBuilder;
            _sessionManager = sessionManager;
            _telemetryBuffer = telemetryBuffer;
            _scopeFactory = scopeFactory;
            _logQueue = logQueue;
        }

        public async Task<byte[]?> DispatchAsync(MeterFrame frame, ConnectionContext context)
        {
            byte[]? data = null;

            if (frame.Type == ProtocolConstants.TypeTransport && string.IsNullOrEmpty(context.MeterId))
            {
                _logger.LogWarning("Unauthorized Transport received before handshake. Session ID: {SessionId}", frame.SessionId);
                data = _protocolBuilder.BuildEndFrameResponse(
                    frame.SessionId,
                    frame.Mid,
                    context.NextServerFrameNumber(),
                    0x01);
            }
            else if (frame.Type == ProtocolConstants.TypeHandshake)
            {
                data = await HandleHandshakeAsync(frame, context);
            }
            else if (frame.Type == ProtocolConstants.TypeTransport)
            {
                if (context.SessionId != frame.SessionId)
                {
                    _logger.LogWarning("Session ID mismatch. Expected: {Expected}, Received: {Received}", context.SessionId, frame.SessionId);
                    data = _protocolBuilder.BuildEndFrameResponse(
                        frame.SessionId,
                        frame.Mid,
                        context.NextServerFrameNumber(),
                        0x01);
                }
                else
                {
                    var disposition = context.AcceptTerminalFrame(
                        frame.Mid,
                        frame.FrameNo);

                    if (disposition == TerminalFrameDisposition.Duplicate)
                    {
                        _logger.LogInformation(
                            "Duplicate terminal frame ignored. Meter={MeterId}, MID={Mid}, FrameNo={FrameNo}",
                            context.MeterId,
                            frame.Mid,
                            frame.FrameNo);
                        return context.LastResponse;
                    }

                    if (disposition == TerminalFrameDisposition.Invalid)
                    {
                        _logger.LogWarning(
                            "Out-of-order terminal frame rejected. Meter={MeterId}, MID={Mid}, FrameNo={FrameNo}",
                            context.MeterId,
                            frame.Mid,
                            frame.FrameNo);
                        data = _protocolBuilder.BuildEndFrameResponse(
                            context.SessionId.Value,
                            frame.Mid,
                            context.NextServerFrameNumber(),
                            0x01);
                    }
                    else
                    {
                        data = await HandleTransportAsync(frame, context);
                    }
                }
            }

            if (frame.Type == ProtocolConstants.TypeTransport && data != null)
            {
                context.LastResponse = data;
            }

            return data;
        }

        private async Task<byte[]?> HandleHandshakeAsync(MeterFrame frame, ConnectionContext context)
        {
            _logger.LogInformation("Processing handshake for Mid: {Mid}", frame.Mid);

            if (frame.DecryptedData.Length >= 41 &&
                frame.DecryptedData[0] == 0x09 &&
                frame.DecryptedData[1] == 0x02)
            {
                int meterIdLength = frame.DecryptedData[2];
                if (meterIdLength > 0 && meterIdLength <= 34)
                {
                    int meterIdBytesLength = (meterIdLength + 1) / 2;
                    var meterIdBytes = frame.DecryptedData.AsSpan(3, meterIdBytesLength);
                    var meterId = Utils.BcdToString(meterIdBytes)
                        .Substring(0, meterIdLength);
                    frame.MeterId = meterId;

                    if (_sessionManager.GetSession(meterId) != null)
                    {
                        _sessionManager.RemoveSession(meterId);
                        _logger.LogInformation("Previous session cleared for Meter: {MeterId}", meterId);
                    }

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var deviceRegistry = scope.ServiceProvider.GetRequiredService<IDeviceRegistry>();
                        var device = await deviceRegistry.GetDeviceAsync(meterId);
                        if (device == null)
                        {
                            _logger.LogWarning("Handshake rejected for unregistered meter: {MeterId}", meterId);
                            return _protocolBuilder.BuildHandshakeResponse(
                                frame.Mid,
                                0,
                                frame.DecryptedData,
                                ProtocolConstants.HandshakeFailure);
                        }

                        var sessionId = _sessionManager.GenerateSessionId(meterId);
                        context.MeterId = meterId;
                        context.SessionId = sessionId;
                        context.CurrentState = ConnectionContext.TransportState.WaitForReporting;
                        context.LastResponse = null;
                        device.LastSessionId = sessionId;
                        device.LastSeenAt = Utils.DateTimeToInstant(DateTime.Now);
                        await deviceRegistry.UpdateDeviceAtivityAsync(device);
                        context.Device = device;

                        return _protocolBuilder.BuildHandshakeResponse(
                            frame.Mid,
                            sessionId,
                            frame.DecryptedData,
                            ProtocolConstants.HandshakeSuccess);
                    }
                }
            }

            return _protocolBuilder.BuildHandshakeResponse(frame.Mid, 0, frame.DecryptedData, ProtocolConstants.HandshakeFailure);
        }

        private async Task<byte[]?> HandleTransportAsync(MeterFrame frame, ConnectionContext context)
        {
            ushort requestSeq = BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(9, 2));

            switch (context.CurrentState)
            {
                case ConnectionContext.TransportState.WaitForReporting:
                    if ((frame.DecryptedData[8] & 0x1F) != 0x01)
                    {
                        _logger.LogWarning(
                            "First transport frame is not a reporting frame. Function=0x{Function:X2}",
                            frame.DecryptedData[8]);
                        context.CurrentState = ConnectionContext.TransportState.EndConnection;
                        return await HandleTransportAsync(frame, context);
                    }

                    await ProcessTelemetryObject(frame, context.Device.Id, context);

                    bool hasMoreData = (frame.DecryptedData[8] & 0x40) != 0;
                    if (hasMoreData)
                    {
                        return _protocolBuilder.BuildContinueFrameResponse(
                            frame.SessionId,
                            frame.Mid,
                            context.NextServerFrameNumber(),
                            requestSeq);
                    }

                    context.CurrentState = ConnectionContext.TransportState.ReportingComplete;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.ReportingComplete:
                    if (context.Device != null && context.Device.HasPendingCommands)
                    {
                        var packet = await SendPendingCommands(frame, context);
                        if (packet != null)
                        {
                            return packet;
                        }
                    }

                    context.CurrentState = ConnectionContext.TransportState.FirmwareUpgrading;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.SendingCommand:
                    _logger.LogInformation("Processing active Uplink response command confirmation for Meter {Serial}", context.MeterId);

                    await ProcessCommandResponseObject(frame, context.Device.Id, context);

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                        var checks = await db.DeviceCommandLogs.AnyAsync(x => x.DeviceId == context.Device.Id && x.Status == DeviceCommandStatus.Pending);
                        if (checks)
                        {
                            context.CurrentState = ConnectionContext.TransportState.ReportingComplete;
                            return await HandleTransportAsync(frame, context);
                        }
                    }

                    if (context.Device != null) context.Device.HasPendingCommands = false;
                    context.CurrentState = ConnectionContext.TransportState.FirmwareUpgrading;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.FirmwareUpgrading:
                    _logger.LogInformation("Processing Firmware Upgrading state for Session: {SessionId}", context.SessionId);
                    var data = await HandleFirmwareUpgrade(frame, context);
                    if (data == null)
                    {
                        context.CurrentState = ConnectionContext.TransportState.EndConnection;
                        return await HandleTransportAsync(frame, context);
                    }
                    else return data;

                case ConnectionContext.TransportState.EndConnection:
                    uint sessionIdToByte = context.SessionId ?? frame.SessionId;
                    return _protocolBuilder.BuildEndFrameResponse(
                        sessionIdToByte,
                        frame.Mid,
                        context.NextServerFrameNumber(),
                        0x00);

                default:
                    return null;
            }
        }

        private async Task<byte[]?> HandleFirmwareUpgrade(MeterFrame frame, ConnectionContext context)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                // 1. جستجوی درخواست ارتقای فعال برای این شماره کنتور
                var activeUpgradeRequests = await db.FirmwareUpgradeRequests
                    .Where(r => r.MeterId == context.MeterId &&
                                r.State != UpgradeState.Completed &&
                                r.State != UpgradeState.Failed)
                    .OrderByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.Id)
                    .Take(2)
                    .ToListAsync();
                var upgradeRequest = activeUpgradeRequests.FirstOrDefault();

                if (upgradeRequest == null)
                {
                    var knownRequests = await db.FirmwareUpgradeRequests
                        .Where(r => r.MeterId == context.MeterId)
                        .OrderByDescending(r => r.CreatedAt)
                        .Select(r => new { r.Id, r.State })
                        .Take(5)
                        .ToListAsync();
                    var connection = db.Database.GetDbConnection();
                    string knownRequestSummary = knownRequests.Count == 0
                        ? "none"
                        : string.Join(", ", knownRequests.Select(r => $"{r.Id}:{r.State}"));

                    _logger.LogWarning(
                        "[FOTA] No active request found for Meter {MeterId}. Database={Database}, DataSource={DataSource}, KnownRequests={KnownRequests}.",
                        context.MeterId,
                        connection.Database,
                        connection.DataSource,
                        knownRequestSummary);
                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                    return await HandleTransportAsync(frame, context);
                }

                if (activeUpgradeRequests.Count > 1)
                {
                    _logger.LogWarning(
                        "[FOTA] Multiple active upgrade requests exist for Meter {MeterId}. Using newest RequestId {RequestId}.",
                        context.MeterId,
                        upgradeRequest.Id);
                }

                _logger.LogInformation(
                    "[FOTA] Processing RequestId {RequestId} in State {State} for Meter {MeterId}.",
                    upgradeRequest.Id,
                    upgradeRequest.State,
                    context.MeterId);

                switch (upgradeRequest.State)
                {
                    case UpgradeState.Idle:
                        {
                            // Step 1: ارسال درخواست ارتقا به کنتور (430CH)
                            _logger.LogInformation("[FOTA] Step 1: Initiating Firmware Upgrade (430CH) for Meter: {MeterId}", context.MeterId);

                            string currentVersion = context.Device?.FirmwareVersion ?? "02605302";
                            ushort currentReqSeq = context.SequenceNumber;

                            byte[] responseBytes = _protocolBuilder.BuildWriteFirmwareRequest(
                                context.SessionId,
                                frame.Mid,
                                context.NextServerFrameNumber(),
                                currentReqSeq,
                                currentVersion,
                                upgradeRequest.TargetVersion
                            );

                            upgradeRequest.State = UpgradeState.RequestInitiated;
                            upgradeRequest.LastUpdatedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync();

                            return responseBytes;
                        }

                    case UpgradeState.RequestInitiated:
                        {
                            // Step 1.5: دریافت تایید اولیه از کنتور (4304H)
                            _logger.LogInformation("[FOTA] Step 1.5: Validating Upgrade Acceptance (4304H) from Meter: {MeterId}", context.MeterId);

                            if (!FotaPayloadParser.TryFindReportedObject(
                                frame.DecryptedData,
                                FirmwareObjectIds.UpgradeStatus,
                                out byte[] statusData))
                            {
                                _logger.LogInformation(
                                    "[FOTA] No 4304H status was included in the initial report. Re-sending 430CH to Meter {MeterId}.",
                                    context.MeterId);

                                string currentVersion = context.Device?.FirmwareVersion ?? "02605302";
                                return _protocolBuilder.BuildWriteFirmwareRequest(
                                    context.SessionId,
                                    frame.Mid,
                                    context.NextServerFrameNumber(),
                                    context.SequenceNumber,
                                    currentVersion,
                                    upgradeRequest.TargetVersion);
                            }

                            if (statusData.Length != 4)
                            {
                                _logger.LogWarning(
                                    "[FOTA] Invalid 4304H payload length {Length}; expected 4 bytes.",
                                    statusData.Length);
                                return null;
                            }

                            byte meterStatus = statusData[0];
                            if (meterStatus is not (0x01 or 0x02))
                            {
                                byte failureReason = statusData[1];
                                _logger.LogError(
                                    "[FOTA] Meter rejected upgrade request. Status: 0x{Status:X2}, Failure: 0x{Failure:X2}",
                                    meterStatus,
                                    failureReason);
                                upgradeRequest.State = UpgradeState.Failed;
                                upgradeRequest.LastErrorMessage =
                                    $"Upgrade rejected by terminal. Status: 0x{meterStatus:X2}, failure: 0x{failureReason:X2}.";
                                await db.SaveChangesAsync();

                                context.CurrentState = ConnectionContext.TransportState.EndConnection;
                                return await HandleTransportAsync(frame, context);
                            }

                            // Step 2: ارسال مشخصات فریم‌ور (4305H)
                            _logger.LogInformation("[FOTA] Step 2: Sending Firmware Info (4305H) to Meter: {MeterId}", context.MeterId);

                            ushort currentReqSeq = BinaryPrimitives.ReadUInt16BigEndian(
                                frame.DecryptedData.AsSpan(9, 2));
                            byte[] responseBytes = _protocolBuilder.BuildFirmwareInfo(
                                context.SessionId,
                                frame.Mid,
                                context.NextServerFrameNumber(),
                                currentReqSeq,
                                upgradeRequest.TargetVersion,
                                upgradeRequest.FileSize,
                                upgradeRequest.FileCrc32
                            );

                            upgradeRequest.State = UpgradeState.InfoSent;
                            upgradeRequest.LastUpdatedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync();

                            return responseBytes;
                        }

                    case UpgradeState.InfoSent:
                    case UpgradeState.SegmentRequested:
                    case UpgradeState.DataTransferring:
                    case UpgradeState.WaitingForStatus:
                        {
                            if (FotaPayloadParser.TryFindReportedObject(
                                frame.DecryptedData,
                                FirmwareObjectIds.Segmentation,
                                out byte[] segmentParams))
                            {
                                if (segmentParams.Length != 8)
                                {
                                    _logger.LogWarning(
                                        "[FOTA] Invalid 4306H payload length {Length}; expected 8 bytes.",
                                        segmentParams.Length);
                                    return null;
                                }

                                ushort incomingReqSeq = BinaryPrimitives.ReadUInt16BigEndian(
                                    frame.DecryptedData.AsSpan(9, 2));
                                uint requestedOffsetValue = BinaryPrimitives.ReadUInt32BigEndian(segmentParams.AsSpan(0, 4));
                                uint requestedLengthValue = BinaryPrimitives.ReadUInt32BigEndian(segmentParams.AsSpan(4, 4));

                                if (requestedOffsetValue > int.MaxValue ||
                                    requestedLengthValue == 0 ||
                                    requestedLengthValue > int.MaxValue)
                                {
                                    upgradeRequest.State = UpgradeState.Failed;
                                    upgradeRequest.LastErrorMessage = "Terminal requested invalid firmware segment parameters.";
                                    await db.SaveChangesAsync();

                                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                                    return await HandleTransportAsync(frame, context);
                                }

                                int requestedOffset = (int)requestedOffsetValue;
                                int requestedLength = (int)requestedLengthValue;
                                int maximumChunkSize = Math.Max(1, upgradeRequest.ChunkSize);

                                if (requestedOffset >= upgradeRequest.FileSize || requestedLength > maximumChunkSize)
                                {
                                    upgradeRequest.State = UpgradeState.Failed;
                                    upgradeRequest.LastErrorMessage =
                                        $"Invalid segment request. Offset: {requestedOffset}, length: {requestedLength}, max: {maximumChunkSize}.";
                                    await db.SaveChangesAsync();

                                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                                    return await HandleTransportAsync(frame, context);
                                }

                                _logger.LogInformation("[FOTA Stream] Meter: {MeterId} requested Offset: {Offset}, Length: {Length}",
                                    context.MeterId, requestedOffset, requestedLength);

                                // خواندن قطعه مستقیم از دیسک سخت با استفاده از FilePath موجود در UpgradeRequest
                                byte[] chunkData;
                                try
                                {
                                    var storageService = scope.ServiceProvider.GetRequiredService<IFirmwareStorageService>();
                                    chunkData = await storageService.ReadChunkAsync(upgradeRequest.FilePath, requestedOffset, requestedLength);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[FOTA Stream] Failed to read chunk from disk. Path: {FilePath}", upgradeRequest.FilePath);
                                    upgradeRequest.State = UpgradeState.Failed;
                                    upgradeRequest.LastErrorMessage = "IO Error reading firmware file from disk.";
                                    await db.SaveChangesAsync();

                                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                                    return await HandleTransportAsync(frame, context);
                                }

                                if (chunkData.Length == 0)
                                {
                                    _logger.LogError("[FOTA Stream] Read 0 bytes. Offset out of bounds or file corrupted.");
                                    upgradeRequest.State = UpgradeState.Failed;
                                    upgradeRequest.LastErrorMessage = "Read zero bytes from disk.";
                                    await db.SaveChangesAsync();

                                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                                    return await HandleTransportAsync(frame, context);
                                }

                                // به‌روزرسانی وضعیت در دیتابیس
                                var chunkStartedAt = DateTime.UtcNow;
                                var chunkLog = new FirmwareChunkLog
                                {
                                    FirmwareUpgradeRequestId = upgradeRequest.Id,
                                    DeviceId = context.Device?.Id ?? 0,
                                    SessionId = context.SessionId ?? 0,
                                    Mid = frame.Mid,
                                    RequestSequence = incomingReqSeq,
                                    Offset = requestedOffset,
                                    Length = chunkData.Length,
                                    Sha256 = Convert.ToHexString(SHA256.HashData(chunkData)),
                                    Result = FirmwareChunkResult.Sent,
                                    StartedAt = chunkStartedAt,
                                    FinishedAt = DateTime.UtcNow
                                };
                                db.FirmwareChunkLogs.Add(chunkLog);

                                upgradeRequest.CurrentOffset = Math.Max(
                                    upgradeRequest.CurrentOffset,
                                    requestedOffset + chunkData.Length);
                                upgradeRequest.State = (upgradeRequest.CurrentOffset >= upgradeRequest.FileSize)
                                    ? UpgradeState.WaitingForStatus
                                    : UpgradeState.DataTransferring;

                                upgradeRequest.LastUpdatedAt = DateTime.UtcNow;
                                await db.SaveChangesAsync();

                                // ساخت و ارسال فریم 4307H با چانک خوانده‌شده
                                return _protocolBuilder.BuildFirmwareChunkResponse(
                                    context.SessionId,
                                    frame.Mid,
                                    context.NextServerFrameNumber(),
                                    incomingReqSeq,
                                    requestedOffset,
                                    chunkData
                                );
                            }

                            if (FotaPayloadParser.TryFindReportedObject(
                                frame.DecryptedData,
                                FirmwareObjectIds.UpgradeStatus,
                                out byte[] statusPayload))
                            {
                                if (statusPayload.Length != 4)
                                {
                                    _logger.LogWarning(
                                        "[FOTA] Invalid 4304H payload length {Length}; expected 4 bytes.",
                                        statusPayload.Length);
                                    return null;
                                }

                                byte status = statusPayload[0];
                                byte failureReason = statusPayload[1];
                                _logger.LogInformation(
                                    "[FOTA] Upgrade status received. Status: 0x{Status:X2}, Failure: 0x{Failure:X2}",
                                    status,
                                    failureReason);

                                if (status == 0x03)
                                {
                                    upgradeRequest.State = UpgradeState.WaitingForStatus;
                                    upgradeRequest.LastUpdatedAt = DateTime.UtcNow;
                                    await db.SaveChangesAsync();

                                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                                    return await HandleTransportAsync(frame, context);
                                }

                                bool isSuccess = status == 0x05;
                                bool isFailure = status is 0x00 or 0x04 or 0x06;
                                if (!isSuccess && !isFailure)
                                {
                                    _logger.LogWarning(
                                        "[FOTA] Status 0x{Status:X2} is not final; waiting for the next terminal report.",
                                        status);
                                    return null;
                                }

                                var firmwareVersionId = await db.FirmwareVersions
                                    .Where(x => x.VersionString == upgradeRequest.TargetVersion)
                                    .Select(x => x.Id)
                                    .FirstOrDefaultAsync();

                                var log = new FirmwareUpgradeLog
                                {
                                    DeviceId = context.Device?.Id ?? 0,
                                    FirmwareVersionId = firmwareVersionId == 0
                                        ? null
                                        : firmwareVersionId,
                                    LastOffsetSent = upgradeRequest.CurrentOffset,
                                    FinishedAt = DateTime.UtcNow,
                                    ExecutionDate = DateTime.UtcNow,
                                    StartedAt = upgradeRequest.CreatedAt,
                                    Status = isSuccess
                                        ? FirmwareUpgradeStatus.Completed
                                        : FirmwareUpgradeStatus.Failed,
                                    IsSuccess = isSuccess
                                };

                                if (isSuccess)
                                {
                                    upgradeRequest.State = UpgradeState.Completed;
                                    upgradeRequest.LastErrorMessage = null;
                                    log.Description = "Firmware installation completed successfully (4304H status 0x05).";
                                }
                                else
                                {
                                    string failureDescription = DescribeFirmwareFailure(status, failureReason);
                                    upgradeRequest.State = UpgradeState.Failed;
                                    upgradeRequest.LastErrorMessage = failureDescription;
                                    log.ErrorMessage = failureDescription;
                                    log.Description = failureDescription;
                                }

                                if (context.Device != null)
                                {
                                    var device = await db.Devices.FindAsync(context.Device.Id);
                                    if (device != null)
                                    {
                                        device.IsUpgradePending = false;
                                        if (isSuccess)
                                        {
                                            var firmwareUpdatedAt = Utils.DateTimeToInstant(DateTime.UtcNow);
                                            device.FirmwareVersion = upgradeRequest.TargetVersion;
                                            device.LastFirmwareUpdateAt = firmwareUpdatedAt;
                                            context.Device.LastFirmwareUpdateAt = firmwareUpdatedAt;
                                        }
                                    }

                                    context.Device.IsUpgradePending = false;
                                    if (isSuccess)
                                    {
                                        context.Device.FirmwareVersion = upgradeRequest.TargetVersion;
                                    }
                                }

                                upgradeRequest.LastUpdatedAt = DateTime.UtcNow;

                                if (firmwareVersionId == 0)
                                {
                                    _logger.LogWarning(
                                        "[FOTA] Firmware version entity not found for {Version}; recording upgrade audit without version reference.",
                                        upgradeRequest.TargetVersion);
                                }

                                // The upgrade request contains the authoritative target version and file metadata.
                                // Keep the final audit record even when the optional catalog entry is missing.
                                await db.FirmwareUpgradeLogs.AddAsync(log);
                                await db.SaveChangesAsync();

                                context.CurrentState = ConnectionContext.TransportState.EndConnection;
                                return await HandleTransportAsync(frame, context);
                            }

                            return null;
                        }

                    default:
                        context.CurrentState = ConnectionContext.TransportState.EndConnection;
                        return await HandleTransportAsync(frame, context);
                }
            }
        }
        private async Task<byte[]?> SendPendingCommands(MeterFrame frame, ConnectionContext context)
        {
            byte[]? result = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                var pendingCommand = await db.DeviceCommandLogs
                    .Where(x => x.DeviceId == context.Device.Id && x.Status == DeviceCommandStatus.Pending)
                    .OrderBy(x => x.Id)
                    .FirstOrDefaultAsync();

                if (pendingCommand != null)
                {
                    var pendingCommands = await db.DeviceCommandLogs
                        .Where(x => x.DeviceId == context.Device.Id && x.Status == DeviceCommandStatus.Pending && x.FunctionCode == pendingCommand.FunctionCode)
                        .OrderBy(x => x.Id)
                        .Take(5)
                        .ToListAsync();

                    if (pendingCommands.Any())
                    {
                        context.CurrentState = ConnectionContext.TransportState.SendingCommand;

                        foreach (var command in pendingCommands)
                        {
                            command.SequenceNumber = (ushort)context.SequenceNumber;
                            command.SentAt = Utils.DateTimeToInstant(DateTime.UtcNow);
                        }

                        await db.SaveChangesAsync();

                        switch (pendingCommand.FunctionCode)
                        {
                            case ProtocolConstants.FunCodeReadData:
                                return _protocolBuilder.BuildReadCommandRequest(
                                    frame.SessionId,
                                    frame.Mid,
                                    context.NextServerFrameNumber(),
                                    context.SequenceNumber,
                                    pendingCommands);

                            case ProtocolConstants.FunCodeWriteData:
                                return _protocolBuilder.BuildWriteCommandRequest(
                                    frame.SessionId,
                                    frame.Mid,
                                    context.NextServerFrameNumber(),
                                    context.SequenceNumber,
                                    pendingCommands);

                            case 0x07:
                                byte[] bcdTime = (pendingCommand.RequestPayload ?? Array.Empty<byte>())
                                    .Take(6)
                                    .ToArray();
                                byte limit = pendingCommand.RequestPayload is { Length: > 6 }
                                    ? pendingCommand.RequestPayload[6]
                                    : (byte)1;
                                return _protocolBuilder.BuildReadRecordsByTimeRequest(
                                    frame.SessionId,
                                    frame.Mid,
                                    context.NextServerFrameNumber(),
                                    context.SequenceNumber,
                                    (ushort)pendingCommand.CommandId,
                                    bcdTime,
                                    limit);

                            case 0x08:
                                byte countToRead = pendingCommand.RequestPayload != null && pendingCommand.RequestPayload.Length > 0 ? pendingCommand.RequestPayload[0] : (byte)1;
                                return _protocolBuilder.BuildReadRecentRecordsRequest(
                                    frame.SessionId,
                                    frame.Mid,
                                    context.NextServerFrameNumber(),
                                    context.SequenceNumber,
                                    (ushort)pendingCommand.CommandId,
                                    countToRead);
                        }
                    }
                }
                else
                {
                    context.Device.HasPendingCommands = false;
                }
            }

            return result;
        }

        private async Task ProcessCommandResponseObject(MeterFrame frame, long deviceId, ConnectionContext context)
        {
            try
            {
                if (frame.DecryptedData == null || frame.DecryptedData.Length < 12) return;

                int offset = 8;
                byte responseFunctionCode = frame.DecryptedData[offset++];
                ushort responseSeq = BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(offset, 2));
                offset += 2;

                using (var scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                    var commandBatch = await db.DeviceCommandLogs
                        .Where(x => x.DeviceId == deviceId && x.SequenceNumber == responseSeq && x.Status == DeviceCommandStatus.Pending)
                        .ToListAsync();

                    if (!commandBatch.Any())
                    {
                        _logger.LogWarning("No pending command batch found for SequenceNumber: {Seq}", responseSeq);
                        return;
                    }

                    var nowInstant = NodaTime.Instant.FromDateTimeUtc(DateTime.UtcNow);

                    foreach (var commandLog in commandBatch)
                    {
                        commandLog.ResponsePayload = frame.DecryptedData.ToArray();
                        commandLog.RespondedAt = nowInstant;
                    }

                    byte objectCount = frame.DecryptedData[offset++];

                    if (objectCount == 0xFF || objectCount == 0)
                    {
                        foreach (var cmd in commandBatch)
                        {
                            cmd.Status = DeviceCommandStatus.Failed;
                            cmd.ExecutionResult = "Failed: Terminal does not recognize or rejected these read data object IDs.";
                        }
                    }
                    else
                    {
                        switch (responseFunctionCode)
                        {
                            case 0x84:
                                await HandleReadObjectsBatch(frame.DecryptedData, objectCount, commandBatch, db, context.Device);
                                break;

                            case 0x85:
                                await HandleWriteObjectsBatch(frame.DecryptedData, objectCount, commandBatch, db, deviceId);
                                break;

                            case 0x87:
                            case 0x88:
                                await HandleRecordResponseBatch(
                                    frame.DecryptedData,
                                    responseFunctionCode,
                                    commandBatch,
                                    objectCount,
                                    deviceId,
                                    db);
                                break;

                            case ProtocolConstants.FunCodeNegativeReadRecordsByTime:
                            case ProtocolConstants.FunCodeNegativeReadRecentRecords:
                                HandleNegativeRecordResponse(
                                    frame.DecryptedData,
                                    responseFunctionCode,
                                    commandBatch);
                                break;

                            default:
                                foreach (var cmd in commandBatch)
                                {
                                    cmd.Status = DeviceCommandStatus.Failed;
                                    cmd.ExecutionResult = $"Critical Mismatch: Unknown function response code 0x{responseFunctionCode:X2}";
                                }
                                break;
                        }
                    }
                    context.SequenceNumber++;
                    await db.SaveChangesAsync();
                    _logger.LogInformation("Batch commands for ReqID {Seq} processed and synchronized successfully.", responseSeq);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error decoding downstream response verification byte loop.");
            }
        }

        private async Task HandleReadObjectsBatch(byte[] data, byte objectCount, List<DeviceCommandLog> batch, WaterMeterDbContext db, Device? device)
        {
            int offset = 12;

            for (int i = 0; i < objectCount; i++)
            {
                if (offset + 2 > data.Length) break;
                ushort objId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                offset += 2;

                int len = GetObjectLength(objId);
                if (offset + len > data.Length) break;

                byte[] objectContent = new byte[len];
                Array.Copy(data, offset, objectContent, 0, len);

                var matchedCmd = batch.FirstOrDefault(x => x.CommandId == objId);
                if (matchedCmd != null)
                {
                    matchedCmd.Status = DeviceCommandStatus.Succeeded;
                    matchedCmd.ExecutionResult = await ParseAndApplyReadObject(objId, objectContent, device, db);
                }

                offset += len;
            }
        }

        private async Task HandleWriteObjectsBatch(byte[] data, byte objectCount, List<DeviceCommandLog> batch, WaterMeterDbContext db, long deviceId)
        {
            int offset = 12;

            for (int i = 0; i < objectCount; i++)
            {
                if (offset + 3 > data.Length) break;
                ushort objId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                byte writeResult = data[offset + 2];
                offset += 3;

                var matchedCmd = batch.FirstOrDefault(x => x.CommandId == objId);
                if (matchedCmd != null)
                {
                    if (writeResult == 0)
                    {
                        matchedCmd.Status = DeviceCommandStatus.Succeeded;
                        string syncSummary = "Success: Configuration applied to terminal. ";
                        if (matchedCmd.RequestPayload != null && matchedCmd.RequestPayload.Length > 0)
                        {
                            syncSummary += await ParseAndSyncWrittenConfig(objId, matchedCmd.RequestPayload, db, deviceId);
                        }
                        matchedCmd.ExecutionResult = syncSummary;
                    }
                    else
                    {
                        matchedCmd.Status = DeviceCommandStatus.Failed;
                        matchedCmd.ExecutionResult = writeResult == 2
                            ? "Failed (Code 2): Permission mismatch. This object is restricted or read-only."
                            : (writeResult == 1 ? "Failed (Code 1): Value out of range limit." : $"Failed with code: {writeResult}");
                    }
                }
            }
        }

        private async Task HandleRecordResponseBatch(
            byte[] data,
            byte functionCode,
            List<DeviceCommandLog> batch,
            byte objectCount,
            long deviceId,
            WaterMeterDbContext db)
        {
            var mainCmd = batch.First();

            if (objectCount == 0xFF || data.Length < 15)
            {
                mainCmd.Status = DeviceCommandStatus.Failed;
                mainCmd.ExecutionResult = "Failed: Terminal does not recognize requested history file or record parameters.";
            }
            else
            {
                ushort recordObjectId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12, 2));
                byte recordCount = data[14];
                if (recordCount == 0xFF)
                {
                    mainCmd.Status = DeviceCommandStatus.Failed;
                    mainCmd.ExecutionResult = "Failed: Terminal rejected the requested record range.";
                    return;
                }

                int offset = 15;
                int recordLength = GetRecordLength(recordObjectId, data.Length - offset, recordCount);
                if (recordCount > 0 && recordLength <= 0)
                {
                    mainCmd.Status = DeviceCommandStatus.Failed;
                    mainCmd.ExecutionResult = $"Failed: Unknown record object 0x{recordObjectId:X4}.";
                    return;
                }

                int stored = 0;
                for (int i = 0; i < recordCount; i++)
                {
                    if (offset + recordLength > data.Length)
                        break;

                    byte[] raw = data.AsSpan(offset, recordLength).ToArray();
                    Instant? recordTime = TryGetRecordTime(recordObjectId, raw);
                    await db.WaterUsageRecords.AddAsync(new WaterUsageRecord
                    {
                        DeviceId = deviceId,
                        RecordObjectId = recordObjectId,
                        RecordIndex = i,
                        ReceivedAt = NodaTime.Instant.FromDateTimeUtc(DateTime.UtcNow),
                        RecordTime = recordTime,
                        RawData = raw
                    });
                    offset += recordLength;
                    stored++;
                }

                mainCmd.Status = DeviceCommandStatus.Succeeded;
                mainCmd.ExecutionResult = functionCode == 0x87
                    ? $"Record Read Success: Retrieved and stored {stored}/{recordCount} records starting from requested BCD timestamp."
                    : $"Recent Log Success: Stored {stored}/{recordCount} historical log data structures from flash.";
            }
        }

        private static void HandleNegativeRecordResponse(
            byte[] data,
            byte responseFunctionCode,
            List<DeviceCommandLog> batch)
        {
            ushort? rejectedObjectId = null;
            byte? rejectionCode = null;

            // Negative record response layout after SID/frame/data length:
            // [Function][Request sequence][Object ID][Result code]
            if (data.Length >= 14)
            {
                rejectedObjectId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(11, 2));
                rejectionCode = data[13];
            }

            string operation = responseFunctionCode == ProtocolConstants.FunCodeNegativeReadRecordsByTime
                ? "Read by start time"
                : "Read recent records";
            string reason = rejectionCode switch
            {
                0x01 => "Terminal rejected the requested record range or start time.",
                0x02 => "Terminal does not support the requested record object.",
                0x03 => "Requested record count is outside the terminal limit.",
                _ => rejectionCode.HasValue
                    ? $"Terminal returned rejection code 0x{rejectionCode.Value:X2}."
                    : "Terminal returned a negative record response without a result code."
            };

            foreach (var command in batch)
            {
                if (rejectedObjectId.HasValue && command.CommandId != rejectedObjectId.Value)
                    continue;

                command.Status = DeviceCommandStatus.Failed;
                command.ExecutionResult = $"{operation} rejected. {reason}";
            }
        }

        private static int GetRecordLength(ushort recordObjectId, int remaining, int recordCount)
        {
            if (recordCount == 0)
                return 0;

            return recordObjectId switch
            {
                0xB06B => 25,
                0xB033 or 0xB034 => 250,
                0xB035 or 0xB036 => 97,
                _ when remaining % recordCount == 0 => remaining / recordCount,
                _ => 0
            };
        }

        private static NodaTime.Instant? TryGetRecordTime(ushort recordObjectId, byte[] raw)
        {
            try
            {
                if (recordObjectId == 0xB06B && raw.Length >= 5)
                {
                    var time = Utils.ParseBcdDateTimeMinute(raw.AsSpan(0, 5));
                    return Utils.DateTimeToInstant(time);
                }

                if ((recordObjectId == 0xB033 || recordObjectId == 0xB034) &&
                    raw.Length >= 2)
                {
                    int year = 2000 + Utils.BcdToByte(raw[0]);
                    int month = Utils.BcdToByte(raw[1]);
                    return Utils.DateTimeToInstant(
                        new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc));
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }

            return null;
        }

        private int GetObjectLength(ushort objId)
        {
            return objId switch
            {
                0xB055 or 0x70DA => 1,
                0x70B6 or 0x200E => 2,
                0x70BE or 0xB061 => 3,
                0x4211 => 4,
                0x0002 => 6,
                0x70C0 => 7,
                0x0016 or 0xA013 => 15,
                0x0067 => 16,
                0x2007 => 18,
                0x200A => 20,
                0x70F4 => 24,
                0x70F5 => 20,
                0x2012 => 32,
                _ => 0
            };
        }

        private async Task<string> ParseAndApplyReadObject(ushort objId, byte[] content, Device? device, WaterMeterDbContext db)
        {
            if (content == null || content.Length == 0)
                return $"Object 0x{objId:X4}: Empty payload.";

            switch (objId)
            {
                case 0x0002:
                    DateTime dt = Utils.ParseBcdDateTime(content);
                    if (device != null)
                    {
                        device.LastSeenAt = NodaTime.Instant.FromDateTimeUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                    }
                    return $"[Clock]: {dt:yyyy-MM-dd HH:mm:ss}; ";

                case 0x4211:
                    string ver = $"{content[0]:X2}.{content[1]:X2}.{content[2]:X2}.{content[3]:X2}";
                    if (device != null) device.FirmwareVersion = ver;
                    return $"[Main Controller Version]: {ver}; ";

                case 0x0067:
                    string keyHex = BitConverter.ToString(content).Replace("-", "");
                    return $"[Customer Key]: {keyHex}; ";

                case 0x70B6:
                    {
                        ushort configBits = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan());
                        string pumpBehavior = $"[Valve Action Config]: 0x{configBits:X4} (";
                        pumpBehavior += (configBits & 0x01) != 0 ? "IsolationDoorOpen:PumpOff, " : "IsolationDoorOpen:NoAction, ";
                        pumpBehavior += (configBits & 0x02) != 0 ? "OverLimitWater:PumpOff, " : "OverLimitWater:NoAction, ";
                        pumpBehavior += (configBits & 0x04) != 0 ? "PowerCableDisconnect:PumpOff, " : "PowerCableDisconnect:NoAction, ";
                        pumpBehavior += (configBits & 0x08) != 0 ? "ControlCableDisconnect:PumpOff, " : "ControlCableDisconnect:NoAction, ";
                        pumpBehavior += (configBits & 0x10) != 0 ? "LowBalance:PumpOff, " : "LowBalance:NoAction, ";
                        pumpBehavior += (configBits & 0x20) != 0 ? "MeterDisconnect:PumpOff, " : "MeterDisconnect:NoAction, ";
                        pumpBehavior += (configBits & 0x40) != 0 ? "MagneticInterference:PumpOff" : "MagneticInterference:NoAction";
                        pumpBehavior += "); ";
                        return pumpBehavior;
                    }

                case 0x70F4:
                    string cyclesTime = "[Cycle Start/End Times]: ";
                    for (int c = 0; c < 4; c++)
                    {
                        int baseIdx = c * 6;
                        cyclesTime += $"Cycle {c + 1}(Start:{content[baseIdx]:X2}{content[baseIdx + 1]:X2}h{content[baseIdx + 2]:X2}, ";
                        cyclesTime += $"End:{content[baseIdx + 3]:X2}{content[baseIdx + 4]:X2}h{content[baseIdx + 5]:X2}); ";
                    }
                    return cyclesTime;

                case 0x70F5:
                    {
                        string cyclesAllowance = "[Cycle Allowed Usage]: ";
                        for (int c = 0; c < 4; c++)
                        {
                            int baseIdx = c * 5;
                            long allowedLiters10 = Utils.ReadUint40BigEndian(content.AsSpan().Slice(baseIdx, 5));
                            double allowedM3 = (allowedLiters10 * 10.0) / 1000.0;
                            cyclesAllowance += $"Cycle {c + 1}: {allowedM3} m³; ";
                        }
                        return cyclesAllowance;
                    }

                case 0x70BE:
                    string prodDate = $"14{content[0]:X2}-{content[1]:X2}-{content[2]:X2}";
                    return $"[Production Date]: {prodDate}; ";

                case 0xB055:
                    byte interval = content[0];
                    return $"[Freeze Interval]: {interval} minutes; ";

                case 0xA102:
                    byte lenByte = content[0];
                    bool isAscii = (lenByte & 0x80) != 0;
                    int actualLen = lenByte & 0x7F;
                    string serialContent = "";

                    if (isAscii)
                    {
                        serialContent = System.Text.Encoding.ASCII.GetString(content, 1, Math.Min(actualLen, 16)).Trim('\0', ' ');
                    }
                    else
                    {
                        serialContent = BitConverter.ToString(content, 1, 16).Replace("-", "");
                    }
                    return $"[Meter Production Serial]: {serialContent}; ";

                case 0xB061:
                    byte intervalCode = content[0];
                    int intervalMinutes = intervalCode * 10;
                    string startTimeHhmm = $"{content[1]:X2}:{content[2]:X2}";
                    return $"[Scheduled Upload]: Every {intervalMinutes} min from {startTimeHhmm}; ";

                case 0x70C0:
                    string dstStart = $"{content[0]:X2}-{content[1]:X2}h{content[2]:X2}";
                    string dstEnd = $"{content[3]:X2}-{content[4]:X2}h{content[5]:X2}";
                    sbyte adjustment10Min = (sbyte)content[6];
                    int adjMinutes = adjustment10Min * 10;
                    return $"[DST Config]: Start:{dstStart}, End:{dstEnd}, Adjust:{adjMinutes} min; ";

                case 0x70DA:
                    {
                        byte pumpState = content[0];
                        string stateLabel = pumpState == 0 ? "Exit Lock" : (pumpState == 1 ? "Lock Open (Valve Closed)" : "Lock Closed (Valve Open)");
                        await SyncDatabaseConfigSnapshot(0x70DA, content, db, device?.Id ?? 0);
                        return $"[Valve Position]: {stateLabel}; ";
                    }

                case 0x200E:
                    {
                        ushort peakCoef = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan());
                        return $"[Staggered Peak Interval]: {peakCoef}; ";
                    }

                case 0x0016:
                    string imei = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    if (device != null) device.DeviceUid = imei;
                    return $"[Modem IMEI]: {imei}; ";

                case 0xA013:
                    string imsi = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    return $"[SIM IMSI]: {imsi}; ";

                case 0x200A:
                    string iccid = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    if (device != null) device.CommunicationIccid = iccid;
                    return $"[SIM ICCID]: {iccid}; ";

                case 0x2012:
                    string apn = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    return $"[APN Name]: {apn}; ";

                case 0x2007:
                    {
                        string ipAddress = System.Text.Encoding.ASCII.GetString(content, 0, 16).Trim('\0', ' ');
                        ushort portServer = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan().Slice(16, 2));
                        return $"[Central Server Destination]: {ipAddress}:{portServer}; ";
                    }

                default:
                    return $"[Unknown Object 0x{objId:X4}]: HexRaw({BitConverter.ToString(content)}); ";
            }
        }

        private async Task<string> ParseAndSyncWrittenConfig(ushort objId, byte[] requestPayload, WaterMeterDbContext db, long deviceId)
        {
            var payloadSpan = requestPayload;
            var device = await db.Devices.FindAsync(deviceId);

            switch (objId)
            {
                case 0x0002:
                    if (requestPayload.Length >= 6)
                    {
                        DateTime writtenTime = Utils.ParseBcdDateTime(requestPayload);
                        if (device != null)
                        {
                            device.LastSeenAt = NodaTime.Instant.FromDateTimeUtc(DateTime.SpecifyKind(writtenTime, DateTimeKind.Utc));
                        }
                        return $"[Sync] System clock updated to {writtenTime:yyyy-MM-dd HH:mm:ss}.";
                    }
                    break;

                case 0x0067:
                    return $"[Sync] Customer security key updated in terminal database.";

                case 0xB055:
                    byte intervalMinutes = requestPayload[0];
                    return $"[Sync] Daily freeze interval synchronized to {intervalMinutes} minutes.";

                case 0x70DA:
                    byte newPumpState = requestPayload[0];
                    var alarmSnapshot = await db.DeviceAlarmSnapshots.FindAsync(deviceId);
                    if (alarmSnapshot != null)
                    {
                        alarmSnapshot.PumpOff = (newPumpState == 1);
                        alarmSnapshot.UpdatedAt = NodaTime.Instant.FromDateTimeUtc(DateTime.UtcNow);
                    }
                    string stateText = newPumpState == 1 ? "Valve Closed (Pump Off)" : "Valve Open (Pump On/Normal)";
                    return $"[Sync] Active Valve State updated to: {stateText}.";

                case 0xB061:
                    if (requestPayload.Length >= 3)
                    {
                        int uploadIntervalMin = requestPayload[0] * 10;
                        string startTime = $"{requestPayload[1]:X2}:{requestPayload[2]:X2}";
                        return $"[Sync] High-frequency upload configured for every {uploadIntervalMin} min starting at {startTime}.";
                    }
                    break;

                case 0x70B6:
                    if (requestPayload.Length >= 2)
                    {
                        ushort bitmask = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                        return $"[Sync] Hardware safety valve bitmask synchronized to 0x{bitmask:X4}.";
                    }
                    break;

                case 0x70C0:
                    return $"[Sync] Daylight Saving Time parameter block updated.";

                case 0x200E:
                    if (requestPayload.Length >= 2)
                    {
                        ushort shiftCoef = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                        return $"[Sync] Staggered peak shifting coefficient synchronized to {shiftCoef}.";
                    }
                    break;

                case 0x2012:
                    string newApn = System.Text.Encoding.ASCII.GetString(requestPayload).Trim('\0', ' ');
                    return $"[Sync] Access Point Name (APN) synchronized to '{newApn}'.";

                case 0x2007:
                    if (requestPayload.Length >= 18)
                    {
                        string ip = System.Text.Encoding.ASCII.GetString(requestPayload, 0, 16).Trim('\0', ' ');
                        ushort port = BinaryPrimitives.ReadUInt16BigEndian(requestPayload.AsSpan().Slice(16, 2));
                        return $"[Sync] Central Server Destination updated to {ip}:{port}.";
                    }
                    break;
            }

            return $"[Sync] Database synchronized for Object ID 0x{objId:X4}.";
        }

        private async Task SyncDatabaseConfigSnapshot(ushort objId, byte[]? payload, WaterMeterDbContext db, long deviceId)
        {
            if (payload == null || payload.Length == 0) return;

            if (objId == 0x70DA)
            {
                var alarmSnapshot = await db.DeviceAlarmSnapshots.FindAsync(deviceId);
                if (alarmSnapshot != null)
                {
                    alarmSnapshot.PumpOff = (payload[0] == 1);
                    alarmSnapshot.UpdatedAt = NodaTime.Instant.FromDateTimeUtc(DateTime.UtcNow);
                }
            }
        }

        private async Task ProcessTelemetryObject(MeterFrame frame, long deviceId, ConnectionContext context)
        {
            try
            {
                int objCount = frame.DecryptedData[11];
                int currentOffset = 12;

                _logger.LogInformation("Processing packet from Mid {Mid} containing {Count} objects.", frame.Mid, objCount);

                for (int i = 0; i < objCount; i++)
                {
                    if (currentOffset + 3 > frame.DecryptedData.Length) return;

                    ushort objId = BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(currentOffset, 2));
                    currentOffset += 2;
                    int len = frame.DecryptedData[currentOffset++];

                    if (currentOffset + len > frame.DecryptedData.Length) return;

                    var nowInstant = Utils.DateTimeToInstant(DateTime.UtcNow);

                    switch (objId)
                    {
                        case 0x70F2: // Terminal real-time data
                            if (len == 49)
                            {
                                var telemetryData = frame.DecryptedData.AsSpan(currentOffset, 49);
                                var record = TelemetryParser.Parse70F2(telemetryData, deviceId);

                                // Log behavior: Push to non-blocking telemetry batch buffer
                                await _telemetryBuffer.PushRecordAsync(record);

                                // Snapshot update behavior
                                if (context.Device != null)
                                {
                                    context.Device.LastMainVoltage = record.MainVoltage;
                                    context.Device.LastBackupVoltage = record.BackupVoltage;
                                    context.Device.LastSignalStrength = record.SignalStrength;
                                    context.Device.LastPositiveCumulative = record.PositiveCumulative;
                                    context.Device.LastReverseCumulative = record.ReverseCumulative;
                                    context.Device.LastInstantaneousFlow = record.InstantaneousFlow;
                                    context.Device.LastRemainingAmount = record.RemainingAmount;
                                    context.Device.LastPumpRunningTime = record.PumpRunningTime;
                                    context.Device.LastSeenAt = nowInstant;
                                }
                            }
                            currentOffset += len;
                            break;

                        case 0xB064: // Terminal identification parameters
                            currentOffset += len;
                            break;

                        case 0xB05C: // Daily frozen data 
                            if (len == 10)
                            {
                                var frozenData = frame.DecryptedData.AsSpan(currentOffset, 10);
                                long rawPositive = Utils.ReadUint40BigEndian(frozenData.Slice(0, 5));
                                long rawReverse = Utils.ReadUint40BigEndian(frozenData.Slice(5, 5));

                                double positiveM3 = (rawPositive * 10.0) / 1000.0;
                                double reverseM3 = (rawReverse * 10.0) / 1000.0;

                                using (var scope = _scopeFactory.CreateScope())
                                {
                                    var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                                    var frozenLog = new DailyFrozenLog
                                    {
                                        DeviceId = deviceId,
                                        FrozenAt = nowInstant,
                                        ReceivedAt = nowInstant,
                                        PositiveCumulative = positiveM3,
                                        ReverseCumulative = reverseM3
                                    };
                                    await db.DailyFrozenLogs.AddAsync(frozenLog);

                                    var snapshot = await db.DeviceDailyFrozenSnapshots.FindAsync(deviceId);
                                    if (snapshot == null)
                                    {
                                        snapshot = new DeviceDailyFrozenSnapshot { DeviceId = deviceId };
                                        await db.DeviceDailyFrozenSnapshots.AddAsync(snapshot);
                                    }
                                    snapshot.FrozenAt = nowInstant;
                                    snapshot.PositiveCumulative = positiveM3;
                                    snapshot.ReverseCumulative = reverseM3;

                                    await db.SaveChangesAsync();
                                }
                            }
                            currentOffset += len;
                            break;

                        case 0xB070: // Alarm status word 
                            if (len == 3)
                            {
                                byte byte0 = frame.DecryptedData[currentOffset];
                                byte byte1 = frame.DecryptedData[currentOffset + 1];
                                byte byte2 = frame.DecryptedData[currentOffset + 2];

                                using (var scope = _scopeFactory.CreateScope())
                                {
                                    var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                                    var alarmSnap = await db.DeviceAlarmSnapshots.FindAsync(deviceId);
                                    if (alarmSnap == null)
                                    {
                                        alarmSnap = new DeviceAlarmSnapshot { DeviceId = deviceId };
                                        await db.DeviceAlarmSnapshots.AddAsync(alarmSnap);
                                    }

                                    // Byte 0: Physical alarms
                                    alarmSnap.PumpOff = (byte0 & 0x01) != 0;
                                    alarmSnap.MeterRemoved = (byte0 & 0x02) != 0;
                                    alarmSnap.MagneticInterference = (byte0 & 0x04) != 0;
                                    alarmSnap.RelayFault = (byte0 & 0x08) != 0;

                                    // Byte 1: Hydraulic alarms
                                    alarmSnap.EmptyPipe = (byte1 & 0x01) != 0;
                                    alarmSnap.ExcitationAlarm = (byte1 & 0x02) != 0;
                                    alarmSnap.LowSignal = (byte1 & 0x08) != 0;
                                    alarmSnap.MeasurementError = (byte1 & 0x10) != 0;
                                    alarmSnap.Backflow = (byte1 & 0x20) != 0;
                                    alarmSnap.AbnormallyHighFlow = (byte1 & 0x40) != 0;

                                    // Byte 2: System alarms
                                    alarmSnap.StorageError = (byte2 & 0x01) != 0;
                                    alarmSnap.TrafficCollectionError = (byte2 & 0x02) != 0;
                                    alarmSnap.LowBattery = (byte2 & 0x08) != 0;
                                    alarmSnap.PowerLockout = (byte2 & 0x10) != 0;
                                    alarmSnap.ExternalPowerConnected = (byte2 & 0x80) != 0;

                                    alarmSnap.UpdatedAt = nowInstant;

                                    // Log critical events
                                    if (alarmSnap.MeterRemoved)
                                    {
                                        await db.AlarmStatusLogs.AddAsync(new AlarmStatusLog
                                        {
                                            DeviceId = deviceId,
                                            Timestamp = nowInstant,
                                            LogType = "Alarm",
                                            Code = "MeterRemoved",
                                            IsActive = true
                                        });
                                    }
                                    if (alarmSnap.LowBattery)
                                    {
                                        await db.AlarmStatusLogs.AddAsync(new AlarmStatusLog
                                        {
                                            DeviceId = deviceId,
                                            Timestamp = nowInstant,
                                            LogType = "Alarm",
                                            Code = "LowBattery",
                                            IsActive = true
                                        });
                                    }

                                    await db.SaveChangesAsync();
                                }
                            }
                            currentOffset += len;
                            break;

                        case 0x70EE: 
                            if (len == 90)
                            {
                                using (var scope = _scopeFactory.CreateScope())
                                {
                                    var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                                    for (int eventIdx = 0; eventIdx < 10; eventIdx++)
                                    {
                                        int eventBaseOffset = currentOffset + (eventIdx * 9);
                                        var eventChunk = frame.DecryptedData.AsSpan(eventBaseOffset, 9);

                                        var eventTimeRaw = Utils.ParseBcdDateTime(eventChunk.Slice(0, 6));
                                        ushort masterEventCode = BinaryPrimitives.ReadUInt16BigEndian(eventChunk.Slice(6, 2));
                                        byte subEventCode = eventChunk[8];

                                        if (masterEventCode != 0)
                                        {
                                            var eventLog = new AlarmStatusLog
                                            {
                                                DeviceId = deviceId,
                                                Timestamp = Utils.DateTimeToInstant(DateTime.SpecifyKind(eventTimeRaw, DateTimeKind.Utc)),
                                                LogType = "Event",
                                                Code = $"0x{masterEventCode:X4}",
                                                IsActive = true,
                                                Description = $"SubCode: 0x{subEventCode:X2}"
                                            };
                                            await db.AlarmStatusLogs.AddAsync(eventLog);
                                        }
                                    }
                                    await db.SaveChangesAsync();
                                }
                            }
                            currentOffset += len;
                            break;

                        default:
                            _logger.LogWarning("Unknown Object ID 0x{Id:X4}. Stopping parse.", objId);
                            return;
                    }
                }

                if (context.Device != null)
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var deviceRegistry = scope.ServiceProvider.GetRequiredService<IDeviceRegistry>();
                        await deviceRegistry.UpdateDeviceAtivityAsync(context.Device);
                        _logger.LogInformation("Device Snapshot values successfully updated for Serial: {Serial}", context.Device.SerialNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error parsing multi-object packet for Mid {Mid}", frame.Mid);
            }
        }
        private static string DescribeFirmwareFailure(byte status, byte failureReason)
        {
            if (status == 0x06)
            {
                return "Firmware installation failed (4304H status 0x06).";
            }

            if (status == 0x00)
            {
                return "Terminal reported that no firmware download is active (4304H status 0x00).";
            }

            string reason = failureReason switch
            {
                0x00 => "Firmware download or installation did not succeed.",
                0x01 => "Connection to the upgrade server failed.",
                0x02 => "Firmware data interaction failed.",
                0x03 => "Firmware verification failed.",
                0x04 => "Writing firmware to flash failed.",
                _ => $"Unknown failure reason 0x{failureReason:X2}."
            };

            return $"Firmware download failed (4304H status 0x04): {reason}";
        }

    }
}
