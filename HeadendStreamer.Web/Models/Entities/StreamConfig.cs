namespace HeadendStreamer.Web.Models.Entities;

public class StreamConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    
    // Input Configuration
    public string InputDevice { get; set; } = "/dev/video0";
    public string InputFormat { get; set; } = "yuyv422"; // This is actually the input format/driver (e.g. dshow, v4l2) or pixel format depending on context. Refactoring to separate.
    public string PixelFormat { get; set; } = "yuyv422";
    public string VideoSize { get; set; } = "1920x1080";
    public int FrameRate { get; set; } = 30;
    
    // Video Encoding
    public string VideoCodec { get; set; } = "libx264";
    public string Preset { get; set; } = "veryfast";
    public string Tune { get; set; } = "zerolatency";
    public string Bitrate { get; set; } = "5000k";
    public int GopSize { get; set; } = 60;
    
    // Audio Configuration
    public bool EnableAudio { get; set; } = true;
    public bool ReStream { get; set; } = false;
    public string AudioDevice { get; set; } = "hw:0,0";
    public string AudioCodec { get; set; } = "aac";
    public string AudioBitrate { get; set; } = "128k";
    public int AudioVolume { get; set; } = 100; // Percentage 0-200
    
    // Output Configuration
    public string MulticastIp { get; set; } = "239.255.255.250";
    public int Port { get; set; } = 1234;
    public int Ttl { get; set; } = 64;
    public string OutputFormat { get; set; } = "mpegts";
    
    // Advanced Settings
    public Dictionary<string, string> AdvancedOptions { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class StreamStatus
{
    public string ConfigId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public int ProcessId { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Uptime { get; set; }
    public double CpuUsage { get; set; }
    public long MemoryUsage { get; set; } // in bytes
    public long Bitrate { get; set; } // in bps
    public long FramesEncoded { get; set; }
    public double Fps { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    
    public double MemoryUsageMb => MemoryUsage / 1024.0 / 1024.0;
}

