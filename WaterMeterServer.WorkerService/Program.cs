using WaterMeterServer.Networking;
using WaterMeterServer.Domain.Interfaces;
using WaterMeterServer.Infrastructure.Persistence;
using WaterMeterServer.Infrastructure.Security;
using WaterMeterServer.Infrastructure.Stores;
using WaterMeterServer.Infrastructure.Buffering;
using Microsoft.EntityFrameworkCore;
using WaterMeterServer.Protocol;
using WaterMeterServer.Application.BackgroundWorkers;
using WaterMeterServer.Application.Dispatchers;

namespace WorkerService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // 1. تنظیمات دیتابیس (اتصال لوکال به پورت فوروارد شده SSH)
            builder.Services.AddDbContext<WaterMeterDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

            // 2. سرویس‌های یکتای لایه زیرساخت (Thread-Safe برای ۵۰۰۰ دستگاه)
            builder.Services.AddSingleton<ICryptoService, AesCryptoService>();
            builder.Services.AddSingleton<ISessionManager, SessionManager>();
            builder.Services.AddSingleton<ITelemetryBuffer, TelemetryBuffer>();
            builder.Services.AddSingleton<ICommandStore, CommandStore>();

            // 3. سرویس‌های لایه پروتکل
            builder.Services.AddSingleton<FrameParser>();
            builder.Services.AddSingleton<IProtocolBuilder, ProtocolBuilder>();

            // 4. سرویس‌های لایه اپلیکیشن
            builder.Services.AddSingleton<FrameDispatcher>();

            // 5. ایجاد مستقیم سرور سوکت روی پورت ۸۰۸۰
            builder.Services.AddSingleton(sp =>
                new TcpServer(
                    8080,
                    sp.GetRequiredService<FrameParser>(),
                    sp.GetRequiredService<FrameDispatcher>(),
                    sp.GetRequiredService<ILogger<TcpServer>>()));

            // 6. پردازشگرهای پس‌زمینه (بدون تکرار و اورلپ)
            builder.Services.AddHostedService<ServerWorker>();
            builder.Services.AddHostedService<TelemetryBatchProcessor>();

            var host = builder.Build();
            host.Run();
        }
    }
}