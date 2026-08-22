using Microsoft.AspNetCore.Mvc;
using HeadendStreamer.Web.Services;
using HeadendStreamer.Web.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;

namespace HeadendStreamer.Web.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly SystemMonitorService _systemMonitor;
    private readonly StreamManagerService _streamManager;
    private readonly ConfigService _configService;
    private readonly ExternalProcessService _externalProcessService;

    public HomeController(
        SystemMonitorService systemMonitor, 
        StreamManagerService streamManager,
        ConfigService configService,
        ExternalProcessService externalProcessService)
    {
        _systemMonitor = systemMonitor;
        _streamManager = streamManager;
        _configService = configService;
        _externalProcessService = externalProcessService;
    }

    public async Task<IActionResult> Index()
    {
        // Update system info
        await _systemMonitor.UpdateSystemInfoAsync();
        var systemInfo = _systemMonitor.GetSystemInfo();
        
        // Get all configs
        var configs = _configService.GetAllConfigs();
        
        var obsStatus = await _externalProcessService.GetStatusAsync("OBS_Scheduler");
        var spxStatus = await _externalProcessService.GetStatusAsync("SPX_Graphics");

        // Build view model
        var viewModel = new DashboardViewModel
        {
            SystemInfo = systemInfo,
            AutoStartOnStartup = _configService.AutoStartOnStartup,
            Streams = configs.Select(c => new StreamViewModel
            {
                Config = c,
                Status = _streamManager.GetStreamStatus(c.Id)
            }).ToList(),
            ObsScheduler = new ExternalServiceStatusViewModel
            {
                ServiceName = "OBS_Scheduler",
                IsRunning = obsStatus.IsRunning,
                ProcessId = obsStatus.ProcessId,
                ServerURL = obsStatus.ServerURL,
                Uptime = obsStatus.Uptime
            },
            SpxGraphics = new ExternalServiceStatusViewModel
            {
                ServiceName = "SPX_Graphics",
                IsRunning = spxStatus.IsRunning,
                ProcessId = spxStatus.ProcessId,
                ServerURL = spxStatus.ServerURL,
                Uptime = spxStatus.Uptime
            }
        };

        return View(viewModel);
    }

    [HttpGet("api/dashboard/stats")]
    public async Task<IActionResult> GetDashboardStats()
    {
        await _systemMonitor.UpdateSystemInfoAsync();
        var systemInfo = _systemMonitor.GetSystemInfo();
        var configs = _configService.GetAllConfigs();
        var streamStatuses = _streamManager.GetAllStreamStatus();
        
        var obsStatus = await _externalProcessService.GetStatusAsync("OBS_Scheduler");
        var spxStatus = await _externalProcessService.GetStatusAsync("SPX_Graphics");

        return Ok(new
        {
            systemInfo = new
            {
                cpuUsage = systemInfo.CpuUsage,
                memoryUsage = systemInfo.MemoryUsage,
                totalMemory = systemInfo.TotalMemory,
                availableMemory = systemInfo.AvailableMemory,
                diskUsage = systemInfo.DiskUsage,
                uptime = systemInfo.Uptime.TotalSeconds,
                hostname = systemInfo.Hostname
            },
            streams = new
            {
                total = configs.Count,
                active = streamStatuses.Count(s => s.Value.IsRunning)
            },
            streamStatuses = streamStatuses.Select(kvp => new
            {
                configId = kvp.Key,
                isRunning = kvp.Value.IsRunning,
                uptime = kvp.Value.Uptime.TotalSeconds,
                processId = kvp.Value.ProcessId
            }),
            externalServices = new
            {
                obsScheduler = new
                {
                    serviceName = "OBS_Scheduler",
                    isRunning = obsStatus.IsRunning,
                    processId = obsStatus.ProcessId,
                    serverURL = obsStatus.ServerURL,
                    uptime = obsStatus.Uptime.TotalSeconds
                },
                spxGraphics = new
                {
                    serviceName = "SPX_Graphics",
                    isRunning = spxStatus.IsRunning,
                    processId = spxStatus.ProcessId,
                    serverURL = spxStatus.ServerURL,
                    uptime = spxStatus.Uptime.TotalSeconds
                }
            }
        });
    }

    [HttpPost("api/external-service/{serviceName}/start")]
    public async Task<IActionResult> StartExternalService(string serviceName)
    {
        var success = await _externalProcessService.StartAsync(serviceName);
        if (!success)
        {
            return BadRequest(new { error = $"Failed to start {serviceName}" });
        }
        var status = await _externalProcessService.GetStatusAsync(serviceName);
        return Ok(status);
    }

    [HttpPost("api/external-service/{serviceName}/stop")]
    public async Task<IActionResult> StopExternalService(string serviceName)
    {
        var success = await _externalProcessService.StopAsync(serviceName);
        if (!success)
        {
            return BadRequest(new { error = $"Failed to stop {serviceName}" });
        }
        var status = await _externalProcessService.GetStatusAsync(serviceName);
        return Ok(status);
    }

    [HttpPost("api/external-service/{serviceName}/restart")]
    public async Task<IActionResult> RestartExternalService(string serviceName)
    {
        var success = await _externalProcessService.RestartAsync(serviceName);
        if (!success)
        {
            return BadRequest(new { error = $"Failed to restart {serviceName}" });
        }
        var status = await _externalProcessService.GetStatusAsync(serviceName);
        return Ok(status);
    }

    [HttpGet("api/settings/autostart")]
    public IActionResult GetAutoStart()
    {
        return Ok(new { autoStart = _configService.AutoStartOnStartup });
    }

    [HttpPost("api/settings/autostart")]
    public IActionResult SetAutoStart([FromQuery] bool autoStart)
    {
        _configService.AutoStartOnStartup = autoStart;
        return Ok(new { autoStart = _configService.AutoStartOnStartup });
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View();
    }
}
