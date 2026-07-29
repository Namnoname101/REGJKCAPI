using System.Net;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Data;
using BAOCODETOOL.Services;
using BAOCODETOOL.TelegramBot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

using var singleInstance = SingleInstanceGuard.TryAcquire();
if (singleInstance is null)
{
    // A second long-polling client would make Telegram return a 409 conflict
    // and can also contend for SQLite. Bring the already-running dashboard
    // forward instead of starting another host.
    LocalDashboardLauncherService.OpenDashboard("http://127.0.0.1:5000");
    return;
}

var hasLocalAppSettings = File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
RuntimePaths.Initialize(createSettingsFile: !hasLocalAppSettings);
Directory.SetCurrentDirectory(RuntimePaths.DataDirectory);

var webRoot = RuntimePaths.WebRootPath;
if (!File.Exists(Path.Combine(webRoot, "index.html")))
{
    throw new InvalidOperationException($"The local dashboard asset was not extracted to '{webRoot}'.");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = webRoot
});

// A settings file under LocalAppData survives EXE replacement and is not
// embedded in the release binary. Development runs keep using the local
// appsettings.json file when one is present. Add environment variables last
// so a secure machine/user setting can still override either source.
if (!hasLocalAppSettings)
{
    builder.Configuration.AddJsonFile(RuntimePaths.SettingsPath, optional: true, reloadOnChange: true);
}

builder.Configuration.AddEnvironmentVariables();

var localListenUrl = builder.Configuration["Hosting:LocalUrl"] ?? "http://localhost:5000";
var webhookListenUrl = builder.Configuration["Hosting:WebhookUrl"] ?? "http://localhost:5001";
var openBrowser = !args.Any(argument => string.Equals(argument, "--no-browser", StringComparison.OrdinalIgnoreCase))
                  && builder.Configuration.GetValue("Hosting:OpenBrowser", true);
builder.WebHost.UseUrls(localListenUrl, webhookListenUrl);

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
        "Telegram bot token is missing. Set Telegram:BotToken in the local settings file or Telegram__BotToken in the environment.")
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

builder.Services
    .AddOptions<CaptchaSolverOptions>()
    .Bind(builder.Configuration.GetSection(CaptchaSolverOptions.SectionName))
    .Validate(
        options => !options.Enabled
                   || string.Equals(
                       options.Provider,
                       CaptchaSolverOptions.TwoCaptchaProvider,
                       StringComparison.OrdinalIgnoreCase),
        "CaptchaSolver:Provider must be TwoCaptcha when the external solver is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
        "CaptchaSolver:ApiKey is required when the external solver is enabled.")
    .Validate(
        options => options.TimeoutSeconds is >= 15 and <= 300,
        "CaptchaSolver:TimeoutSeconds must be between 15 and 300.")
    .Validate(
        options => options.InitialPollingDelayMilliseconds is >= 1000 and <= 10000,
        "CaptchaSolver:InitialPollingDelayMilliseconds must be between 1000 and 10000.")
    .Validate(
        options => options.PollingIntervalMilliseconds is >= 1000 and <= 10000,
        "CaptchaSolver:PollingIntervalMilliseconds must be between 1000 and 10000.")
    .ValidateOnStart();

builder.Services
    .AddOptions<SePayOptions>()
    .Bind(builder.Configuration.GetSection(SePayOptions.SectionName))
    .Validate(options => options.AllowedClockSkewSeconds is >= 30 and <= 900,
        "SePay:AllowedClockSkewSeconds must be between 30 and 900 seconds.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.BankName),
        "SePay:BankName is required when SePay is enabled.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.AccountNumber),
        "SePay:AccountNumber is required when SePay is enabled.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.AccountHolder),
        "SePay:AccountHolder is required when SePay is enabled.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.TransferPrefix),
        "SePay:TransferPrefix is required when SePay is enabled.")
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
builder.Services.AddScoped<SePayWebhookService>();

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
builder.Services.AddSingleton(new LocalDashboardLauncherOptions(localListenUrl, openBrowser));
builder.Services.AddHostedService<LocalDashboardLauncherService>();
builder.Services.AddHostedService<SystemTrayIconService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

