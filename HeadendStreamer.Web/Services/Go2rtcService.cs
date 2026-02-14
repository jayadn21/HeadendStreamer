using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace HeadendStreamer.Web.Services;

public class Go2rtcService
{
    private readonly ILogger<Go2rtcService> _logger;
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string _configPath;
    private Process? _process;

    public Go2rtcService(ILogger<Go2rtcService> logger, IConfiguration configuration)
    {
        _logger = logger;
        
        var rootDir = "/mnt/JC_Data/Simpfo/Src/Siti/HeadendStreamer";
        _executablePath = Path.Combine(rootDir, "3p-tools", "go2rtc", "go2rtc_linux_amd64");
        _workingDirectory = Path.Combine(rootDir, "3p-tools", "go2rtc");
        _configPath = Path.Combine(_workingDirectory, "go2rtc.yaml");
    }

    public async Task<List<string>> GetStreamsAsync()
    {
        var streams = new List<string>();
        try
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogWarning($"Config file not found: {_configPath}");
                return streams;
            }

            var lines = await File.ReadAllLinesAsync(_configPath);
            bool inStreamsSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                if (trimmed.StartsWith("streams:"))
                {
                    inStreamsSection = true;
                    continue;
                }

                if (inStreamsSection)
                {
                    // If we encounter a line that is not indented, we are out of the streams section
                    // (Assuming standard YAML indentation)
                    if (!line.StartsWith(" ") && !line.StartsWith("\t") && !string.IsNullOrWhiteSpace(line))
                    {
                        // Some other root level key
                        if (trimmed.Contains(":"))
                        {
                            inStreamsSection = false;
                            continue;
                        }
                    }

                    // Look for keys under streams:
                    // e.g., "  udp_stream: " or "  udp_stream_noaudio: ..."
                    var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^([^:#\s]+)\s*:");
                    if (match.Success)
                    {
                        streams.Add(match.Groups[1].Value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing go2rtc.yaml");
        }
        return streams;
    }

    public async Task<bool> IsRunningAsync()
    {
        if (_process != null && !_process.HasExited)
        {
            return true;
        }

        // Double check by process name in case it was started outside or lost reference
        var processes = Process.GetProcessesByName("go2rtc_linux_amd64");
        return processes.Length > 0;
    }

    public async Task<bool> StartAsync()
    {
        if (await IsRunningAsync())
        {
            _logger.LogInformation("go2rtc is already running.");
            return true;
        }

        try
        {
            _logger.LogInformation($"Starting go2rtc from {_executablePath}");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _process = new Process { StartInfo = startInfo };
            
            _process.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogInformation($"go2rtc: {e.Data}"); };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger.LogWarning($"go2rtc error: {e.Data}"); };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start go2rtc");
            return false;
        }
    }

    public async Task<bool> StopAsync()
    {
        try
        {
            // Kill all instances to ensure none are left
            var processes = Process.GetProcessesByName("go2rtc_linux_amd64");
            foreach (var p in processes)
            {
                _logger.LogInformation($"Killing go2rtc process {p.Id}");
                p.Kill();
                await p.WaitForExitAsync();
            }

            _process = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop go2rtc");
            return false;
        }
    }
}
