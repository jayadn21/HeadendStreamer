using Microsoft.AspNetCore.SignalR;
using HeadendStreamer.Web.Services;
using HeadendStreamer.Web.Models.Entities;

namespace HeadendStreamer.Web.Hubs;

public class StreamHub : Hub
{
    private readonly StreamManagerService _streamManager;
    private readonly SystemMonitorService _systemMonitor;
    private readonly ILogger<StreamHub> _logger;
    
    public StreamHub(
        StreamManagerService streamManager,
        SystemMonitorService systemMonitor,
        ILogger<StreamHub> logger)
    {
        _streamManager = streamManager;
        _systemMonitor = systemMonitor;
        _logger = logger;
    }
    
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"Client connected: {Context.ConnectionId}");
        
        // Send initial data
        var systemInfo = _systemMonitor.GetSystemInfo();
        var streamStatus = _streamManager.GetAllStreamStatus();
        
        await Clients.Caller.SendAsync("SystemInfo", systemInfo);
        await Clients.Caller.SendAsync("StreamStatus", streamStatus);
        
        await base.OnConnectedAsync();
    }
    
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Client disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }
    
    public async Task JoinStreamGroup(string streamId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"stream-{streamId}");
        _logger.LogInformation($"Client {Context.ConnectionId} joined stream group {streamId}");
    }
    
    public async Task LeaveStreamGroup(string streamId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stream-{streamId}");
        _logger.LogInformation($"Client {Context.ConnectionId} left stream group {streamId}");
    }
    
    public async Task RequestStreamLogs(string streamId, int lines = 100)
    {
        try
        {
            // Note: Implement GetStreamLogsAsync in StreamManagerService
            // var logs = await _streamManager.GetStreamLogsAsync(streamId, lines);
            // await Clients.Caller.SendAsync("StreamLogs", streamId, logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get logs for stream {streamId}");
        }
    }
    
    public async Task SendCommand(string streamId, string command)
    {
        _logger.LogInformation($"Command received for stream {streamId}: {command}");
        
        // Handle different commands
        switch (command.ToLower())
        {
            case "start":
                // await _streamManager.StartStreamAsync(streamId);
                break;
            case "stop":
                // await _streamManager.StopStreamAsync(streamId);
                break;
            case "restart":
                // await _streamManager.RestartStreamAsync(streamId);
                break;
            default:
                _logger.LogWarning($"Unknown command: {command}");
                break;
        }
    }
}