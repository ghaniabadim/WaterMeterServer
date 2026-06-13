using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Domain.Models;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Infrastructure.Persistence;
using WaterMeterServer.Protocol;

namespace WaterMeterServer.Application.Dispatchers
{
    public class FrameDispatcher
    {
        private readonly ILogger<FrameDispatcher> _logger;
        private readonly IProtocolBuilder _protocolBuilder;
        private readonly ISessionManager _sessionManager;
        private readonly ITelemetryBuffer _telemetryBuffer;
        private readonly ICommandStore _commandStore;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LogQueue _logQueue;
        public FrameDispatcher(
            ILogger<FrameDispatcher> logger,
            IProtocolBuilder protocolBuilder,
            ISessionManager sessionManager,
            ITelemetryBuffer telemetryBuffer,
            ICommandStore commandStore,
            IServiceScopeFactory scopeFactory,
            LogQueue logQueue)
        {
            _logger = logger;
            _protocolBuilder = protocolBuilder;
            _sessionManager = sessionManager;
            _telemetryBuffer = telemetryBuffer;
            _commandStore = commandStore;
            _scopeFactory = scopeFactory;
            _logQueue = logQueue;
        }

        public async Task<byte[]?> DispatchAsync(MeterFrame frame, ConnectionContext context)
        {
            byte[]? data = null;
            if (frame.Type == ProtocolConstants.TypeTransport && string.IsNullOrEmpty(context.MeterId))
            {
                _logger.LogWarning("Unauthorized Transport received before handshake. Session ID: {SessionId}", frame.SessionId);
                data = _protocolBuilder.BuildEndFrameResponse(frame.SessionId, frame.Mid, (ushort)(frame.FrameNo + 1), 0x01);
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
                    data = _protocolBuilder.BuildEndFrameResponse(frame.SessionId, frame.Mid, (ushort)(frame.FrameNo + 1), 0x01);
                }

                data = await HandleTransportAsync(frame, context);
            }

            return data;
        }

        private async Task<byte[]?> HandleHandshakeAsync(MeterFrame frame, ConnectionContext context)
        {
            _logger.LogInformation("Processing handshake for Mid: {Mid}", frame.Mid);

            if (frame.DecryptedData.Length > 23)
            {
                int meterIdLength = frame.DecryptedData[2];
                if (meterIdLength > 0 && meterIdLength <= 34)
                {
                    var meterIdBytes = frame.DecryptedData.AsSpan(3, meterIdLength / 2);
                    var meterId = Utils.BcdToString(meterIdBytes);
                    frame.MeterId = meterId;

                    if (_sessionManager.GetSession(meterId) != null)
                    {
                        _sessionManager.RemoveSession(meterId);
                        _logger.LogInformation("Previous session cleared for Meter: {MeterId}", meterId);
                    }

                    var sessionId = _sessionManager.GenerateSessionId(meterId);
                    context.MeterId = meterId;
                    context.SessionId = sessionId;
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var deviceRegistry = scope.ServiceProvider.GetRequiredService<IDeviceRegistry>();
                        var device = await deviceRegistry.EnsureDeviceExistsAsync(meterId);
                        device.LastSessionId = sessionId;
                        device.LastSeenAt = Utils.DateTimeToInstant(DateTime.Now);
                        await deviceRegistry.UpdateDeviceAtivityAsync(device);
                        context.Device = device;
                    }
                    return _protocolBuilder.BuildHandshakeResponse(frame.Mid, sessionId, frame.DecryptedData, ProtocolConstants.HandshakeSuccess);
                }
            }

