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

            // 1. تنظیمات دیتابیس (PostgreSQL)
            builder.Services.AddDbContext<WaterMeterDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

            // 2. ثبت سرویس‌های لایه Infrastructure (Singleton برای اشتراک بین ۵۰۰۰ دستگاه)
            builder.Services.AddSingleton<ICryptoService, AesCryptoService>();
            builder.Services.AddSingleton<ISessionManager, SessionManager>();
            builder.Services.AddSingleton<ITelemetryBuffer, TelemetryBuffer>();
            builder.Services.AddSingleton<ICommandStore, CommandStore>();

            // 3. ثبت لایه Protocol
            builder.Services.AddSingleton<FrameParser>();
            builder.Services.AddSingleton<IProtocolBuilder, ProtocolBuilder>();

            // 4. ثبت لایه Application
            builder.Services.AddSingleton<FrameDispatcher>();

            // 5. ثبت لایه Networking (TcpServer روی پورت ۸۰۸۰)
            builder.Services.AddSingleton(sp =>
                new TcpServer(8080,
                    sp.GetRequiredService<FrameParser>(),
                    sp.GetRequiredService<FrameDispatcher>(),
                    sp.GetRequiredService<ILogger<TcpServer>>()));

            // 6. ثبت Worker اصلی برای استارت سرور
            builder.Services.AddHostedService<ServerWorker>();

            // 7. ثبت پردازشگر دسته‌جمعی تلمتری در پس‌زمینه
            // این سرویس داده‌ها را از بافر برداشته و در دیتابیس می‌ریزد
            builder.Services.AddHostedService<TelemetryBatchProcessor>();

            var host = builder.Build();
            host.Run();
        }
    }
}
