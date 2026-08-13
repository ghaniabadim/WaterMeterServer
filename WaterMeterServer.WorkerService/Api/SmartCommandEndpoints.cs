using Microsoft.AspNetCore.Builder;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Persistence;
using WaterMeterServer.Protocol;

namespace WorkerService.Api;

public static class SmartCommandEndpoints
{
    private static readonly ObjectDefinition[] Objects =
    {
        new("RealTimeData", "تلمتری لحظه‌ای", 0x70F2, "read", 0, "none", null),
        new("AlarmStatus", "وضعیت آلارم", 0xB070, "read", 0, "none", null),
        new("DailyFrozenData", "داده فریز روزانه", 0xB05C, "read", 0, "none", null),
        new("TerminalEvents", "رویدادهای کنتور", 0x70EE, "read", 0, "none", null),
        new("Clock", "ساعت سیستم", 0x0002, "read,write", 6, "datetime", null),
        new("SecurityKey", "کلید امنیتی", 0x0067, "write", 16, "hex", null),
        new("DailyFreezeInterval", "دوره فریز روزانه", 0xB055, "read,write", 1, "byte", null),
        new("ValveState", "وضعیت شیر/پمپ", 0x70DA, "read,write", 1, "enum", new[] { "0", "1" }),
        new("SafetyValveMask", "ماسک شیر ایمنی", 0x70B6, "read,write", 2, "uint16", null),
        new("HighFrequencyUpload", "ارسال پرتواتر", 0xB061, "read,write", 3, "hex", null),
        new("DaylightSaving", "ساعت تابستانی", 0x70C0, "read,write", 7, "hex", null),
        new("PeakShiftCoefficient", "ضریب جابه‌جایی پیک", 0x200E, "read,write", 2, "uint16", null),
        new("Imei", "IMEI مودم", 0x0016, "read", 15, "text", null),
        new("Imsi", "IMSI سیم‌کارت", 0xA013, "read", 15, "text", null),
        new("Iccid", "ICCID سیم‌کارت", 0x200A, "read", 20, "text", null),
        new("Apn", "نام APN", 0x2012, "read,write", 32, "text", null),
        new("ServerDestination", "مقصد سرور", 0x2007, "read,write", 18, "server", null),
        new("PositiveCumulative", "تجمعی رفت", 0x70F4, "read", 24, "none", null),
        new("ReverseCumulative", "تجمعی برگشت", 0x70F5, "read", 20, "none", null),
        new("HourlyRecord", "رکورد ساعتی", 0xB06B, "record", 0, "none", null),
        new("DailyRecord", "رکورد روزانه", 0xB033, "record", 0, "none", null),
        new("MonthlyRecord", "رکورد ماهانه", 0xB035, "record", 0, "none", null)
    };

