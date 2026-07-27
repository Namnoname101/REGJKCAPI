using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Data;
using BAOCODETOOL.Services;
using BAOCODETOOL.TelegramBot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(webRoot) && Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")))
{
    webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = webRoot
});
builder.WebHost.UseUrls("http://localhost:5000");

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.BotToken),
        "Telegram bot token is missing. Set Telegram:BotToken in appsettings.json or Telegram__BotToken in the environment.")
    .Validate(options => options.AdminChatId != 0, "Telegram:AdminChatId must be configured.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AutomationOptions>()
    .Bind(builder.Configuration.GetSection(AutomationOptions.SectionName))
    .Validate(options => options.MaxConcurrentJobs > 0, "Automation:MaxConcurrentJobs must be greater than zero.")
    .Validate(options => options.MaxBrowserThreadsPerJob > 0, "Automation:MaxBrowserThreadsPerJob must be greater than zero.")
    .Validate(options => options.MaxConcurrentSitesPerAccount > 0, "Automation:MaxConcurrentSitesPerAccount must be greater than zero.")
    .Validate(options => options.MaxConcurrentBrowserStarts > 0, "Automation:MaxConcurrentBrowserStarts must be greater than zero.")
    .Validate(options => options.QueueCapacity > 0, "Automation:QueueCapacity must be greater than zero.")
    .Validate(
        options => !options.EnablePrewarmedBrowserPool || options.PrewarmedBrowserPoolSize > 0,
        "Automation:PrewarmedBrowserPoolSize must be greater than zero when the browser pool is enabled.")
    .Validate(
        options => !options.EnablePrewarmedBrowserPool || options.BrowserPoolMaxUsesPerSession > 0,
        "Automation:BrowserPoolMaxUsesPerSession must be greater than zero when the browser pool is enabled.")
    .Validate(options => options.MaxActiveBrowserSessions > 0, "Automation:MaxActiveBrowserSessions must be greater than zero.")
    .Validate(
        options => options.MaxCpuPercentBeforeThrottling is >= 1 and <= 99,
        "Automation:MaxCpuPercentBeforeThrottling must be between 1 and 99.")
    .Validate(
        options => options.BrowserAdmissionStabilizationMs is >= 0 and <= 30000,
        "Automation:BrowserAdmissionStabilizationMs must be between 0 and 30000.")
    .Validate(
        options => options.CpuLowSamplesRequired is >= 1 and <= 20,
        "Automation:CpuLowSamplesRequired must be between 1 and 20.")
    .ValidateOnStart();

builder.Services.AddSingleton<ITelegramBotClient>(services =>
{
    var options = services.GetRequiredService<IOptions<TelegramOptions>>().Value;
    return new TelegramBotClient(options.BotToken);
});

builder.Services.AddDbContext<AppDbContext>();
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString;
});
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ProxyVnService>();

builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
builder.Services.AddSingleton<IJobQueueService, JobQueueService>();
builder.Services.AddSingleton<BrowserSessionPool>();
builder.Services.AddSingleton<IBrowserSessionPool>(services =>
    services.GetRequiredService<BrowserSessionPool>());
builder.Services.AddSingleton<IBrowserConcurrencyGovernor, AdaptiveBrowserConcurrencyGovernor>();
builder.Services.AddSingleton<IBrowserAutomationService, BrowserAutomationService>();
builder.Services.AddSingleton<IRegistrationOrchestrator, RegistrationOrchestrator>();
builder.Services.AddSingleton<IResultSenderService, ResultSenderService>();
builder.Services.AddSingleton<BotService>();

builder.Services.AddSingleton<IHostedService>(services =>
    services.GetRequiredService<BrowserSessionPool>());
