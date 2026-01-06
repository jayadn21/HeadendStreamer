using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Models.ViewModels;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace HeadendStreamer.Web.Controllers;

public class StreamController : Controller
{
    private readonly StreamManagerService _streamManager;
    private readonly ConfigService _configService;
    private readonly ILogger<StreamController> _logger;
    
    public StreamController(
        StreamManagerService streamManager,
        ConfigService configService,
        ILogger<StreamController> logger)
    {
        _streamManager = streamManager;
        _configService = configService;
        _logger = logger;
    }

    // MVC Actions

    [HttpGet]
    public async Task<IActionResult> Details(string id)
    {
        var config = await _configService.GetConfigAsync(id);
        if (config == null)
        {
            return NotFound();
        }

        var status = _streamManager.GetStreamStatus(id);
        
        var viewModel = new StreamViewModel
        {
            Config = config,
            Status = status
        };

        return View(viewModel);
    }
    
    // API Actions
    
    [HttpGet("api/stream")]
    public IActionResult GetAllStreams()
    {
        var configs = _configService.GetAllConfigs();
        var statuses = _streamManager.GetAllStreamStatus();
        
        var streams = configs.Select(config => new StreamViewModel
        {
            Config = config,
            Status = statuses.TryGetValue(config.Id, out var status) ? status : null
        }).ToList();
        
        return Ok(streams);
    }
    
    [HttpGet("api/stream/{id}")]
    public async Task<IActionResult> GetStream(string id)
    {
        var config = await _configService.GetConfigAsync(id);
        if (config == null)
            return NotFound();
        
        var status = _streamManager.GetStreamStatus(id);
        
        return Ok(new StreamViewModel
        {
            Config = config,
            Status = status
        });
    }
    
    [HttpPost("api/stream/{id}/start")]
    public async Task<IActionResult> StartStream(string id)
    {
        try
        {
            var status = await _streamManager.StartStreamAsync(id);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to start stream {id}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("api/stream/{id}/stop")]
    public async Task<IActionResult> StopStream(string id)
    {
        try
        {
            var result = await _streamManager.StopStreamAsync(id);
            if (result)
                return Ok(new { message = "Stream stopped successfully" });
            
            return NotFound(new { error = "Stream not found or not running" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to stop stream {id}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("api/stream/{id}/restart")]
    public async Task<IActionResult> RestartStream(string id)
    {
        try
        {
            var status = await _streamManager.RestartStreamAsync(id);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to restart stream {id}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpGet("api/stream/{id}/logs")]
    public async Task<IActionResult> GetLogs(string id, [FromQuery] int lines = 100)
    {
        try
        {
            var logs = await _streamManager.GetStreamLogsAsync(id, lines);
            return Ok(logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get logs for stream {id}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpGet("api/stream/{id}/stats")]
    public IActionResult GetStreamStats(string id)
    {
        var status = _streamManager.GetStreamStatus(id);
        if (status == null)
            return NotFound();
        
        return Ok(status);
    }
}