            return _protocolBuilder.BuildHandshakeResponse(frame.Mid, 0, frame.DecryptedData, ProtocolConstants.HandshakeFailure);
        }

        private async Task<byte[]?> HandleTransportAsync(MeterFrame frame, ConnectionContext context)
        {
            ushort nextFrameNo = (ushort)(frame.FrameNo + 1);
            ushort requestSeq = BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(9, 2));

            switch (context.CurrentState)
            {
                case ConnectionContext.TransportState.WaitForReporting:
                    await ProcessTelemetryObject(frame, context.Device.Id, context);

                    bool hasMoreData = (frame.ControlCode & 0x80) != 0;
                    if (hasMoreData)
                    {
                        return _protocolBuilder.BuildContinueFrameResponse(frame.SessionId, frame.Mid, nextFrameNo, requestSeq);
                    }

                    context.CurrentState = ConnectionContext.TransportState.ReportingComplete;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.ReportingComplete:
                    
                    if (context.Device != null && context.Device.HasPendingCommands)
                    {
                        var packet =  await SendPendingCommands(frame, context);
                        if(packet != null)
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
                    context.CurrentState = ConnectionContext.TransportState.EndConnection;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.EndConnection:
                    uint sessionIdToByte = context.SessionId ?? frame.SessionId;
                    return _protocolBuilder.BuildEndFrameResponse(sessionIdToByte, frame.Mid, nextFrameNo, 0x00);

                default:
                    return null;
            }
        }

        private async Task<byte[]?> SendPendingCommands(MeterFrame frame, ConnectionContext context)
        {
            byte[] result = null;
            ushort nextFrameNo = (ushort)(frame.FrameNo + 1);
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                // Get first command
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
                        ushort generatedReqId = 10000;
                        context.CurrentState = ConnectionContext.TransportState.SendingCommand;
                        context.SequenceNumber = generatedReqId;

                        foreach (var command in pendingCommands)
                        {
                            command.SequenceNumber = (ushort)generatedReqId;
                            pendingCommand.SentAt = Utils.DateTimeToInstant(DateTime.UtcNow);
                        }

                        await db.SaveChangesAsync();

                        // تفکیک کدهای عملکرد پروتکل بر اساس فاکشن کدهای ارسالی شما
                        switch (pendingCommand.FunctionCode)
                        {
                            case ProtocolConstants.FunCodeReadData: // Read Data Object
                                return _protocolBuilder.BuildReadCommandRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, pendingCommands);

                            case ProtocolConstants.FunCodeWriteData: // Write Data Object
                                return _protocolBuilder.BuildWriteCommandRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, pendingCommands);

                            case 0x07: // Read Records by Start Time
                                byte[] bcdTime = pendingCommand.RequestPayload.Take(6).ToArray();
                                byte limit = pendingCommand.RequestPayload.Length > 6 ? pendingCommand.RequestPayload[6] : (byte)1;
                                return _protocolBuilder.BuildReadRecordsByTimeRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, (ushort)pendingCommand.CommandId, bcdTime, limit);

                            case 0x08: // Read Recent Records
                                byte countToRead = pendingCommand.RequestPayload != null && pendingCommand.RequestPayload.Length > 0 ? pendingCommand.RequestPayload[0] : (byte)1;
                                return _protocolBuilder.BuildReadRecentRecordsRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, (ushort)pendingCommand.CommandId, countToRead);
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
                int offset = 8; // 4 bytes seesionID + 2 bytes frame number + 2 bytes data length
                if (frame.DecryptedData == null || frame.DecryptedData.Length < 5) return;

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
                                await HandleReadObjectsBatch(frame.DecryptedData, objectCount, commandBatch, db,context.Device);
                                break;

                            case 0x85:
                                await HandleWriteObjectsBatch(frame.DecryptedData, objectCount, commandBatch, db, deviceId);
                                break;

                            case 0x87:
                            case 0x88:
                                HandleRecordResponseBatch(frame.DecryptedData, responseFunctionCode, commandBatch);
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

                var content = data.AsSpan(offset, len);

                var matchedCmd = batch.FirstOrDefault(x => x.CommandId == objId);
                if (matchedCmd != null)
                {
                    matchedCmd.Status = DeviceCommandStatus.Succeeded;
                    matchedCmd.ExecutionResult = await ParseAndApplyReadObject(objId,content.ToArray(), device, db);
                }

                offset += len; // جلو بردن آفست به ابتدای اوبجکت بعدی
            }
        }

        // ==================== ۲. تابع تفکیک‌شده پردازش پاسخ چند آبجکتی نوشتن (0x85) ====================
        private async Task HandleWriteObjectsBatch(byte[] data, byte objectCount, List<DeviceCommandLog> batch, WaterMeterDbContext db, long deviceId)
        {


            int offset = 12;

            // پیمایش جفت‌های فشرده ۳ بایتی: [2 بایت اوبجکت آی‌دی] + [1 بایت نتیجه رایت]
            for (int i = 0; i < objectCount; i++)
            {
                if (offset + 3 > data.Length) break;
                ushort objId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                byte writeResult = data[offset + 2];
                offset += 3;

                // پیدا کردن کامند متناظر با این آبجکت از داخل لیت بچ دیتابیس
                var matchedCmd = batch.FirstOrDefault(x => x.CommandId == objId);
                if (matchedCmd != null)
                {
                    if (writeResult == 0) // عدد 0 یعنی موفقیت مطلق روی سخت‌افزار کنتور
                    {
                        matchedCmd.Status = DeviceCommandStatus.Succeeded;

                        // تحلیل و پارس معکوس پی‌لود ارسالی برای اعمال روی جداول کانفیگ اصلی سرور
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
                        if (writeResult == 2)
                            matchedCmd.ExecutionResult = "Failed (Code 2): Permission mismatch. This object is restricted or read-only.";
                        else if (writeResult == 1)
                            matchedCmd.ExecutionResult = "Failed (Code 1): Value out of range. The data exceeds terminal configuration limit.";
                        else
                            matchedCmd.ExecutionResult = $"Failed: Terminal returned unmapped error status code: {writeResult}";
                    }
                }
            }
        }
        // ==================== ۳. تابع تفکیک‌شده پردازش پاسخ سوابق و آرشیو (0x87 و 0x88) ====================
        private void HandleRecordResponseBatch(byte[] data, byte functionCode, List<DeviceCommandLog> batch)
        {
            byte recordCount = data[7]; // بایت 7 طبق سند لایه انتقال تعداد رکوردهای موجود است
            var mainCmd = batch.First(); // دستور سوابق تاریخی همیشه تک اوبجکتی فرستاده می‌شود

            if (recordCount == 0xFF)
            {
                mainCmd.Status = DeviceCommandStatus.Failed;
                mainCmd.ExecutionResult = "Failed: Terminal does not recognize requested history file or record parameters.";
            }
            else
            {
                mainCmd.Status = DeviceCommandStatus.Succeeded;
                mainCmd.ExecutionResult = functionCode == 0x87
                    ? $"Record Read Success: Retrieved {recordCount} logs starting from requested BCD timestamp."
                    : $"Recent Log Success: Successfully unpacked {recordCount} historical log data structures from flash.";
            }
        }

        // ==================== توابع کمکی پارسر و تحلیل‌گر فیزیکی اوبجکت‌ها ====================

        private int GetObjectLength(ushort objId)
        {
            return objId switch
            {
                0xB055 or 0x70DA => 1,                  // بازه فریز، کنترل پمپ (1 بایت)
                0x70B6 or 0x200E => 2,                  // پیکربندی پمپ، ضریب شیفت (2 بایت)
                0x70BE or 0xB061 => 3,                  // تاریخ تولید، زمان‌بندی آپلود (3 بایت)
                0x4211 => 4,                            // نسخه فریمور اصلی (4 بایت)
                0x0002 => 6,                            // ساعت داخلی کنتور (6 بایت)
                0x70C0 => 7,                            // پارامترهای ساعت تابستانه (7 بایت)
                0x0016 or 0xA013 => 15,                 // IMEI و IMSI (15 بایت)
                0x0067 => 16,                           // کلید مشتری (16 بایت)
                0x2007 => 18,                           // آی‌پی سرور و پورت (16 + 2 = 18 بایت)
                0x200A => 20,                           // کد ICCID سیم کارت (20 بایت)
                0x70F4 or 0x70F5 => 24,                 // شروع/پایان دوره‌ها و دبی مجاز (24 بایت)
                0x2012 => 32,                           // کلاینت APN (32 بایت)
                _ => 0
            };
        }

        private async Task<string> ParseAndApplyReadObject(ushort objId, byte[] content, Device? device, WaterMeterDbContext db)
        {
            if (content == null || content.Length == 0)
                return $"Object 0x{objId:X4}: Empty payload.";

            var contentSpan = content.AsSpan();

            switch (objId)
            {
                case 0x0002: // ساعت داخلی کنتور (6 Bytes BCD)
                    DateTime dt = Utils.ParseBcdDateTime(content);
                    if (device != null)
                    {
                        device.LastSeenAt = NodaTime.Instant.FromDateTimeUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                    }
                    return $"[Clock]: {dt:yyyy-MM-dd HH:mm:ss}; ";

                case 0x4211: // نسخه کنترلر اصلی (4 Bytes BCD)
                    string ver = $"{content[0]:X2}.{content[1]:X2}.{content[2]:X2}.{content[3]:X2}";
                    if (device != null) device.FirmwareVersion = ver;
                    return $"[Main Controller Version]: {ver}; ";

                case 0x0067: // کلید اختصاصی مشتری (16 Bytes HEX)
                    string keyHex = BitConverter.ToString(content).Replace("-", "");
                    return $"[Customer Key]: {keyHex}; ";

                case 0x70B6: // کانفیگ رفتاری شیر/پمپ هنگام رخداد وقایع (2 Bytes Bitmask)
                    ushort configBits = BinaryPrimitives.ReadUInt16BigEndian(contentSpan);
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

                case 0x70F4: // زمان شروع/پایان دوره‌های ۴ گانه (24 Bytes - 4 Cycles * 6 Bytes)
                    string cyclesTime = "[Cycle Start/End Times]: ";
                    for (int c = 0; c < 4; c++)
                    {
                        int baseIdx = c * 6;
                        cyclesTime += $"Cycle {c + 1}(Start:{content[baseIdx]:X2}{content[baseIdx + 1]:X2}h{content[baseIdx + 2]:X2}, ";
                        cyclesTime += $"End:{content[baseIdx + 3]:X2}{content[baseIdx + 4]:X2}h{content[baseIdx + 5]:X2}); ";
                    }
                    return cyclesTime;

                case 0x70F5: // حجم دبی مجاز دوره‌ها (20 Bytes - 4 Cycles * 5 Bytes Unsigned Integer)
                    string cyclesAllowance = "[Cycle Allowed Usage]: ";
                    for (int c = 0; c < 4; c++)
                    {
                        int baseIdx = c * 5;
                        // خواندن مقدار 5 بایتی (40 بیتی) بزرگ به صورت Big Endian
                        long allowedLiters10 = Utils.ReadUint40BigEndian(contentSpan.Slice(baseIdx, 5));
                        double allowedM3 = (allowedLiters10 * 10.0) / 1000.0;
                        cyclesAllowance += $"Cycle {c + 1}: {allowedM3} m³; ";
                    }
                    return cyclesAllowance;

                case 0x70BE: // تاریخ تولید کنتور (3 Bytes BCD: YYMMDD)
                    string prodDate = $"14{content[0]:X2}-{content[1]:X2}-{content[2]:X2}";
                    return $"[Production Date]: {prodDate}; ";

                case 0xB055: // بازه فریز روزانه (1 Byte Integer)
                    byte interval = content[0];
                    return $"[Freeze Interval]: {interval} minutes; ";

                case 0xA102: // پارامترهای شماره سریال کل ماشین (17 Bytes)
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
                        // فرمت پیش‌فرض BCD هفده بایتی
                        serialContent = BitConverter.ToString(content, 1, 16).Replace("-", "");
                    }
                    return $"[Meter Production Serial]: {serialContent}; ";

                case 0xB061: // تنظیمات آپلود زمان‌بندی شده (3 Bytes)
                    byte intervalCode = content[0];
                    int intervalMinutes = intervalCode * 10;
                    string startTimeHhmm = $"{content[1]:X2}:{content[2]:X2}";
                    return $"[Scheduled Upload]: Every {intervalMinutes} min from {startTimeHhmm}; ";

                case 0x70C0: // کانفیگ پارامترهای ساعت تابستانه (7 Bytes)
                    string dstStart = $"{content[0]:X2}-{content[1]:X2}h{content[2]:X2}";
                    string dstEnd = $"{content[3]:X2}-{content[4]:X2}h{content[5]:X2}";
                    sbyte adjustment10Min = (sbyte)content[6];
                    int adjMinutes = adjustment10Min * 10;
                    return $"[DST Config]: Start:{dstStart}, End:{dstEnd}, Adjust:{adjMinutes} min; ";

                case 0x70DA: // وضعیت رله کنترل پمپ / شیر برقی (1 Byte)
                    byte pumpState = content[0];
                    string stateLabel = pumpState == 0 ? "Exit Lock" : (pumpState == 1 ? "Lock Open (Valve Closed)" : "Lock Closed (Valve Open)");

                    // همگام‌سازی آنی اسنپ‌شات در لایه پایگاه داده
                    await SyncDatabaseConfigSnapshot(0x70DA, content, db, device?.Id ?? 0);
                    return $"[Valve Position]: {stateLabel}; ";

                case 0x200E: // زمان اینتروال استگرد یا همان Peak Shifting (2 Bytes Unsigned Integer)
                    ushort peakCoef = BinaryPrimitives.ReadUInt16BigEndian(contentSpan);
                    return $"[Staggered Peak Interval]: {peakCoef}; ";

                case 0x0016: // شناسه IMEI مودم کنتور (15 Bytes ASCII)
                    string imei = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    if (device != null) device.DeviceUid = imei;
                    return $"[Modem IMEI]: {imei}; ";

                case 0xA013: // شناسه IMSI سیم‌کارت (15 Bytes ASCII)
                    string imsi = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    return $"[SIM IMSI]: {imsi}; ";

                case 0x200A: // کد ICCID بیست بایتی سیم‌کارت (20 Bytes ASCII)
                    string iccid = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    if (device != null) device.CommunicationIccid = iccid;
                    return $"[SIM ICCID]: {iccid}; ";

                case 0x2012: // نقطه دسترسی اختصاصی مشتری APN (32 Bytes ASCII)
                    string apn = System.Text.Encoding.ASCII.GetString(content).Trim('\0', ' ');
                    return $"[APN Name]: {apn}; ";

                case 0x2007: // آی‌پی آدرس سرور مرکزی و پورت اتصال (16 Bytes ASCII IP + 2 Bytes Unsigned Port)
                    string ipAddress = System.Text.Encoding.ASCII.GetString(content, 0, 16).Trim('\0', ' ');
                    ushort portServer = BinaryPrimitives.ReadUInt16BigEndian(contentSpan.Slice(16, 2));
                    return $"[Central Server Destination]: {ipAddress}:{portServer}; ";

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
                case 0x0002: // ۱. ست کردن ساعت کنتور (6 Bytes BCD)
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

                case 0x0067: // ۲. پیکربندی کلید رمزنگاری مشتری (16 Bytes HEX)
                    string hexKey = BitConverter.ToString(requestPayload).Replace("-", "");
                    return $"[Sync] Customer security key updated in terminal database.";

                case 0xB055: // ۳. تغییر بازه فریز روزانه کنتور (1 Byte Integer)
                    byte intervalMinutes = requestPayload[0];
                    // اگر ستونی برای این کانفیگ در جدول دیتابیس دارید، اینجا آپدیت کنید:
                    // if (device != null) device.FreezeInterval = intervalMinutes;
                    return $"[Sync] Daily freeze interval synchronized to {intervalMinutes} minutes.";

                case 0x70DA: // ۴. دستور باز/بسته کردن شیر برقی یا پمپ (1 Byte)
                    byte newPumpState = requestPayload[0];
                    // 0: Exit lock, 1: Lock open (شیر بسته), 2: Lock closed (شیر باز)
                    var alarmSnapshot = await db.DeviceAlarmSnapshots.FindAsync(deviceId);
                    if (alarmSnapshot != null)
                    {
                        // طبق منطق آلارم‌ها: کد 1 یعنی شیر برقی قطع جریان کرده (PumpOff = true)
                        alarmSnapshot.PumpOff = (newPumpState == 1);
                        alarmSnapshot.UpdatedAt = NodaTime.Instant.FromDateTimeUtc(DateTime.UtcNow);
                    }
                    string stateText = newPumpState == 1 ? "Valve Closed (Pump Off)" : "Valve Open (Pump On/Normal)";
                    return $"[Sync] Active Valve State updated to: {stateText}.";

                case 0xB061: // ۵. زمان‌بندی آپلود پارامترها (3 Bytes: 1 Byte interval + 2 Bytes BCD time)
                    if (requestPayload.Length >= 3)
                    {
                        int uploadIntervalMin = requestPayload[0] * 10;
                        string startTime = $"{requestPayload[1]:X2}:{requestPayload[2]:X2}";
                        return $"[Sync] High-frequency upload configured for every {uploadIntervalMin} min starting at {startTime}.";
                    }
                    break;

                case 0x70B6: // ۶. رفتار رله پمپ هنگام وقوع خطاهای فیزیکی/مغناطیسی (2 Bytes Bitmask)
                    if (requestPayload.Length >= 2)
                    {
                        ushort bitmask = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                        return $"[Sync] Hardware safety valve bitmask synchronized to 0x{bitmask:X4}.";
                    }
                    break;

                case 0x70C0: // ۷. پیکربندی ساعت تابستانه DST (7 Bytes)
                    return $"[Sync] Daylight Saving Time parameter block updated.";

                case 0x200E: // ۸. ضریب تغییر زمان اوج بار یا همان Peak Shifting (2 Bytes Unsigned Integer)
                    if (requestPayload.Length >= 2)
                    {
                        ushort shiftCoef = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                        return $"[Sync] Staggered peak shifting coefficient synchronized to {shiftCoef}.";
                    }
                    break;

                case 0x2012: // ۹. تنظیم نقطه دسترسی APN سیم‌کارت (32 Bytes ASCII)
                    string newApn = System.Text.Encoding.ASCII.GetString(requestPayload).Trim('\0', ' ');
                    return $"[Sync] Access Point Name (APN) synchronized to '{newApn}'.";

                case 0x2007: // ۱۰. تغییر آی‌پی آدرس و پورت سرور مرکزی (16 Bytes IP ASCII + 2 Bytes Port)
                    if (requestPayload.Length >= 18)
                    {
                        string ip = System.Text.Encoding.ASCII.GetString(requestPayload, 0, 16).Trim('\0', ' ');
                        ushort port = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan);
                        return $"[Sync] Target Central Destination Redirected to -> {ip}:{port}.";
                    }
                    break;
            }

            return $"[Sync] Database synchronized for Object ID 0x{objId:X4}.";
        }

        private async Task SyncDatabaseConfigSnapshot(ushort objId, byte[]? payload, WaterMeterDbContext db, long deviceId)
        {
            if (payload == null || payload.Length == 0 || deviceId == 0) return;

            var payloadSpan = payload.AsSpan();

            switch (objId)
            {
                case 0x70DA: // ۱. همگام‌سازی شیر برقی / پمپ
                    var alarmSnapshot = await db.DeviceAlarmSnapshots.FindAsync(deviceId);
                    if (alarmSnapshot != null)
                    {
                        // بر اساس بایت نوشته شده: 1 یعنی شیر بسته شود (PumpOff = true)، 2 یعنی شیر باز شود (PumpOff = false)
                        alarmSnapshot.PumpOff = (payload[0] == 1);
                        alarmSnapshot.UpdatedAt = NodaTime.Instant.FromDateTimeUtc(DateTime.UtcNow);
                    }
                    break;

                case 0x0002: // ۲. همگام‌سازی مجدد ساعت ثبت دستگاه در جدول اصلی در صورت ست کردن دستی ساعت
                    if (payload.Length >= 6)
                    {
                        DateTime dt = Utils.ParseBcdDateTime(payload);
                        var device = await db.Devices.FindAsync(deviceId);
                        if (device != null)
                        {
                            device.LastSeenAt = NodaTime.Instant.FromDateTimeUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                        }
                    }
                    break;

                case 0xB055: // ۳. در صورت تمایل، بروزرسانی ستون بازه فریز در یک جدول کانفیگ اختصاصی
                    byte intervalMinutes = payload[0];
                    // مثلا: updates local configuration entity or device metadata row
                    break;

                default:
                    break;
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
                        case 0x70F2: // ۱. داده‌های زمان واقعی ترمینال (Terminal real-time data)
                            if (len == 49)
                            {
                                var telemetryData = frame.DecryptedData.AsSpan(currentOffset, 49);
                                var record = TelemetryParser.Parse70F2(telemetryData, deviceId);

                                // الف) ماهیت لاگ: درج در بافر کانال جهت ذخیره تاریخی دسته‌ای (Non-blocking)
                                await _telemetryBuffer.PushRecordAsync(record);

                                // ب) ماهیت آخرین وضعیت (Snapshot): به‌روزرسانی آنی کانتکست زنده دستگاه در حافظه RAM
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

                        case 0xB064: // ۲. کد شناسایی ارتباطی ترمینال
                            currentOffset += len;
                            break;

                        case 0xB05C: // ۳. داده‌های فریز روزانه کنتور (Daily frozen data)
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

                                    // الف) ماهیت لاگ: ثبت لاگ تاریخی فریز روزانه
                                    var frozenLog = new DailyFrozenLog
                                    {
                                        DeviceId = deviceId,
                                        FrozenAt = nowInstant,
                                        ReceivedAt = nowInstant,
                                        PositiveCumulative = positiveM3,
                                        ReverseCumulative = reverseM3
                                    };
                                    await db.DailyFrozenLogs.AddAsync(frozenLog);

                                    // ب) ماهیت آخرین وضعیت (Snapshot): به‌روزرسانی یا ایجاد آخرین وضعیت فریز روزانه دستگاه
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

                        case 0xB070: // ۴. کلمه وضعیت آلارم‌های ترمینال (Terminal alarm status word)
                            if (len == 3)
                            {
                                byte byte0 = frame.DecryptedData[currentOffset];
                                byte byte1 = frame.DecryptedData[currentOffset + 1];
                                byte byte2 = frame.DecryptedData[currentOffset + 2];

                                using (var scope = _scopeFactory.CreateScope())
                                {
                                    var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                                    // الف) ماهیت آخرین وضعیت: خواندن یا ایجاد اسنپ‌شات آلارم‌ها برای دستگاه
                                    var alarmSnap = await db.DeviceAlarmSnapshots.FindAsync(deviceId);
                                    if (alarmSnap == null)
                                    {
                                        alarmSnap = new DeviceAlarmSnapshot { DeviceId = deviceId };
                                        await db.DeviceAlarmSnapshots.AddAsync(alarmSnap);
                                    }

                                    // استخراج بیتی بایت 0 (مسائل فیزیکی)
                                    alarmSnap.PumpOff = (byte0 & 0x01) != 0;
                                    alarmSnap.MeterRemoved = (byte0 & 0x02) != 0;
                                    alarmSnap.MagneticInterference = (byte0 & 0x04) != 0;
                                    alarmSnap.RelayFault = (byte0 & 0x08) != 0;

                                    // استخراج بیتی بایت 1 (مسائل هیدرولیک)
                                    alarmSnap.EmptyPipe = (byte1 & 0x01) != 0;
                                    alarmSnap.ExcitationAlarm = (byte1 & 0x02) != 0;
                                    alarmSnap.LowSignal = (byte1 & 0x08) != 0;
                                    alarmSnap.MeasurementError = (byte1 & 0x14) != 0;
                                    alarmSnap.Backflow = (byte1 & 0x20) != 0;
                                    alarmSnap.AbnormallyHighFlow = (byte1 & 0x40) != 0;

                                    // استخراج بیتی بایت 2 (مسائل سیستمی)
                                    alarmSnap.StorageError = (byte2 & 0x01) != 0;
                                    alarmSnap.TrafficCollectionError = (byte2 & 0x02) != 0;
                                    alarmSnap.LowBattery = (byte2 & 0x08) != 0;
                                    alarmSnap.PowerLockout = (byte2 & 0x10) != 0;
                                    alarmSnap.ExternalPowerConnected = (byte2 & 0x80) != 0;

                                    alarmSnap.UpdatedAt = nowInstant;

                                    // ب) ماهیت لاگ تاریخی: ثبت واقعه در جدول لاگ در صورت بروز خطای بحرانی (مثلاً دزدیده شدن یا باتری ضعیف)
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

                        case 0x70EE: // ۵. وقایع اخیر (Recent events - 10 Items)
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

                                        if (masterEventCode != 0) // رخدادهایی که دارای کد معتبر هستند
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

                        // ارسال شیء بروزرسانی شده به رجیستری جهت ذخیره یکپارچه در دیتابیس
                        await deviceRegistry.UpdateDeviceAtivityAsync(context.Device);
                        _logger.LogInformation("Device Snapshot values successfully updated in 'devices' table for Serial: {Serial}", context.Device.SerialNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error parsing multi-object packet for Mid {Mid}", frame.Mid);
            }
        }
    }
}