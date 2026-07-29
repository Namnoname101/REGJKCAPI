using System.Diagnostics;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

var site = args.Length > 0 && Enum.TryParse<TargetSite>(args[0], ignoreCase: true, out var parsedSite)
    ? parsedSite
    : TargetSite.RR88;

var count = args.Length > 1 && int.TryParse(args[1], out var parsedCount)
    ? Math.Max(1, parsedCount)
    : 1;
var concurrency = args.Length > 2 && int.TryParse(args[2], out var parsedConcurrency)
    ? Math.Max(1, parsedConcurrency)
    : count;
var runBankSetup = args.Length > 3
                   && args[3].Equals("full", StringComparison.OrdinalIgnoreCase);
var runBotManagerPath = args.Any(arg => arg.Equals("botmanager", StringComparison.OrdinalIgnoreCase));
var bankName = args
                   .FirstOrDefault(arg => arg.StartsWith("bank=", StringComparison.OrdinalIgnoreCase))
                   ?["bank=".Length..]
               ?? "VIETCOMBANK";
var captchaSolverOptions = CreateCaptchaSolverOptions();

if (runBotManagerPath)
{
    await RunBotManagerBenchmarkAsync(
        site,
        count,
        concurrency,
        bankName,
        captchaSolverOptions);
    return;
}

if (count == 1)
{
    var singleAccount = CreateAccount(1, bankName);
    var singleStopwatch = Stopwatch.StartNew();
    var singleOutcome = await DirectRegistrationWorkflow.TryRegisterAsync(
        singleAccount,
        site,
        CancellationToken.None,
        captchaSolverOptions);
    singleStopwatch.Stop();

    Console.WriteLine($"site={site}");
    Console.WriteLine($"username={singleAccount.GetUsername(site)}");
    Console.WriteLine($"password={singleAccount.GetPassword(site)}");
    Console.WriteLine($"elapsedMs={singleStopwatch.ElapsedMilliseconds}");
    Console.WriteLine($"captchaProvider={singleOutcome.VerificationProvider ?? string.Empty}");
    Console.WriteLine($"captchaElapsedMs={singleOutcome.VerificationMs}");
    Console.WriteLine($"status={singleOutcome.Status}");
    Console.WriteLine($"message={singleOutcome.Message}");
    Console.WriteLine($"usernameConflict={singleOutcome.UsernameConflict}");
    Console.WriteLine($"response={SummarizeResponse(singleOutcome.RawResponse)}");
    if (runBankSetup && singleOutcome.Status == DirectRegistrationStatus.Success)
    {
        var bankStopwatch = Stopwatch.StartNew();
        var bankOutcome = singleOutcome.Session is null
            ? new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                "Registration did not return a session token.")
            : await DirectBankSetupWorkflow.TrySetupAsync(
                singleAccount,
                site,
                singleOutcome.Session,
                CancellationToken.None);
        bankStopwatch.Stop();

        Console.WriteLine($"bankElapsedMs={bankStopwatch.ElapsedMilliseconds}");
        Console.WriteLine($"bankStatus={bankOutcome.Status}");
        Console.WriteLine($"bankMessage={bankOutcome.Message}");
        Console.WriteLine($"bankResponse={SummarizeResponse(bankOutcome.RawResponse)}");
    }

    return;
}

