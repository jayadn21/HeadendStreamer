using HeadendStreamer.Web.Hubs;
using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Models.ViewModels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace HeadendStreamer.Web.Services;

public class StreamManagerService
{
    private readonly Dictionary<string, StreamProcess> _processes = new();
    private readonly ILogger<StreamManagerService> _logger;
    private readonly ConfigService _configService;
    private readonly IHubContext<StreamHub> _hubContext; // Remove SystemMonitorService

    public StreamManagerService(
        ILogger<StreamManagerService> logger,
        ConfigService configService,
        IHubContext<StreamHub> hubContext)
    {
        _logger = logger;
        _configService = configService;
        _hubContext = hubContext;
    }
    
    public async Task<StreamStatus> StartStreamAsync(string configId)
    {
        try
        {
            var config = await _configService.GetConfigAsync(configId);
            if (config == null)
                throw new ArgumentException($"Configuration {configId} not found");
            
            // Check if already running
            if (_processes.ContainsKey(configId))
            {
                var streamStatus = GetStreamStatus(configId);
                if (streamStatus.IsRunning)
                    return streamStatus;
                
                // Clean up old process
                await StopStreamAsync(configId);
            }
            
            // Build FFmpeg command
            var ffmpegCmd = BuildFfmpegCommand(config);
            _logger.LogInformation($"Starting stream {config.Name} with command: {ffmpegCmd}");
            
            // Create process
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = ffmpegCmd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            var process = new Process { StartInfo = processInfo };
            
            // Setup output handlers
            process.ErrorDataReceived += (sender, e) => 
                HandleFfmpegOutput(configId, e.Data);
            process.OutputDataReceived += (sender, e) => 
                HandleFfmpegOutput(configId, e.Data);
            
            // Start process
            if (!process.Start())
                throw new Exception("Failed to start FFmpeg process");
            
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            
            // Store process info
            var streamProcess = new StreamProcess
            {
                Config = config,
                Process = process,
                StartTime = DateTime.UtcNow,
                LogFile = $"logs/ffmpeg/{configId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log"
            };
            
            _processes[configId] = streamProcess;
            
            // Write startup log
            await File.WriteAllTextAsync(streamProcess.LogFile, 
                $"Started at: {DateTime.UtcNow}\nCommand: {ffmpegCmd}\n\n");
            
            // Monitor process in background
            _ = MonitorStreamProcessAsync(configId, process);
            
            // Get initial status
            var status = CreateStreamStatus(configId, streamProcess);
            
            // Notify via SignalR
            await _hubContext.Clients.All.SendAsync("StreamStarted", status);
            
            _logger.LogInformation($"Stream {config.Name} started successfully (PID: {process.Id})");
            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to start stream {configId}");
            throw;
        }
    }
    
    public async Task<bool> StopStreamAsync(string configId)
    {
        try
        {
            if (!_processes.TryGetValue(configId, out var streamProcess))
                return false;
            
            _logger.LogInformation($"Stopping stream {streamProcess.Config.Name}");
            
            // Send 'q' to FFmpeg to quit gracefully
            await streamProcess.Process.StandardInput.WriteLineAsync("q");
            
            // Wait for graceful shutdown
            if (!streamProcess.Process.WaitForExit(5000))
            {
                _logger.LogWarning($"Force killing stream {configId}");
                streamProcess.Process.Kill();
            }
            
            streamProcess.Process.Dispose();
            _processes.Remove(configId);
            
            // Update log
            await File.AppendAllTextAsync(streamProcess.LogFile,
                $"\n\nStopped at: {DateTime.UtcNow}\n");
            
            // Notify via SignalR
            await _hubContext.Clients.All.SendAsync("StreamStopped", configId);
            
            _logger.LogInformation($"Stream {configId} stopped");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error stopping stream {configId}");
            return false;
        }
    }
    
    public async Task<StreamStatus> RestartStreamAsync(string configId)
    {
        await StopStreamAsync(configId);
        await Task.Delay(1000); // Brief pause
        return await StartStreamAsync(configId);
    }
    
    public StreamStatus? GetStreamStatus(string configId)
    {
        if (!_processes.TryGetValue(configId, out var streamProcess))
            return null;
        
        return CreateStreamStatus(configId, streamProcess);
    }
    
