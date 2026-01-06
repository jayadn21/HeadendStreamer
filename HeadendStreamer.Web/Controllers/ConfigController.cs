using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace HeadendStreamer.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ConfigController : ControllerBase
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
    
    [HttpGet]
    public IActionResult GetAllConfigs()
    {
        var configs = _configService.GetAllConfigs();
        return Ok(configs);
    }
    
    [HttpGet("{id}")]
    public async Task<IActionResult> GetConfig(string id)
    {
        var config = await _configService.GetConfigAsync(id);
        if (config == null)
            return NotFound();
        
        return Ok(config);
    }
    
    [HttpPost]
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
    
    [HttpPut("{id}")]
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
    
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteConfig(string id)
    {
        var result = await _configService.DeleteConfigAsync(id);
        if (!result)
            return NotFound();
        
        return Ok(new { message = "Config deleted successfully" });
    }
    
    [HttpGet("templates/{name}")]
    public IActionResult GetTemplate(string name)
    {
        // Create template from ConfigService method
        return Ok(new { message = "Template endpoint" });
    }
    
    [HttpGet("devices")]
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
    
    [HttpPost("test/device")]
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
    
    [HttpPost("test/stream")]
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
    
    [HttpPost("backup")]
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
    
    [HttpPost("restore")]
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
}