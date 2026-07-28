using System.Net;
using System.Net.Sockets;
using System.Text;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenQA.Selenium;

if (args.Any(argument => argument.Equals("temporary-profile", StringComparison.OrdinalIgnoreCase)))
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "BAOCODETOOL",
        "BrowserProfiles");
    var before = Directory.Exists(temporaryRoot)
        ? Directory.GetDirectories(temporaryRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var account = new AccountModel();
    using (var driver = DriverFactory.CreateDriver(account, TargetSite.RR88, headless: true))
    {
        _ = driver.Url;
    }

    await Task.Delay(250);
    var leaked = Directory.Exists(temporaryRoot)
        ? Directory.GetDirectories(temporaryRoot)
            .Where(path => !before.Contains(path))
            .ToArray()
        : [];
    if (leaked.Length > 0)
    {
        throw new InvalidOperationException(
            $"Temporary browser profiles were not deleted: {string.Join(", ", leaked)}");
    }

    Console.WriteLine("PASS: account browser profile was temporary and deleted on dispose.");
    return;
}

await using var server = LocalHttpServer.Start();
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(options => options.SingleLine = true));

var options = Options.Create(new AutomationOptions
{
    EnablePrewarmedBrowserPool = true,
    PrewarmedBrowserPoolSize = 2,
    BrowserPoolMaxUsesPerSession = 6,
    MaxActiveBrowserSessions = 2,
    EnableAdaptiveCpuThrottle = true,
    MaxCpuPercentBeforeThrottling = 99,
    BrowserAdmissionStabilizationMs = 1000,
    CpuLowSamplesRequired = 2,
    Headless = true
});

using var pool = new BrowserSessionPool(
    options,
    loggerFactory.CreateLogger<BrowserSessionPool>());

await pool.StartAsync(CancellationToken.None);

