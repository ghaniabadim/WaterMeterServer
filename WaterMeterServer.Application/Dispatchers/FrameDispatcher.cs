// مسیر فیزیکی: WaterMeterServer.Application/Dispatchers/FrameDispatcher.cs
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
                    data =  _protocolBuilder.BuildEndFrameResponse(frame.SessionId, frame.Mid, (ushort)(frame.FrameNo + 1), 0x01);
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
                    // بررسی فوق‌العاده سریع با استفاده از پرچم بیتی دستگاه در حافظه RAM کانتکست
                    if (context.Device != null && context.Device.HasPendingCommands)
                    {
                        using (var scope = _scopeFactory.CreateScope())
                        {
                            var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                            // پیدا کردن اولین دستور فعال در صف (Status = Pending / 1)
                            var pendingCommand = await db.DeviceCommandLogs
                                .Where(x => x.DeviceId == context.Device.Id && x.Status == DeviceCommandStatus.Pending)
                                .OrderBy(x => x.Id)
                                .FirstOrDefaultAsync();

                            if (pendingCommand != null)
                            {
                                context.CurrentState = ConnectionContext.TransportState.SendingCommand;
                                ushort generatedReqId = (ushort)pendingCommand.Id; // نگاشت شناسه ردیف به عنوان REQID پکت

                                // به‌روزرسانی فیلد توالی و زمان ارسال در جدول
                                pendingCommand.SequenceNumber = (ushort)generatedReqId;
                                pendingCommand.SentAt = Utils.DateTimeToInstant( DateTime.UtcNow);
                                await db.SaveChangesAsync();

                                // تفکیک کدهای عملکرد پروتکل بر اساس فاکشن کدهای ارسالی شما
                                switch (pendingCommand.FunctionCode)
                                {
                                    case 0x04: // Read Data Object
                                        return _protocolBuilder.BuildReadCommandRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, (ushort)pendingCommand.CommandId);

                                    case 0x05: // Write Data Object
                                        return _protocolBuilder.BuildWriteCommandRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, (ushort)pendingCommand.CommandId, pendingCommand.RequestPayload);

                                    case 0x07: // Read Records by Start Time
                                        byte[] bcdTime = pendingCommand.RequestPayload.Take(6).ToArray();
                                        byte limit = pendingCommand.RequestPayload.Length > 6 ? pendingCommand.RequestPayload[6] : (byte)1;
                                        return _protocolBuilder.BuildReadRecordsByTimeRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, (ushort)pendingCommand.CommandId, bcdTime, limit);

                                    case 0x08: // Read Recent Records
                                        byte countToRead = pendingCommand.RequestPayload != null && pendingCommand.RequestPayload.Length > 0 ? pendingCommand.RequestPayload[0] : (byte)1;
                                        return _protocolBuilder.BuildReadRecentRecordsRequest(frame.SessionId, frame.Mid, nextFrameNo, generatedReqId, (ushort)pendingCommand.CommandId, countToRead);
                                }
                            }
                            else
                            {
                                context.Device.HasPendingCommands = false;
                            }
                        }
                    }

                    context.CurrentState = ConnectionContext.TransportState.FirmwareUpgrading;
                    return await HandleTransportAsync(frame, context);

                case ConnectionContext.TransportState.SendingCommand:
                    _logger.LogInformation("Processing active Uplink response command confirmation for Meter {Serial}", context.MeterId);

                    // پارس نتایج اجرای هر ۴ نوع کامند و ذخیره در بدنه کلاس جدید شما
                    await ProcessCommandResponseObject(frame, context.Device.Id);

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();
                        var checks = await db.DeviceCommandLogs.AnyAsync(x => x.DeviceId == context.Device.Id && x.Status ==DeviceCommandStatus.Pending);
                        if (checks)
                        {
                            // اگر هنوز کامند در صف هست، وضعیت را برای لوپ بعدی تمدید کن
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

        private async Task ProcessCommandResponseObject(MeterFrame frame, long deviceId)
        {
            try
            {
                byte responseFunctionCode = frame.DecryptedData[12];
                ushort responseSeq = BinaryPrimitives.ReadUInt16BigEndian(frame.DecryptedData.AsSpan(13, 2));

                using (var scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<WaterMeterDbContext>();

                    // رفع قطعی امپراتور مقایسه انوم و استنتاج کواِری با فراخوانی صریح فرمت ای‌اف‌کور
                    var commandLog = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                        .FirstOrDefaultAsync<DeviceCommandLog>(
                            db.DeviceCommandLogs.Where(x => x.DeviceId == deviceId && x.SequenceNumber == responseSeq && x.Status == DeviceCommandStatus.Pending)
                        );

                    if (commandLog != null)
                    {
                        commandLog.ResponsePayload = frame.DecryptedData.ToArray();
                        commandLog.RespondedAt = Utils.DateTimeToInstant(DateTime.UtcNow);

                        switch (responseFunctionCode)
                        {
                            case 0x05:
                            case 0x85: // فیدبک اجرای تغییرات دستور نوشتن (Write Object Response)
                                byte writeResult = frame.DecryptedData[18];
                                commandLog.Status = writeResult == 0 ? DeviceCommandStatus.Succeeded : DeviceCommandStatus.Failed;
                                commandLog.ExecutionResult = writeResult == 0 ? "Success" : $"Failed with terminal status code {writeResult}";
                                break;

                            case 0x04:
                            case 0x84: // فیدبک محتوای اوبجکت درخواستی دستور خواندن (Read Object Response)
                                byte objectCount = frame.DecryptedData[15];
                                commandLog.Status = DeviceCommandStatus.Succeeded;
                                commandLog.ExecutionResult = $"Read successfully. Contained Objects: {objectCount}. Payload: {frame.DecryptedData.Length - 16} bytes.";
                                break;

                            case 0x07:
                            case 0x87: // پاسخ موفق استخراج آرشیو بر اساس زمان شروع
                            case 0x08:
                            case 0x88: // پاسخ موفق استخراج رکوردهای مصرفی اخیر کنتور
                                byte recordCount = frame.DecryptedData[17];
                                commandLog.Status = DeviceCommandStatus.Succeeded;
                                commandLog.ExecutionResult = $"Successfully retrieved {recordCount} flash historical log records from internal storage.";
                                break;

                            default:
                                commandLog.Status = DeviceCommandStatus.Failed;
                                commandLog.ExecutionResult = $"Unknown or invalid function response operation code 0x{responseFunctionCode:X2}";
                                break;
                        }

                        await db.SaveChangesAsync();
                        _logger.LogInformation("DeviceCommandLog ID {Id} synchronized successfully with status: {Status}", commandLog.Id, commandLog.Status);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error decoding downstream response verification byte loop.");
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