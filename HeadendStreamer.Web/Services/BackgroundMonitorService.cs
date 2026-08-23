using HeadendStreamer.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadendStreamer.Web.Services;

public class BackgroundMonitorService : BackgroundService
{
    private readonly ILogger<BackgroundMonitorService> _logger;
    private readonly SystemMonitorService _systemMonitor;
    private readonly IHubContext<StreamHub> _hubContext;
    private readonly ConfigService _configService;
    private readonly StreamManagerService _streamManager;

    public BackgroundMonitorService(
        ILogger<BackgroundMonitorService> logger,
        SystemMonitorService systemMonitor,
        IHubContext<StreamHub> hubContext,
        ConfigService configService,
        StreamManagerService streamManager)
    {
        _logger = logger;
        _systemMonitor = systemMonitor;
        _hubContext = hubContext;
        _configService = configService;
        _streamManager = streamManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Monitor Service started");

        // Initial delay to allow other services to start
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        // Auto Start streams if enabled
        if (_configService.AutoStartOnStartup)
        {
            _logger.LogInformation("Auto-starting enabled streams on startup...");
            var configs = _configService.GetAllConfigs().Where(c => c.Enabled);
            foreach (var config in configs)
            {
                try
                {
                    var status = _streamManager.GetStreamStatus(config.Id);
                    if (status == null || !status.IsRunning)
                    {
                        _logger.LogInformation($"Auto-starting stream: {config.Name} ({config.Id})");
                        await _streamManager.StartStreamAsync(config.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to auto-start stream {config.Name} ({config.Id})");
                }
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Update system info via SystemMonitorService
                await _systemMonitor.UpdateSystemInfoAsync();

                // Get updated system info
                var systemInfo = _systemMonitor.GetSystemInfo();

                // Broadcast updates to SignalR clients
                await _hubContext.Clients.All.SendAsync("SystemInfo", systemInfo);

                // Log periodic status
                if (DateTime.UtcNow.Second % 30 == 0) // Every 30 seconds
                {
                    _logger.LogInformation(
                        "System Status - CPU: {CpuUsage}%, Memory: {MemoryUsage}%, Disk: {DiskUsage}%, Active Streams: {ActiveStreams}",
                        systemInfo.CpuUsage.ToString("0.0"),
                        systemInfo.MemoryUsage.ToString("0.0"),
                        systemInfo.DiskUsage.ToString("0.0"),
                        systemInfo.ActiveStreams);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background monitor");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Background Monitor Service stopped");
    }
}