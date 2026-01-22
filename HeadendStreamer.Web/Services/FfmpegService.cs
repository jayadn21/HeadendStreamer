using HeadendStreamer.Web.Models.Entities;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HeadendStreamer.Web.Services;

public class FfmpegService
{
    private readonly ILogger<FfmpegService> _logger;
    private readonly IConfiguration _configuration;
    
    public FfmpegService(ILogger<FfmpegService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return await GetWindowsDevicesAsync();
            }

            // Check /dev/video* devices (Linux)
            if (Directory.Exists("/dev"))
            {
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing video devices");
        }
        
        return devices;
    }

    private async Task<List<VideoDeviceInfo>> GetWindowsDevicesAsync()
    {
        var devices = new List<VideoDeviceInfo>();
        try
        {
            var ffmpegPath = _configuration["HeadendStreamer:FfmpegPath"] ?? "ffmpeg";
            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-list_devices true -f dshow -i dummy",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null) return devices;

            var output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse dshow output
            // Example lines:
            // [dshow @ 0000021c5b8e4f00]  "Integrated Camera"
            // [dshow @ 0000021c5b8e4f00]     Alternative name "@device_pnp_\\?\usb#vid_04f2&pid_b6d0&mi_00#6&36e8b26a&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
            
            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                // Skip alternative name entries which are cryptic IDs
                if (line.Contains("Alternative name"))
                    continue;

                // Look for device names in quotes followed by (video) or (audio)
                // Example: [dshow @ 000001] "Integrated Camera" (video)
                var match = Regex.Match(line, "\"([^\"]+)\"\\s+\\((video|audio)\\)");
                if (match.Success)
                {
                    var deviceName = match.Groups[1].Value;
                    var type = match.Groups[2].Value;

                    devices.Add(new VideoDeviceInfo
                    {
                        Name = deviceName,
                        DevicePath = deviceName,
                        IsAvailable = true,
                        Type = type
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Windows video devices");
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

    public async Task<DeviceOptions?> GetDeviceOptionsAsync(string deviceName, string inputFormat = "dshow")
    {
        try
        {
            var ffmpegPath = _configuration["HeadendStreamer:FfmpegPath"] ?? "ffmpeg";
            
            // Construct the device path based on input format
            string devicePath = deviceName;
            if (inputFormat == "dshow" && !deviceName.StartsWith("video="))
            {
                devicePath = $"video={deviceName}";
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-list_options true -f {inputFormat} -i \"{devicePath}\"",
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

            // Parse the output to extract device option combinations
            var options = new DeviceOptions
            {
                DeviceName = deviceName,
                OptionCombinations = ParseDeviceOptionCombinations(output)
            };

            return options;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting device options for {deviceName}");
            return null;
        }
    }

    private List<DeviceOptionCombination> ParseDeviceOptionCombinations(string ffmpegOutput)
    {
        var combinations = new List<DeviceOptionCombination>();
        
        try
        {
            var lines = ffmpegOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                // Look for lines with vcodec or pixel_format
                // Example: vcodec=mjpeg  min s=1280x720 fps=30 max s=1280x720 fps=30
                // Example: pixel_format=yuyv422  min s=640x480 fps=30 max s=640x480 fps=30
                
                var vcodecMatch = Regex.Match(line, @"vcodec=(\w+)\s+min s=(\d+x\d+)\s+fps=(\d+)\s+max s=(\d+x\d+)\s+fps=(\d+)");
                var pixelFormatMatch = Regex.Match(line, @"pixel_format=(\w+)\s+min s=(\d+x\d+)\s+fps=(\d+)\s+max s=(\d+x\d+)\s+fps=(\d+)");
                
                if (vcodecMatch.Success)
                {
                    var codec = vcodecMatch.Groups[1].Value;
                    var minRes = vcodecMatch.Groups[2].Value;
                    var minFps = vcodecMatch.Groups[3].Value;
                    var maxRes = vcodecMatch.Groups[4].Value;
                    var maxFps = vcodecMatch.Groups[5].Value;
                    
                    combinations.Add(new DeviceOptionCombination
                    {
                        Codec = codec,
                        PixelFormat = "",
                        Resolution = minRes, 
                        FrameRate = minFps,
                        DisplayText = $"{codec} - {minRes} @ {minFps} fps"
                    });

                    if (maxRes != minRes || maxFps != minFps)
                    {
                        combinations.Add(new DeviceOptionCombination
                        {
                            Codec = codec,
                            PixelFormat = "",
                            Resolution = maxRes,
                            FrameRate = maxFps,
                            DisplayText = $"{codec} - {maxRes} @ {maxFps} fps"
                        });
                    }
                }
                else if (pixelFormatMatch.Success)
                {
                    var pixelFormat = pixelFormatMatch.Groups[1].Value;
                    var minRes = pixelFormatMatch.Groups[2].Value;
                    var minFps = pixelFormatMatch.Groups[3].Value;
                    var maxRes = pixelFormatMatch.Groups[4].Value;
                    var maxFps = pixelFormatMatch.Groups[5].Value;
                    
                    combinations.Add(new DeviceOptionCombination
                    {
                        Codec = "",
                        PixelFormat = pixelFormat,
                        Resolution = minRes,
                        FrameRate = minFps,
                        DisplayText = $"{pixelFormat} - {minRes} @ {minFps} fps"
                    });

                    if (maxRes != minRes || maxFps != minFps)
                    {
                        combinations.Add(new DeviceOptionCombination
                        {
                            Codec = "",
                            PixelFormat = pixelFormat,
                            Resolution = maxRes,
                            FrameRate = maxFps,
                            DisplayText = $"{pixelFormat} - {maxRes} @ {maxFps} fps"
                        });
                    }
                }
            }
            
            // Remove duplicates based on DisplayText
            combinations = combinations
                .GroupBy(c => c.DisplayText)
                .Select(g => g.First())
                .OrderBy(c => c.Resolution)
                .ThenBy(c => int.Parse(c.FrameRate))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing device option combinations");
        }
        
        return combinations;
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
    public string Type { get; set; } = "video"; // "video" or "audio"
    public bool IsAvailable { get; set; }
    public List<string> Formats { get; set; } = new();
    public List<string> Resolutions { get; set; } = new();
    public List<string> FrameRates { get; set; } = new() { "24", "25", "30", "50", "60" };
}

public class DeviceOptions
{
    public string DeviceName { get; set; } = string.Empty;
    public List<DeviceOptionCombination> OptionCombinations { get; set; } = new();
}

public class DeviceOptionCombination
{
    public string Codec { get; set; } = string.Empty;
    public string PixelFormat { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string FrameRate { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
}