    public Dictionary<string, StreamStatus> GetAllStreamStatus()
    {
        return _processes.ToDictionary(
            kvp => kvp.Key,
            kvp => CreateStreamStatus(kvp.Key, kvp.Value)
        );
    }
    
    public async Task<StreamLog[]> GetStreamLogsAsync(string configId, int lines = 100)
    {
        if (!_processes.TryGetValue(configId, out var streamProcess))
            return Array.Empty<StreamLog>();
        
        try
        {
            var logContent = await File.ReadAllLinesAsync(streamProcess.LogFile);
            return logContent.TakeLast(lines)
                .Select(line => ParseLogLine(configId, line))
                .Where(log => log != null)
                .ToArray()!;
        }
        catch
        {
            return Array.Empty<StreamLog>();
        }
    }
    
    private string BuildFfmpegCommand(StreamConfig config)
    {
        var args = new List<string>();
        
        // Input configuration
        args.AddRange(new[] { "-f", "v4l2" });
        args.AddRange(new[] { "-thread_queue_size", "1024" });
        args.AddRange(new[] { "-input_format", config.InputFormat });
        args.AddRange(new[] { "-video_size", config.VideoSize });
        args.AddRange(new[] { "-framerate", config.FrameRate.ToString() });
        args.AddRange(new[] { "-i", config.InputDevice });
        
        // Audio input if enabled
        if (config.EnableAudio && !string.IsNullOrEmpty(config.AudioDevice))
        {
            args.AddRange(new[] { "-f", "alsa" });
            args.AddRange(new[] { "-thread_queue_size", "512" });
            args.AddRange(new[] { "-i", config.AudioDevice });
        }
        
        // Video encoding
        args.AddRange(new[] { "-c:v", config.VideoCodec });
        args.AddRange(new[] { "-preset", config.Preset });
        args.AddRange(new[] { "-tune", config.Tune });
        args.AddRange(new[] { "-b:v", config.Bitrate });
        args.AddRange(new[] { "-maxrate", config.Bitrate });
        args.AddRange(new[] { "-bufsize", $"{ParseBitrate(config.Bitrate) / 2}k" });
        args.AddRange(new[] { "-g", config.GopSize.ToString() });
        args.AddRange(new[] { "-keyint_min", config.GopSize.ToString() });
        args.AddRange(new[] { "-sc_threshold", "0" });
        
        // Audio encoding if enabled
        if (config.EnableAudio && !string.IsNullOrEmpty(config.AudioDevice))
        {
            args.AddRange(new[] { "-c:a", config.AudioCodec });
            args.AddRange(new[] { "-b:a", config.AudioBitrate });
            args.AddRange(new[] { "-ac", "2" });
        }
        
        // Output configuration
        args.AddRange(new[] { "-f", config.OutputFormat });
        args.AddRange(new[] { "-flags", "+global_header" });
        
        // Advanced options
        foreach (var option in config.AdvancedOptions)
        {
            args.AddRange(new[] { option.Key, option.Value });
        }
        
        // Output URL
        var outputUrl = $"udp://{config.MulticastIp}:{config.Port}" +
                       $"?pkt_size=1316&buffer_size=65536&ttl={config.Ttl}";
        args.Add(outputUrl);
        
        return string.Join(" ", args);
    }
    
