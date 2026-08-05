using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace HeadendStreamer.Web.Controllers;

[Authorize]
public class ConfigController : Controller
{
    private readonly ConfigService _configService;
    private readonly FfmpegService _ffmpegService;
    private readonly StreamManagerService _streamManager;
    private readonly Go2rtcService _go2rtcService;
    private readonly ILogger<ConfigController> _logger;
    
    public ConfigController(
        ConfigService configService,
        FfmpegService ffmpegService,
        StreamManagerService streamManager,
        Go2rtcService go2rtcService,
        ILogger<ConfigController> logger)
    {
        _configService = configService;
        _ffmpegService = ffmpegService;
        _streamManager = streamManager;
        _go2rtcService = go2rtcService;
        _logger = logger;
    }

    // MVC Actions

    [HttpGet]
    public IActionResult Index()
    {
        var configs = _configService.GetAllConfigs();
        var statuses = _streamManager.GetAllStreamStatus();
        
        var viewModels = configs.Select(config => new HeadendStreamer.Web.Models.ViewModels.StreamViewModel
        {
            Config = config,
            Status = statuses.TryGetValue(config.Id, out var status) ? status : null
        }).ToList();
        
        return View(viewModels);
    }

    [HttpGet]
    public IActionResult Create()
    {
        return View(new StreamConfig());
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var config = await _configService.GetConfigAsync(id);
        if (config == null)
        {
            return NotFound();
        }

        var status = _streamManager.GetStreamStatus(id);
        var viewModel = new HeadendStreamer.Web.Models.ViewModels.StreamViewModel
        {
            Config = config,
            Status = status
        };
        
        return View(viewModel);
    }
    
    // API Actions

    [HttpGet("api/config")]
    public IActionResult GetAllConfigs()
    {
        var configs = _configService.GetAllConfigs();
        return Ok(configs);
    }
    
    [HttpGet("api/config/{id}")]
    public async Task<IActionResult> GetConfig(string id)
    {
        var config = await _configService.GetConfigAsync(id);
        if (config == null)
            return NotFound();
        
        return Ok(config);
    }
    
