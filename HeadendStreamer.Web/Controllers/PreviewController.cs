using Microsoft.AspNetCore.Mvc;
using HeadendStreamer.Web.Services;

namespace HeadendStreamer.Web.Controllers;

public class PreviewController : Controller
{
    private readonly Go2rtcService _go2rtcService;
    private readonly ILogger<PreviewController> _logger;

    public PreviewController(Go2rtcService go2rtcService, ILogger<PreviewController> logger)
    {
        _go2rtcService = go2rtcService;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Status()
    {
        var isRunning = await _go2rtcService.IsRunningAsync();
        return Json(new { isRunning });
    }

    [HttpGet]
    public async Task<IActionResult> Streams()
    {
        var streams = await _go2rtcService.GetStreamsAsync();
        return Json(streams);
    }

    [HttpPost]
    public async Task<IActionResult> Start()
    {
        var result = await _go2rtcService.StartAsync();
        return Json(new { success = result, isRunning = await _go2rtcService.IsRunningAsync() });
    }

    [HttpPost]
    public async Task<IActionResult> Stop()
    {
        var result = await _go2rtcService.StopAsync();
        return Json(new { success = result, isRunning = await _go2rtcService.IsRunningAsync() });
    }
}
