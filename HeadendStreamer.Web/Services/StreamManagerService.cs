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
    private readonly object _processLock = new();
    private readonly ILogger<StreamManagerService> _logger;
    private readonly ConfigService _configService;
    private readonly IConfiguration _configuration;
    private readonly IHubContext<StreamHub> _hubContext; // Remove SystemMonitorService
    private readonly System.Threading.Timer _saveStateTimer;

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
        _saveStateTimer = new System.Threading.Timer(SavePlaybackStates, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }
    
    public async Task<StreamStatus> StartStreamAsync(string configId)
    {
        try
        {
            var config = await _configService.GetConfigAsync(configId);
            if (config == null)
                throw new ArgumentException($"Configuration {configId} not found");
            
            // Check if already running
            bool isAlreadyRunning;
            lock (_processLock)
            {
                isAlreadyRunning = _processes.ContainsKey(configId);
            }
            if (isAlreadyRunning)
            {
                var streamStatus = GetStreamStatus(configId);
                if (streamStatus.IsRunning)
                    return streamStatus;
                
                // Clean up old process
                await StopStreamAsync(configId);
            }
            
            // Build FFmpeg command
            double ssPos = 0;
            string? overrideInput = null;
            FolderPlaybackState? folderState = null;
            if (config.InputFormat?.ToLower() == "folder")
            {
                folderState = GetFolderPlaybackState(configId);
                if (folderState != null && folderState.ProcessId.HasValue)
                {
                    try
                    {
                        var oldProcess = Process.GetProcessById(folderState.ProcessId.Value);
                        if (oldProcess != null && !oldProcess.HasExited && oldProcess.ProcessName.ToLowerInvariant().Contains("ffmpeg"))
                        {
                            _logger.LogWarning($"Found orphaned FFmpeg process (PID {folderState.ProcessId.Value}) for folder stream {configId}. Killing it.");
                            oldProcess.Kill(true);
                            oldProcess.WaitForExit(2000);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process not running
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to kill orphaned FFmpeg process {folderState.ProcessId.Value}");
                    }
                }

                if (folderState == null)
                {
                    folderState = new FolderPlaybackState { StreamId = configId };
                }
                
                SetupNextVideoForFolder(configId, config, folderState);
                
                // Save immediately
                var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var filePath = Path.Combine(dir, $"{configId}.json");
                var json = System.Text.Json.JsonSerializer.Serialize(folderState, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);

                overrideInput = folderState.CurrentVideoPath;
                ssPos = folderState.ResumePositionSeconds;
            }

            var ffmpegCmd = BuildFfmpegCommand(config, ssPos, overrideInput);
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
            
            // Fix for shared builds: Add ../lib to LD_LIBRARY_PATH
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var ffmpegDir = Path.GetDirectoryName(ffmpegPath);
                if (ffmpegDir != null)
                {
                    var libDir = Path.GetFullPath(Path.Combine(ffmpegDir, "../lib"));
                    if (Directory.Exists(libDir))
                    {
                        var currentLdPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                        processInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = $"{libDir}:{currentLdPath}";
                        _logger.LogInformation($"Setting LD_LIBRARY_PATH to include: {libDir}");
                    }
                }
            }
            
            var process = new Process { StartInfo = processInfo };
            
            // Setup output handlers
            process.ErrorDataReceived += (sender, e) => 
                HandleFfmpegOutput(configId, e.Data);
            process.OutputDataReceived += (sender, e) => 
                HandleFfmpegOutput(configId, e.Data);
            
            // Start process
            if (!process.Start())
                throw new Exception("Failed to start FFmpeg process");
            
            if (config.InputFormat?.ToLower() == "folder" && folderState != null)
            {
                folderState.ProcessId = process.Id;
                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
                    var filePath = Path.Combine(dir, $"{configId}.json");
                    var json = System.Text.Json.JsonSerializer.Serialize(folderState, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filePath, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to save PID for stream {configId}");
                }
            }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            
            // Store process info
            var streamProcess = new StreamProcess
            {
                Config = config,
                Process = process,
                StartTime = DateTime.UtcNow,
                LogFile = $"logs/ffmpeg/{configId}_{DateTime.Now:yyyyMMdd_HHmmss}.log",
                FolderState = folderState,
                StartSeekPositionSeconds = ssPos
            };
            
            lock (_processLock)
            {
                _processes[configId] = streamProcess;
            }
            
            // Write startup log
            await File.WriteAllTextAsync(streamProcess.LogFile, 
                $"Started at: {DateTime.Now}\nCommand: {ffmpegCmd}\n\n");
            
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
            StreamProcess? streamProcess;
            lock (_processLock)
            {
                if (!_processes.TryGetValue(configId, out streamProcess))
                {
                    _logger.LogWarning($"Attempted to stop stream {configId} but it's not in the active processes list.");
                    return false;
                }
            }
            
            _logger.LogInformation($"Stopping stream {streamProcess.Config.Name} (ID: {configId})");
            
            // Save state before stopping if folder playback
            if (streamProcess.Config.InputFormat?.ToLower() == "folder" && streamProcess.FolderState != null)
            {
                double currentPosition = streamProcess.FolderState.ResumePositionSeconds;
                if (streamProcess.LastStats != null && streamProcess.LastStats.TryGetValue("time", out var timeVal))
                {
                    if (TimeSpan.TryParse(timeVal.ToString(), out var parsedTime))
                    {
                        currentPosition = streamProcess.StartSeekPositionSeconds + parsedTime.TotalSeconds;
                    }
                }
                streamProcess.FolderState.ResumePositionSeconds = currentPosition;
                streamProcess.FolderState.ProcessId = null;
                streamProcess.FolderState.LastSaved = DateTime.UtcNow;

                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    var filePath = Path.Combine(dir, $"{configId}.json");
                    var json = JsonSerializer.Serialize(streamProcess.FolderState, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(filePath, json);
                    _logger.LogInformation($"Saved playback resume state for stopped stream {configId} at position {currentPosition}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to save final state for stopped stream {configId}");
                }
            }

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
            lock (_processLock)
            {
                _processes.Remove(configId);
            }

            // Update log safely
            await AppendToLogAsync(streamProcess, $"\n\nStopped at: {DateTime.Now}\n");
            
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
    
    public StreamStatus GetStreamStatus(string configId)
    {
        StreamProcess? streamProcess;
        lock (_processLock)
        {
            _processes.TryGetValue(configId, out streamProcess);
        }
        
        if (streamProcess != null)
        {
            return CreateStreamStatus(configId, streamProcess);
        }
        
        // Return a "Stopped" status
        // We can try to get the config name if it exists in the config service
        // Since this is sync, we use the sync GetAllConfigs or we just return an empty name
        var configs = _configService.GetAllConfigs();
        var config = configs.FirstOrDefault(c => c.Id == configId);
        
        return new StreamStatus
        {
            ConfigId = configId,
            Name = config?.Name ?? "Unknown",
            IsRunning = false,
            LastUpdated = DateTime.UtcNow
        };
    }
    
    public Dictionary<string, StreamStatus> GetAllStreamStatus()
    {
        var result = new Dictionary<string, StreamStatus>();
        var configs = _configService.GetAllConfigs();
        
        foreach (var config in configs)
        {
            result[config.Id] = GetStreamStatus(config.Id);
        }
        
        return result;
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
    
    private string BuildFfmpegCommand(StreamConfig config, double ssPosition = 0, string? overrideInputDevice = null)
    {
        var args = new List<string>();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var inputFormat = config.InputFormat?.ToLower() ?? "auto";
        var isLocalFile = inputFormat == "file" || inputFormat == "local file" || inputFormat == "folder";
        var isMpegTs = inputFormat == "mpegts";
        
        var isNvidiaCodec = config.VideoCodec == "h264_nvenc" || config.VideoCodec == "hevc_nvenc";
        var isCompressedInput = isLocalFile || isMpegTs;

        _logger.LogInformation($"Building FFmpeg command. OS: {(isWindows ? "Windows" : "Linux/Other")}, Input Format: {inputFormat}, Nvidia Codec: {isNvidiaCodec}");
        
        var hasLogo = !string.IsNullOrEmpty(config.LogoPath) && File.Exists(config.LogoPath);

        if (isNvidiaCodec && isCompressedInput)
        {
            args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        }

        if (config.ReStream && (inputFormat == "mpegts") && !hasLogo)
        {
            // Re-stream mode: copy streams without transcoding
            args.AddRange(new[] { "-i", $"\"{config.InputDevice}\"" });
            args.AddRange(new[] { "-c:v", "copy" });
            args.AddRange(new[] { "-c:a", "copy" });
            args.AddRange(new[] { "-f", "mpegts" });
            args.AddRange(new[] { "-flags", "+global_header" });
            
            var reStreamOutputUrl = $"udp://{config.MulticastIp}:{config.Port}" +
                           $"?pkt_size=1316&buffer_size=65536&ttl={config.Ttl}";
            args.Add($"\"{reStreamOutputUrl}\"");
            
            return string.Join(" ", args);
        }
        
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
            else if (!isMpegTs)
            {
                args.AddRange(new[] { "-f", "dshow" });
            }
            args.AddRange(new[] { "-thread_queue_size", "1024" });
        }
        else
        {
            if (!isMpegTs)
            {
                args.AddRange(new[] { "-f", "v4l2" });
            }
            args.AddRange(new[] { "-thread_queue_size", "1024" });
        }
        
        // input_format / pixel_format (only for live devices, not files, YouTube or network streams)
        if (!isLocalFile && !isMpegTs && !string.IsNullOrEmpty(config.PixelFormat))
        {
            if (isWindows)
            {
                // Only use -pixel_format for dshow and if it's a known pixel format
                bool isGdigrab = args.Contains("gdigrab");
                if (!isGdigrab && !config.PixelFormat.Contains("auto"))
                {
                    args.AddRange(new[] { "-pixel_format", config.PixelFormat });
                }
            }
            else
            {
                if (!config.PixelFormat.Contains("auto"))
                {
                    args.AddRange(new[] { "-input_format", config.PixelFormat });
                }
            }
        }

        if (!isLocalFile && !isMpegTs)
        {
            args.AddRange(new[] { "-video_size", config.VideoSize });
            args.AddRange(new[] { "-framerate", config.FrameRate.ToString() });
        }
        
        var inputDevice = overrideInputDevice ?? config.InputDevice;
        if (!isLocalFile && !isMpegTs)
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
        
        if (ssPosition > 0)
        {
            args.AddRange(new[] { "-ss", ssPosition.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) });
        }
        // Always quote paths/devices
        args.AddRange(new[] { "-i", $"\"{inputDevice}\"" });
        
        // Audio input if enabled - skip for MPEG-TS
        if (!isLocalFile && !isMpegTs && config.EnableAudio && !string.IsNullOrEmpty(config.AudioDevice))
        {
            var audioDevice = config.AudioDevice;
            if (isWindows && !audioDevice.StartsWith("audio="))
            {
                audioDevice = "audio=" + audioDevice;
            }
            // On Linux with PulseAudio, we don't need "audio=" prefix usually, just the source name.
            // If the user selected a device from our list, it's already the correct name/ID.

            if (isWindows)
            {
                args.AddRange(new[] { "-f", "dshow" });
                args.AddRange(new[] { "-thread_queue_size", "512" });
                args.AddRange(new[] { "-i", $"\"{audioDevice}\"" });
            }
            else
            {
                // Use PulseAudio
                args.AddRange(new[] { "-f", "pulse" });
                args.AddRange(new[] { "-thread_queue_size", "512" });
                args.AddRange(new[] { "-i", $"\"{audioDevice}\"" });
            }
        }
        
        if (hasLogo)
        {
            var logoExt = Path.GetExtension(config.LogoPath).ToLowerInvariant();
            var isImage = logoExt == ".png" || logoExt == ".jpg" || logoExt == ".jpeg" || logoExt == ".gif" || logoExt == ".bmp" || logoExt == ".svg";
            
            if (isImage)
            {
                // Removed -loop 1 option to reduce CPU utilization
            }
            else
            {
                args.AddRange(new[] { "-stream_loop", "-1" });
            }
            args.AddRange(new[] { "-i", $"\"{config.LogoPath}\"" });
        }
        
        // Font setup for drawing date
        string fontFile = null;
        if (isWindows)
        {
            fontFile = "C\\:/Windows/Fonts/arial.ttf";
        }
        else
        {
            if (File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"))
            {
                fontFile = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            }
            else if (File.Exists("/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"))
            {
                fontFile = "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
            }
        }

        // Cycle text between Date, Time, and Day of the Week every 5 seconds (15-second period total) using FFmpeg filter timeline expressions
        // Note: %{localtime} splits arguments on unescaped colons in drawtext options list.
        // Double-escape colons for drawtext option parser: %I\\\%M\\\%S %p
        string dateTextExpr = "%{localtime\\:%d/%m/%Y}";
        string timeTextExpr = "%{localtime\\:%I\\\\\\:%M\\\\\\:%S %p}";
        string dayTextExpr = "%{localtime\\:%A}";

        int dml = config.DateTimeMarginLeft ?? 10;
        int dmt = config.DateTimeMarginTop ?? 10;
        int dmr = config.DateTimeMarginRight ?? 10;
        int dmb = config.DateTimeMarginBottom ?? 10;

        _logger.LogInformation($"DateTime margins for stream {config.Name}: Left={dml}, Top={dmt}, Right={dmr}, Bottom={dmb}");

        int fontSize = config.DateTimeFontSize ?? 24;
        int boxW = (int)(fontSize * 7.5);
        int boxH = (int)(fontSize * 1.66);
        string bx, by;

        if (hasLogo)
        {
            int mt = config.LogoMarginTop ?? 10;
            int mb = config.LogoMarginBottom ?? 10;
            int ml = config.LogoMarginLeft ?? 10;
            int mr = config.LogoMarginRight ?? 10;
            int lw = (config.LogoWidth ?? 0) > 0 ? config.LogoWidth.Value : 100;
            int lh = (config.LogoHeight ?? 0) > 0 ? config.LogoHeight.Value : 100;

            string logoCenter = config.LogoPosition?.ToLowerInvariant() switch
            {
                "top right" => $"w-{mr}-{lw}/2",
                "bottom left" => $"{ml}+{lw}/2",
                "bottom right" => $"w-{mr}-{lw}/2",
                _ => $"{ml}+{lw}/2" // "top left"
            };

            bx = $"({logoCenter})-{boxW}/2";

            by = config.LogoPosition?.ToLowerInvariant() switch
            {
                "bottom left" => $"h-{lh}-{mb}-{boxH}-{dmb}",
                "bottom right" => $"h-{lh}-{mb}-{boxH}-{dmb}",
                _ => $"{mt + lh + dmt}"
            };
        }
        else
        {
            bx = $"w-{boxW}-{dmr}";
            by = $"{dmt}";
        }

        string fontColor = config.DateTimeFontColor ?? "white";
        string fontOpt = string.IsNullOrEmpty(fontFile) ? "" : $"fontfile='{fontFile}':";
        string dateFilter = $"{fontOpt}text='{dateTextExpr}':x='{bx}+({boxW}-tw)/2':y='{by}+({boxH}-th)/2':fontsize={fontSize}:fontcolor={fontColor}:enable='eq(mod(floor(t/5)\\,3)\\,0)'";
        string timeFilter = $"{fontOpt}text='{timeTextExpr}':x='{bx}+({boxW}-tw)/2':y='{by}+({boxH}-th)/2':fontsize={fontSize}:fontcolor={fontColor}:enable='eq(mod(floor(t/5)\\,3)\\,1)'";
        string dayFilter  = $"{fontOpt}text='{dayTextExpr}':x='{bx}+({boxW}-tw)/2':y='{by}+({boxH}-th)/2':fontsize={fontSize}:fontcolor={fontColor}:enable='eq(mod(floor(t/5)\\,3)\\,2)'";

        string overlayTextChain = $"drawbox=x='{bx}':y='{by}':w={boxW}:h={boxH}:color=black@0.0:t=fill,drawtext={dateFilter},drawtext={timeFilter},drawtext={dayFilter}";

        string videoInputNode = "[0:v]";
        string preScaleFilter = "";
        if (isNvidiaCodec && isCompressedInput)
        {
            if (!string.IsNullOrEmpty(config.VideoSize))
            {
                var sizeParts = config.VideoSize.Split('x');
                if (sizeParts.Length == 2 && int.TryParse(sizeParts[0], out _) && int.TryParse(sizeParts[1], out _))
                {
                    preScaleFilter = $"[0:v]scale_cuda={sizeParts[0]}:{sizeParts[1]},hwdownload,format=nv12[scaled_in]; ";
                    videoInputNode = "[scaled_in]";
                }
            }
            else
            {
                preScaleFilter = "[0:v]hwdownload,format=nv12[downloaded_in]; ";
                videoInputNode = "[downloaded_in]";
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(config.VideoSize))
            {
                var sizeParts = config.VideoSize.Split('x');
                if (sizeParts.Length == 2 && int.TryParse(sizeParts[0], out _) && int.TryParse(sizeParts[1], out _))
                {
                    preScaleFilter = $"[0:v]scale={sizeParts[0]}:{sizeParts[1]}[scaled_in]; ";
                    videoInputNode = "[scaled_in]";
                }
            }
        }

        string filterString;
        if (hasLogo)
        {
            int logoInputIndex = (config.EnableAudio && !isLocalFile && !isMpegTs && !string.IsNullOrEmpty(config.AudioDevice)) ? 2 : 1;
            
            int ml = config.LogoMarginLeft ?? 10;
            int mt = config.LogoMarginTop ?? 10;
            int mr = config.LogoMarginRight ?? 10;
            int mb = config.LogoMarginBottom ?? 10;

            string overlayCoords = config.LogoPosition?.ToLowerInvariant() switch
            {
                "top right" => $"main_w-overlay_w-{mr}:{mt}",
                "bottom left" => $"{ml}:main_h-overlay_h-{mb}",
                "bottom right" => $"main_w-overlay_w-{mr}:main_h-overlay_h-{mb}",
                _ => $"{ml}:{mt}" // "top left"
            };

            if ((config.LogoWidth ?? 0) > 0 || (config.LogoHeight ?? 0) > 0)
            {
                var w = (config.LogoWidth ?? 0) > 0 ? config.LogoWidth.ToString() : "-1";
                var h = (config.LogoHeight ?? 0) > 0 ? config.LogoHeight.ToString() : "-1";
                filterString = $"\"{preScaleFilter}[{logoInputIndex}:v]scale={w}:{h}[scaled_logo]; {videoInputNode}[scaled_logo]overlay={overlayCoords}[temp_v]; [temp_v]{overlayTextChain}[outv]\"";
            }
            else
            {
                filterString = $"\"{preScaleFilter}{videoInputNode}[{logoInputIndex}:v]overlay={overlayCoords}[temp_v]; [temp_v]{overlayTextChain}[outv]\"";
            }
        }
        else
        {
            filterString = $"\"{preScaleFilter}{videoInputNode}{overlayTextChain}[outv]\"";
        }

        args.AddRange(new[] { "-filter_complex", filterString });
        args.AddRange(new[] { "-map", "[outv]" });

        if (config.EnableAudio)
        {
            string audioMap = (config.EnableAudio && !isLocalFile && !isMpegTs && !string.IsNullOrEmpty(config.AudioDevice)) ? "1:a" : "0:a";
            args.AddRange(new[] { "-map", audioMap });
        }

        // Video encoding
        args.AddRange(new[] { "-c:v", config.VideoCodec });
        if (isLocalFile)
        {
            if (!string.IsNullOrEmpty(config.VideoSize) && !(isNvidiaCodec && isCompressedInput))
            {
                args.AddRange(new[] { "-s", config.VideoSize });
            }
            args.AddRange(new[] { "-r", config.FrameRate.ToString() });
        }
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
        
        if (isNvidiaCodec)
        {
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
        }
        
        // Audio encoding if enabled
        if (config.EnableAudio)
        {
            args.AddRange(new[] { "-c:a", config.AudioCodec });
            args.AddRange(new[] { "-b:a", config.AudioBitrate });
            args.AddRange(new[] { "-ac", "2" });
            
            // Apply volume if not 100%
            if (config.AudioVolume != 100)
            {
                // Volume filter format: volume=1.5
                double vol = config.AudioVolume / 100.0;
                args.AddRange(new[] { $"-filter:a", $"\"volume={vol}\"" });
            }
        }
        
        // Output configuration
        args.AddRange(new[] { "-f", config.OutputFormat });
        args.AddRange(new[] { "-flags", "+global_header" });
        if (hasLogo && isLocalFile)
        {
            args.Add("-shortest");
        }
        
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
            if (output.Contains("Duration: "))
            {
                var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+)(?:\.(\d+))?");
                if (match.Success)
                {
                    var hours = int.Parse(match.Groups[1].Value);
                    var minutes = int.Parse(match.Groups[2].Value);
                    var seconds = int.Parse(match.Groups[3].Value);
                    var ms = 0;
                    if (match.Groups[4].Success)
                    {
                        var msStr = match.Groups[4].Value;
                        if (msStr.Length == 1) ms = int.Parse(msStr) * 100;
                        else if (msStr.Length == 2) ms = int.Parse(msStr) * 10;
                        else if (msStr.Length >= 3) ms = int.Parse(msStr.Substring(0, 3));
                    }
                    
                    if (_processes.TryGetValue(configId, out var proc))
                    {
                        // Only set if not already set, to prevent logo inputs (Input #1) from overwriting main video (Input #0) duration
                        if (proc.TotalDuration == null || proc.TotalDuration == TimeSpan.Zero)
                        {
                            proc.TotalDuration = new TimeSpan(0, hours, minutes, seconds, ms);
                        }
                    }
                }
            }
            if (output.Contains("frame=") && output.Contains("fps="))
            {
                await UpdateStreamStats(configId, output);
            }
            
            // Log the output
            if (_processes.TryGetValue(configId, out var streamProcess))
            {
                await AppendToLogAsync(streamProcess, $"[{DateTime.Now:HH:mm:ss}] {output}\n");
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
                
                var config = streamProcess.Config;
                if (config.InputFormat?.ToLower() == "folder")
                {
                    var state = streamProcess.FolderState;
                    if (state != null)
                    {
                        if (config.Shuffle)
                        {
                            state.PlayedVideos.Add(new PlayedVideoInfo
                            {
                                VideoPath = state.CurrentVideoPath,
                                VideoName = state.CurrentVideoName,
                                PlayedAt = DateTime.UtcNow
                            });
                        }
                        
                        var nextVideo = state.UpcomingVideoPath;
                        state.ResumePositionSeconds = 0;

                        try
                        {
                            SetupNextVideoForFolder(configId, config, state, nextVideo);

                            var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
                            if (!Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                            var filePath = Path.Combine(dir, $"{configId}.json");
                            var json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(filePath, json);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to setup next video for stream {configId}");
                        }
                    }

                    _logger.LogInformation($"Transitioning to next video for stream {configId}");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            lock (_processLock)
                            {
                                _processes.Remove(configId);
                            }
                            await StartStreamAsync(configId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to auto-transition folder playback for stream {configId}");
                        }
                    });
                    return;
                }

                // Auto-restart logic
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

    private void SavePlaybackStates(object? state)
    {
        try
        {
            List<StreamProcess> activeFolderProcesses;
            lock (_processLock)
            {
                activeFolderProcesses = _processes.Values
                    .Where(p => p.Config.InputFormat?.ToLower() == "folder")
                    .ToList();
            }

            foreach (var sp in activeFolderProcesses)
            {
                if (sp.FolderState == null) continue;

                double currentPosition = sp.FolderState.ResumePositionSeconds;
                if (sp.LastStats != null && sp.LastStats.TryGetValue("time", out var timeVal))
                {
                    if (TimeSpan.TryParse(timeVal.ToString(), out var parsedTime))
                    {
                        currentPosition = sp.StartSeekPositionSeconds + parsedTime.TotalSeconds;
                    }
                }

                sp.FolderState.ResumePositionSeconds = currentPosition;
                sp.FolderState.LastSaved = DateTime.UtcNow;

                var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var filePath = Path.Combine(dir, $"{sp.Config.Id}.json");
                var json = JsonSerializer.Serialize(sp.FolderState, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving playback states");
        }
    }

    public FolderPlaybackState? GetFolderPlaybackState(string configId)
    {
        try
        {
            lock (_processLock)
            {
                if (_processes.TryGetValue(configId, out var sp) && sp.FolderState != null)
                {
                    if (sp.LastStats != null && sp.LastStats.TryGetValue("time", out var timeVal))
                    {
                        if (TimeSpan.TryParse(timeVal.ToString(), out var parsedTime))
                        {
                            sp.FolderState.ResumePositionSeconds = sp.StartSeekPositionSeconds + parsedTime.TotalSeconds;
                        }
                    }
                    return sp.FolderState;
                }
            }

            var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
            var filePath = Path.Combine(dir, $"{configId}.json");
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<FolderPlaybackState>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting folder playback state for {configId}");
        }
        return null;
    }

    public async Task ClearFolderPlaybackHistoryAsync(string configId)
    {
        var state = GetFolderPlaybackState(configId);
        if (state != null)
        {
            state.PlayedVideos.Clear();
            
            var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var filePath = Path.Combine(dir, $"{configId}.json");
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);

            lock (_processLock)
            {
                if (_processes.TryGetValue(configId, out var sp))
                {
                    sp.FolderState = state;
                }
            }
        }
    }

    public TimeSpan? GetTotalDuration(string configId)
    {
        lock (_processLock)
        {
            if (_processes.TryGetValue(configId, out var sp))
                return sp.TotalDuration;
        }
        return null;
    }

    public async Task SeekStreamAsync(string configId, double offsetSeconds)
    {
        var config = await _configService.GetConfigAsync(configId);
        if (config == null) return;

        var state = GetFolderPlaybackState(configId);
        if (state == null) return;

        double totalDurationSeconds = GetTotalDuration(configId)?.TotalSeconds ?? 0;
        
        // Find current position from active process if possible
        double currentPosition = state.ResumePositionSeconds;
        lock (_processLock)
        {
            if (_processes.TryGetValue(configId, out var sp))
            {
                if (sp.LastStats != null && sp.LastStats.TryGetValue("time", out var timeVal))
                {
                    if (TimeSpan.TryParse(timeVal.ToString(), out var parsedTime))
                    {
                        currentPosition = sp.StartSeekPositionSeconds + parsedTime.TotalSeconds;
                    }
                }
            }
        }

        double newPosition = currentPosition + offsetSeconds;

        if (totalDurationSeconds > 0)
        {
            if (newPosition >= totalDurationSeconds)
            {
                // Go to next video!
                await PlayNextVideoAsync(configId);
                return;
            }
        }

        if (newPosition < 0)
        {
            newPosition = 0;
        }

        state.ResumePositionSeconds = newPosition;

        // Save immediately
        var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var filePath = Path.Combine(dir, $"{configId}.json");
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);

        // Restart stream if running, otherwise just save state
        bool isRunning;
        lock (_processLock)
        {
            isRunning = _processes.ContainsKey(configId);
        }
        if (isRunning)
        {
            await RestartStreamAsync(configId);
        }
    }

    public async Task PlayNextVideoAsync(string configId)
    {
        var config = await _configService.GetConfigAsync(configId);
        if (config == null) return;

        var state = GetFolderPlaybackState(configId);
        if (state == null) return;

        if (config.Shuffle)
        {
            state.PlayedVideos.Add(new PlayedVideoInfo
            {
                VideoPath = state.CurrentVideoPath,
                VideoName = state.CurrentVideoName,
                PlayedAt = DateTime.UtcNow
            });
        }

        var nextVideo = state.UpcomingVideoPath;
        state.ResumePositionSeconds = 0;

        SetupNextVideoForFolder(configId, config, state, nextVideo);

        var dir = Path.Combine(AppContext.BaseDirectory, "logs", "playback_status");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var filePath = Path.Combine(dir, $"{configId}.json");
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);

        await RestartStreamAsync(configId);
    }

    private void SetupNextVideoForFolder(string configId, StreamConfig config, FolderPlaybackState state, string? currentOverride = null)
    {
        var folderPath = config.InputDevice;
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder path not found: {folderPath}");
        }

        var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".ts", ".m2ts", ".flv", ".webm" };
        var videoFiles = Directory.GetFiles(folderPath)
            .Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLower()))
            .OrderBy(f => f)
            .ToList();

        if (videoFiles.Count == 0)
        {
            throw new FileNotFoundException($"No video files found in folder: {folderPath}");
        }

        string currentVideo;
        var normalizedCurrentPath = string.IsNullOrEmpty(state.CurrentVideoPath) ? "" : Path.GetFullPath(state.CurrentVideoPath).ToLowerInvariant();
        var matchingFile = videoFiles.FirstOrDefault(f => Path.GetFullPath(f).ToLowerInvariant() == normalizedCurrentPath);

        if (!string.IsNullOrEmpty(currentOverride))
        {
            var normalizedOverride = Path.GetFullPath(currentOverride).ToLowerInvariant();
            currentVideo = videoFiles.FirstOrDefault(f => Path.GetFullPath(f).ToLowerInvariant() == normalizedOverride) ?? videoFiles[0];
            state.ResumePositionSeconds = 0;
        }
        else if (matchingFile != null)
        {
            currentVideo = matchingFile;
        }
        else
        {
            state.ResumePositionSeconds = 0;
            if (config.Shuffle)
            {
                var unplayed = videoFiles.Where(f => !state.PlayedVideos.Any(pv => Path.GetFullPath(pv.VideoPath).ToLowerInvariant() == Path.GetFullPath(f).ToLowerInvariant())).ToList();
                if (unplayed.Count == 0)
                {
                    state.PlayedVideos.Clear();
                    unplayed = videoFiles;
                }
                var rnd = new Random();
                currentVideo = unplayed[rnd.Next(unplayed.Count)];
            }
            else
            {
                currentVideo = videoFiles[0];
            }
        }

        state.CurrentVideoPath = currentVideo;
        state.CurrentVideoName = Path.GetFileName(currentVideo);

        string upcomingVideo;
        if (config.Shuffle)
        {
            var unplayed = videoFiles
                .Where(f => f != currentVideo && !state.PlayedVideos.Any(pv => Path.GetFullPath(pv.VideoPath).ToLowerInvariant() == Path.GetFullPath(f).ToLowerInvariant()))
                .ToList();
            if (unplayed.Count == 0)
            {
                unplayed = videoFiles.Where(f => f != currentVideo).ToList();
                if (unplayed.Count == 0)
                {
                    unplayed = videoFiles;
                }
            }
            var rnd = new Random();
            upcomingVideo = unplayed[rnd.Next(unplayed.Count)];
        }
        else
        {
            var currentIndex = videoFiles.IndexOf(currentVideo);
            var upcomingIndex = (currentIndex + 1) % videoFiles.Count;
            upcomingVideo = videoFiles[upcomingIndex];
        }

        state.UpcomingVideoPath = upcomingVideo;
        state.UpcomingVideoName = Path.GetFileName(upcomingVideo);
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
    public TimeSpan? TotalDuration { get; set; }
    public FolderPlaybackState? FolderState { get; set; }
    public double StartSeekPositionSeconds { get; set; }

    public void Dispose()
    {
        LogLock.Dispose();
        Process?.Dispose();
    }
}