    [HttpPost("api/config")]
    public async Task<IActionResult> CreateConfig([FromBody] StreamConfig config)
    {
        try
        {
            var createdConfig = await _configService.CreateConfigAsync(config);
            
            // Sync with go2rtc
            await _go2rtcService.AddOrUpdateStreamAsync(null, createdConfig.Name, createdConfig.MulticastIp, createdConfig.Port);
            
            return CreatedAtAction(nameof(GetConfig), new { id = createdConfig.Id }, createdConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create config");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPut("api/config/{id}")]
    public async Task<IActionResult> UpdateConfig(string id, [FromBody] StreamConfig updates)
    {
        try
        {
            // Get existing to find the old name before update
            var existing = await _configService.GetConfigAsync(id);
            string? oldName = existing?.Name;

            var updatedConfig = await _configService.UpdateConfigAsync(id, updates);
            if (updatedConfig == null)
                return NotFound();
            
            // Sync with go2rtc
            await _go2rtcService.AddOrUpdateStreamAsync(oldName, updatedConfig.Name, updatedConfig.MulticastIp, updatedConfig.Port);
            
            return Ok(updatedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to update config {id}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            _logger.LogInformation($"Received form-based delete request for config ID: {id}");
            
            // Stop the stream if it's running
            await _streamManager.StopStreamAsync(id);

            // Delete the config and its file
            var deletedConfig = await _configService.DeleteConfigAsync(id);
            if (deletedConfig != null)
            {
                // Sync with go2rtc
                await _go2rtcService.DeleteStreamAsync(deletedConfig.Name);
                _logger.LogInformation($"Successfully deleted config {id} ({deletedConfig.Name}) via form");
            }
            
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete config {id} via form");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("api/config/{id}/delete")]
    public async Task<IActionResult> DeleteConfig([FromRoute] string id)
    {
        try
        {
            _logger.LogInformation($"Received API delete request for config ID: {id}");
            
            // Stop the stream if it's running
            await _streamManager.StopStreamAsync(id);

            // Delete the config and its file
            var deletedConfig = await _configService.DeleteConfigAsync(id);
            if (deletedConfig == null)
            {
                _logger.LogWarning($"Config with ID {id} not found for deletion");
                return NotFound();
            }
            
            // Sync with go2rtc
            await _go2rtcService.DeleteStreamAsync(deletedConfig.Name);
            
            _logger.LogInformation($"Successfully deleted config {id} ({deletedConfig.Name}) via API");
            return Ok(new { message = "Config deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete config {id} via API");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpGet("api/config/templates/{name}")]
    public IActionResult GetTemplate(string name)
    {
        // Create template from ConfigService method
        return Ok(new { message = "Template endpoint" });
    }
    
    [HttpGet("api/config/devices")]
    public async Task<IActionResult> GetVideoDevices()
    {
        try
        {
            var devices = await _ffmpegService.GetVideoDevicesAsync();
            return Ok(devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get video devices");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("api/config/test/device")]
    public async Task<IActionResult> TestDevice([FromBody] DeviceTestRequest request)
    {
        try
        {
            var result = await _ffmpegService.TestInputDeviceAsync(request.DevicePath);
            return Ok(new { available = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test device");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("api/config/device-options")]
    public async Task<IActionResult> GetDeviceOptions([FromQuery] string deviceName, [FromQuery] string inputFormat = "dshow")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return BadRequest(new { error = "Device name is required" });

            var options = await _ffmpegService.GetDeviceOptionsAsync(deviceName, inputFormat);
            if (options == null)
                return NotFound(new { error = "Could not retrieve device options" });

            return Ok(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get device options for {deviceName}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpGet("api/config/browse")]
    public IActionResult Browse(string? path = null)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                // Default to root drives on Windows or home directory
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new FileItem
                        {
                            Name = d.Name,
                            Path = d.Name,
                            IsDirectory = true,
                            Size = 0,
                            Modified = DateTime.MinValue
                        })
                        .ToList();
                    return Ok(new { currentPath = "", items = drives });
                }
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (!Directory.Exists(path))
                return BadRequest(new { error = "Directory does not exist" });

            var di = new DirectoryInfo(path);
            var items = new List<FileItem>();

            // Add folders
            foreach (var dir in di.GetDirectories())
            {
                if ((dir.Attributes & FileAttributes.Hidden) != 0) continue;
                items.Add(new FileItem
                {
                    Name = dir.Name,
                    Path = dir.FullName,
                    IsDirectory = true,
                    Modified = dir.LastWriteTime
                });
            }

            // Add files (videos and images)
            var allowedExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".ts", ".m2ts", ".flv", ".webm", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg" };
            foreach (var file in di.GetFiles())
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0) continue;
                if (!allowedExtensions.Contains(file.Extension.ToLower())) continue;

                items.Add(new FileItem
                {
                    Name = file.Name,
                    Path = file.FullName,
                    IsDirectory = false,
                    Size = file.Length,
                    Modified = file.LastWriteTime
                });
            }

            return Ok(new { currentPath = path, parentPath = di.Parent?.FullName, items = items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to browse path: {path}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("api/config/verify-path")]
    public async Task<IActionResult> VerifyPath([FromBody] PathVerificationRequest request)
    {
        try
        {
            var exists = await _ffmpegService.VerifyPathAsync(request.Path);
            return Ok(new { exists });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify path");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("api/config/logo-preview")]
    public IActionResult GetLogoPreview([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                return NotFound("Logo file not found.");
            }

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            string contentType;
            switch (ext)
            {
                case ".png":
                    contentType = "image/png";
                    break;
                case ".jpg":
                case ".jpeg":
                    contentType = "image/jpeg";
                    break;
                case ".gif":
                    contentType = "image/gif";
                    break;
                case ".bmp":
                    contentType = "image/bmp";
                    break;
                case ".svg":
                    contentType = "image/svg+xml";
                    break;
                default:
                    contentType = "application/octet-stream";
                    break;
            }

            var bytes = System.IO.File.ReadAllBytes(path);
            return File(bytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to read logo preview for path {path}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("api/config/test/stream")]
    public async Task<IActionResult> TestStream([FromBody] StreamConfig config)
    {
        try
        {
            var result = await _ffmpegService.TestStreamOutputAsync(config);
            return Ok(new { success = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test stream");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("api/config/backup")]
    public async Task<IActionResult> CreateBackup()
    {
        try
        {
            var backupFile = await _configService.CreateBackupAsync();
            var bytes = await System.IO.File.ReadAllBytesAsync(backupFile);
            return File(bytes, "application/json", Path.GetFileName(backupFile));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("api/config/restore")]
    public async Task<IActionResult> RestoreBackup(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });
        
        try
        {
            var tempPath = Path.GetTempFileName();
            using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            
            var result = await _configService.RestoreBackupAsync(tempPath);
            System.IO.File.Delete(tempPath);
            
            if (result)
                return Ok(new { message = "Backup restored successfully" });
            
            return BadRequest(new { error = "Failed to restore backup" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore backup");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    public class DeviceTestRequest
    {
        public string DevicePath { get; set; } = string.Empty;
    }

    public class PathVerificationRequest
    {
        public string Path { get; set; } = string.Empty;
    }

    public class FileItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
    }
}