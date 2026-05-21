using WaterMeterServer.Networking;

public class ServerWorker : BackgroundService
{
    private readonly TcpServer _server;
    private readonly ILogger<ServerWorker> _logger;

    public ServerWorker(TcpServer server, ILogger<ServerWorker> logger)
    {
        _server = server;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Water Meter TCP Server...");
        await _server.StartAsync(stoppingToken);
    }
}