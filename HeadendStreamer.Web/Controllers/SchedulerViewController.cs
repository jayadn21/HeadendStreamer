using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace HeadendStreamer.Web.Controllers;

/// <summary>
/// Controller for rendering the Scheduler view.
/// </summary>
[Authorize]
public class SchedulerViewController : Controller
{
    public IActionResult Index()
    {
        return View("~/Views/Scheduler/Index.cshtml");
    }
}
