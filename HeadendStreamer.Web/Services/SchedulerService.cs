using HeadendStreamer.Web.Hubs;
using HeadendStreamer.Web.Models.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace HeadendStreamer.Web.Services;

/// <summary>
/// Scheduler service that manages automated stream scheduling based on predefined schedules.
/// Evaluates the schedule every second and automatically starts/stops streams according to timing.
/// </summary>
public class SchedulerService : IDisposable
{
    private readonly ILogger<SchedulerService> _logger;
    private readonly IHubContext<StreamHub> _hubContext;
    private readonly StreamManagerService _streamManager;
    private readonly ConfigService _configService;
    private readonly string _scheduleFilePath;
    
    // Threading and lifecycle
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _evaluationLoopTask;
    private bool _isRunning;
    private readonly object _lock = new();
    
    // State
    private Schedule? _currentSchedule;
    private ScheduledProgram? _activeProgram;
    private ScheduledProgram? _nextProgram;
    private DateTime _lastEvaluationTime;
    private readonly ConcurrentDictionary<string, DateTime> _programHistory = new();
    
    // Constants
    private const int EvaluationIntervalMs = 1000; // Evaluate every second
    private const string DefaultProgramId = "default-source";
    
    public SchedulerService(
        ILogger<SchedulerService> logger,
        IHubContext<StreamHub> hubContext,
        StreamManagerService streamManager,
        ConfigService configService)
    {
        _logger = logger;
        _hubContext = hubContext;
        _streamManager = streamManager;
        _configService = configService;
        
        // Schedule file path
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HeadendStreamer");
        Directory.CreateDirectory(appDataPath);
        _scheduleFilePath = Path.Combine(appDataPath, "schedule.json");
    }
    
