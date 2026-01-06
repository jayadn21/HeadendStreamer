using HeadendStreamer.Web.Models.Entities;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace HeadendStreamer.Web.Services;

public class SystemMonitorService
{
    private readonly ILogger<SystemMonitorService> _logger;
    private SystemInfo _systemInfo = new();

    public SystemMonitorService(ILogger<SystemMonitorService> logger)
    {
        _logger = logger;
    }

    public SystemInfo GetSystemInfo()
    {
        return _systemInfo;
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

    public async Task UpdateSystemInfoAsync()
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

            _systemInfo = info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update system info");
        }
    }

    private double GetCpuUsage()
    {
        try
        {
            using var cpuCounter = new PerformanceCounter(
                "Processor", "% Processor Time", "_Total");
            cpuCounter.NextValue();
            Thread.Sleep(1000);
            return cpuCounter.NextValue();
        }
        catch
        {
            return 0;
        }
    }

    private (long Total, long Available) GetMemoryInfo()
    {
        try
        {
            using var memoryCounter = new PerformanceCounter(
                "Memory", "Available Bytes");
            var available = (long)memoryCounter.NextValue();

            using var totalCounter = new PerformanceCounter(
                "Memory", "Commit Limit");
            var total = (long)totalCounter.NextValue();

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
                var stats = ni.GetIPv4Statistics();
                var ip = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.Address.ToString() ?? string.Empty;

                var macAddress = string.Join(":", ni.GetPhysicalAddress()
                    .GetAddressBytes()
                    .Select(b => b.ToString("X2")));

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

            foreach (var process in dotnetProcesses)
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
}