using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeadendStreamer.Web.Models.ViewModels;

namespace HeadendStreamer.Web.Services;

public class SystemConfigService
{
    private readonly ILogger<SystemConfigService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly ConfigService _configService;
    private readonly string _appSettingsPath;
    private readonly string _backupsDirectory;
    private readonly object _lock = new();

    public SystemConfigService(
        ILogger<SystemConfigService> logger,
        IWebHostEnvironment env,
        ConfigService configService)
    {
        _logger = logger;
        _env = env;
        _configService = configService;
        _appSettingsPath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _backupsDirectory = Path.Combine(env.ContentRootPath, "configs", "backups");
        
        try
        {
            Directory.CreateDirectory(_backupsDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create backups directory at {Path}", _backupsDirectory);
        }
    }

    public SystemConfigViewModel GetSystemConfig()
    {
        lock (_lock)
        {
            var model = new SystemConfigViewModel();

            if (!File.Exists(_appSettingsPath))
            {
                _logger.LogWarning("appsettings.json not found at {Path}", _appSettingsPath);
                return model;
            }

            try
            {
                var jsonText = File.ReadAllText(_appSettingsPath);
                model.RawJson = jsonText;

                var root = JsonNode.Parse(jsonText) as JsonObject;
                if (root == null)
                {
                    return model;
                }

                // HeadendStreamer Section
                if (root["HeadendStreamer"] is JsonObject hs)
                {
                    model.FfmpegPath = CleanPath(hs["FfmpegPath"]?.ToString());
                    model.ConfigDirectory = hs["ConfigDirectory"]?.ToString() ?? "configs";
                    model.LogDirectory = hs["LogDirectory"]?.ToString() ?? "logs";
                    model.DefaultMulticastBase = hs["DefaultMulticastBase"]?.ToString() ?? "239.255.255";

                    if (hs["DefaultPortRange"] is JsonObject portRange)
                    {
                        model.PortRangeStart = portRange["Start"]?.GetValue<int>() ?? 1234;
                        model.PortRangeEnd = portRange["End"]?.GetValue<int>() ?? 1334;
                    }

                    model.AutoRestart = hs["AutoRestart"]?.GetValue<bool>() ?? true;
                    model.HealthCheckInterval = hs["HealthCheckInterval"]?.GetValue<int>() ?? 30;
                    model.MaxLogFiles = hs["MaxLogFiles"]?.GetValue<int>() ?? 10;
                    model.MaxLogSizeMB = hs["MaxLogSizeMB"]?.GetValue<int>() ?? 10;

                    // Go2rtc
                    if (hs["Go2rtc"] is JsonObject go2rtc)
                    {
                        model.Go2rtcExecutablePath = CleanPath(go2rtc["ExecutablePath"]?.ToString());
                        model.Go2rtcWorkingDirectory = CleanPath(go2rtc["WorkingDirectory"]?.ToString());
                    }

                    // OBS_Scheduler
                    if (hs["OBS_Scheduler"] is JsonObject obs)
                    {
                        model.ObsExecutablePath = CleanPath(obs["ExecutablePath"]?.ToString());
                        model.ObsExePath = obs["ExePath"]?.ToString() ?? "";
                        model.ObsServerUrl = obs["ServerURL"]?.ToString() ?? "";
                        model.ObsEnabled = obs["Enabled"]?.GetValue<bool>() ?? false;
                    }

                    // SPX_Graphics
                    if (hs["SPX_Graphics"] is JsonObject spx)
                    {
                        model.SpxExecutablePath = CleanPath(spx["ExecutablePath"]?.ToString());
                        model.SpxExePath = spx["ExePath"]?.ToString() ?? "";
                        model.SpxServerUrl = spx["ServerURL"]?.ToString() ?? "";
                        model.SpxEnabled = spx["Enabled"]?.GetValue<bool>() ?? false;
                    }

                    // Scroll_Ads
                    if (hs["Scroll_Ads"] is JsonObject scroll)
                    {
                        model.ScrollAdsExecutablePath = CleanPath(scroll["ExecutablePath"]?.ToString());
                        model.ScrollAdsExePath = scroll["ExePath"]?.ToString() ?? "";
                        model.ScrollAdsServerUrl = scroll["ServerURL"]?.ToString() ?? "";
                        model.ScrollAdsEnabled = scroll["Enabled"]?.GetValue<bool>() ?? false;
                    }
                }

                // Logging Section
                if (root["Logging"] is JsonObject logging && logging["LogLevel"] is JsonObject logLevels)
                {
                    model.LogLevelDefault = logLevels["Default"]?.ToString() ?? "Information";
                    model.LogLevelAspNetCore = logLevels["Microsoft.AspNetCore"]?.ToString() ?? "Warning";
                }

                // Serilog Section
                if (root["Serilog"] is JsonObject serilog)
                {
                    if (serilog["MinimumLevel"] is JsonObject minLevel)
                    {
                        model.SerilogMinimumLevelDefault = minLevel["Default"]?.ToString() ?? "Information";
                    }

                    if (serilog["WriteTo"] is JsonArray writeToArray)
                    {
                        foreach (var item in writeToArray)
                        {
                            if (item is JsonObject writeToItem &&
                                writeToItem["Name"]?.ToString() == "File" &&
                                writeToItem["Args"] is JsonObject args)
                            {
                                model.SerilogFilePath = args["path"]?.ToString() ?? "logs/headend-.log";
                                model.SerilogRetainedFileCountLimit = args["retainedFileCountLimit"]?.GetValue<int>() ?? 7;
                            }
                        }
                    }
                }

                // Allowed Hosts
                model.AllowedHosts = root["AllowedHosts"]?.ToString() ?? "*";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse appsettings.json from {Path}", _appSettingsPath);
            }

            return model;
        }
    }

    public (bool Success, string Message) SaveSystemConfig(SystemConfigViewModel model)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_appSettingsPath))
                {
                    return (false, "appsettings.json file does not exist.");
                }

                var jsonText = File.ReadAllText(_appSettingsPath);
                var root = JsonNode.Parse(jsonText) as JsonObject ?? new JsonObject();

                // 1. HeadendStreamer
                JsonObject hs;
                if (root.ContainsKey("HeadendStreamer") && root["HeadendStreamer"] is JsonObject existingHs)
                {
                    hs = existingHs;
                }
                else
                {
                    hs = new JsonObject();
                    root["HeadendStreamer"] = hs;
                }

                hs["FfmpegPath"] = CleanPath(model.FfmpegPath);
                hs["ConfigDirectory"] = string.IsNullOrWhiteSpace(model.ConfigDirectory) ? "configs" : CleanPath(model.ConfigDirectory);
                hs["LogDirectory"] = string.IsNullOrWhiteSpace(model.LogDirectory) ? "logs" : CleanPath(model.LogDirectory);
                hs["DefaultMulticastBase"] = string.IsNullOrWhiteSpace(model.DefaultMulticastBase) ? "239.255.255" : model.DefaultMulticastBase.Trim();

                JsonObject portRange;
                if (hs.ContainsKey("DefaultPortRange") && hs["DefaultPortRange"] is JsonObject existingPr)
                {
                    portRange = existingPr;
                }
                else
                {
                    portRange = new JsonObject();
                    hs["DefaultPortRange"] = portRange;
                }
                portRange["Start"] = model.PortRangeStart;
                portRange["End"] = model.PortRangeEnd;

                hs["AutoRestart"] = model.AutoRestart;
                hs["HealthCheckInterval"] = model.HealthCheckInterval;
                hs["MaxLogFiles"] = model.MaxLogFiles;
                hs["MaxLogSizeMB"] = model.MaxLogSizeMB;

                // Go2rtc
                JsonObject go2rtc;
                if (hs.ContainsKey("Go2rtc") && hs["Go2rtc"] is JsonObject existingGo2rtc)
                {
                    go2rtc = existingGo2rtc;
                }
                else
                {
                    go2rtc = new JsonObject();
                    hs["Go2rtc"] = go2rtc;
                }
                go2rtc["ExecutablePath"] = CleanPath(model.Go2rtcExecutablePath);
                go2rtc["WorkingDirectory"] = CleanPath(model.Go2rtcWorkingDirectory);

                // OBS_Scheduler
                UpdateExternalServiceNode(hs, "OBS_Scheduler", CleanPath(model.ObsExecutablePath), model.ObsExePath?.Trim(), model.ObsServerUrl?.Trim(), model.ObsEnabled);

                // SPX_Graphics
                UpdateExternalServiceNode(hs, "SPX_Graphics", CleanPath(model.SpxExecutablePath), model.SpxExePath?.Trim(), model.SpxServerUrl?.Trim(), model.SpxEnabled);

                // Scroll_Ads
                UpdateExternalServiceNode(hs, "Scroll_Ads", CleanPath(model.ScrollAdsExecutablePath), model.ScrollAdsExePath?.Trim(), model.ScrollAdsServerUrl?.Trim(), model.ScrollAdsEnabled);

                // 2. Logging
                JsonObject logging;
                if (root.ContainsKey("Logging") && root["Logging"] is JsonObject existingLogging)
                {
                    logging = existingLogging;
                }
                else
                {
                    logging = new JsonObject();
                    root["Logging"] = logging;
                }

                JsonObject logLevels;
                if (logging.ContainsKey("LogLevel") && logging["LogLevel"] is JsonObject existingLogLevels)
                {
                    logLevels = existingLogLevels;
                }
                else
                {
                    logLevels = new JsonObject();
                    logging["LogLevel"] = logLevels;
                }
                logLevels["Default"] = string.IsNullOrWhiteSpace(model.LogLevelDefault) ? "Information" : model.LogLevelDefault;
                logLevels["Microsoft.AspNetCore"] = string.IsNullOrWhiteSpace(model.LogLevelAspNetCore) ? "Warning" : model.LogLevelAspNetCore;

                // 3. Serilog
                if (root.ContainsKey("Serilog") && root["Serilog"] is JsonObject serilog)
                {
                    if (serilog.ContainsKey("MinimumLevel") && serilog["MinimumLevel"] is JsonObject minLevel)
                    {
                        minLevel["Default"] = string.IsNullOrWhiteSpace(model.SerilogMinimumLevelDefault) ? "Information" : model.SerilogMinimumLevelDefault;
                    }

                    if (serilog.ContainsKey("WriteTo") && serilog["WriteTo"] is JsonArray writeToArray)
                    {
                        foreach (var item in writeToArray)
                        {
                            if (item is JsonObject writeToItem &&
                                writeToItem["Name"]?.ToString() == "File" &&
                                writeToItem["Args"] is JsonObject args)
                            {
                                args["path"] = string.IsNullOrWhiteSpace(model.SerilogFilePath) ? "logs/headend-.log" : model.SerilogFilePath;
                                args["retainedFileCountLimit"] = model.SerilogRetainedFileCountLimit;
                            }
                        }
                    }
                }

                // 4. Allowed Hosts
                root["AllowedHosts"] = string.IsNullOrWhiteSpace(model.AllowedHosts) ? "*" : model.AllowedHosts;

                // Backup before write
                CreateBackupInternal();

                // Format and Write
                var options = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = root.ToJsonString(options);
                File.WriteAllText(_appSettingsPath, updatedJson);

                // Sync external services status cache
                _configService.SetExternalServiceEnabled("OBS_Scheduler", model.ObsEnabled);
                _configService.SetExternalServiceEnabled("SPX_Graphics", model.SpxEnabled);
                _configService.SetExternalServiceEnabled("Scroll_Ads", model.ScrollAdsEnabled);

                _logger.LogInformation("Successfully updated appsettings.json from System Config form.");
                return (true, "Settings saved successfully to appsettings.json.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save system config to appsettings.json");
                return (false, $"Error saving settings: {ex.Message}");
            }
        }
    }

    public (bool Success, string Message) SaveRawJson(string rawJson)
    {
        lock (_lock)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return (false, "JSON content cannot be empty.");
                }

                // Validate syntax
                JsonNode? root;
                try
                {
                    root = JsonNode.Parse(rawJson);
                    if (root == null || root is not JsonObject)
                    {
                        return (false, "JSON root must be a valid JSON object.");
                    }
                }
                catch (JsonException jex)
                {
                    return (false, $"Invalid JSON syntax: {jex.Message}");
                }

                // Format indented
                var options = new JsonSerializerOptions { WriteIndented = true };
                var formatted = root.ToJsonString(options);

                // Backup before write
                CreateBackupInternal();

                File.WriteAllText(_appSettingsPath, formatted);

                _logger.LogInformation("Successfully updated appsettings.json from Raw JSON editor.");
                return (true, "appsettings.json updated successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save raw JSON to appsettings.json");
                return (false, $"Error saving raw JSON: {ex.Message}");
            }
        }
    }

    public async Task<(bool Success, string Output, string Error)> TestFfmpegAsync(string? ffmpegPath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                var cfg = GetSystemConfig();
                ffmpegPath = cfg.FfmpegPath;
            }

            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                ffmpegPath = "ffmpeg";
            }

            ffmpegPath = CleanPath(ffmpegPath);

            // Expand environment variables if any
            ffmpegPath = Environment.ExpandEnvironmentVariables(ffmpegPath);

            // Check if file exists when path contains a separator
            if ((ffmpegPath.Contains(Path.DirectorySeparatorChar) || ffmpegPath.Contains(Path.AltDirectorySeparatorChar)) && !File.Exists(ffmpegPath))
            {
                return (false, "", $"File not found at specified path: '{ffmpegPath}'");
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();

            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(exitTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                try { process.Kill(true); } catch { }
                return (false, "", "FFmpeg test timed out after 5 seconds.");
            }

            await exitTask;
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var summary = string.Join(Environment.NewLine, lines.Take(4));
                return (true, summary, "");
            }
            else
            {
                var err = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                return (false, "", string.IsNullOrWhiteSpace(err) ? $"Process exited with code {process.ExitCode}" : err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test FFmpeg path: {Path}", ffmpegPath);
            return (false, "", ex.Message);
        }
    }

    private static string CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var cleaned = path.Trim('\"', '\'').Trim();
        while (cleaned.Contains(@"\\"))
        {
            cleaned = cleaned.Replace(@"\\", @"\");
        }
        return cleaned;
    }

    public List<BackupFileInfo> GetBackups()
    {
        var list = new List<BackupFileInfo>();
        try
        {
            if (!Directory.Exists(_backupsDirectory))
                return list;

            var files = new DirectoryInfo(_backupsDirectory).GetFiles("appsettings_backup_*.json");
            foreach (var f in files.OrderByDescending(f => f.CreationTimeUtc))
            {
                list.Add(new BackupFileInfo
                {
                    FileName = f.Name,
                    CreatedAt = f.LastWriteTime,
                    SizeBytes = f.Length
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list backups");
        }
        return list;
    }

    public (bool Success, string Message) RestoreBackup(string fileName)
    {
        lock (_lock)
        {
            try
            {
                // Prevent path traversal
                var safeName = Path.GetFileName(fileName);
                var backupPath = Path.Combine(_backupsDirectory, safeName);

                if (!File.Exists(backupPath))
                {
                    return (false, $"Backup file '{safeName}' not found.");
                }

                // Backup current state first
                CreateBackupInternal();

                // Restore
                File.Copy(backupPath, _appSettingsPath, overwrite: true);
                _logger.LogInformation("Restored appsettings.json from backup {FileName}", safeName);
                return (true, $"Restored settings from {safeName}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore backup {FileName}", fileName);
                return (false, $"Error restoring backup: {ex.Message}");
            }
        }
    }

    public BrowseResult BrowseDirectory(string? targetPath)
    {
        var result = new BrowseResult();

        try
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                // Root drives on Windows or root / on Linux
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    result.CurrentPath = "";
                    result.ParentPath = null;
                    foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                    {
                        result.Items.Add(new BrowseItem
                        {
                            Name = drive.Name,
                            Path = drive.Name,
                            IsDirectory = true,
                            Extension = ""
                        });
                    }
                    return result;
                }
                else
                {
                    targetPath = "/";
                }
            }

            // Normalize path
            if (File.Exists(targetPath))
            {
                targetPath = Path.GetDirectoryName(targetPath) ?? targetPath;
            }

            if (!Directory.Exists(targetPath))
            {
                targetPath = _env.ContentRootPath;
            }

            var di = new DirectoryInfo(targetPath);
            result.CurrentPath = di.FullName;
            result.ParentPath = di.Parent?.FullName;

            // Folders
            foreach (var dir in di.GetDirectories().OrderBy(d => d.Name))
            {
                if ((dir.Attributes & FileAttributes.Hidden) != 0) continue;
                result.Items.Add(new BrowseItem
                {
                    Name = dir.Name,
                    Path = dir.FullName,
                    IsDirectory = true,
                    Modified = dir.LastWriteTime,
                    Extension = ""
                });
            }

            // Files (show executables, scripts, configs, and all other files)
            foreach (var file in di.GetFiles().OrderBy(f => f.Name))
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0) continue;
                result.Items.Add(new BrowseItem
                {
                    Name = file.Name,
                    Path = file.FullName,
                    IsDirectory = false,
                    Size = file.Length,
                    Modified = file.LastWriteTime,
                    Extension = file.Extension.ToLowerInvariant()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse directory {Path}", targetPath);
        }

        return result;
    }

    private void UpdateExternalServiceNode(JsonObject parent, string key, string? execPath, string? exePath, string? serverUrl, bool enabled)
    {
        JsonObject obj;
        if (parent.ContainsKey(key) && parent[key] is JsonObject existingObj)
        {
            obj = existingObj;
        }
        else
        {
            obj = new JsonObject();
            parent[key] = obj;
        }

        obj["ExecutablePath"] = execPath ?? string.Empty;
        obj["ExePath"] = exePath ?? string.Empty;
        obj["ServerURL"] = serverUrl ?? string.Empty;
        obj["Enabled"] = enabled;
    }

    private void CreateBackupInternal()
    {
        try
        {
            if (!File.Exists(_appSettingsPath)) return;

            Directory.CreateDirectory(_backupsDirectory);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var backupFile = Path.Combine(_backupsDirectory, $"appsettings_backup_{timestamp}.json");
            File.Copy(_appSettingsPath, backupFile, overwrite: true);

            // Prune backups beyond last 15
            var backups = new DirectoryInfo(_backupsDirectory)
                .GetFiles("appsettings_backup_*.json")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(15);

            foreach (var oldFile in backups)
            {
                try { oldFile.Delete(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create automatic backup of appsettings.json");
        }
    }
}

public class BrowseResult
{
    public string CurrentPath { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public List<BrowseItem> Items { get; set; } = new();
}

public class BrowseItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long? Size { get; set; }
    public DateTime? Modified { get; set; }
    public string Extension { get; set; } = string.Empty;
}
