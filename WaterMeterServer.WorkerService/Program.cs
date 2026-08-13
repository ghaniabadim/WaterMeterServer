using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using WaterMeterServer.Application.BackgroundWorkers;
using WaterMeterServer.Application.Dispatchers;
using WaterMeterServer.Application.Services;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Buffering;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Infrastructure.Persistence;
using WaterMeterServer.Infrastructure.Security;
using WaterMeterServer.Infrastructure.Services;
using WaterMeterServer.Infrastructure.Stores;
using WaterMeterServer.Networking;
using WaterMeterServer.Protocol;
using WorkerService.Api;

namespace WorkerService
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings__DefaultConnection must be configured through a secure deployment secret.");
            }

            var aesKey = builder.Configuration["Protocol:AesKey"];
            if (string.IsNullOrWhiteSpace(aesKey))
            {
                throw new InvalidOperationException(
                    "Protocol__AesKey must be configured through a secure deployment secret.");
            }

            var tcpPort = builder.Configuration.GetValue<int?>("TcpServer:Port") ?? 502;
            if (tcpPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("TcpServer:Port must be between 1 and 65535.");
            }

            var apiKey = builder.Configuration["Api:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Api__ApiKey must be configured through a secure deployment secret.");
            var apiPort = builder.Configuration.GetValue<int?>("Api:Port") ?? 5080;
            if (apiPort is < 1 or > 65535)
                throw new InvalidOperationException("Api:Port must be between 1 and 65535.");
            var apiListen = builder.Configuration["Api:Listen"] ?? "http://127.0.0.1";
            builder.WebHost.UseUrls($"{apiListen}:{apiPort}");
            builder.Services.AddSingleton(new ApiKeyOptions(apiKey));

            // 1. تنظیمات دیتابیس (اتصال لوکال به پورت فوروارد شده SSH)
            builder.Services.AddDbContext<WaterMeterDbContext>(options =>
                options.UseNpgsql(
                connectionString,
                o => o.UseNodaTime()));

            builder.Services.AddScoped<IDeviceRegistry, DeviceRegistry>();
            builder.Services.AddScoped<IFirmwareStorageService, PhysicalFirmwareStorageService>();

            // 2. سرویس‌های یکتای لایه زیرساخت (Thread-Safe برای ۵۰۰۰ دستگاه)
            builder.Services.AddSingleton<ICryptoService>(_ => new AesCryptoService(aesKey));
            builder.Services.AddSingleton<ISessionManager, SessionManager>();
            builder.Services.AddSingleton<ITelemetryBuffer, TelemetryBuffer>();

            // 3. سرویس‌های لایه پروتکل
            builder.Services.AddSingleton<FrameParser>();
            builder.Services.AddSingleton<IProtocolBuilder, ProtocolBuilder>();

            // 4. سرویس‌های لایه اپلیکیشن
            builder.Services.AddSingleton<FrameDispatcher>();
            builder.Services.AddSingleton<LogQueue>();
            builder.Services.AddSingleton<SystemEventLogger>();
            builder.Services.AddSingleton<WaterUsageAggregationService>();
            // 5. ایجاد مستقیم سرور سوکت روی پورت ۸۰۸۰
            builder.Services.AddSingleton(sp =>
                new TcpServer(
                    tcpPort,
                    sp.GetRequiredService<FrameParser>(),
                    sp.GetRequiredService<FrameDispatcher>(),
                    sp.GetRequiredService<ILogger<TcpServer>>(),
                    sp.GetRequiredService<LogQueue>(),
                    sp.GetRequiredService<SystemEventLogger>()));

            // 6. پردازشگرهای پس‌زمینه (بدون تکرار و اورلپ)
            builder.Services.AddHostedService<ServerWorker>();
            builder.Services.AddHostedService<TelemetryBatchProcessor>();
            builder.Services.AddHostedService<CommunicationLogWorker>();
            builder.Services.AddHostedService<CommandTimeoutWorker>();
        

            var host = builder.Build();
            host.Use(async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments("/api"))
                {
                    await next();
                    return;
                }

                var options = context.RequestServices.GetRequiredService<ApiKeyOptions>();
                if (!context.Request.Headers.TryGetValue("X-Api-Key", out var supplied) ||
                    !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(supplied.ToString()),
                        System.Text.Encoding.UTF8.GetBytes(options.Value)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
            });
            host.MapCommandRequestEndpoints();

            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                try
                {
                    var context = services.GetRequiredService<WaterMeterDbContext>();
                    // این دستور بررسی میکند؛ اگر دیتابیس یا جدولی روی VPS نباشد، فوراً آن را خلق میکند
                    await context.Database.EnsureCreatedAsync();
                    Console.WriteLine("Database and tables created successfully on VPS!");
                }
                catch (Exception ex)
                {
                    var logger = services.GetRequiredService<ILogger<Program>>();
                    logger.LogError(ex, "An error occurred while creating the database.");
                    throw;
                }
            }


            await host.RunAsync();
        }

        private sealed record ApiKeyOptions(string Value);
    }
}
