using HeadendStreamer.Web.Hubs;
using HeadendStreamer.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using Microsoft.AspNetCore.Authentication.Cookies;

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
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddHostedService<BackgroundMonitorService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
    });

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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<StreamHub>("/streamHub");

Directory.CreateDirectory("logs");
Directory.CreateDirectory("logs/ffmpeg");
Directory.CreateDirectory(keysPath);

// Initialize Database
using (var scope = app.Services.CreateScope())
{
    var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
    await userService.InitializeAsync();
}

app.Run();