using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace HeadendStreamer.Web.Controllers;

public class ConfigController : Controller
{
    private readonly ConfigService _configService;
    private readonly FfmpegService _ffmpegService;
    private readonly ILogger<ConfigController> _logger;
    
    public ConfigController(
        ConfigService configService,
        FfmpegService ffmpegService,
        ILogger<ConfigController> logger)
    {
        _configService = configService;
        _ffmpegService = ffmpegService;
        _logger = logger;
    }

    // MVC Actions

    [HttpGet]
    public IActionResult Index()
    {
        var configs = _configService.GetAllConfigs();
        return View(configs);
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
        return View(config);
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
            var updatedConfig = await _configService.UpdateConfigAsync(id, updates);
            if (updatedConfig == null)
                return NotFound();
            
            return Ok(updatedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to update config {id}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpDelete("api/config/{id}")]
    public async Task<IActionResult> DeleteConfig(string id)
    {
        var result = await _configService.DeleteConfigAsync(id);
        if (!result)
            return NotFound();
        
        return Ok(new { message = "Config deleted successfully" });
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

            // Add files (videos focused)
            var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".ts", ".m2ts", ".flv", ".webm" };
            foreach (var file in di.GetFiles())
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0) continue;
                if (!videoExtensions.Contains(file.Extension.ToLower())) continue;

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