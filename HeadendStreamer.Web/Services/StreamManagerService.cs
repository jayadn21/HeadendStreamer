using HeadendStreamer.Web.Hubs;
using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Models.ViewModels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeadendStreamer.Web.Services;

public class StreamManagerService
{
    private readonly Dictionary<string, StreamProcess> _processes = new();
    private readonly ILogger<StreamManagerService> _logger;
    private readonly ConfigService _configService;
    private readonly IConfiguration _configuration;
    private readonly IHubContext<StreamHub> _hubContext; // Remove SystemMonitorService

    public StreamManagerService(
        ILogger<StreamManagerService> logger,
        ConfigService configService,
        IHubContext<StreamHub> hubContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _configService = configService;
        _hubContext = hubContext;
        _configuration = configuration;
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
            var ffmpegPath = _configuration["HeadendStreamer:FfmpegPath"];
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                ffmpegPath = "ffmpeg";
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
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
            {
                _logger.LogWarning($"Attempted to stop stream {configId} but it's not in the active processes list.");
                return false;
            }
            
            _logger.LogInformation($"Stopping stream {streamProcess.Config.Name} (ID: {configId})");
            
            // Mark as stopping to prevent auto-restart
            streamProcess.IsStopping = true;
            
            try 
            {
                if (!streamProcess.Process.HasExited)
                {
                    // Send 'q' to FFmpeg to quit gracefully
                    _logger.LogInformation($"Sending 'q' to FFmpeg PID {streamProcess.Process.Id}");
                    await streamProcess.Process.StandardInput.WriteLineAsync("q");
                    
                    // Wait for graceful shutdown
                    if (!streamProcess.Process.WaitForExit(5000))
                    {
                        _logger.LogWarning($"FFmpeg PID {streamProcess.Process.Id} did not exit gracefully. Force killing.");
                        streamProcess.Process.Kill();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error during process shutdown for {configId}");
                try { if (!streamProcess.Process.HasExited) streamProcess.Process.Kill(); } catch { }
            }
            
            // Remove from tracking first to avoid race conditions with monitor
            _processes.Remove(configId);

            // Update log safely
            await AppendToLogAsync(streamProcess, $"\n\nStopped at: {DateTime.UtcNow}\n");
            
            streamProcess.Dispose();
            
            // Notify via SignalR
            await _hubContext.Clients.All.SendAsync("StreamStopped", configId);
            
            _logger.LogInformation($"Stream {configId} stopped successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to stop stream {configId}");
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
        string logFile;

        if (_processes.TryGetValue(configId, out var streamProcess))
        {
            logFile = streamProcess.LogFile;
        }
        else
        {
            // Try to find the latest log file on disk
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs", "ffmpeg");
            if (!Directory.Exists(logDir))
                return Array.Empty<StreamLog>();

            var files = Directory.GetFiles(logDir, $"{configId}_*.log");
            if (files.Length == 0)
                return Array.Empty<StreamLog>();

            logFile = files.OrderByDescending(f => f).First();
        }

        try
        {
            if (!File.Exists(logFile))
                return Array.Empty<StreamLog>();

            var logContent = await File.ReadAllLinesAsync(logFile);
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
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var inputFormat = config.InputFormat?.ToLower() ?? "auto";
        var isLocalFile = inputFormat == "file" || inputFormat == "local file";
        
        _logger.LogInformation($"Building FFmpeg command. OS: {(isWindows ? "Windows" : "Linux/Other")}, Input Format: {inputFormat}");
        
        // Input configuration
        if (isLocalFile)
        {
            // Real-time reading for local files
            args.Add("-re");
            args.AddRange(new[] { "-thread_queue_size", "1024" });
        }
        else if (isWindows)
        {
            if (config.InputDevice.Contains("desktop") || config.InputDevice.Contains("screen"))
            {
                args.AddRange(new[] { "-f", "gdigrab" });
            }
            else
            {
                args.AddRange(new[] { "-f", "dshow" });
            }
            args.AddRange(new[] { "-thread_queue_size", "1024" });
        }
        else
        {
            args.AddRange(new[] { "-f", "v4l2" });
            args.AddRange(new[] { "-thread_queue_size", "1024" });
        }
        
        // input_format / pixel_format (only for live devices, not files)
        if (!isLocalFile && !string.IsNullOrEmpty(config.InputFormat))
        {
            if (isWindows)
            {
                // Only use -pixel_format for dshow and if it's a known pixel format
                bool isGdigrab = args.Contains("gdigrab");
                if (!isGdigrab && !config.InputFormat.Contains("mpegts") && !config.InputFormat.Contains("auto"))
                {
                    args.AddRange(new[] { "-pixel_format", config.InputFormat });
                }
            }
            else
            {
                args.AddRange(new[] { "-input_format", config.InputFormat });
            }
        }

        if (!isLocalFile)
        {
            args.AddRange(new[] { "-video_size", config.VideoSize });
            args.AddRange(new[] { "-framerate", config.FrameRate.ToString() });
        }
        
        var inputDevice = config.InputDevice;
        if (!isLocalFile)
        {
            if (isWindows && !args.Contains("gdigrab") && !inputDevice.StartsWith("video="))
            {
                inputDevice = "video=" + inputDevice;
            }
            if (args.Contains("gdigrab") && inputDevice.StartsWith("video="))
            {
                inputDevice = inputDevice.Replace("video=", "");
            }
        }
        
        // Always quote paths/devices
        args.AddRange(new[] { "-i", $"\"{inputDevice}\"" });
        
        // Audio input if enabled
        if (config.EnableAudio && !string.IsNullOrEmpty(config.AudioDevice))
        {
            var audioDevice = config.AudioDevice;
            if (isWindows && !audioDevice.StartsWith("audio="))
            {
                audioDevice = "audio=" + audioDevice;
            }
            else if (!isWindows && !audioDevice.StartsWith("audio="))
            {
                audioDevice = "audio=" + audioDevice;
            }

            if (isWindows)
            {
                args.AddRange(new[] { "-f", "dshow" });
                args.AddRange(new[] { "-thread_queue_size", "512" });
                args.AddRange(new[] { "-i", $"\"{audioDevice}\"" });
            }
            else
            {
                args.AddRange(new[] { "-f", "alsa" });
                args.AddRange(new[] { "-thread_queue_size", "512" });
                args.AddRange(new[] { "-i", $"\"{audioDevice}\"" });
            }
        }
        
        // Video encoding
        args.AddRange(new[] { "-c:v", config.VideoCodec });
        if (!string.IsNullOrEmpty(config.Preset))
            args.AddRange(new[] { "-preset", config.Preset });
        if (!string.IsNullOrEmpty(config.Tune))
            args.AddRange(new[] { "-tune", config.Tune });
            
        args.AddRange(new[] { "-b:v", config.Bitrate });
        args.AddRange(new[] { "-maxrate", config.Bitrate });
        args.AddRange(new[] { "-bufsize", $"{ParseBitrate(config.Bitrate) / 2}k" });
        args.AddRange(new[] { "-g", config.GopSize.ToString() });
        args.AddRange(new[] { "-keyint_min", config.GopSize.ToString() });
        args.AddRange(new[] { "-sc_threshold", "0" });
        
        // Audio encoding if enabled
        if (config.EnableAudio)
        {
            args.AddRange(new[] { "-c:a", config.AudioCodec });
            args.AddRange(new[] { "-b:a", config.AudioBitrate });
            args.AddRange(new[] { "-ac", "2" });
        }
        
        // Output configuration
        args.AddRange(new[] { "-f", config.OutputFormat });
        args.AddRange(new[] { "-flags", "+global_header" });
        
        // Advanced options
        if (config.AdvancedOptions != null)
        {
            foreach (var option in config.AdvancedOptions)
            {
                args.AddRange(new[] { option.Key, option.Value });
            }
        }
        
        // Output URL
        var outputUrl = $"udp://{config.MulticastIp}:{config.Port}" +
                       $"?pkt_size=1316&buffer_size=65536&ttl={config.Ttl}";
        args.Add($"\"{outputUrl}\"");
        
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
                await AppendToLogAsync(streamProcess, $"[{DateTime.UtcNow:HH:mm:ss}] {output}\n");
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
            
            if (_processes.TryGetValue(configId, out var streamProcess))
            {
                if (streamProcess.IsStopping)
                {
                    _logger.LogInformation($"Stream {configId} stopped cleanly by user request.");
                    return;
                }

                _logger.LogWarning($"Stream process {configId} exited with code {process.ExitCode}");
                
                // Auto-restart logic
                var config = streamProcess.Config;
                if (config.Enabled && process.ExitCode != 0)
                {
                    _logger.LogInformation($"Auto-restarting stream {configId}");
                    await Task.Delay(5000);
                    
                    // Check again if we've been stopped during the delay
                    if (streamProcess.IsStopping || !_processes.ContainsKey(configId))
                        return;
                        
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
        long bitrate = 0;
        
        try
        {
            if (!process.HasExited)
            {
                using var proc = System.Diagnostics.Process.GetProcessById(process.Id);
                var currentCpuTime = proc.TotalProcessorTime;
                var currentTime = DateTime.UtcNow;

                if (streamProcess.LastCpuUpdate.HasValue)
                {
                    var cpuDelta = (currentCpuTime - streamProcess.LastCpuTime).TotalMilliseconds;
                    var timeDelta = (currentTime - streamProcess.LastCpuUpdate.Value).TotalMilliseconds;

                    if (timeDelta > 0)
                    {
                        // Calculate percentage: (delta CPU time / delta wall time / processor count) * 100
                        cpuUsage = (cpuDelta / timeDelta / Environment.ProcessorCount) * 100.0;
                    }
                }

                streamProcess.LastCpuTime = currentCpuTime;
                streamProcess.LastCpuUpdate = currentTime;
                memoryUsage = proc.WorkingSet64;
            }
        }
        catch { }
        
        // Extract bitrate from FFmpeg stats
        if (streamProcess.LastStats != null && streamProcess.LastStats.TryGetValue("bitrate", out var bitrateValue))
        {
            try
            {
                // FFmpeg bitrate format: "2450.0kbits/s" or "2.4Mbits/s"
                var bitrateStr = bitrateValue.ToString();
                if (!string.IsNullOrEmpty(bitrateStr))
                {
                    // Remove "kbits/s" or "Mbits/s" suffix and parse
                    bitrateStr = bitrateStr.ToLower()
                        .Replace("kbits/s", "")
                        .Replace("mbits/s", "")
                        .Replace("bits/s", "")
                        .Trim();
                    
                    if (double.TryParse(bitrateStr, out var bitrateDouble))
                    {
                        // If original string contained "Mbits/s", convert to kbps
                        if (bitrateValue.ToString().ToLower().Contains("mbits/s"))
                        {
                            bitrate = (long)(bitrateDouble * 1000);
                        }
                        else
                        {
                            bitrate = (long)bitrateDouble;
                        }
                    }
                }
            }
            catch
            {
                // If parsing fails, bitrate remains 0
            }
        }
        
        return new StreamStatus
        {
            ConfigId = configId,
            Name = config.Name,
            IsRunning = !process.HasExited,
            ProcessId = process.HasExited ? 0 : process.Id,
            StartTime = streamProcess.StartTime,
            Uptime = DateTime.UtcNow - streamProcess.StartTime,
            CpuUsage = cpuUsage,
            MemoryUsage = memoryUsage,
            Bitrate = bitrate,
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

    private async Task AppendToLogAsync(StreamProcess process, string message)
    {
        try
        {
            await process.LogLock.WaitAsync();
            try
            {
                var directory = Path.GetDirectoryName(process.LogFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                await File.AppendAllTextAsync(process.LogFile, message);
            }
            finally
            {
                process.LogLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Could not write to log file {process.LogFile}: {ex.Message}");
        }
    }
}

internal class StreamProcess : IDisposable
{
    public StreamConfig Config { get; set; } = null!;
    public Process Process { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public string LogFile { get; set; } = string.Empty;
    public Dictionary<string, object>? LastStats { get; set; }
    public DateTime? LastStatsUpdate { get; set; }
    public TimeSpan LastCpuTime { get; set; }
    public DateTime? LastCpuUpdate { get; set; }
    public bool IsStopping { get; set; }
    public SemaphoreSlim LogLock { get; } = new(1, 1);

    public void Dispose()
    {
        LogLock.Dispose();
        Process?.Dispose();
    }
}