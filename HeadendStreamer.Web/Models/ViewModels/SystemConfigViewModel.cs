namespace HeadendStreamer.Web.Models.ViewModels;

public class SystemConfigViewModel
{
    // HeadendStreamer Core Settings
    public string FfmpegPath { get; set; } = string.Empty;
    public string ConfigDirectory { get; set; } = "configs";
    public string LogDirectory { get; set; } = "logs";
    public string DefaultMulticastBase { get; set; } = "239.255.255";
    public int PortRangeStart { get; set; } = 1234;
    public int PortRangeEnd { get; set; } = 1334;
    public bool AutoRestart { get; set; } = true;
    public int HealthCheckInterval { get; set; } = 30;
    public int MaxLogFiles { get; set; } = 10;
    public int MaxLogSizeMB { get; set; } = 10;

    // Go2rtc Bridge Settings
    public string Go2rtcExecutablePath { get; set; } = string.Empty;
    public string Go2rtcWorkingDirectory { get; set; } = string.Empty;

    // External Services: OBS_Scheduler
    public string ObsExecutablePath { get; set; } = string.Empty;
    public string ObsExePath { get; set; } = string.Empty;
    public string ObsServerUrl { get; set; } = string.Empty;
    public bool ObsEnabled { get; set; }

    // External Services: SPX_Graphics
    public string SpxExecutablePath { get; set; } = string.Empty;
    public string SpxExePath { get; set; } = string.Empty;
    public string SpxServerUrl { get; set; } = string.Empty;
    public bool SpxEnabled { get; set; }

    // External Services: Scroll_Ads
    public string ScrollAdsExecutablePath { get; set; } = string.Empty;
    public string ScrollAdsExePath { get; set; } = string.Empty;
    public string ScrollAdsServerUrl { get; set; } = string.Empty;
    public bool ScrollAdsEnabled { get; set; }

    // ASP.NET Logging & Serilog
    public string LogLevelDefault { get; set; } = "Information";
    public string LogLevelAspNetCore { get; set; } = "Warning";
    public string SerilogMinimumLevelDefault { get; set; } = "Information";
    public string SerilogFilePath { get; set; } = "logs/headend-.log";
    public int SerilogRetainedFileCountLimit { get; set; } = 7;

    // Allowed Hosts
    public string AllowedHosts { get; set; } = "*";

    // Raw JSON string
    public string RawJson { get; set; } = string.Empty;
}

public class BackupFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }
}