var webhookListenPort = new Uri(webhookListenUrl).Port;
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == webhookListenPort
        && !context.Request.Path.Equals("/api/webhooks/sepay", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next(context);
});

app.MapPost("/api/webhooks/sepay", async (
    HttpRequest request,
    SePayWebhookService sePay,
    ITelegramBotClient bot,
    IOptions<TelegramOptions> telegramOptions,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    const int maximumPayloadBytes = 64 * 1024;
    if (request.ContentLength > maximumPayloadBytes)
        return Results.Json(new { success = false, message = "Payload is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);

    string rawBody;
    using (var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
    {
        rawBody = await reader.ReadToEndAsync(cancellationToken);
    }

    if (Encoding.UTF8.GetByteCount(rawBody) > maximumPayloadBytes)
        return Results.Json(new { success = false, message = "Payload is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);

    if (!sePay.IsConfigured)
        return Results.Json(new { success = false, message = "SePay webhook is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    if (!sePay.VerifySignature(
            rawBody,
            request.Headers["X-SePay-Signature"].FirstOrDefault(),
            request.Headers["X-SePay-Timestamp"].FirstOrDefault(),
            out var signatureError))
    {
        return Results.Json(new { success = false, message = signatureError }, statusCode: StatusCodes.Status401Unauthorized);
    }

    SePayWebhookPayload? payload;
    try
    {
        payload = JsonSerializer.Deserialize<SePayWebhookPayload>(rawBody);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { success = false, message = "Invalid JSON payload." });
    }

    if (payload is null)
        return Results.BadRequest(new { success = false, message = "Webhook payload is required." });

    SePayProcessingResult result;
    try
    {
        result = await sePay.ProcessAsync(payload, cancellationToken);
    }
    catch (Exception exception)
    {
        loggerFactory.CreateLogger("SePayWebhook")
            .LogError(exception, "Failed to process SePay transaction {TransactionId}.", payload.Id);
        return Results.Json(new { success = false, message = "Webhook processing failed." }, statusCode: StatusCodes.Status500InternalServerError);
    }

    if (result.Status == "Credited" && result.ChatId.HasValue)
    {
        try
        {
            await bot.SendMessage(
                result.ChatId.Value,
                $"✅ <b>NẠP TIỀN THÀNH CÔNG</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                $"💵 Đã cộng: <b>{result.Amount:N0} Xu</b>\n" +
                $"💰 Số dư mới: <b>{result.NewBalance:N0} Xu</b>\n" +
                $"🔖 Mã giao dịch: <code>SEPAY:{result.TransactionId}</code>",
                ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("SePayWebhook")
                .LogWarning(exception, "Could not notify ChatId {ChatId} about SePay deposit.", result.ChatId);
        }
    }
    else if (result.Status == "Ignored")
    {
        try
        {
            await bot.SendMessage(
                telegramOptions.Value.AdminChatId,
                $"⚠️ <b>SEPAY CHƯA CỘNG TIỀN</b>\n" +
                $"Mã giao dịch: <code>{result.TransactionId}</code>\n" +
                $"Lý do: {WebUtility.HtmlEncode(result.Message)}",
                ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("SePayWebhook")
                .LogWarning(exception, "Could not notify admin about unmatched SePay transaction {TransactionId}.", result.TransactionId);
        }
    }

    return Results.Ok(new { success = true });
});

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

app.MapPost("/api/users/balance/{id}", async (AppDbContext db, IUserService userService, ITelegramBotClient bot, int id, UserBalanceUpdateRequest req) =>
{
    var user = await db.Users.FindAsync(id);
    if (user == null) return Results.NotFound();

    decimal oldBalance = user.Balance;
    bool success = false;
    string actionText = "";

    if (req.Mode == "set")
    {
        success = await userService.SetBalanceAsync(user.ChatId, req.Amount, req.Reason ?? "Admin điều chỉnh từ Web Local", "ADMIN_WEB");
        actionText = $"được đặt thành <b>{req.Amount:N0} Xu</b>";
    }
    else if (req.Mode == "add")
    {
        success = await userService.AddBalanceAsync(user.ChatId, req.Amount, req.Reason ?? "Admin cộng thêm từ Web Local", "ADMIN_WEB");
        actionText = $"được cộng thêm <b>+{req.Amount:N0} Xu</b> (Số dư mới: <b>{(oldBalance + req.Amount):N0} Xu</b>)";
    }
    else if (req.Mode == "deduct")
    {
        success = await userService.DeductBalanceAsync(user.ChatId, req.Amount, req.Reason ?? "Admin trừ từ Web Local", "ADMIN_WEB");
        actionText = $"bị trừ <b>-{req.Amount:N0} Xu</b> (Số dư mới: <b>{(oldBalance - req.Amount):N0} Xu</b>)";
    }

    if (!success) return Results.BadRequest(new { error = "Không thể thực hiện điều chỉnh số dư." });

    if (req.NotifyUser)
    {
        try
        {
            var msgText = $"💵 <b>THÔNG BÁO TỪ ADMIN:</b>\n" +
                          $"━━━━━━━━━━━━━━━━━━━━\n" +
                          $"Số dư tài khoản của bạn vừa {actionText}.\n" +
                          $"📝 Ghi chú: <b>{WebUtility.HtmlEncode(req.Reason ?? "Điều chỉnh từ BQT")}</b>";
            await bot.SendMessage(user.ChatId, msgText, ParseMode.Html);
        }
        catch { }
    }

    return Results.Ok(new { success = true, newBalance = user.Balance });
});

app.MapPost("/api/notifications/broadcast", async (AppDbContext db, ITelegramBotClient bot, BroadcastRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "Nội dung thông báo không được để trống." });

    var chatIds = await db.Users.Select(u => u.ChatId).Distinct().ToListAsync();
    int successCount = 0;
    int failCount = 0;

    string formattedMsg = $"<b>Thông báo từ admin:</b>\n\n" +
                          $"{WebUtility.HtmlEncode(req.Message.Trim())}";

    foreach (var chatId in chatIds)
    {
        try
        {
            await bot.SendMessage(chatId, formattedMsg, ParseMode.Html);
            successCount++;
        }
        catch
        {
            failCount++;
        }
        await Task.Delay(50);
    }

    return Results.Ok(new { successCount, failCount, total = chatIds.Count });
});

try
{
    await app.RunAsync();
}
catch (OptionsValidationException exception)
{
    Console.Error.WriteLine($"[CONFIG ERROR] {string.Join(" ", exception.Failures)}");
    Console.Error.WriteLine($"Update the local settings file: {RuntimePaths.SettingsPath}");

    if (OperatingSystem.IsWindows() && Environment.UserInteractive)
    {
        try
        {
            var answer = System.Windows.Forms.MessageBox.Show(
                "Chưa cấu hình Telegram nên ứng dụng chưa thể khởi động.\n\n" +
                $"Hãy điền BotToken và AdminChatId trong:\n{RuntimePaths.SettingsPath}\n\n" +
                "Mở file cấu hình ngay bây giờ?",
                "VibeCodeReg cần cấu hình",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Warning);

            if (answer == System.Windows.Forms.DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RuntimePaths.SettingsPath)
                {
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // The console error above remains available if the interactive
            // desktop cannot display a message box.
        }
    }

    Environment.ExitCode = 2;
}

public record UserBalanceUpdateRequest(string Mode, decimal Amount, string? Reason, bool NotifyUser);
public record BroadcastRequest(string Message);