using var gate = new SemaphoreSlim(concurrency);
var totalStopwatch = Stopwatch.StartNew();
var tasks = Enumerable.Range(1, count)
    .Select(async index =>
    {
        await gate.WaitAsync();
        try
        {
            var account = CreateAccount(index, bankName);
            var stopwatch = Stopwatch.StartNew();
            var outcome = await DirectRegistrationWorkflow.TryRegisterAsync(
                account,
                site,
                CancellationToken.None,
                captchaSolverOptions);
            DirectBankSetupOutcome? bankOutcome = null;
            if (runBankSetup && outcome.Status == DirectRegistrationStatus.Success)
            {
                bankOutcome = outcome.Session is null
                    ? new DirectBankSetupOutcome(
                        DirectBankSetupStatus.Failed,
                        "Registration did not return a session token.")
                    : await DirectBankSetupWorkflow.TrySetupAsync(
                        account,
                        site,
                        outcome.Session,
                        CancellationToken.None);
            }

            stopwatch.Stop();

            return new BurstResult(
                Index: index,
                Username: account.GetUsername(site),
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                Outcome: outcome,
                BankOutcome: bankOutcome);
        }
        finally
        {
            gate.Release();
        }
    })
    .ToArray();

var results = await Task.WhenAll(tasks);
totalStopwatch.Stop();

