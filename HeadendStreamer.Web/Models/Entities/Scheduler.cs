using System.Text.Json.Serialization;

namespace HeadendStreamer.Web.Models.Entities;

// ============================================================================
// SCHEDULE TYPES
// ============================================================================

/// <summary>
/// Root object representing a full scheduling configuration.
/// Maps to the schedule.json file.
/// </summary>
public class Schedule
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";
    
    [JsonPropertyName("scheduleName")]
    public string ScheduleName { get; set; } = "Default Schedule";
    
    [JsonPropertyName("schedule")]
    public List<ScheduledProgram> Programs { get; set; } = new();
}

// ============================================================================
// PROGRAM TYPES
// ============================================================================

/// <summary>
/// Defines a single scheduled event, including metadata, source, timing, and behavior configuration.
/// This is the internal domain model for the scheduler.
/// </summary>
public class ScheduledProgram
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
    
    [JsonPropertyName("general")]
    public General General { get; set; } = new();
    
    [JsonPropertyName("source")]
    public Source Source { get; set; } = new();
    
    [JsonPropertyName("timing")]
    public Timing Timing { get; set; } = new();
    
    [JsonPropertyName("behavior")]
    public Behavior Behavior { get; set; } = new();
}

/// <summary>
/// Stores metadata for program visualization in the frontend calendar.
/// </summary>
public class General
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
    
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
    
    [JsonPropertyName("classNames")]
    public List<string> ClassNames { get; set; } = new();
    
    [JsonPropertyName("textColor")]
    public string TextColor { get; set; } = "#ffffff";
    
    [JsonPropertyName("backgroundColor")]
    public string BackgroundColor { get; set; } = "#0d6e13";
    
    [JsonPropertyName("borderColor")]
    public string BorderColor { get; set; } = "#141d9f";
}

/// <summary>
/// Defines a stream configuration source that should be activated during the program.
/// </summary>
public class Source
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("inputKind")]
    public string InputKind { get; set; } = "stream_config";
    
    [JsonPropertyName("configId")]
    public string ConfigId { get; set; } = string.Empty;
    
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;
    
    [JsonPropertyName("inputSettings")]
    public Dictionary<string, object>? InputSettings { get; set; }
    
    [JsonPropertyName("transform")]
    public Dictionary<string, object>? Transform { get; set; }
}

// ============================================================================
// TIMING AND RECURRENCE TYPES
// ============================================================================

/// <summary>
/// Defines when the program should run, either once or recurrently.
/// </summary>
public class Timing
{
    [JsonPropertyName("start")]
    public DateTime Start { get; set; }
    
    [JsonPropertyName("end")]
    public DateTime End { get; set; }
    
    [JsonPropertyName("isRecurring")]
    public bool IsRecurring { get; set; }
    
    [JsonPropertyName("recurrence")]
    public Recurrence Recurrence { get; set; } = new();
}

/// <summary>
/// Defines the rule for repeating programs.
/// </summary>
public class Recurrence
{
    [JsonPropertyName("daysOfWeek")]
    public List<string> DaysOfWeek { get; set; } = new();
    
    [JsonPropertyName("startRecur")]
    public string StartRecur { get; set; } = string.Empty;
    
    [JsonPropertyName("endRecur")]
    public string EndRecur { get; set; } = string.Empty;
}

// ============================================================================
// BEHAVIOR TYPES
// ============================================================================

/// <summary>
/// Defines how the program should behave during and after execution.
/// </summary>
public class Behavior
{
    [JsonPropertyName("onEndAction")]
    public string OnEndAction { get; set; } = "hide"; // hide, none, stop
    
    [JsonPropertyName("preloadSeconds")]
    public int PreloadSeconds { get; set; } = 0;
}

// ============================================================================
// EXECUTABLE PROGRAM (DTO)
// ============================================================================

/// <summary>
/// DTO for execution - represents the currently active program state.
/// </summary>
public class ExecutableProgram
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? ConfigId { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public bool IsActive { get; set; }
    public TimeSpan? SeekOffset { get; set; }
}

// ============================================================================
// SCHEDULER STATE
// ============================================================================

/// <summary>
/// Represents the current state of the scheduler for UI display.
/// </summary>
public class SchedulerState
{
    public ExecutableProgram? CurrentProgram { get; set; }
    public ExecutableProgram? NextProgram { get; set; }
    public DateTime LastEvaluation { get; set; } = DateTime.UtcNow;
    public bool IsRunning { get; set; }
    public string? ActiveConfigId => CurrentProgram?.IsActive == true ? CurrentProgram?.ConfigId : null;
}
