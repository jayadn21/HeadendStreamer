using HeadendStreamer.Web.Models.ViewModels;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeadendStreamer.Web.Controllers;

[Authorize]
public class SystemConfigController : Controller
{
    private readonly SystemConfigService _systemConfigService;
    private readonly ILogger<SystemConfigController> _logger;

    public SystemConfigController(
        SystemConfigService systemConfigService,
        ILogger<SystemConfigController> logger)
    {
        _systemConfigService = systemConfigService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var model = _systemConfigService.GetSystemConfig();
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Save(SystemConfigViewModel model)
    {
        var result = _systemConfigService.SaveSystemConfig(model);

        if (IsAjaxRequest())
        {
            return Json(new { success = result.Success, message = result.Message });
        }

        if (result.Success)
        {
            TempData["SuccessMessage"] = result.Message;
        }
        else
        {
            TempData["ErrorMessage"] = result.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult SaveRaw([FromBody] RawJsonRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.RawJson))
        {
            return Json(new { success = false, message = "No JSON content provided." });
        }

        var result = _systemConfigService.SaveRawJson(request.RawJson);
        return Json(new { success = result.Success, message = result.Message });
    }

    [HttpPost]
    public async Task<IActionResult> TestFfmpeg([FromBody] TestFfmpegRequest? request)
    {
        var result = await _systemConfigService.TestFfmpegAsync(request?.FfmpegPath);
        return Json(new
        {
            success = result.Success,
            output = result.Output,
            error = result.Error
        });
    }

    [HttpGet]
    public IActionResult Browse([FromQuery] string? path)
    {
        var result = _systemConfigService.BrowseDirectory(path);
        return Json(result);
    }

    [HttpGet]
    public IActionResult Backups()
    {
        var backups = _systemConfigService.GetBackups();
        return Json(backups);
    }

    [HttpPost]
    public IActionResult RestoreBackup([FromBody] RestoreBackupRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.FileName))
        {
            return Json(new { success = false, message = "File name is required." });
        }

        var result = _systemConfigService.RestoreBackup(request.FileName);
        return Json(new { success = result.Success, message = result.Message });
    }

    [HttpPost]
    public IActionResult ClearOldFfmpegLogs([FromServices] FfmpegLogCleanupService cleanupService)
    {
        try
        {
            var count = cleanupService.CleanupOldLogs();
            return Json(new { success = true, deletedCount = count, message = $"Deleted {count} old FFmpeg log file(s). Today's logs retained." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear old FFmpeg logs");
            return Json(new { success = false, message = ex.Message });
        }
    }

    private bool IsAjaxRequest()
    {
        return Request.Headers["X-Requested-With"] == "XMLHttpRequest" ||
               Request.Headers.Accept.ToString().Contains("application/json");
    }

    public class RawJsonRequest
    {
        public string RawJson { get; set; } = string.Empty;
    }

    public class TestFfmpegRequest
    {
        public string? FfmpegPath { get; set; }
    }

    public class RestoreBackupRequest
    {
        public string FileName { get; set; } = string.Empty;
    }
}