Console.WriteLine($"site={site}");
Console.WriteLine($"count={count}");
Console.WriteLine($"concurrency={concurrency}");
Console.WriteLine($"totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
Console.WriteLine($"success={results.Count(result => result.Outcome.Status == DirectRegistrationStatus.Success)}");
Console.WriteLine($"failed={results.Count(result => result.Outcome.Status == DirectRegistrationStatus.Failed)}");
Console.WriteLine($"fallback={results.Count(result => result.Outcome.Status == DirectRegistrationStatus.FallbackToBrowser)}");
if (runBankSetup)
{
    Console.WriteLine($"bankSuccess={results.Count(result => result.BankOutcome?.Status == DirectBankSetupStatus.Success)}");
    Console.WriteLine($"bankFailed={results.Count(result => result.BankOutcome?.Status == DirectBankSetupStatus.Failed)}");
}
Console.WriteLine($"avgElapsedMs={(long)results.Average(result => result.ElapsedMs)}");
Console.WriteLine($"maxElapsedMs={results.Max(result => result.ElapsedMs)}");
Console.WriteLine($"avgCaptchaElapsedMs={(long)results.Average(result => result.Outcome.VerificationMs)}");

foreach (var result in results.OrderBy(result => result.Index))
{
    Console.WriteLine(
        $"#{result.Index} username={result.Username} elapsedMs={result.ElapsedMs} captchaProvider={result.Outcome.VerificationProvider} captchaElapsedMs={result.Outcome.VerificationMs} status={result.Outcome.Status} message={result.Outcome.Message}" +
        (result.BankOutcome is null
            ? string.Empty
            : $" bankStatus={result.BankOutcome.Status} bankMessage={result.BankOutcome.Message}"));
    if (result.Outcome.Status != DirectRegistrationStatus.Success)
    {
        Console.WriteLine($"#{result.Index} response={SummarizeResponse(result.Outcome.RawResponse)}");
    }
    else if (result.BankOutcome?.Status != DirectBankSetupStatus.Success)
    {
        Console.WriteLine($"#{result.Index} bankResponse={SummarizeResponse(result.BankOutcome?.RawResponse)}");
    }
}

static AccountModel CreateAccount(int index, string bankName)
{
    return new AccountModel
{
    Username = $"d{Random.Shared.Next(1_000_000, 9_999_999)}",
    Password = $"Dt{Random.Shared.Next(100000, 999999)}x",
    PhoneNumber = $"052{Random.Shared.Next(1_000_000, 9_999_999):D7}",
    FullName = "NGUYEN VAN TEST",
    Level2Password = Random.Shared.Next(100000, 999999).ToString(),
    AccountNumber = $"000{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}",
    BankName = bankName,
    Branch = "HA NOI",
    Proxy = Environment.GetEnvironmentVariable("VIBECODE_PROXY") ?? string.Empty,
    UserAgent = string.Empty
};
}

static async Task RunBotManagerBenchmarkAsync(
    TargetSite site,
    int count,
    int concurrency,
    string bankName,
    CaptchaSolverOptions captchaSolverOptions)
{
    var accounts = Enumerable
        .Range(1, count)
        .Select(index => CreateAccount(index, bankName))
        .ToList();

    foreach (var account in accounts)
    {
        SelectOnlySite(account, site);
    }

    var usernames = accounts
        .Select(account => account.GetUsername(site))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var successPath = Path.Combine(AppContext.BaseDirectory, "success.txt");
    var completedCallbacks = 0;

    var stopwatch = Stopwatch.StartNew();
    var manager = new BotManager();
    await manager.RunMassRegistrationAsync(
        accounts,
        maxThreads: concurrency,
        onSiteCompleted: () => Interlocked.Increment(ref completedCallbacks),
        enableDirectHttpRegistration: true,
        maxConcurrentSitesPerAccount: 1,
        captchaSolverOptions: captchaSolverOptions);
    stopwatch.Stop();

    var matchingSuccessLines = File.Exists(successPath)
        ? File.ReadLines(successPath).Count(line => IsSuccessLineForRun(line, site, usernames))
        : 0;

    var selectedRuns = accounts
        .Select(account => new
        {
            Account = account,
            SiteRun = account.SiteRuns.Single(siteRun => siteRun.Site == site)
        })
        .ToList();

    Console.WriteLine($"mode=botmanager");
    Console.WriteLine($"site={site}");
    Console.WriteLine($"count={count}");
    Console.WriteLine($"concurrency={concurrency}");
    Console.WriteLine($"totalElapsedMs={stopwatch.ElapsedMilliseconds}");
    Console.WriteLine($"siteSuccess={selectedRuns.Count(item => item.SiteRun.Status == AccountStatus.Success)}");
    Console.WriteLine($"siteFailed={selectedRuns.Count(item => item.SiteRun.Status == AccountStatus.Failed)}");
    Console.WriteLine($"accountSuccess={accounts.Count(account => account.Status == AccountStatus.Success)}");
    Console.WriteLine($"successFilePath={successPath}");
    Console.WriteLine($"successFileMatches={matchingSuccessLines}");
    Console.WriteLine($"siteCompletedCallbacks={completedCallbacks}");

    foreach (var item in selectedRuns)
    {
        Console.WriteLine(
            $"username={item.Account.GetUsername(site)} siteStatus={item.SiteRun.Status} accountStatus={item.Account.Status} message={item.SiteRun.LogMessage}");
    }
}

static CaptchaSolverOptions CreateCaptchaSolverOptions()
{
    var apiKey = Environment.GetEnvironmentVariable("CaptchaSolver__ApiKey")
                 ?? Environment.GetEnvironmentVariable("TWOCAPTCHA_API_KEY")
                 ?? string.Empty;
    return new CaptchaSolverOptions
    {
        Enabled = !string.IsNullOrWhiteSpace(apiKey),
        ApiKey = apiKey,
        UseRegistrationProxy = true
    };
}

static void SelectOnlySite(AccountModel account, TargetSite selectedSite)
{
    foreach (var siteRun in account.SiteRuns)
    {
        siteRun.IsSelected = siteRun.Site == selectedSite;
    }
}

static bool IsSuccessLineForRun(string line, TargetSite site, ISet<string> usernames)
{
    var parts = line.Split('|');
    return parts.Length >= 3
           && parts[0].Equals(site.ToString(), StringComparison.OrdinalIgnoreCase)
           && usernames.Contains(parts[1]);
}

static string SummarizeResponse(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    try
    {
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        var summary = new Dictionary<string, object?>();
        foreach (var name in new[] { "success", "code", "errorCode", "message", "msg" })
        {
            if (root.TryGetProperty(name, out var element))
            {
                summary[name] = element.Clone();
            }
        }

        return JsonSerializer.Serialize(summary);
    }
    catch (JsonException)
    {
        return $"non-json length={value.Length}";
    }
}

internal sealed record BurstResult(
    int Index,
    string Username,
    long ElapsedMs,
    DirectRegistrationOutcome Outcome,
    DirectBankSetupOutcome? BankOutcome);
