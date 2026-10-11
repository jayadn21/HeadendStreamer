using System;
using System.Collections.Generic;

namespace HeadendStreamer.Web.Models.Entities;

public class FolderPlaybackState
{
    public string StreamId { get; set; } = string.Empty;
    public string CurrentVideoPath { get; set; } = string.Empty;
    public string CurrentVideoName { get; set; } = string.Empty;
    public string UpcomingVideoPath { get; set; } = string.Empty;
    public string UpcomingVideoName { get; set; } = string.Empty;
    public double ResumePositionSeconds { get; set; }
    public int? ProcessId { get; set; }
    public DateTime LastSaved { get; set; } = DateTime.UtcNow;
    public List<PlayedVideoInfo> PlayedVideos { get; set; } = new();
}

public class PlayedVideoInfo
{
    public string VideoPath { get; set; } = string.Empty;
    public string VideoName { get; set; } = string.Empty;
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
}
