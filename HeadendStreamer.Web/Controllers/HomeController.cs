using Microsoft.AspNetCore.Mvc;
using HeadendStreamer.Web.Services;
using HeadendStreamer.Web.Models.ViewModels;

namespace HeadendStreamer.Web.Controllers;

public class HomeController : Controller
{
    private readonly SystemMonitorService _systemMonitor;
    private readonly StreamManagerService _streamManager;
    private readonly ConfigService _configService;

    public HomeController(
        SystemMonitorService systemMonitor, 
        StreamManagerService streamManager,
        ConfigService configService)
    {
        _systemMonitor = systemMonitor;
        _streamManager = streamManager;
        _configService = configService;
    }

    public async Task<IActionResult> Index()
    {
        // Update system info
        await _systemMonitor.UpdateSystemInfoAsync();
        var systemInfo = _systemMonitor.GetSystemInfo();
        
        // Get all configs
        var configs = _configService.GetAllConfigs();
        
        // Build view model
        var viewModel = new DashboardViewModel
        {
            SystemInfo = systemInfo,
            Streams = configs.Select(c => new StreamViewModel
            {
                Config = c,
                Status = _streamManager.GetStreamStatus(c.Id)
            }).ToList()
        };

        return View(viewModel);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View();
    }
}
