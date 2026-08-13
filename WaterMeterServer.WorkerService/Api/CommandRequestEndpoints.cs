using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using WaterMeterServer.Domain.Constants;
using WaterMeterServer.Domain.Entities;
using WaterMeterServer.Infrastructure.Persistence;

namespace WorkerService.Api;

public static class CommandRequestEndpoints
{
    public static IEndpointRouteBuilder MapCommandRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/meters", async (WaterMeterDbContext db, CancellationToken ct) =>
        {
            var meters = await db.Devices.AsNoTracking()
                .OrderBy(x => x.SerialNumber)
                .Select(x => new MeterSummary(x.Id, x.SerialNumber, x.Status, x.LastSeenAt, x.HasPendingCommands))
                .ToListAsync(ct);
            return Results.Ok(meters);
        });

        api.MapGet("/meters/{serialNumber}/requests", async (
            string serialNumber, WaterMeterDbContext db, CancellationToken ct) =>
        {
            var device = await db.Devices.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SerialNumber == serialNumber, ct);
            if (device is null)
                return Results.NotFound(new { message = "کنتور با این سریال پیدا نشد." });

            var requests = await db.DeviceCommandLogs.AsNoTracking()
                .Where(x => x.DeviceId == device.Id)
                .OrderByDescending(x => x.Id)
                .Take(100)
                .Select(x => new CommandRequestSummary(
                    x.Id, x.FunctionCode, x.CommandId, x.Status.ToString(),
                    x.ExecutionResult, x.CreatedAt,
                    x.SentAt == Instant.MaxValue ? null : x.SentAt,
                    x.RespondedAt))
                .ToListAsync(ct);
            return Results.Ok(requests);
        });

        api.MapGet("/requests/{id:long}", async (
            long id, WaterMeterDbContext db, CancellationToken ct) =>
        {
            var request = await db.DeviceCommandLogs.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new
                {
                    x.Id,
                    x.Device.SerialNumber,
                    x.FunctionCode,
                    x.CommandId,
                    x.Status,
                    x.ExecutionResult,
                    x.RequestPayload,
                    x.ResponsePayload,
                    x.CreatedAt,
                    SentAt = x.SentAt == Instant.MaxValue ? (Instant?)null : x.SentAt,
                    x.RespondedAt
                })
                .SingleOrDefaultAsync(ct);
            return request is null
                ? Results.NotFound(new { message = "درخواست پیدا نشد." })
                : Results.Ok(request);
        });

        api.MapPost("/meters/{serialNumber}/requests", async (
            string serialNumber, CreateCommandRequest request,
            WaterMeterDbContext db, CancellationToken ct) =>
        {
            if (request.Commands is null || request.Commands.Count == 0 || request.Commands.Count > 5)
                return Results.BadRequest(new { message = "تعداد درخواست‌ها باید بین ۱ تا ۵ باشد." });

            if (request.FunctionCode is not (
                ProtocolConstants.FunCodeReadData or
                ProtocolConstants.FunCodeWriteData or 0x07 or 0x08))
                return Results.BadRequest(new { message = "FunctionCode فقط 4، 5، 7 یا 8 است." });

            var device = await db.Devices.SingleOrDefaultAsync(x => x.SerialNumber == serialNumber, ct);
            if (device is null)
                return Results.NotFound(new { message = "کنتور با این سریال پیدا نشد." });

            var now = SystemClock.Instance.GetCurrentInstant();
            var logs = new List<DeviceCommandLog>();
            foreach (var item in request.Commands)
            {
                if (item.CommandId is < 0 or > ushort.MaxValue)
                    return Results.BadRequest(new { message = $"CommandId نامعتبر است: {item.CommandId}." });

                byte[] payload;
                try
                {
                    payload = string.IsNullOrWhiteSpace(item.PayloadBase64)
                        ? Array.Empty<byte>()
                        : Convert.FromBase64String(item.PayloadBase64);
                }
                catch (FormatException)
                {
                    return Results.BadRequest(new { message = "PayloadBase64 معتبر نیست." });
                }

                if (request.FunctionCode == ProtocolConstants.FunCodeWriteData && payload.Length == 0)
                    return Results.BadRequest(new { message = "برای FunctionCode=5 ارسال payload الزامی است." });
                if (request.FunctionCode == 0x07 && payload.Length != 7)
                    return Results.BadRequest(new { message = "برای FunctionCode=7، payload باید ۷ بایت BCD زمان شروع و limit باشد." });
                if (request.FunctionCode == 0x08 && payload.Length != 1)
                    return Results.BadRequest(new { message = "برای FunctionCode=8، payload باید یک بایت تعداد رکورد باشد." });

                logs.Add(new DeviceCommandLog
                {
                    DeviceId = device.Id,
                    FunctionCode = request.FunctionCode,
                    CommandId = item.CommandId,
                    RequestPayload = payload,
                    Status = DeviceCommandStatus.Pending,
                    CreatedAt = now,
                    SentAt = Instant.MaxValue
                });
            }

            await db.DeviceCommandLogs.AddRangeAsync(logs, ct);
            device.HasPendingCommands = true;
            await db.SaveChangesAsync(ct);

            return Results.Accepted(
                $"/api/meters/{Uri.EscapeDataString(serialNumber)}/requests",
                new { meter = device.SerialNumber, status = "Pending", requestIds = logs.Select(x => x.Id) });
        });

        return endpoints;
    }

    public sealed record CreateCommandRequest(byte FunctionCode, List<CommandItem> Commands);
    public sealed record CommandItem(int CommandId, string? PayloadBase64);
    public sealed record MeterSummary(long Id, string SerialNumber, short Status, Instant? LastSeenAt, bool HasPendingCommands);
    public sealed record CommandRequestSummary(long Id, byte FunctionCode, int CommandId, string Status,
        string? ExecutionResult, Instant CreatedAt, Instant? SentAt, Instant? RespondedAt);
}
