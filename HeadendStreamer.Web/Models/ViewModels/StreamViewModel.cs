using HeadendStreamer.Web.Models.Entities;

namespace HeadendStreamer.Web.Models.ViewModels;

public class StreamViewModel
{
    public StreamConfig Config { get; set; } = new();
    public StreamStatus? Status { get; set; }
    public bool CanStart => !Status?.IsRunning ?? true;
    public bool CanStop => Status?.IsRunning ?? false;
}

public class DashboardViewModel
{
    public List<StreamViewModel> Streams { get; set; } = new();
    public SystemInfo SystemInfo { get; set; } = new();
    public int TotalStreams => Streams.Count;
    public int ActiveStreams => Streams.Count(s => s.Status?.IsRunning ?? false);
    public long TotalBitrate => Streams.Where(s => s.Status?.IsRunning ?? false)
                                      .Sum(s => s.Status?.Bitrate ?? 0);
}

public class StreamLog
{
    public string StreamId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // "ffmpeg", "system", "app"
}