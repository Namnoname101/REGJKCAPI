using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Services;
using BAOCODETOOL.TelegramBot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

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

try
{
    await builder.Build().RunAsync();
}
catch (OptionsValidationException exception)
{
    Console.Error.WriteLine($"[CONFIG ERROR] {string.Join(" ", exception.Failures)}");
    Environment.ExitCode = 2;
}