    private async void HandleFfmpegOutput(string configId, string? output)
    {
        if (string.IsNullOrEmpty(output))
            return;
        
        try
        {
            // Parse FFmpeg output for stats
            if (output.Contains("frame=") && output.Contains("fps="))
            {
                await UpdateStreamStats(configId, output);
            }
            
            // Log the output
            if (_processes.TryGetValue(configId, out var streamProcess))
            {
                await File.AppendAllTextAsync(streamProcess.LogFile, 
                    $"[{DateTime.UtcNow:HH:mm:ss}] {output}\n");
            }
            
            // Send to SignalR for real-time monitoring
            await _hubContext.Clients.Group($"stream-{configId}")
                .SendAsync("StreamOutput", new { configId, output, timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling FFmpeg output");
        }
    }
    
    private async Task MonitorStreamProcessAsync(string configId, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            
            if (_processes.ContainsKey(configId))
            {
                _logger.LogWarning($"Stream process {configId} exited with code {process.ExitCode}");
                
                // Auto-restart logic
                var config = _processes[configId].Config;
                if (config.Enabled && process.ExitCode != 0)
                {
                    _logger.LogInformation($"Auto-restarting stream {configId}");
                    await Task.Delay(5000);
                    await StartStreamAsync(configId);
                }
                else
                {
                    _processes.Remove(configId);
                    await _hubContext.Clients.All.SendAsync("StreamExited", configId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error monitoring stream process {configId}");
        }
    }
    
    private StreamStatus CreateStreamStatus(string configId, StreamProcess streamProcess)
    {
        var process = streamProcess.Process;
        var config = streamProcess.Config;
        
        // Get process stats
        double cpuUsage = 0;
        long memoryUsage = 0;
        
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(process.Id);
            cpuUsage = proc.TotalProcessorTime.TotalMilliseconds;
            memoryUsage = proc.WorkingSet64;
        }
        catch { }
        
        return new StreamStatus
        {
            ConfigId = configId,
            Name = config.Name,
            IsRunning = !process.HasExited,
            ProcessId = process.Id,
            StartTime = streamProcess.StartTime,
            Uptime = DateTime.UtcNow - streamProcess.StartTime,
            CpuUsage = cpuUsage,
            MemoryUsage = memoryUsage,
            LastUpdated = DateTime.UtcNow
        };
    }
    
    private async Task UpdateStreamStats(string configId, string ffmpegOutput)
    {
        // Parse FFmpeg stats output
        // Example: "frame=  123 fps= 30 q=29.0 size=    1234kB time=00:00:04.12 bitrate=2450.0kbits/s"
        
        try
        {
            var stats = ParseFfmpegStats(ffmpegOutput);
            
            if (_processes.TryGetValue(configId, out var streamProcess))
            {
                streamProcess.LastStats = stats;
                streamProcess.LastStatsUpdate = DateTime.UtcNow;
                
                // Send update via SignalR
                await _hubContext.Clients.Group($"stream-{configId}")
                    .SendAsync("StreamStats", new { configId, stats });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing FFmpeg stats");
        }
    }
    
    private Dictionary<string, object> ParseFfmpegStats(string output)
    {
        var stats = new Dictionary<string, object>();
        var parts = output.Split(' ').Where(p => !string.IsNullOrEmpty(p)).ToArray();
        
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Contains('='))
            {
                var keyValue = parts[i].Split('=');
                if (keyValue.Length == 2)
                {
                    stats[keyValue[0]] = keyValue[1];
                }
            }
        }
        
        return stats;
    }
    
    private int ParseBitrate(string bitrate)
    {
        if (string.IsNullOrEmpty(bitrate))
            return 0;
        
        bitrate = bitrate.ToLower();
        var multiplier = 1;
        
        if (bitrate.EndsWith("k"))
        {
            multiplier = 1000;
            bitrate = bitrate[..^1];
        }
        else if (bitrate.EndsWith("m"))
        {
            multiplier = 1000000;
            bitrate = bitrate[..^1];
        }
        
        if (int.TryParse(bitrate, out var value))
            return value * multiplier;
        
        return 0;
    }
    
    private StreamLog? ParseLogLine(string configId, string line)
    {
        try
        {
            var parts = line.Split(']', 2);
            if (parts.Length != 2)
                return null;
            
            var timestampStr = parts[0].TrimStart('[');
            var message = parts[1].Trim();
            
            if (DateTime.TryParse(timestampStr, out var timestamp))
            {
                return new StreamLog
                {
                    StreamId = configId,
                    Timestamp = timestamp,
                    Message = message,
                    Source = "ffmpeg"
                };
            }
        }
        catch
        {
            // Ignore parsing errors
        }
        
        return null;
    }
}

internal class StreamProcess
{
    public StreamConfig Config { get; set; } = null!;
    public Process Process { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public string LogFile { get; set; } = string.Empty;
    public Dictionary<string, object>? LastStats { get; set; }
    public DateTime? LastStatsUpdate { get; set; }
}