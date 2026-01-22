using HeadendStreamer.Web.Hubs;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
// Configure Serilog
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Configure Data Protection for Windows
var keysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HeadendStreamer", "keys");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("HeadendStreamer")
    .SetDefaultKeyLifetime(TimeSpan.FromDays(90));

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR()
    .AddJsonProtocol(options => {
        options.PayloadSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

// Register services in CORRECT ORDER
builder.Services.AddSingleton<SystemMonitorService>();
builder.Services.AddSingleton<StreamManagerService>();
builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddHostedService<BackgroundMonitorService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Only use HTTPS redirection if HTTPS is configured
if (builder.Configuration.GetValue<bool>("EnableHttpsRedirection", false))
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<StreamHub>("/streamHub");

// Ensure directories exist
Directory.CreateDirectory("logs");
Directory.CreateDirectory("logs/ffmpeg");
Directory.CreateDirectory(keysPath);

app.Run();