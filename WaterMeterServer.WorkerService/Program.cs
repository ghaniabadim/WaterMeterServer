using Microsoft.EntityFrameworkCore;
using WaterMeterServer.Application.BackgroundWorkers;
using WaterMeterServer.Application.Dispatchers;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Buffering;
using WaterMeterServer.Infrastructure.Logging;
using WaterMeterServer.Infrastructure.Persistence;
using WaterMeterServer.Infrastructure.Security;
using WaterMeterServer.Infrastructure.Services;
using WaterMeterServer.Infrastructure.Stores;
using WaterMeterServer.Networking;
using WaterMeterServer.Protocol;

namespace WorkerService
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
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
            builder.Services.AddSingleton<ICommandStore, CommandStore>();

            // 3. سرویس‌های لایه پروتکل
            builder.Services.AddSingleton<FrameParser>();
            builder.Services.AddSingleton<IProtocolBuilder, ProtocolBuilder>();

            // 4. سرویس‌های لایه اپلیکیشن
            builder.Services.AddSingleton<FrameDispatcher>();
            builder.Services.AddSingleton<LogQueue>();
            // 5. ایجاد مستقیم سرور سوکت روی پورت ۸۰۸۰
            builder.Services.AddSingleton(sp =>
                new TcpServer(
                    tcpPort,
                    sp.GetRequiredService<FrameParser>(),
                    sp.GetRequiredService<FrameDispatcher>(),
                    sp.GetRequiredService<ILogger<TcpServer>>(),
                    sp.GetRequiredService< LogQueue>()));

            // 6. پردازشگرهای پس‌زمینه (بدون تکرار و اورلپ)
            builder.Services.AddHostedService<ServerWorker>();
            builder.Services.AddHostedService<TelemetryBatchProcessor>();
            builder.Services.AddHostedService<CommunicationLogWorker>();
        

            var host = builder.Build();

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


            host.Run();
        }
    }
}
