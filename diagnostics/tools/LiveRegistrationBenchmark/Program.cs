using System.Diagnostics;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var site = args.Length > 0 && Enum.TryParse<TargetSite>(args[0], ignoreCase: true, out var parsedSite)
    ? parsedSite
    : TargetSite.RR88;

var account = new AccountModel
{
    Username = $"c{Random.Shared.Next(1_000_000, 9_999_999)}",
    Password = $"Ct{Random.Shared.Next(100000, 999999)}x",
    PhoneNumber = $"09{Random.Shared.NextInt64(10_000_000, 99_999_999)}",
    FullName = "NGUYEN VAN TEST",
    Level2Password = Random.Shared.Next(100000, 999999).ToString(),
    AccountNumber = $"000{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}",
    BankName = "VIETCOMBANK",
    Branch = "HA NOI",
    Proxy = string.Empty,
    UserAgent = string.Empty
};

foreach (var siteRun in account.SiteRuns)
{
    siteRun.IsSelected = siteRun.Site == site;
}

var automationOptions = Options.Create(new AutomationOptions
{
    MaxConcurrentJobs = 1,
    MaxBrowserThreadsPerJob = 1,
    MaxConcurrentSitesPerAccount = 1,
    MaxConcurrentBrowserStarts = 1,
    // The benchmark measures the direct HTTP path. Do not open a visible
    // fallback browser just by starting the diagnostic tool.
    EnablePrewarmedBrowserPool = false,
    PrewarmedBrowserPoolSize = 0,
    BrowserPoolMaxUsesPerSession = 5,
    MaxActiveBrowserSessions = 1,
    MaxCpuPercentBeforeThrottling = 90,
    EnableAdaptiveCpuThrottle = false,
    Headless = true,
    EnableBrowserFallback = false,
    AllowDirectConnectionForTesting = true
});
var captchaSolverOptions = Options.Create(new CaptchaSolverOptions());

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    });
});

using var pool = new BrowserSessionPool(
    automationOptions,
    loggerFactory.CreateLogger<BrowserSessionPool>());
using var governor = new AdaptiveBrowserConcurrencyGovernor(
    automationOptions,
    loggerFactory.CreateLogger<AdaptiveBrowserConcurrencyGovernor>());
using var geetestPool = new BrowserGeetestTokenPool(
    captchaSolverOptions,
    loggerFactory.CreateLogger<BrowserGeetestTokenPool>());
var repository = new BenchmarkProfileRepository();
using var automation = new BrowserAutomationService(
    automationOptions,
    captchaSolverOptions,
    repository,
    pool,
    governor,
    geetestPool,
    geetestPool,
    loggerFactory.CreateLogger<BrowserAutomationService>());

var benchmarkStopwatch = Stopwatch.StartNew();
var statusHandler = new EventHandler<AutomationStatusChangedEventArgs>((_, eventArgs) =>
{
    if (!ReferenceEquals(eventArgs.Account, account) || string.IsNullOrWhiteSpace(eventArgs.Message))
    {
        return;
    }

    var target = eventArgs.SiteRun?.Site.ToString() ?? "ACCOUNT";
    Console.WriteLine(
        $"BENCHMARK +{benchmarkStopwatch.Elapsed.TotalSeconds,7:0.000}s [{target}] " +
        $"{eventArgs.Status?.ToString() ?? "-"} {eventArgs.Message}");
});

AutomationUi.StatusChanged += statusHandler;
await pool.StartAsync(CancellationToken.None);
await geetestPool.StartAsync(CancellationToken.None);

try
{
    Console.WriteLine(
        $"BENCHMARK START Site={site}; ProfileId={account.ProfileId}; " +
        $"Username={account.Username}; SyntheticData=true");

    await automation.RegisterAsync(account, CancellationToken.None);
}
finally
{
    benchmarkStopwatch.Stop();
    await geetestPool.StopAsync(CancellationToken.None);
    await pool.StopAsync(CancellationToken.None);
    AutomationUi.StatusChanged -= statusHandler;
}

var selectedRun = account.SiteRuns.Single(siteRun => siteRun.Site == site);
Console.WriteLine(
    $"BENCHMARK END Site={site}; Outcome={selectedRun.Status}; " +
    $"ElapsedMs={benchmarkStopwatch.ElapsedMilliseconds}; Message={selectedRun.LogMessage}");

Environment.ExitCode = selectedRun.Status == AccountStatus.Success ? 0 : 1;

internal sealed class BenchmarkProfileRepository : IProfileRepository
{
    public IReadOnlyList<AccountModel> GetAll() => [];

    public void Add(AccountModel account)
    {
    }

    public void AddRange(IEnumerable<AccountModel> accounts)
    {
    }

    public void Save()
    {
    }
}