    public static IEndpointRouteBuilder MapSmartCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/command-catalog", () => Results.Ok(new
        {
            operations = new[]
            {
                new { name = "read", title = "خواندن داده", functionCode = ProtocolConstants.FunCodeReadData },
                new { name = "write", title = "نوشتن داده", functionCode = ProtocolConstants.FunCodeWriteData },
                new { name = "readByStartTime", title = "خواندن از زمان شروع", functionCode = ProtocolConstants.FunCodeReadRecordsByTime },
                new { name = "readByRecords", title = "خواندن تعداد رکورد اخیر", functionCode = ProtocolConstants.FunCodeReadRecentRecords },
                new { name = "firmware", title = "ارتقای فریمور", functionCode = ProtocolConstants.FunCodeDataDistribution }
            },
            objects = Objects.Select(x => new
            {
                x.Name, x.Title, objectId = $"0x{x.Id:X4}", x.Id, modes = x.Modes.Split(','),
                x.PayloadLength, x.ValueType, x.AllowedValues
            })
        }));

        api.MapPost("/meters/{serialNumber}/commands", async (
            string serialNumber, SmartCommandRequest request, WaterMeterDbContext db, CancellationToken ct) =>
        {
            var device = await db.Devices.SingleOrDefaultAsync(x => x.SerialNumber == serialNumber, ct);
            if (device is null)
                return Results.NotFound(new { message = "کنتور پیدا نشد." });

            var operation = request.Operation?.Trim();
            if (operation is not ("read" or "write" or "readByStartTime" or "readByRecords"))
                return Results.BadRequest(new { message = "عملیات نامعتبر است. از کاتالوگ API استفاده کنید." });

            var definition = ResolveObject(request.ObjectName, request.ObjectId);
            if (definition is null)
                return Results.BadRequest(new { message = "Object انتخاب‌شده در کاتالوگ وجود ندارد." });

            if (operation == "write" && !definition.Modes.Split(',').Contains("write"))
                return Results.BadRequest(new { message = "این Object برای نوشتن مجاز نیست." });
            if (operation != "write" && !definition.Modes.Split(',').Contains(operation.StartsWith("read") ? "record" : operation))
                return Results.BadRequest(new { message = "این Object برای این نوع خواندن مجاز نیست." });
            if (operation == "read" && !definition.Modes.Split(',').Contains("read"))
                return Results.BadRequest(new { message = "این Object برای خواندن مجاز نیست." });

            byte functionCode;
            byte[] payload;
            switch (operation)
            {
                case "read":
                    functionCode = ProtocolConstants.FunCodeReadData;
                    payload = Array.Empty<byte>();
                    break;
                case "write":
                    functionCode = ProtocolConstants.FunCodeWriteData;
                    try
                    {
                        payload = EncodeValue(request.Value, request.PayloadBase64, definition);
                    }
                    catch (Exception ex) when (ex is FormatException or ArgumentException)
                    {
                        return Results.BadRequest(new { message = ex.Message });
                    }
                    if (payload.Length == 0)
                        return Results.BadRequest(new { message = "برای write، payloadBase64 الزامی است." });
                    if (definition.PayloadLength > 0 && payload.Length != definition.PayloadLength)
                        return Results.BadRequest(new { message = $"طول مقدار باید دقیقاً {definition.PayloadLength} بایت باشد." });
                    break;
                case "readByStartTime":
                    functionCode = ProtocolConstants.FunCodeReadRecordsByTime;
                    if (!DateTime.TryParse(request.StartTime, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
                        return Results.BadRequest(new { message = "StartTime باید ISO-8601 معتبر باشد." });
                    if (request.RecordLimit is < 1 or > 255)
                        return Results.BadRequest(new { message = "RecordLimit باید بین 1 و 255 باشد." });
                    payload = BuildBcdTime(start, (byte)request.RecordLimit!);
                    break;
                default:
                    functionCode = ProtocolConstants.FunCodeReadRecentRecords;
                    if (request.RecordCount is < 1 or > 255)
                        return Results.BadRequest(new { message = "RecordCount باید بین 1 و 255 باشد." });
                    payload = new[] { (byte)request.RecordCount! };
                    break;
            }

            var now = SystemClock.Instance.GetCurrentInstant();
            var log = new DeviceCommandLog
            {
                DeviceId = device.Id,
                FunctionCode = functionCode,
                CommandId = definition.Id,
                RequestPayload = payload,
                Status = DeviceCommandStatus.Pending,
                CreatedAt = now,
                SentAt = Instant.MaxValue
            };
            db.DeviceCommandLogs.Add(log);
            device.HasPendingCommands = true;
            await db.SaveChangesAsync(ct);

            return Results.Accepted($"/api/requests/{log.Id}", new
            {
                requestId = log.Id, meter = serialNumber, operation,
                objectName = definition.Name, objectId = $"0x{definition.Id:X4}",
                status = "Pending"
            });
        });

        api.MapPost("/meters/{serialNumber}/firmware", async (
            string serialNumber, HttpRequest http, WaterMeterDbContext db,
            IFirmwareStorageService storage, IConfiguration config, CancellationToken ct) =>
        {
            var device = await db.Devices.SingleOrDefaultAsync(x => x.SerialNumber == serialNumber, ct);
            if (device is null)
                return Results.NotFound(new { message = "کنتور پیدا نشد." });

            var form = await http.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            var targetVersion = form["targetVersion"].ToString().Trim();
            var currentVersion = form["currentVersion"].ToString().Trim();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { message = "فایل فریمور الزامی است." });
            if (string.IsNullOrWhiteSpace(targetVersion) || string.IsNullOrWhiteSpace(currentVersion))
                return Results.BadRequest(new { message = "currentVersion و targetVersion الزامی هستند." });
            if (file.Length > 100 * 1024 * 1024)
                return Results.BadRequest(new { message = "حداکثر اندازه فریمور 100MB است." });

            var root = config["Firmware:RootPath"] ?? "/var/lib/water-meter/firmware";
            Directory.CreateDirectory(root);
            var safeVersion = string.Concat(targetVersion.Where(char.IsLetterOrDigit));
            var path = Path.Combine(root, $"{serialNumber}-{safeVersion}-{Guid.NewGuid():N}.bin");
            await using (var stream = File.Create(path))
                await file.CopyToAsync(stream, ct);

            var crc = await storage.CalculateCrc32Async(path, ct);
            var active = await db.FirmwareUpgradeRequests.AnyAsync(x =>
                x.MeterId == serialNumber && x.State != UpgradeState.Completed && x.State != UpgradeState.Failed, ct);
            if (active)
            {
                File.Delete(path);
                return Results.Conflict(new { message = "برای این کنتور یک ارتقای فریمور فعال وجود دارد." });
            }

            var request = new FirmwareUpgradeRequest
            {
                MeterId = serialNumber, TargetVersion = targetVersion, FilePath = path,
                FileSize = checked((int)file.Length), FileCrc32 = crc,
                ChunkSize = 256, State = UpgradeState.Idle
            };
            db.FirmwareUpgradeRequests.Add(request);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/firmware/{request.Id}", new
            {
                requestId = request.Id, meter = serialNumber, targetVersion,
                fileSize = request.FileSize, crc32 = $"0x{crc:X8}", status = request.State.ToString()
            });
        });

        api.MapGet("/firmware/{id:long}", async (long id, WaterMeterDbContext db, CancellationToken ct) =>
        {
            var item = await db.FirmwareUpgradeRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return item is null ? Results.NotFound() : Results.Ok(new
            {
                item.Id, item.MeterId, item.TargetVersion, item.FileSize,
                crc32 = $"0x{item.FileCrc32:X8}", state = item.State.ToString(),
                item.CurrentOffset, item.ChunkSize, item.LastErrorMessage,
                item.CreatedAt, item.LastUpdatedAt
            });
        });

        return endpoints;
    }

    private static ObjectDefinition? ResolveObject(string? name, int? id)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return Objects.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return id is >= 0 and <= ushort.MaxValue ? Objects.FirstOrDefault(x => x.Id == id) : null;
    }

    private static byte[] DecodePayload(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Array.Empty<byte>() : Convert.FromBase64String(value);

    private static byte[] BuildBcdTime(DateTime value, byte limit) => new[]
    {
        Utils.ByteToBcd(value.Year % 100), Utils.ByteToBcd(value.Month), Utils.ByteToBcd(value.Day),
        Utils.ByteToBcd(value.Hour), Utils.ByteToBcd(value.Minute), Utils.ByteToBcd(value.Second), limit
    };

    public sealed record SmartCommandRequest(
        string? Operation, string? ObjectName, int? ObjectId, string? Value, string? PayloadBase64,
        string? StartTime, int? RecordLimit, int? RecordCount);
    private sealed record ObjectDefinition(string Name, string Title, ushort Id, string Modes,
        int PayloadLength, string ValueType, string[]? AllowedValues);

    private static byte[] EncodeValue(string? value, string? payloadBase64, ObjectDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(payloadBase64))
            return Convert.FromBase64String(payloadBase64);
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<byte>();

        if (definition.AllowedValues is not null && !definition.AllowedValues.Contains(value))
            throw new ArgumentException($"مقدار مجاز برای {definition.Name}: {string.Join(", ", definition.AllowedValues)}");

        return definition.ValueType switch
        {
            "byte" when byte.TryParse(value, out var b) => new[] { b },
            "uint16" when ushort.TryParse(value, out var u) => new[] { (byte)(u >> 8), (byte)u },
            "hex" => Convert.FromHexString(value.Replace(" ", "")),
            "text" => System.Text.Encoding.ASCII.GetBytes(value.PadRight(definition.PayloadLength, '\0')[..definition.PayloadLength]),
            "datetime" when DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) =>
                BuildBcdTime(dt, 0)[..6],
            "server" => EncodeServer(value),
            _ => throw new ArgumentException("مقدار واردشده معتبر نیست.")
        };
    }

    private static byte[] EncodeServer(string value)
    {
        var parts = value.Split(':', 2);
        if (parts.Length != 2 || !ushort.TryParse(parts[1], out var port))
            throw new ArgumentException("مقصد سرور باید به شکل IP:Port باشد.");
        var ip = System.Text.Encoding.ASCII.GetBytes(parts[0]);
        if (ip.Length > 16) throw new ArgumentException("IP حداکثر 16 بایت است.");
        return ip.Concat(new byte[16 - ip.Length]).Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray();
    }
}