builder.Services.AddHostedService<ApplicationLifetimeLoggingService>();
builder.Services.AddHostedService<AutomationStatusLoggingService>();
builder.Services.AddHostedService<TelegramBotService>();
builder.Services.AddHostedService<JobWorkerService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/stats", async (AppDbContext db, IJobQueueService jobQueue) =>
{
    var totalUsers = await db.Users.CountAsync();
    var balances = await db.Users.Select(u => u.Balance).ToListAsync();
    var totalBalance = balances.Sum();
    var queueStatus = jobQueue.GetSnapshot();
    var dbRunning = await db.Jobs.CountAsync(j => j.Status == "Running");
    var runningJobs = Math.Max(queueStatus.Processing, dbRunning);

    var today = DateTime.UtcNow.Date;
    var dbSuccess = await db.Jobs.CountAsync(j => j.Status == "Success" && j.StartTime >= today);
    var dbFailed = await db.Jobs.CountAsync(j => j.Status == "Failed" && j.StartTime >= today);
    var todaySuccess = Math.Max((int)queueStatus.Succeeded, dbSuccess);
    var todayFailed = Math.Max((int)queueStatus.Failed, dbFailed);

    return Results.Ok(new
    {
        totalUsers,
        totalBalance,
        runningJobs,
        todaySuccess,
        todayFailed
    });
});

app.MapGet("/api/jobs", async (AppDbContext db) =>
{
    var jobs = await db.Jobs
        .OrderByDescending(j => j.StartTime)
        .Take(10)
        .Select(j => new
        {
            j.Id,
            j.UserId,
            j.Provider,
            j.Status,
            StartTime = j.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
            EndTime = j.EndTime.HasValue ? j.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : null,
            j.Logs
        })
        .ToListAsync();

    return Results.Ok(jobs);
});

app.MapGet("/api/users", async (AppDbContext db) =>
{
    var users = await db.Users
        .OrderByDescending(u => u.CreatedAt)
        .Select(u => new
        {
            u.Id,
            u.ChatId,
            Username = string.IsNullOrEmpty(u.Username) ? "(Không có)" : u.Username,
            u.Balance,
            u.TotalSpent,
            CreatedAt = u.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            JobsCount = db.Jobs.Count(j => j.UserId == u.Id)
        })
        .ToListAsync();

    return Results.Ok(users);
});

app.MapGet("/api/proxypackages", async (AppDbContext db) =>
{
    var packages = await db.ProxyPackages
        .OrderBy(p => p.Id)
        .ToListAsync();
    return Results.Ok(packages);
});

app.MapPost("/api/proxypackages", async (AppDbContext db, BAOCODETOOL.Models.ProxyPackage pkg) =>
{
    pkg.CreatedAt = DateTime.UtcNow;
    pkg.UpdatedAt = DateTime.UtcNow;
    db.ProxyPackages.Add(pkg);
    await db.SaveChangesAsync();
    return Results.Ok(pkg);
});

app.MapPost("/api/proxypackages/update/{id}", async (AppDbContext db, int id, BAOCODETOOL.Models.ProxyPackage update) =>
{
    var pkg = await db.ProxyPackages.FindAsync(id);
    if (pkg == null) return Results.NotFound();

    pkg.Name = update.Name;
    pkg.Category = update.Category;
    pkg.ProxyType = update.ProxyType;
    pkg.PeriodDays = update.PeriodDays;
    pkg.OriginalPrice = update.OriginalPrice;
    pkg.SellingPrice = update.SellingPrice;
    pkg.IsActive = update.IsActive;
    pkg.Description = update.Description;
    pkg.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(pkg);
});

app.MapDelete("/api/proxypackages/{id}", async (AppDbContext db, int id) =>
{
    var pkg = await db.ProxyPackages.FindAsync(id);
    if (pkg == null) return Results.NotFound();
    db.ProxyPackages.Remove(pkg);
    await db.SaveChangesAsync();
    return Results.Ok();
});

try
{
    await app.RunAsync();
}
catch (OptionsValidationException exception)
{
    Console.Error.WriteLine($"[CONFIG ERROR] {string.Join(" ", exception.Failures)}");
    Environment.ExitCode = 2;
}
