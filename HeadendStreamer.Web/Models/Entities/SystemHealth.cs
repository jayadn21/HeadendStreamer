using System.Text.Json.Serialization;

namespace HeadendStreamer.Web.Models.Entities;

public class SystemHealth
{
    public string Status { get; set; } = "healthy";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public double DiskUsage { get; set; }
    public int ActiveStreams { get; set; }
    public List<HealthCheck> Checks { get; set; } = new();
    public Dictionary<string, object>? Metadata { get; set; }
}

public class HealthCheck
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string? Message { get; set; }
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }
    public Dictionary<string, object>? Tags { get; set; }
}

public class SystemInfo
{
    public string Hostname { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public TimeSpan Uptime { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public long TotalMemory { get; set; } // in bytes
    public long AvailableMemory { get; set; } // in bytes
    public double DiskUsage { get; set; }
    public List<NetworkInterfaceInfo> NetworkInterfaces { get; set; } = new();
    public int ActiveStreams { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // Add these properties for SystemMonitorService
    [JsonIgnore]
    public double CpuTemperature { get; set; }

    [JsonIgnore]
    public List<ProcessInfo> RunningProcesses { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, object> AdditionalMetrics { get; set; } = new();
}

public class NetworkInterfaceInfo
{
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public double SendRate { get; set; } // bps
    public double ReceiveRate { get; set; } // bps
    public string? MacAddress { get; set; }
    public bool IsOperational { get; set; } = true;
}

public class ProcessInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Memory { get; set; }
    public TimeSpan CpuTime { get; set; }
    public DateTime StartTime { get; set; }
    public string? CommandLine { get; set; }
}