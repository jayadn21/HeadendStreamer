using HeadendStreamer.Web.Models.Entities;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HeadendStreamer.Web.Services;

public class SystemMonitorService : IDisposable
{
    private readonly ILogger<SystemMonitorService> _logger;
    private SystemInfo _systemInfo = new();
    private readonly object _lock = new();

    // Persistent counters
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramAvailableCounter;
    private PerformanceCounter? _ramTotalCounter; // Sometimes we can't get total RAM via counter easily, but we'll try or use Environment
    
    // Fallback for non-windows
    private readonly bool _isWindows;

    public SystemMonitorService(ILogger<SystemMonitorService> logger)
    {
        _logger = logger;
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (_isWindows)
        {
            InitializeCounters();
        }
    }

    private void InitializeCounters()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            // First call always returns 0
            _cpuCounter.NextValue();

            _ramAvailableCounter = new PerformanceCounter("Memory", "Available Bytes");
            
            // "Commit Limit" is roughly total memory available to processes
            _ramTotalCounter = new PerformanceCounter("Memory", "Commit Limit");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to initialize performance counters: {ex.Message}");
        }
    }

    public SystemInfo GetSystemInfo()
    {
        lock (_lock)
        {
            // Return a copy or just the reference if it's treated as immutable snapshot
            return _systemInfo;
        }
    }

    public async Task<SystemHealth> CheckSystemHealthAsync()
    {
        await UpdateSystemInfoAsync();
        var systemInfo = GetSystemInfo();

        // Create health checks based on system info
        var checks = new List<HealthCheck>
        {
            new HealthCheck
            {
                Name = "cpu",
                Status = systemInfo.CpuUsage < 90 ? "healthy" : "warning",
                Message = systemInfo.CpuUsage < 90 ? "CPU usage normal" : "High CPU usage detected"
            },
            new HealthCheck
            {
                Name = "memory",
                Status = systemInfo.MemoryUsage < 85 ? "healthy" : "warning",
                Message = systemInfo.MemoryUsage < 85 ? "Memory usage normal" : "High memory usage detected"
            },
            new HealthCheck
            {
                Name = "disk",
                Status = systemInfo.DiskUsage < 90 ? "healthy" : "warning",
                Message = systemInfo.DiskUsage < 90 ? "Disk usage normal" : "High disk usage detected"
            },
            new HealthCheck
            {
                Name = "network",
                Status = "healthy",
                Message = "Network interfaces operational"
            }
        };

        // Check network interfaces
        if (systemInfo.NetworkInterfaces != null)
        {
            foreach (var ni in systemInfo.NetworkInterfaces)
            {
                if (!ni.IsOperational)
                {
                    checks.Add(new HealthCheck
                    {
                        Name = $"network_{ni.Name}",
                        Status = "error",
                        Message = $"Network interface {ni.Name} is not operational"
                    });
                }
            }
        }

        return new SystemHealth
        {
            Status = checks.All(c => c.Status == "healthy") ? "healthy" :
                     checks.Any(c => c.Status == "error") ? "error" : "warning",
            Timestamp = DateTime.UtcNow,
            CpuUsage = systemInfo.CpuUsage,
            MemoryUsage = systemInfo.MemoryUsage,
            DiskUsage = systemInfo.DiskUsage,
            ActiveStreams = systemInfo.ActiveStreams,
            Checks = checks
        };
    }

    public Task UpdateSystemInfoAsync()
    {
        // Use Task.Run to offload potentially heavy WMI/Counter calls from main thread
        return Task.Run(() =>
        {
            try
            {
                var info = new SystemInfo
                {
                    Hostname = Environment.MachineName,
                    OsVersion = Environment.OSVersion.VersionString,
                    Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
                    LastUpdated = DateTime.UtcNow
                };

                // CPU Usage
                info.CpuUsage = GetCpuUsage();

                // Memory
                var memoryInfo = GetMemoryInfo();
                info.TotalMemory = memoryInfo.Total;
                info.AvailableMemory = memoryInfo.Available;
                info.MemoryUsage = memoryInfo.Total > 0 ?
                    100.0 - (memoryInfo.Available * 100.0 / memoryInfo.Total) : 0;

                // Disk Usage
                info.DiskUsage = GetDiskUsage("C:\\");

                // Network Interfaces
                info.NetworkInterfaces = GetNetworkInterfaces();

                // Get running processes (FFmpeg processes)
                info.RunningProcesses = GetRunningProcesses();
                info.ActiveStreams = info.RunningProcesses.Count(p => p.Name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase));

                lock (_lock)
                {
                    _systemInfo = info;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update system info");
            }
        });
    }

    private double GetCpuUsage()
    {
        if (!_isWindows) return 0;

        try
        {
            if (_cpuCounter != null)
            {
                return _cpuCounter.NextValue();
            }
        }
        catch
        {
            // Try to re-init?
        }
        return 0;
    }

    private (long Total, long Available) GetMemoryInfo()
    {
        if (!_isWindows) return (0, 0);

        try
        {
            long available = 0;
            long total = 0;

            if (_ramAvailableCounter != null)
                available = (long)_ramAvailableCounter.NextValue();

            if (_ramTotalCounter != null)
                total = (long)_ramTotalCounter.NextValue();

            return (total, available);
        }
        catch
        {
            return (Environment.WorkingSet, Environment.WorkingSet);
        }
    }

    private double GetDiskUsage(string path)
    {
        try
        {
            var driveInfo = new DriveInfo(path);
            if (driveInfo.IsReady)
            {
                var totalSpace = driveInfo.TotalSize;
                var freeSpace = driveInfo.AvailableFreeSpace;
                return 100.0 - (freeSpace * 100.0 / totalSpace);
            }
        }
        catch { }

        return 0;
    }

    private List<NetworkInterfaceInfo> GetNetworkInterfaces()
    {
        var interfaces = new List<NetworkInterfaceInfo>();

        try
        {
            var netInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                           ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToArray();

            foreach (var ni in netInterfaces)
            {
                try 
                {
                    var stats = ni.GetIPv4Statistics();
                    var ip = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?.Address.ToString() ?? string.Empty;

                    var macAddress = string.Empty;
                    try {
                        macAddress = string.Join(":", ni.GetPhysicalAddress()
                            .GetAddressBytes()
                            .Select(b => b.ToString("X2")));
                    } catch {}

                    interfaces.Add(new NetworkInterfaceInfo
                    {
                        Name = ni.Name,
                        IpAddress = ip,
                        BytesSent = stats.BytesSent,
                        BytesReceived = stats.BytesReceived,
                        SendRate = 0,
                        ReceiveRate = 0,
                        MacAddress = macAddress,
                        IsOperational = ni.OperationalStatus == OperationalStatus.Up
                    });
                }
                catch { continue; }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get network interfaces");
        }

        return interfaces;
    }

    private List<ProcessInfo> GetRunningProcesses()
    {
        var processes = new List<ProcessInfo>();

        try
        {
            // Get FFmpeg processes and other relevant processes
            var ffmpegProcesses = Process.GetProcessesByName("ffmpeg");
            var dotnetProcesses = Process.GetProcessesByName("dotnet");

            foreach (var process in ffmpegProcesses)
            {
                try
                {
                    processes.Add(new ProcessInfo
                    {
                        Id = process.Id,
                        Name = process.ProcessName,
                        Memory = process.WorkingSet64,
                        CpuTime = process.TotalProcessorTime,
                        StartTime = process.StartTime
                    });
                }
                catch
                {
                    // Ignore processes we can't access
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get running processes");
        }

        return processes;
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _ramAvailableCounter?.Dispose();
        _ramTotalCounter?.Dispose();
    }
}