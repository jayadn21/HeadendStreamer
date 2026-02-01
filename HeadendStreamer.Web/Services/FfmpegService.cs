using HeadendStreamer.Web.Models.Entities;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System;

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
            var ffmpegPath = _configuration["HeadendStreamer:FfmpegPath"] ?? "ffmpeg";
            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
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
                
                _logger.LogInformation($"Found {videoDevices.Length} video devices in /dev: {string.Join(", ", videoDevices)}");

                foreach (var device in videoDevices)
                {
                    _logger.LogInformation($"Probing device: {device}");
                    var info = await GetDeviceInfoAsync(device);
                    if (info != null)
                    {
                        devices.Add(info);
                        _logger.LogInformation($"Successfully added device: {device}");
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to get info for device: {device}");
                    }
                }

                // Also get audio devices
                var audioDevices = await GetLinuxAudioDevicesAsync();
                devices.AddRange(audioDevices);
            }
            else
            {
                _logger.LogError("/dev directory does not exist");
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
            _logger.LogInformation("===> List Devices");
            _logger.LogInformation(processInfo.Arguments);

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
            Console.WriteLine(ex.Message);
        }
        return devices;
    }
    
    public async Task<VideoDeviceInfo?> GetDeviceInfoAsync(string devicePath)
    {
        try
        {
            var ffmpegPath = _configuration["HeadendStreamer:FfmpegPath"] ?? "ffmpeg";
            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f v4l2 -list_formats all -i {devicePath}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _logger.LogInformation("===> List Devices");
            _logger.LogInformation(processInfo.Arguments);
            
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
            
            _logger.LogInformation($"FFmpeg exit code for {devicePath}: {process.ExitCode}. Output length: {output.Length}");
            
            // Try to get a better name from sysfs regardless of availability
            string friendlyName = await GetLinuxDeviceNameAsync(devicePath);
            
            // Parse device information from ffmpeg output as secondary source
            var ffmpegName = ExtractDeviceName(output);
            
            // Prioritize sysfs name, then ffmpeg name (if valid), then fallback to path
            if (!string.IsNullOrEmpty(friendlyName))
            {
                deviceInfo.Name = friendlyName;
                _logger.LogInformation($"Using sysfs name: '{friendlyName}' for {devicePath}");
            }
            else if (!string.IsNullOrEmpty(ffmpegName) && ffmpegName != "Unknown Device")
            {
                deviceInfo.Name = ffmpegName;
                _logger.LogInformation($"Using ffmpeg name: '{ffmpegName}' for {devicePath}");
            }
            else
            {
                deviceInfo.Name = devicePath; // Fallback to /dev/videoX
                _logger.LogInformation($"Using fallback path name: '{devicePath}' for {devicePath}");
            }
            
            _logger.LogInformation($"Final resolved name for {devicePath}: '{deviceInfo.Name}' (Length: {deviceInfo.Name.Length})");

            if (deviceInfo.IsAvailable)
            {
                deviceInfo.Formats = ExtractVideoFormats(output);
                deviceInfo.Resolutions = ExtractResolutions(output);
            }
            
            return deviceInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception in GetDeviceInfoAsync for {devicePath}");
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

            // Linux V4L2 handling
            if (inputFormat == "v4l2")
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f v4l2 -list_formats all -i \"{devicePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Fix for shared builds: Add ../lib to LD_LIBRARY_PATH
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

                using var process = Process.Start(processInfo);
                if (process == null) return null;

                // var output = await process.StandardError.ReadToEndAsync();
                // await process.WaitForExitAsync();

                var output = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                _logger.LogInformation($"V4L2 Options Output for {devicePath}:\n{output}");

                var result = new DeviceOptions
                {
                    DeviceName = deviceName,
                    OptionCombinations = ParseLinuxDeviceOptions(output)
                };
                
                if (process.ExitCode != 0)
                {
                    result.Error = $"FFmpeg exited with code {process.ExitCode}. This usually means missing libraries or incompatible binary. Output: {output}";
                }
                
                return result;
            }

            // Windows dshow handling (existing logic)
            var dshowProcessInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-list_options true -f {inputFormat} -i \"{devicePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var dshowProcess = Process.Start(dshowProcessInfo);
            if (dshowProcess == null)
                return null;

            var dshowOutput = await dshowProcess.StandardError.ReadToEndAsync();
            await dshowProcess.WaitForExitAsync();

            // Parse the output to extract device option combinations
            var options = new DeviceOptions
            {
                DeviceName = deviceName,
                OptionCombinations = ParseDeviceOptionCombinations(dshowOutput)
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

    private List<DeviceOptionCombination> ParseLinuxDeviceOptions(string ffmpegOutput)
    {
        var combinations = new List<DeviceOptionCombination>();
        try
        {
            var lines = ffmpegOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            _logger.LogInformation($"Parsing {lines.Length} lines of V4L2 output");
            
            foreach (var line in lines)
            {
                // Format example: [video4linux2,v4l2 @ 0x...] Raw       :     yuyv422 :           YUYV 4:2:2 : 640x480 320x240
                // Format example: [video4linux2,v4l2 @ 0x...] Compressed:       mjpeg :          Motion-JPEG : 1280x720 640x480
                
                if (!line.Contains(" : ")) continue;

                var parts = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    var codec = parts[1].Trim(); // e.g., mjpeg or yuyv422
                    var resolutionsPart = parts[parts.Length - 1].Trim(); // The last part contains resolutions
                    
                    _logger.LogInformation($"Found codec: {codec}, Resolutions: {resolutionsPart}");
                    
                    var resolutions = resolutionsPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var res in resolutions)
                    {
                        if (Regex.IsMatch(res, @"^\d+x\d+$"))
                        {
                            combinations.Add(new DeviceOptionCombination
                            {
                                Codec = codec,
                                Resolution = res,
                                FrameRate = "30", // Default as V4L2 list_formats often doesn't show FPS explicitly per line here
                                DisplayText = $"{codec} - {res}"
                            });
                        }
                    }
                }
            }
            
            _logger.LogInformation($"Found {combinations.Count} combinations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Linux device options");
        }
        return combinations.OrderBy(c => c.DisplayText).ToList();
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

    private async Task<string> GetLinuxDeviceNameAsync(string devicePath)
    {
        try
        {
            // devicePath is like /dev/video0
            var deviceName = Path.GetFileName(devicePath); // video0
            var namePath = $"/sys/class/video4linux/{deviceName}/name";
            
            if (File.Exists(namePath))
            {
                var name = await File.ReadAllTextAsync(namePath);
                return name.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to read device name from sysfs for {devicePath}");
        }
        return string.Empty;
    }
    private async Task<List<VideoDeviceInfo>> GetLinuxAudioDevicesAsync()
    {
        var devices = new List<VideoDeviceInfo>();
        try
        {
            var ffmpegPath = _configuration["HeadendStreamer:FfmpegPath"] ?? "ffmpeg";
            var processInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-sources pulse",
                RedirectStandardOutput = true,
                RedirectStandardError = true, // sources output often goes to stderr or stdout depending on version, capturing both is safer or checking which one
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Fix for shared builds: Add ../lib to LD_LIBRARY_PATH
            var ffmpegDir = Path.GetDirectoryName(ffmpegPath);
            if (ffmpegDir != null)
            {
                var libDir = Path.GetFullPath(Path.Combine(ffmpegDir, "../lib"));
                if (Directory.Exists(libDir))
                {
                    var currentLdPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                    processInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = $"{libDir}:{currentLdPath}";
                }
            }

            using var process = Process.Start(processInfo);
            if (process == null) return devices;

            // ffmpeg -sources output usually comes effectively on stdout, but sometimes mixed.
            // The command we tested specifically sent stderr to file, so it might be on stderr? 
            // ffmpeg generally prints info on stderr, but -sources might be stdout.
            // Let's read both to be safe.
            
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            var output = stdoutTask.Result + "\n" + stderrTask.Result;
            
            // Example Pulse output:
            // Auto-detected sources for pulse:
            //   auto_null.monitor [Monitor of Dummy Output] (none)
            // * alsa_input.usb-MACROSILICON_USB3._0_capture-02.analog-stereo [USB3. 0 capture Analog Stereo] (none)

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("Auto-detected")) continue;

                // Regex to match: [* ] device_id [Description] ...
                // Matches optional *, then whitespace, then non-whitespace device ID, then whitespace, then [Description]
                var match = Regex.Match(line, @"^\s*\*?\s*(\S+)\s+\[(.*?)\]");
                
                if (match.Success)
                {
                    var deviceId = match.Groups[1].Value;
                    var description = match.Groups[2].Value;
                    
                    // Skip monitors if we only want real inputs? Usually monitors are useless for capture unless loopback.
                    // But user might want it. Let's include everything but maybe prioritize real hardware?
                    // For now, simple list.
                    
                    devices.Add(new VideoDeviceInfo
                    {
                        Name = description,
                        DevicePath = deviceId, // For pulse, the device is passed as -i "default" or -i "device_id" with -f pulse
                        Type = "audio",
                        IsAvailable = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Linux audio devices via PulseAudio");
        }
        return devices;
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
    public string? Error { get; set; }
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