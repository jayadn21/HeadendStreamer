using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadendStreamer.Web.Services;

/// <summary>
/// Background service that runs every 5 minutes to clean up FFmpeg log files in logs/ffmpeg,
/// deleting all logs except today's logs.
/// </summary>
public class FfmpegLogCleanupService : BackgroundService
{
    private readonly ILogger<FfmpegLogCleanupService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly StreamManagerService _streamManager;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly Regex DatePattern = new(@"_(\d{4})(\d{2})(\d{2})_\d{6}\.log$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FfmpegLogCleanupService(
        ILogger<FfmpegLogCleanupService> logger,
        IHostEnvironment environment,
        IConfiguration configuration,
        StreamManagerService streamManager)
    {
        _logger = logger;
        _environment = environment;
        _configuration = configuration;
        _streamManager = streamManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FfmpegLogCleanupService started. Scheduled to run every 5 minutes.");

        // Initial short delay on startup to allow app initialization
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Run initial cleanup on startup
        try
        {
            CleanupOldLogs();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during initial FFmpeg log cleanup.");
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    CleanupOldLogs();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during scheduled FFmpeg log cleanup.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        _logger.LogInformation("FfmpegLogCleanupService stopped.");
    }

    /// <summary>
    /// Cleans up log files in logs/ffmpeg, deleting all logs except today's logs.
    /// </summary>
    /// <returns>Number of deleted log files.</returns>
    public int CleanupOldLogs()
    {
        var today = DateTime.Today;
        var directoriesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Content root path
        if (!string.IsNullOrEmpty(_environment?.ContentRootPath))
        {
            directoriesToCheck.Add(Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "logs", "ffmpeg")));
        }

        // 2. Current working directory
        directoriesToCheck.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "logs", "ffmpeg")));

        // 3. Application base directory
        directoriesToCheck.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs", "ffmpeg")));

        // 4. Configured log directory (if specified in config)
        var configLogDir = _configuration?["HeadendStreamer:LogDirectory"];
        if (!string.IsNullOrEmpty(configLogDir))
        {
            if (Path.IsPathRooted(configLogDir))
            {
                directoriesToCheck.Add(Path.GetFullPath(Path.Combine(configLogDir, "ffmpeg")));
            }
            else if (!string.IsNullOrEmpty(_environment?.ContentRootPath))
            {
                directoriesToCheck.Add(Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configLogDir, "ffmpeg")));
            }
        }

        // Retrieve active log files from StreamManagerService so active streams are never touched
        var activeLogFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_streamManager != null)
        {
            try
            {
                foreach (var activeFile in _streamManager.GetActiveLogFiles())
                {
                    if (!string.IsNullOrWhiteSpace(activeFile))
                    {
                        activeLogFiles.Add(Path.GetFullPath(activeFile));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve active stream log files.");
            }
        }

        int totalDeleted = 0;
        int totalScanned = 0;

        foreach (var dir in directoriesToCheck)
        {
            if (!Directory.Exists(dir))
                continue;

            try
            {
                var dirInfo = new DirectoryInfo(dir);
                var files = dirInfo.GetFiles("*.log");

                foreach (var file in files)
                {
                    totalScanned++;
                    string fullPath = file.FullName;

                    // Never delete active stream logs
                    if (activeLogFiles.Contains(fullPath))
                        continue;

                    if (IsTodayLog(file, today))
                    {
                        // Retain today's logs
                        continue;
                    }

                    try
                    {
                        file.Delete();
                        totalDeleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to delete old FFmpeg log file {File}: {Message}", fullPath, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning directory for FFmpeg logs: {Directory}", dir);
            }
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation("FFmpeg log cleanup completed: deleted {DeletedCount} old log file(s) (total scanned: {ScannedCount}).",
                totalDeleted, totalScanned);
        }

        return totalDeleted;
    }

    /// <summary>
    /// Determines whether a log file belongs to today.
    /// </summary>
    private static bool IsTodayLog(FileInfo file, DateTime today)
    {
        // 1. Try to extract date from filename pattern: {configId}_yyyyMMdd_HHmmss.log
        var match = DatePattern.Match(file.Name);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out int year) &&
                int.TryParse(match.Groups[2].Value, out int month) &&
                int.TryParse(match.Groups[3].Value, out int day))
            {
                try
                {
                    var fileDate = new DateTime(year, month, day);
                    if (fileDate.Date == today)
                        return true;

                    // If filename has a valid past date and file wasn't written to today, it is definitely not today's log
                    if (file.LastWriteTime.Date != today)
                        return false;
                }
                catch
                {
                    // If date values were somehow invalid, fall through to filesystem check
                }
            }
        }

        // 2. Check filesystem timestamps: if modified or created today, treat as today's log
        if (file.LastWriteTime.Date == today || file.CreationTime.Date == today)
        {
            return true;
        }

        return false;
    }
}