    /// <summary>
    /// Starts the scheduler evaluation loop.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Scheduler is already running");
                return;
            }
            
            _isRunning = true;
            _logger.LogInformation("Starting Scheduler evaluation loop");
            
            // Load initial schedule
            LoadSchedule();
            
            // Start evaluation loop
            _evaluationLoopTask = Task.Run(() => EvaluationLoopAsync(_cancellationTokenSource.Token));
        }
    }
    
    /// <summary>
    /// Stops the scheduler evaluation loop.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning)
                return;
                
            _logger.LogInformation("Stopping Scheduler");
            _cancellationTokenSource.Cancel();
            _isRunning = false;
        }
    }
    
    /// <summary>
    /// Main evaluation loop - runs every second to check if target program has changed.
    /// </summary>
    private async Task EvaluationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var interval = TimeSpan.FromMilliseconds(EvaluationIntervalMs);
            var timer = new PeriodicTimer(interval);
            
            using (timer)
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    EvaluateAndSwitch();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduler evaluation loop canceled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in scheduler evaluation loop");
        }
    }
    
    /// <summary>
    /// Core scheduling logic - evaluates current time against schedule and switches streams if needed.
    /// </summary>
    private void EvaluateAndSwitch()
    {
        var now = DateTime.Now;
        _lastEvaluationTime = now;
        
        var currentSchedule = _currentSchedule;
        if (currentSchedule == null)
            return;
        
        // Find the program active at current time
        var targetProgram = FindProgramAtTime(currentSchedule.Programs, now);
        
        // Check if we need to switch
        bool shouldSwitch = false;
        if (targetProgram == null && _activeProgram != null)
        {
            // No program active, but one was before - stop it
            shouldSwitch = true;
        }
        else if (targetProgram != null && (_activeProgram == null || targetProgram.Id != _activeProgram.Id))
        {
            // New program is active - switch to it
            shouldSwitch = true;
        }
        
        if (shouldSwitch)
        {
            SwitchToProgram(targetProgram, now);
        }
        
        // Calculate next program for informational purposes
        _nextProgram = targetProgram != null 
            ? FindNextProgramAfter(currentSchedule.Programs, GetProgramEndTime(targetProgram, now))
            : FindNextProgramAfter(currentSchedule.Programs, now);
        
        _activeProgram = targetProgram;
    }
    
    /// <summary>
    /// Switches to a new program by stopping current stream and starting the new one.
    /// </summary>
    private async void SwitchToProgram(ScheduledProgram? program, DateTime now)
    {
        try
        {
            // Stop current stream if running
            var currentStatus = _streamManager.GetAllStreamStatus();
            foreach (var kvp in currentStatus.Where(s => s.Value.IsRunning))
            {
                _logger.LogInformation($"Stopping stream {kvp.Key} due to schedule change");
                await _streamManager.StopStreamAsync(kvp.Key);
            }
            
            if (program != null && program.Enabled)
            {
                // Start new program's stream
                var configId = program.Source.ConfigId;
                if (!string.IsNullOrEmpty(configId))
                {
                    _logger.LogInformation($"Starting scheduled program '{program.Title}' (Config: {configId})");
                    await _streamManager.StartStreamAsync(configId);
                    
                    // Record in history
                    _programHistory[program.Id] = now;
                    
                    // Notify via SignalR
                    await _hubContext.Clients.All.SendAsync("SchedulerProgramChanged", new
                    {
                        programId = program.Id,
                        title = program.Title,
                        configId = configId,
                        startTime = now,
                        endTime = GetProgramEndTime(program, now)
                    });
                }
            }
            else
            {
                _logger.LogInformation("No scheduled program active - all streams stopped");
                
                // Notify via SignalR
                await _hubContext.Clients.All.SendAsync("SchedulerProgramChanged", new
                {
                    programId = (string?)null,
                    title = (string?)null,
                    configId = (string?)null,
                    startTime = (DateTime?)null,
                    endTime = (DateTime?)null
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching to program {ProgramId}", program?.Id);
        }
    }
    
    /// <summary>
    /// Loads the schedule from disk.
    /// </summary>
    public void LoadSchedule()
    {
        try
        {
            if (!File.Exists(_scheduleFilePath))
            {
                _logger.LogInformation("Schedule file not found, creating default schedule");
                CreateDefaultSchedule();
                return;
            }
            
            var json = File.ReadAllText(_scheduleFilePath);
            var schedule = JsonSerializer.Deserialize<Schedule>(json);
            
            if (schedule != null)
            {
                _currentSchedule = schedule;
                _logger.LogInformation("Loaded schedule with {Count} programs", schedule.Programs.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load schedule from {Path}", _scheduleFilePath);
            CreateDefaultSchedule();
        }
    }
    
    /// <summary>
    /// Saves the schedule to disk.
    /// </summary>
    public void SaveSchedule(Schedule schedule)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            
            var json = JsonSerializer.Serialize(schedule, options);
            File.WriteAllText(_scheduleFilePath, json);
            
            _currentSchedule = schedule;
            _logger.LogInformation("Saved schedule with {Count} programs", schedule.Programs.Count);
            
            // Re-evaluate immediately in case of changes
            EvaluateAndSwitch();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save schedule");
            throw;
        }
    }
    
    /// <summary>
    /// Creates a default empty schedule.
    /// </summary>
    private void CreateDefaultSchedule()
    {
        var defaultSchedule = new Schedule
        {
            Version = "1.0",
            ScheduleName = "Default Schedule",
            Programs = new List<ScheduledProgram>()
        };
        
        SaveSchedule(defaultSchedule);
    }
    
    /// <summary>
    /// Gets the current scheduler state.
    /// </summary>
    public SchedulerState GetState()
    {
        return new SchedulerState
        {
            CurrentProgram = _activeProgram != null ? ToExecutableProgram(_activeProgram) : null,
            NextProgram = _nextProgram != null ? ToExecutableProgram(_nextProgram) : null,
            LastEvaluation = _lastEvaluationTime,
            IsRunning = _isRunning
        };
    }
    
    /// <summary>
    /// Finds the program active at the given time.
    /// </summary>
    private static ScheduledProgram? FindProgramAtTime(List<ScheduledProgram> programs, DateTime t)
    {
        var tLocal = t; // Already local time
        
        foreach (var program in programs)
        {
            if (!program.Enabled)
                continue;
            
            if (program.Timing.IsRecurring)
            {
                // For recurring events: check today and yesterday (for overnight events)
                for (int dayOffset = 0; dayOffset >= -1; dayOffset--)
                {
                    var checkDay = tLocal.AddDays(dayOffset);
                    
                    // Check if recurrence rule is active for this date
                    var checkDayStr = checkDay.ToString("yyyy-MM-dd");
                    if (!string.IsNullOrEmpty(program.Timing.Recurrence.StartRecur) && 
                        checkDayStr < program.Timing.Recurrence.StartRecur)
                        continue;
                    if (!string.IsNullOrEmpty(program.Timing.Recurrence.EndRecur) && 
                        checkDayStr > program.Timing.Recurrence.EndRecur)
                        continue;
                    
                    // Check if day of week matches
                    var dayMatches = false;
                    var checkWeekday = checkDay.DayOfWeek;
                    foreach (var dayStr in program.Timing.Recurrence.DaysOfWeek)
                    {
                        if (TryParseDayOfWeek(dayStr, out var mappedDay) && mappedDay == checkWeekday)
                        {
                            dayMatches = true;
                            break;
                        }
                    }
                    if (!dayMatches)
                        continue;
                    
                    // Extract ONLY the time part from the templates
                    var templateStart = program.Timing.Start;
                    var templateEnd = program.Timing.End;
                    if (templateStart.TimeOfDay.TotalSeconds == 0 || templateEnd.TimeOfDay.TotalSeconds == 0)
                        continue;
                    
                    // Build local times using only the H:M:S from templates
                    var eventStart = new DateTime(checkDay.Year, checkDay.Month, checkDay.Day,
                        templateStart.Hour, templateStart.Minute, templateStart.Second, DateTimeKind.Local);
                    
                    var eventEnd = new DateTime(checkDay.Year, checkDay.Month, checkDay.Day,
                        templateEnd.Hour, templateEnd.Minute, templateEnd.Second, DateTimeKind.Local);
                    
                    // Handle overnight events
                    if (eventEnd <= eventStart)
                        eventEnd = eventEnd.AddHours(24);
                    
                    // Check if current time is within [eventStart, eventEnd)
                    if ((tLocal >= eventStart && tLocal < eventEnd))
                        return program;
                }
            }
            else
            {
                // Non-recurring: use absolute timestamps
                var start = program.Timing.Start;
                var end = program.Timing.End;
                if (start != default && end != default && tLocal >= start && tLocal < end)
                    return program;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Finds the next program that starts after the given time.
    /// </summary>
    private static ScheduledProgram? FindNextProgramAfter(List<ScheduledProgram> programs, DateTime after)
    {
        ScheduledProgram? next = null;
        DateTime? nextStartTime = null;
        
        foreach (var program in programs.Where(p => p.Enabled))
        {
            DateTime? candidateStart = null;
            
            if (program.Timing.IsRecurring)
            {
                candidateStart = FindNextRecurrenceAfter(program, after);
            }
            else
            {
                var start = program.Timing.Start;
                if (start != default && start > after)
                    candidateStart = start;
            }
            
            if (candidateStart.HasValue)
            {
                if (next == null || candidateStart < nextStartTime)
                {
                    next = program;
                    nextStartTime = candidateStart;
                }
            }
        }
        
        return next;
    }
    
    /// <summary>
    /// Finds the next occurrence of a recurring program after the given time.
    /// </summary>
    private static DateTime? FindNextRecurrenceAfter(ScheduledProgram program, DateTime after)
    {
        var templateStart = program.Timing.Start;
        if (templateStart == default)
            return null;
        
        // Check the next 7 days for a matching occurrence
        for (int dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var checkDay = after.AddDays(dayOffset);
            
            // Check if recurrence rule is active for this date
            var checkDayStr = checkDay.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(program.Timing.Recurrence.StartRecur) && 
                checkDayStr < program.Timing.Recurrence.StartRecur)
                continue;
            if (!string.IsNullOrEmpty(program.Timing.Recurrence.EndRecur) && 
                checkDayStr > program.Timing.Recurrence.EndRecur)
                continue;
            
            // Check if this weekday is in the recurrence pattern
            var checkWeekday = checkDay.DayOfWeek;
            var dayMatches = false;
            foreach (var dayStr in program.Timing.Recurrence.DaysOfWeek)
            {
                if (TryParseDayOfWeek(dayStr, out var mappedDay) && mappedDay == checkWeekday)
                {
                    dayMatches = true;
                    break;
                }
            }
            if (!dayMatches)
                continue;
            
            // Build the candidate start time using the template time
            var candidateStart = new DateTime(checkDay.Year, checkDay.Month, checkDay.Day,
                templateStart.Hour, templateStart.Minute, templateStart.Second, DateTimeKind.Local);
            
            // This is a valid occurrence if it's after the reference time
            if (candidateStart > after)
                return candidateStart;
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets the effective end time of a program for a given day.
    /// </summary>
    private static DateTime GetProgramEndTime(ScheduledProgram program, DateTime now)
    {
        if (program.Timing.Start == default || program.Timing.End == default)
            return default;
        
        var nowLocal = now;
        
        if (program.Timing.IsRecurring)
        {
            var templateStart = program.Timing.Start;
            var templateEnd = program.Timing.End;
            
            var todayEnd = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day,
                templateEnd.Hour, templateEnd.Minute, templateEnd.Second, DateTimeKind.Local);
            
            // Handle overnight programs
            if (templateEnd <= templateStart)
                todayEnd = todayEnd.AddHours(24);
            
            return todayEnd;
        }
        
        return program.Timing.End;
    }
    
    /// <summary>
    /// Converts a ScheduledProgram to an ExecutableProgram DTO.
    /// </summary>
    private static ExecutableProgram ToExecutableProgram(ScheduledProgram program)
    {
        return new ExecutableProgram
        {
            Id = program.Id,
            Title = program.Title,
            ConfigId = program.Source.ConfigId,
            Start = program.Timing.Start,
            End = program.Timing.End,
            IsActive = true
        };
    }
    
    /// <summary>
    /// Tries to parse a day of week string (e.g., "MON", "TUE").
    /// </summary>
    private static bool TryParseDayOfWeek(string dayStr, out DayOfWeek dayOfWeek)
    {
        dayOfWeek = DayOfWeek.Sunday;
        return dayStr.ToUpperInvariant() switch
        {
            "SUN" => (dayOfWeek = DayOfWeek.Sunday) != default,
            "MON" => (dayOfWeek = DayOfWeek.Monday) != default,
            "TUE" => (dayOfWeek = DayOfWeek.Tuesday) != default,
            "WED" => (dayOfWeek = DayOfWeek.Wednesday) != default,
            "THU" => (dayOfWeek = DayOfWeek.Thursday) != default,
            "FRI" => (dayOfWeek = DayOfWeek.Friday) != default,
            "SAT" => (dayOfWeek = DayOfWeek.Saturday) != default,
            _ => false
        };
    }
    
    /// <summary>
    /// Disposes the scheduler resources.
    /// </summary>
    public void Dispose()
    {
        Stop();
        _cancellationTokenSource.Dispose();
    }
}
