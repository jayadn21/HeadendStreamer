namespace HeadendStreamer.Web.Models.Options;

public class ExternalServiceConfig
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public string ServerURL { get; set; } = string.Empty;
}

public class ExternalServiceOptions
{
    public ExternalServiceConfig OBS_Scheduler { get; set; } = new();
    public ExternalServiceConfig SPX_Graphics { get; set; } = new();
}
