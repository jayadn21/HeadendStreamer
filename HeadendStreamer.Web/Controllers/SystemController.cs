using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace HeadendStreamer.Web.Controllers;

[Route("api/[controller]")]
[ApiController]
public class SystemController : ControllerBase
{
    private readonly SystemMonitorService _systemMonitor;
    private readonly ILogger<SystemController> _logger;

    public SystemController(
        SystemMonitorService systemMonitor,
        ILogger<SystemController> logger)
    {
        _systemMonitor = systemMonitor;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetSystemStatus()
    {
        var info = _systemMonitor.GetSystemInfo();
        return Ok(info);
    }

    [HttpGet("health")]
    public async Task<IActionResult> GetHealth()
    {
        var health = await _systemMonitor.CheckSystemHealthAsync();
        return Ok(health);
    }

    [HttpGet("processes")]
    public IActionResult GetProcesses()
    {
        var systemInfo = _systemMonitor.GetSystemInfo();
        return Ok(systemInfo.RunningProcesses);
    }
}