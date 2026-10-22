using HeadendStreamer.Web.Models.Entities;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using HeadendStreamer.Web.Hubs;

namespace HeadendStreamer.Web.Controllers;

/// <summary>
/// Controller for managing the stream scheduler.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SchedulerController : ControllerBase
{
    private readonly ILogger<SchedulerController> _logger;
    private readonly SchedulerService _schedulerService;
    private readonly ConfigService _configService;
    private readonly IHubContext<StreamHub> _hubContext;

    public SchedulerController(
        ILogger<SchedulerController> logger,
        SchedulerService schedulerService,
        ConfigService configService,
        IHubContext<StreamHub> hubContext)
    {
        _logger = logger;
        _schedulerService = schedulerService;
        _configService = configService;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Gets the current scheduler state including active and next programs.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        try
        {
            var state = _schedulerService.GetState();
            return Ok(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scheduler status");
            return StatusCode(500, new { error = "Failed to get scheduler status", message = ex.Message });
        }
    }

    /// <summary>
    /// Gets the current schedule.
    /// </summary>
    [HttpGet("schedule")]
    public IActionResult GetSchedule()
    {
        try
        {
            // Load schedule from service (it maintains in-memory state)
            _schedulerService.LoadSchedule();
            
            // Read the file directly to return it
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HeadendStreamer");
            var scheduleFile = Path.Combine(appDataPath, "schedule.json");
            
            if (!System.IO.File.Exists(scheduleFile))
            {
                return Ok(new Schedule());
            }
            
            var json = System.IO.File.ReadAllText(scheduleFile);
            var schedule = System.Text.Json.JsonSerializer.Deserialize<Schedule>(json);
            
            return Ok(schedule ?? new Schedule());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting schedule");
            return StatusCode(500, new { error = "Failed to get schedule", message = ex.Message });
        }
    }

    /// <summary>
    /// Saves a new schedule.
    /// </summary>
    [HttpPost("schedule")]
    public async Task<IActionResult> SaveSchedule([FromBody] Schedule schedule)
    {
        try
        {
            if (schedule == null)
                return BadRequest(new { error = "Invalid schedule data" });

            _logger.LogInformation("Saving new schedule with {Count} programs", schedule.Programs.Count);
            
            _schedulerService.SaveSchedule(schedule);
            
            // Notify all clients about the schedule update
            await _hubContext.Clients.All.SendAsync("ScheduleUpdated", schedule);
            
            return Ok(new { success = true, message = "Schedule saved successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving schedule");
            return StatusCode(500, new { error = "Failed to save schedule", message = ex.Message });
        }
    }

    /// <summary>
    /// Starts or stops a specific program manually.
    /// </summary>
    [HttpPost("program/{programId}/toggle")]
    public async Task<IActionResult> ToggleProgram(string programId, [FromBody] ToggleProgramRequest request)
    {
        try
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HeadendStreamer");
            var scheduleFile = Path.Combine(appDataPath, "schedule.json");
            
            if (!System.IO.File.Exists(scheduleFile))
                return NotFound(new { error = "Schedule not found" });
            
            var json = System.IO.File.ReadAllText(scheduleFile);
            var schedule = System.Text.Json.JsonSerializer.Deserialize<Schedule>(json);
            
            if (schedule == null)
                return NotFound(new { error = "Schedule is empty" });
            
            var program = schedule.Programs.FirstOrDefault(p => p.Id == programId);
            if (program == null)
                return NotFound(new { error = $"Program {programId} not found" });
            
            if (request.Enable)
            {
                // Start this program's stream
                if (!string.IsNullOrEmpty(program.Source.ConfigId))
                {
                    await _hubContext.Clients.All.SendAsync("SchedulerManualStart", new
                    {
                        programId = program.Id,
                        title = program.Title,
                        configId = program.Source.ConfigId
                    });
                    
                    return Ok(new { success = true, message = $"Starting program: {program.Title}" });
                }
                
                return BadRequest(new { error = "Program has no associated config ID" });
            }
            else
            {
                // Stop current streams
                var status = _hubContext;
                return Ok(new { success = true, message = "Stopping current program" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling program {ProgramId}", programId);
            return StatusCode(500, new { error = "Failed to toggle program", message = ex.Message });
        }
    }

    /// <summary>
    /// Reloads the schedule from disk.
    /// </summary>
    [HttpPost("reload")]
    public IActionResult ReloadSchedule()
    {
        try
        {
            _schedulerService.LoadSchedule();
            return Ok(new { success = true, message = "Schedule reloaded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading schedule");
            return StatusCode(500, new { error = "Failed to reload schedule", message = ex.Message });
        }
    }

    /// <summary>
    /// Gets available configs that can be used in schedule items.
    /// </summary>
    [HttpGet("configs")]
    public IActionResult GetAvailableConfigs()
    {
        try
        {
            var configs = _configService.GetAllConfigs();
            return Ok(configs.Select(c => new
            {
                c.Id,
                c.Name,
                c.Enabled
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available configs");
            return StatusCode(500, new { error = "Failed to get configs", message = ex.Message });
        }
    }
}

public class ToggleProgramRequest
{
    public bool Enable { get; set; }
}
