using HeadendStreamer.Web.Models.Entities;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HeadendStreamer.Web.Services;

public class FfmpegService
{
    private readonly ILogger<FfmpegService> _logger;
    
    public FfmpegService(ILogger<FfmpegService> logger)
    {
        _logger = logger;
    }
    
    public async Task<bool> TestInputDeviceAsync(string devicePath)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f v4l2 -list_formats all -i {devicePath}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(processInfo);
            if (process == null)
                return false;
            
            var output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            return process.ExitCode == 0 && !output.Contains("Cannot open video device");
        }
        catch
        {
            return false;
        }
    }
    
    public async Task<List<VideoDeviceInfo>> GetVideoDevicesAsync()
    {
        var devices = new List<VideoDeviceInfo>();
        
        try
        {
            // Check /dev/video* devices
            var videoDevices = Directory.GetFiles("/dev", "video*")
                .OrderBy(d => d)
                .ToArray();
            
            foreach (var device in videoDevices)
            {
                var info = await GetDeviceInfoAsync(device);
                if (info != null)
                    devices.Add(info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing video devices");
        }
        
        return devices;
    }
    
    public async Task<VideoDeviceInfo?> GetDeviceInfoAsync(string devicePath)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f v4l2 -list_formats all -i {devicePath}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(processInfo);
            if (process == null)
                return null;
            
            var output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            var deviceInfo = new VideoDeviceInfo
            {
                DevicePath = devicePath,
                IsAvailable = process.ExitCode == 0
            };
            
            if (deviceInfo.IsAvailable)
            {
                // Parse device information
                deviceInfo.Name = ExtractDeviceName(output);
                deviceInfo.Formats = ExtractVideoFormats(output);
                deviceInfo.Resolutions = ExtractResolutions(output);
            }
            
            return deviceInfo;
        }
        catch
        {
            return null;
        }
    }
    
    public async Task<bool> VerifyPathAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // Remove quotes if present
            path = path.Trim('"');

            return File.Exists(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error verifying path: {path}");
            return false;
        }
    }
    
    public async Task<bool> TestStreamOutputAsync(StreamConfig config, int timeoutSeconds = 10)
    {
        try
        {
            var command = BuildTestCommand(config);
            
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = Process.Start(processInfo);
            if (process == null)
                return false;
            
            // Wait for process to start producing output
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line != null && line.Contains("frame="))
                        return true; // Stream is producing frames
                    
                    await Task.Delay(100, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout
            }
            
            process.Kill();
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    private string BuildTestCommand(StreamConfig config)
    {
        // Build a short test command that runs for a few seconds
        var baseCmd = BuildFfmpegCommand(config);
        return $"{baseCmd} -t 5 -f null -"; // Run for 5 seconds, output to null
    }
    
    private string BuildFfmpegCommand(StreamConfig config)
    {
        // Similar to StreamManagerService.BuildFfmpegCommand
        // but simplified for testing
        var args = new List<string>
        {
            "-f", "v4l2",
            "-input_format", config.InputFormat,
            "-video_size", config.VideoSize,
            "-framerate", config.FrameRate.ToString(),
            "-i", config.InputDevice,
            "-c:v", config.VideoCodec,
            "-preset", "ultrafast", // Use ultrafast for testing
            "-tune", config.Tune,
            "-b:v", "1000k", // Low bitrate for testing
            "-f", config.OutputFormat
        };
        
        return string.Join(" ", args);
    }
    
    private string ExtractDeviceName(string ffmpegOutput)
    {
        var match = Regex.Match(ffmpegOutput, @"\[.*?\]\s*(.*?)\s*\(.*?\)");
        return match.Success ? match.Groups[1].Value : "Unknown Device";
    }
    
    private List<string> ExtractVideoFormats(string ffmpegOutput)
    {
        var formats = new List<string>();
        var matches = Regex.Matches(ffmpegOutput, @"\[\d+\]\s*('.*?'|RAW)\s*:\s*(.*?)\s*\\n");
        
        foreach (Match match in matches)
        {
            if (match.Groups.Count >= 3)
                formats.Add(match.Groups[2].Value);
        }
        
        return formats.Distinct().ToList();
    }
    
    private List<string> ExtractResolutions(string ffmpegOutput)
    {
        var resolutions = new List<string>();
        var matches = Regex.Matches(ffmpegOutput, @"(\d+x\d+)(?:\s*\(\d+\.\d+fps\))?");
		        foreach (Match match in matches)
        {
            if (match.Groups.Count >= 1)
                resolutions.Add(match.Groups[1].Value);
        }
        
        return resolutions.Distinct().OrderBy(r => r).ToList();
    }
}

public class VideoDeviceInfo
{
    public string DevicePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public List<string> Formats { get; set; } = new();
    public List<string> Resolutions { get; set; } = new();
    public List<string> FrameRates { get; set; } = new() { "24", "25", "30", "50", "60" };
}