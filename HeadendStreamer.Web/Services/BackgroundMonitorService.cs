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

    public BackgroundMonitorService(
        ILogger<BackgroundMonitorService> logger,
        SystemMonitorService systemMonitor,
        IHubContext<StreamHub> hubContext)
    {
        _logger = logger;
        _systemMonitor = systemMonitor;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Monitor Service started");

        // Initial delay to allow other services to start
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

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