try
{
    using (var lease = pool.Rent(TargetSite.RR88, CancellationToken.None))
    {
        var driver = lease.Driver;
        driver.Navigate().GoToUrl(server.Url);

        var javaScript = (IJavaScriptExecutor)driver;
        javaScript.ExecuteScript(
            """
            localStorage.setItem('pool-smoke', 'must-be-cleared');
            document.cookie = 'pool-smoke=must-be-cleared; path=/';
            window.open(arguments[0], '_blank');
            """,
            server.Url);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (driver.WindowHandles.Count < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (driver.WindowHandles.Count < 2)
        {
            throw new InvalidOperationException("Could not create the extra smoke-test tab.");
        }
    }

    using (var lease = pool.Rent(TargetSite.RR88, CancellationToken.None))
    {
        var driver = lease.Driver;
        if (driver.WindowHandles.Count != 1 || driver.Url != "about:blank")
        {
            throw new InvalidOperationException(
                $"Browser was not returned to a single blank tab. Handles={driver.WindowHandles.Count}; Url={driver.Url}");
        }

        driver.Navigate().GoToUrl(server.Url);
        var javaScript = (IJavaScriptExecutor)driver;
        var state = javaScript.ExecuteScript(
            """
            return {
                cookie: document.cookie,
                localStorageValue: localStorage.getItem('pool-smoke')
            };
            """) as Dictionary<string, object>;

        var cookie = state?.GetValueOrDefault("cookie")?.ToString() ?? string.Empty;
        var localStorageValue = state?.GetValueOrDefault("localStorageValue")?.ToString();

    if (cookie.Contains("pool-smoke", StringComparison.Ordinal)
            || localStorageValue is not null)
        {
            throw new InvalidOperationException(
                $"Browser state leaked between leases. Cookie={cookie}; LocalStorage={localStorageValue}");
        }
    }

    var activeLeases = 0;
    var peakLeases = 0;
    var concurrentWorkers = Enumerable.Range(0, 5)
        .Select(async _ =>
        {
            using var lease = pool.Rent(TargetSite.RR88, CancellationToken.None);
            var active = Interlocked.Increment(ref activeLeases);
            InterlockedMax(ref peakLeases, active);
            await Task.Delay(250);
            Interlocked.Decrement(ref activeLeases);
        })
        .ToArray();

    await Task.WhenAll(concurrentWorkers);
    if (peakLeases > options.Value.PrewarmedBrowserPoolSize)
    {
        throw new InvalidOperationException(
            $"Pool admitted more active leases than its capacity. Peak={peakLeases}; Capacity={options.Value.PrewarmedBrowserPoolSize}");
    }

    Console.WriteLine(
        $"PASS: pooled browser was reused with clean state and handled 5 concurrent jobs. PeakLeases={peakLeases}");

    for (var cycle = 0; cycle < 12; cycle++)
    {
        using var lease = pool.Rent(TargetSite.RR88, CancellationToken.None);
        lease.Driver.Navigate().GoToUrl(server.Url);
        ((IJavaScriptExecutor)lease.Driver).ExecuteScript(
            """
            localStorage.setItem('pool-long-run', arguments[0]);
            document.cookie = 'pool-long-run=' + arguments[0] + '; path=/';
            """,
            $"cycle-{cycle}");
    }

    using (var postLoopLease = pool.Rent(TargetSite.RR88, CancellationToken.None))
    {
        if (postLoopLease.Driver.WindowHandles.Count != 1
            || postLoopLease.Driver.Url != "about:blank")
        {
            throw new InvalidOperationException(
                "Browser pool did not recover to a clean blank session after repeated leases.");
        }
    }

    Console.WriteLine(
        "PASS: repeated lease/reset/recycle cycles completed without leaked tabs or storage.");

    using var governor = new AdaptiveBrowserConcurrencyGovernor(
        options,
        loggerFactory.CreateLogger<AdaptiveBrowserConcurrencyGovernor>());
    using var governorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var firstGovernorLease = governor.Acquire(governorTimeout.Token);
    var admissionStopwatch = System.Diagnostics.Stopwatch.StartNew();
    var releaseSecond = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var secondAdmission = new TaskCompletionSource<long>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    var secondGovernorTask = Task.Run(() =>
    {
        using var secondLease = governor.Acquire(governorTimeout.Token);
        secondAdmission.TrySetResult(admissionStopwatch.ElapsedMilliseconds);
        releaseSecond.Task.Wait(governorTimeout.Token);
    }, governorTimeout.Token);

    await Task.Delay(500, governorTimeout.Token);
    if (secondAdmission.Task.IsCompleted)
    {
        throw new InvalidOperationException(
            "CPU governor admitted a second workflow before the stabilization window elapsed.");
    }

    await Task.Delay(700, governorTimeout.Token);
    firstGovernorLease.Dispose();
    var secondAdmissionMs = await secondAdmission.Task.WaitAsync(governorTimeout.Token);
    releaseSecond.TrySetResult();
    await secondGovernorTask;

    if (secondAdmissionMs < options.Value.BrowserAdmissionStabilizationMs)
    {
        throw new InvalidOperationException(
            $"CPU governor admitted too early. ElapsedMs={secondAdmissionMs}; " +
            $"RequiredMs={options.Value.BrowserAdmissionStabilizationMs}");
    }

    Console.WriteLine(
        $"PASS: CPU governor enforced stable admission. SecondAdmissionMs={secondAdmissionMs}");

    var heldLease = pool.Rent(TargetSite.RR88, CancellationToken.None);
    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var stopTask = pool.StopAsync(stopTimeout.Token);
    await Task.Delay(150);
    heldLease.Dispose();
    await stopTask;

    Console.WriteLine(
        "PASS: pool shutdown drained an active lease before disposing Chrome.");
}
finally
{
    await pool.StopAsync(CancellationToken.None);
}

static void InterlockedMax(ref int location, int value)
{
    while (true)
    {
        var current = Volatile.Read(ref location);
        if (value <= current || Interlocked.CompareExchange(ref location, value, current) == current)
        {
            return;
        }
    }
}

internal sealed class LocalHttpServer : IAsyncDisposable
{
    private static readonly byte[] ResponseBytes = CreateResponse();

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    private LocalHttpServer(TcpListener listener)
    {
        _listener = listener;
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Url = $"http://127.0.0.1:{endpoint.Port}/";
        _acceptLoop = AcceptLoopAsync();
    }

    public string Url { get; }

    public static LocalHttpServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalHttpServer(listener);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            _ = Task.Run(() => RespondAsync(client));
        }
    }

    private static async Task RespondAsync(TcpClient client)
    {
        using var disposableClient = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        await stream.WriteAsync(ResponseBytes);
        await stream.FlushAsync();
    }

    private static byte[] CreateResponse()
    {
        const string body = "<!doctype html><html><body><h1>Browser pool smoke</h1></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headerBytes = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n");

        return [.. headerBytes, .. bodyBytes];
    }
}
