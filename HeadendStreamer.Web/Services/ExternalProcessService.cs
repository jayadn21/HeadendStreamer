using System.Diagnostics;
using HeadendStreamer.Web.Models.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HeadendStreamer.Web.Services;

public class ExternalServiceStatus
{
    public string ServiceName { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public int? ProcessId { get; set; }
    public string ServerURL { get; set; } = string.Empty;
    public TimeSpan Uptime { get; set; }
    public DateTime? StartTime { get; set; }
}

public class ExternalProcessService
{
    private readonly ILogger<ExternalProcessService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, Process> _trackedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _startTimes = new(StringComparer.OrdinalIgnoreCase);

    public ExternalProcessService(ILogger<ExternalProcessService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public ExternalServiceConfig GetServiceConfig(string serviceName)
    {
        var section = _configuration.GetSection($"HeadendStreamer:{serviceName}");
        return new ExternalServiceConfig
        {
            ExecutablePath = section.GetValue<string>("ExecutablePath") ?? "",
            ExePath = section.GetValue<string>("ExePath") ?? "",
            ServerURL = section.GetValue<string>("ServerURL") ?? ""
        };
    }

    public async Task<ExternalServiceStatus> GetStatusAsync(string serviceName)
    {
        var config = GetServiceConfig(serviceName);
        var existingProcess = FindRunningProcess(serviceName, config);

        if (existingProcess != null)
        {
            DateTime startTime;
            try
            {
                startTime = existingProcess.StartTime;
            }
            catch
            {
                if (!_startTimes.TryGetValue(serviceName, out startTime))
                {
                    startTime = DateTime.Now;
                }
            }

            return new ExternalServiceStatus
            {
                ServiceName = serviceName,
                IsRunning = true,
                ProcessId = existingProcess.Id,
                ServerURL = config.ServerURL,
                StartTime = startTime,
                Uptime = DateTime.Now - startTime
            };
        }

        return new ExternalServiceStatus
        {
            ServiceName = serviceName,
            IsRunning = false,
            ProcessId = null,
            ServerURL = config.ServerURL,
            Uptime = TimeSpan.Zero
        };
    }

    public async Task<bool> StartAsync(string serviceName)
    {
        var config = GetServiceConfig(serviceName);
        if (string.IsNullOrWhiteSpace(config.ExePath))
        {
            _logger.LogError($"Cannot start {serviceName}: ExePath is empty.");
            return false;
        }

        var existingProcess = FindRunningProcess(serviceName, config);
        if (existingProcess != null)
        {
            _logger.LogInformation($"{serviceName} is already running with PID {existingProcess.Id}. Skipping launch.");
            return true;
        }

        try
        {
            string fileName;
            string arguments = "";

            var exePath = config.ExePath.Trim();
            if (exePath.Contains(" "))
            {
                var parts = exePath.Split(' ', 2);
                fileName = parts[0];
                arguments = parts[1];
            }
            else
            {
                fileName = exePath;
            }

            var workingDir = !string.IsNullOrWhiteSpace(config.ExecutablePath) && Directory.Exists(config.ExecutablePath)
                ? config.ExecutablePath
                : Directory.GetCurrentDirectory();

            _logger.LogInformation($"Starting {serviceName}: FileName={fileName}, Arguments={arguments}, WorkingDir={workingDir}");

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = new Process { StartInfo = startInfo };
            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogInformation($"[{serviceName}] {e.Data}"); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger.LogWarning($"[{serviceName} ERR] {e.Data}"); };
            process.Exited += (s, e) =>
            {
                _logger.LogWarning($"[{serviceName}] process exited.");
                lock (_trackedProcesses)
                {
                    _trackedProcesses.Remove(serviceName);
                }
            };

            if (!process.Start())
            {
                _logger.LogError($"Failed to start process for {serviceName}");
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_trackedProcesses)
            {
                _trackedProcesses[serviceName] = process;
                _startTimes[serviceName] = DateTime.Now;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception occurred while starting {serviceName}");
            return false;
        }
    }

    public async Task<bool> StopAsync(string serviceName)
    {
        var config = GetServiceConfig(serviceName);
        var existingProcess = FindRunningProcess(serviceName, config);

        if (existingProcess == null)
        {
            _logger.LogInformation($"{serviceName} is not running.");
            return true;
        }

        try
        {
            _logger.LogInformation($"Killing {serviceName} process {existingProcess.Id}");
            existingProcess.Kill(entireProcessTree: true);
            await existingProcess.WaitForExitAsync();

            lock (_trackedProcesses)
            {
                _trackedProcesses.Remove(serviceName);
                _startTimes.Remove(serviceName);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to stop {serviceName}");
            return false;
        }
    }

    public async Task<bool> RestartAsync(string serviceName)
    {
        await StopAsync(serviceName);
        await Task.Delay(1000);
        return await StartAsync(serviceName);
    }

    private Process? FindRunningProcess(string serviceName, ExternalServiceConfig config)
    {
        lock (_trackedProcesses)
        {
            if (_trackedProcesses.TryGetValue(serviceName, out var tracked) && !tracked.HasExited)
            {
                return tracked;
            }
        }

        if (string.IsNullOrWhiteSpace(config.ExePath)) return null;

        string targetBinaryName = config.ExePath.Trim().Split(' ')[0];
        string binaryFileName = Path.GetFileNameWithoutExtension(targetBinaryName);

        var matchingProcesses = Process.GetProcessesByName(binaryFileName);
        foreach (var proc in matchingProcesses)
        {
            if (IsMatchingProcess(proc, binaryFileName, config))
            {
                lock (_trackedProcesses)
                {
                    _trackedProcesses[serviceName] = proc;
                }
                return proc;
            }
        }

        // Fallback: Check all processes
        var allProcesses = Process.GetProcesses();
        foreach (var proc in allProcesses)
        {
            if (IsMatchingProcess(proc, binaryFileName, config))
            {
                lock (_trackedProcesses)
                {
                    _trackedProcesses[serviceName] = proc;
                }
                return proc;
            }
        }

        return null;
    }

    private bool IsMatchingProcess(Process process, string binaryFileName, ExternalServiceConfig config)
    {
        try
        {
            if (process.HasExited) return false;

            if (!process.ProcessName.Equals(binaryFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var exePath = config.ExePath.Trim();
            string expectedArgs = "";
            if (exePath.Contains(' '))
            {
                expectedArgs = exePath.Split(' ', 2)[1].Trim();
            }

            if (string.IsNullOrEmpty(expectedArgs))
            {
                return true; 
            }

            string cmdLine = GetProcessCommandLine(process);
            if (string.IsNullOrEmpty(cmdLine))
            {
                // If we are looking for a generic runtime like 'node' or 'dotnet' and cannot get command line,
                // do not match it blindly to avoid false positives.
                if (binaryFileName.Equals("node", StringComparison.OrdinalIgnoreCase) ||
                    binaryFileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                    binaryFileName.Equals("python", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }

            return cmdLine.Contains(expectedArgs, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string GetProcessCommandLine(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
                using var objects = searcher.Get();
                foreach (var obj in objects)
                {
                    return obj["CommandLine"]?.ToString() ?? string.Empty;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                string cmdlinePath = $"/proc/{process.Id}/cmdline";
                if (File.Exists(cmdlinePath))
                {
                    var bytes = File.ReadAllBytes(cmdlinePath);
                    return System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, $"Failed to get command line for process {process.Id}");
        }
        return string.Empty;
    }
}
