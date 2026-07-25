using System.Diagnostics;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var site = args.Length > 0 && Enum.TryParse<TargetSite>(
    args[0],
    ignoreCase: true,
    out var parsedSite)
    ? parsedSite
    : TargetSite.RR88;

var count = args.Length > 1 && int.TryParse(args[1], out var parsedCount)
    ? Math.Clamp(parsedCount, 2, 4)
    : 2;
var maxActiveSessions = args.Length > 2
                        && int.TryParse(args[2], out var parsedMaxActiveSessions)
    ? Math.Clamp(parsedMaxActiveSessions, 1, count)
    : 1;

var accounts = Enumerable.Range(0, count)
    .Select(_ => CreateSyntheticAccount())
    .ToArray();

foreach (var account in accounts)
{
    foreach (var siteRun in account.SiteRuns)
    {
        siteRun.IsSelected = siteRun.Site == site;
    }
}

var automationOptions = Options.Create(new AutomationOptions
{
    MaxConcurrentJobs = count,
    MaxBrowserThreadsPerJob = 1,
    MaxConcurrentSitesPerAccount = 1,
    MaxConcurrentBrowserStarts = 1,
    EnablePrewarmedBrowserPool = true,
    PrewarmedBrowserPoolSize = count,
    BrowserPoolMaxUsesPerSession = 5,
    MaxActiveBrowserSessions = maxActiveSessions,
    MaxCpuPercentBeforeThrottling = 75,
    EnableAdaptiveCpuThrottle = true,
    Headless = false,
    AllowDirectConnectionForTesting = true
});

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
using var automation = new BrowserAutomationService(
    automationOptions,
    new BenchmarkProfileRepository(),
    pool,
    governor,
    loggerFactory.CreateLogger<BrowserAutomationService>());

var benchmarkStopwatch = Stopwatch.StartNew();
var statusHandler = new EventHandler<AutomationStatusChangedEventArgs>((_, eventArgs) =>
{
    if (eventArgs.Account is null
        || string.IsNullOrWhiteSpace(eventArgs.Message)
        || !accounts.Contains(eventArgs.Account))
    {
        return;
    }

    var target = eventArgs.SiteRun?.Site.ToString() ?? "ACCOUNT";
    Console.WriteLine(
        $"BURST +{benchmarkStopwatch.Elapsed.TotalSeconds,7:0.000}s " +
        $"[{eventArgs.Account.Username}/{target}] " +
        $"{eventArgs.Status?.ToString() ?? "-"} {eventArgs.Message}");
});

AutomationUi.StatusChanged += statusHandler;
await pool.StartAsync(CancellationToken.None);

try
{
    Console.WriteLine(
        $"BURST START Site={site}; Count={count}; SyntheticData=true; " +
        $"SharedPool={count}; MaxActiveSessions={maxActiveSessions}");

    await Task.WhenAll(accounts.Select(account =>
        automation.RegisterAsync(account, CancellationToken.None)));
}
finally
{
    benchmarkStopwatch.Stop();
    await pool.StopAsync(CancellationToken.None);
    AutomationUi.StatusChanged -= statusHandler;
}

foreach (var account in accounts)
{
    var selectedRun = account.SiteRuns.Single(siteRun => siteRun.Site == site);
    Console.WriteLine(
        $"BURST RESULT Username={account.Username}; Site={site}; " +
        $"Outcome={selectedRun.Status}; Message={selectedRun.LogMessage}");
}

Console.WriteLine(
    $"BURST END Site={site}; Count={count}; ElapsedMs={benchmarkStopwatch.ElapsedMilliseconds}; " +
    $"Successes={accounts.Count(account => account.SiteRuns.Single(run => run.Site == site).Status == AccountStatus.Success)}");

Environment.ExitCode = accounts.All(account =>
    account.SiteRuns.Single(run => run.Site == site).Status == AccountStatus.Success)
    ? 0
    : 1;

static AccountModel CreateSyntheticAccount()
{
    var username = $"c{Random.Shared.Next(1_000_000, 9_999_999)}";
    return new AccountModel
    {
        Username = username,
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
}

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
