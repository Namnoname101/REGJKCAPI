using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenQA.Selenium;

namespace BAOCODETOOL.Services;

/// <summary>
/// A demand-created, proxy-bound Chromium pool for GeeTest v4. Unlike the
/// general browser pool, it never prewarms or opens visible Chrome windows.
/// A proxy keeps the same browser profile for short retries and nearby jobs,
/// while an LRU eviction policy prevents a burst of distinct proxies from
/// accumulating Chromium processes.
/// </summary>
public sealed class BrowserGeetestTokenPool :
    IGeetestBrowserTokenService,
    IGgGeeGuardTokenService,
    IHostedService,
    IDisposable
{
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";

    private readonly CaptchaSolverOptions _options;
    private readonly ILogger<BrowserGeetestTokenPool> _logger;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _availability = new(0);
    private readonly Dictionary<string, BrowserSlot> _slots = new(StringComparer.Ordinal);
    private readonly string _poolRoot = Path.Combine(
        RuntimePaths.BrowserPoolDirectory,
        "GeeTest",
        Guid.NewGuid().ToString("N"));
    private int _stopping;
    private int _disposed;

    public BrowserGeetestTokenPool(
        IOptions<CaptchaSolverOptions> options,
        ILogger<BrowserGeetestTokenPool> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Token browser pool ready. GG88 uses Edge headless mobile; GeeTest browser fallback mode is {Mode}. MaxSessions={MaxSessions}.",
            _options.LocalGeeTestMode,
            _options.LocalBrowserMaxSessions);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_slots.Values.All(slot => slot.ActiveLeases == 0))
                {
                    break;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        List<BrowserSlot> slots;
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            slots = _slots.Values.ToList();
            _slots.Clear();
        }
        finally
        {
            _stateGate.Release();
        }

        foreach (var slot in slots)
        {
            slot.Dispose();
        }

        TryDeletePoolRoot();
        _logger.LogInformation("Token browser pool stopped.");
    }

    public async Task<BrowserGeetestTokenResult?> GetTokenAsync(
        BrowserGeetestTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CaptchaId)
            || string.IsNullOrWhiteSpace(request.RegistrationUrl))
        {
            return null;
        }

        await using var lease = await RentAsync(
                request.Proxy,
                BrowserEngine.Chrome,
                cancellationToken)
            .ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var attempt = await Task.Run(
                    () => GetTokenBlocking(lease.Slot, request, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            if (attempt.Token is null)
            {
                _logger.LogWarning(
                    "Local GeeTest browser did not return a token. ProxyKey={ProxyKey}, Site={Site}, Reason={Reason}",
                    lease.Slot.Key,
                    request.Site,
                    attempt.FailureReason ?? "unknown");
                return null;
            }

            lease.MarkSuccessfulUse();
            return new BrowserGeetestTokenResult(
                attempt.Token.Value,
                lease.ReusedBrowser,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebDriverException exception)
        {
            stopwatch.Stop();
            lease.Invalidate();
            _logger.LogWarning(
                exception,
                "Local GeeTest browser failed. ProxyKey={ProxyKey}, Site={Site}",
                lease.Slot.Key,
                request.Site);
            return null;
        }
        catch (JsonException exception)
        {
            stopwatch.Stop();
            lease.Invalidate();
            _logger.LogWarning(
                exception,
                "Local GeeTest browser returned invalid token JSON. ProxyKey={ProxyKey}, Site={Site}",
                lease.Slot.Key,
                request.Site);
            return null;
        }
    }

    public async Task<string?> GetGeeGuardTokenAsync(
        ProxyConfiguration? proxy,
        string registrationUrl,
        string appId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        await using var lease = await RentAsync(
                proxy,
                BrowserEngine.Edge,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var token = await Task.Run(
                    () => GetGeeGuardTokenBlocking(
                        lease.Slot,
                        registrationUrl,
                        appId,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                lease.Invalidate();
                return null;
            }

            lease.MarkSuccessfulUse();
            return token;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebDriverException exception)
        {
            lease.Invalidate();
            _logger.LogWarning(
                exception,
                "GG88 Edge token session failed. ProxyKey={ProxyKey}",
                lease.Slot.Key);
            return null;
        }
    }

    public BrowserGeetestPoolSnapshot GetSnapshot()
    {
        _stateGate.Wait();
        try
        {
            return new BrowserGeetestPoolSnapshot(
                _slots.Count,
                _slots.Values.Count(slot => slot.Driver is not null),
                _slots.Values.Sum(slot => slot.ActiveLeases),
                _slots.Values.Sum(slot => slot.SuccessfulUses));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _stateGate.Dispose();
        _availability.Dispose();
    }

    private async Task<BrowserLease> RentAsync(
        ProxyConfiguration? proxy,
        BrowserEngine engine,
        CancellationToken cancellationToken)
    {
        var key = CreateProxyKey(proxy, engine);
        while (true)
        {
            BrowserSlot? slot = null;
            List<BrowserSlot>? toDispose = null;

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfStopping();
                toDispose = RemoveExpiredSlotsLocked(DateTimeOffset.UtcNow);

                if (!_slots.TryGetValue(key, out slot))
                {
                    if (_slots.Count >= _options.LocalBrowserMaxSessions)
                    {
                        var eviction = _slots.Values
                            .Where(candidate => candidate.ActiveLeases == 0)
                            .OrderBy(candidate => candidate.LastReleasedAt)
                            .FirstOrDefault();
                        if (eviction is not null)
                        {
                            _slots.Remove(eviction.Key);
                            toDispose.Add(eviction);
                        }
                    }

                    if (_slots.Count < _options.LocalBrowserMaxSessions)
                    {
                        slot = new BrowserSlot(
                            key,
                            proxy,
                            Path.Combine(_poolRoot, key),
                            engine,
                            DateTimeOffset.UtcNow);
                        _slots.Add(key, slot);
                    }
                }

                if (slot is not null)
                {
                    slot.ActiveLeases++;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            DisposeSlots(toDispose);

            if (slot is null)
            {
                await _availability.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new BrowserLease(this, slot, slot.Driver is not null);
            }
            catch
            {
                await ReturnAsync(
                        slot,
                        gateHeld: false,
                        successfulUse: false,
                        invalidate: false)
                    .ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task ReturnAsync(
        BrowserSlot slot,
        bool gateHeld,
        bool successfulUse,
        bool invalidate)
    {
        if (gateHeld)
        {
            slot.Gate.Release();
        }

        BrowserSlot? toDispose = null;
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            slot.ActiveLeases = Math.Max(0, slot.ActiveLeases - 1);
            slot.LastReleasedAt = DateTimeOffset.UtcNow;
            if (successfulUse)
            {
                slot.SuccessfulUses++;
            }

            if (invalidate || slot.SuccessfulUses >= _options.LocalBrowserMaxUsesPerSession)
            {
                slot.RecycleRequested = true;
            }

            if (slot.ActiveLeases == 0 && slot.RecycleRequested)
            {
                _slots.Remove(slot.Key);
                toDispose = slot;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        toDispose?.Dispose();
        SignalAvailability();
    }

    private BrowserTokenAttempt GetTokenBlocking(
        BrowserSlot slot,
        BrowserGeetestTokenRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driver = slot.Driver ??= CreateDriver(slot, request.Site);
        if (!string.Equals(
                slot.LastSuccessfulRegistrationUrl,
                request.RegistrationUrl,
                StringComparison.Ordinal))
        {
            // Once a page has produced a usable token, keep its loaded SDK,
            // cookies, and risk state. Re-initialising GeeTest on that page
            // is much cheaper than loading the complete registration bundle
            // again for the same proxy.
            driver.Navigate().GoToUrl(request.RegistrationUrl);
        }

        var javaScript = (IJavaScriptExecutor)driver;
        if (!WaitForInitBotion(javaScript, TimeSpan.FromSeconds(12), cancellationToken))
        {
            var bootstrapResult = BootstrapGeeTestSdk(javaScript, request);
            if (!string.Equals(bootstrapResult, "ready", StringComparison.Ordinal)
                && !WaitForInitBotion(javaScript, TimeSpan.FromSeconds(4), cancellationToken))
            {
                return new BrowserTokenAttempt(
                    Token: null,
                    FailureReason: $"initBotion was unavailable after page and SDK bootstrap ({bootstrapResult ?? "no result"}).");
            }
        }

        var tokenJson = javaScript.ExecuteAsyncScript(
            """
            const captchaId = arguments[0];
            const done = arguments[arguments.length - 1];
            let completed = false;
            let poll = null;
            let timeout = null;
            const describe = error => {
                if (!error) return 'unknown';
                try {
                    const name = error.name ? String(error.name) : '';
                    const message = error.message ? String(error.message) : '';
                    const stack = error.stack ? String(error.stack).split('\n')[0] : '';
                    if (name || message || stack) {
                        return [name, message, stack]
                            .filter(Boolean)
                            .join(': ')
                            .slice(0, 300);
                    }
                    return typeof error === 'string'
                        ? error.slice(0, 300)
                        : JSON.stringify(error).slice(0, 300);
                } catch {
                    return String(error).slice(0, 300);
                }
            };
            const finish = value => {
                if (completed) return;
                completed = true;
                if (poll) clearInterval(poll);
                if (timeout) clearTimeout(timeout);
                done(value ? JSON.stringify(value) : null);
            };
            const fail = reason => finish({ __vibe_geetest_failure: reason });
            timeout = setTimeout(() => fail('token timeout after 15 seconds'), 15000);

            try {
                window.initBotion({
                    captchaId,
                    product: 'bind',
                    language: 'vie',
                hideSuccess: true,
                timeout: 5000,
                protocol: 'https://'
            }, captcha => {
                const describeCaptcha = () => {
                    try {
                        const own = Object.getOwnPropertyNames(captcha || {});
                        const prototype = captcha
                            ? Object.getOwnPropertyNames(Object.getPrototypeOf(captcha) || {})
                            : [];
                        return 'own=[' + own.slice(0, 30).join(',') +
                            ']; prototype=[' + prototype.slice(0, 30).join(',') + ']';
                    } catch {
                        return 'captcha shape unavailable';
                    }
                };
                const capture = () => {
                    try {
                        const value = captcha.getValidate?.();
                            if (value && typeof value === 'object') finish(value);
                        } catch {}
                };
                captcha.onSuccess?.(capture);
                captcha.onError?.(error => fail('captcha onError: ' + describe(error)));
                const verify = () => {
                    try {
                        capture();
                        if (completed) return;
                        if (typeof captcha.verify !== 'function') {
                            fail('captcha verify is unavailable; ' + describeCaptcha());
                            return;
                        }
                        captcha.verify();
                        poll = setInterval(capture, 50);
                    } catch (error) {
                            fail('captcha verify threw: ' + describe(error));
                        }
                    };
                    if (captcha.onReady) captcha.onReady(verify);
                    else verify();
                });
            } catch (error) {
                fail('initBotion threw: ' + describe(error));
            }
            """,
            request.CaptchaId)?.ToString();

        if (string.IsNullOrWhiteSpace(tokenJson))
        {
            return new BrowserTokenAttempt(
                Token: null,
                FailureReason: "GeeTest did not yield a value within 8 seconds.");
        }

        using var document = JsonDocument.Parse(tokenJson);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(
                "__vibe_geetest_failure",
                out var failure)
            && failure.ValueKind == JsonValueKind.String)
        {
            return new BrowserTokenAttempt(
                Token: null,
                FailureReason: failure.GetString() ?? "GeeTest reported an unknown failure.");
        }

        if (!IsUsableToken(document.RootElement))
        {
            return new BrowserTokenAttempt(
                Token: null,
                FailureReason: "GeeTest returned a value with required fields missing.");
        }

        slot.LastSuccessfulRegistrationUrl = request.RegistrationUrl;
        return new BrowserTokenAttempt(document.RootElement.Clone(), null);
    }

    private static string? GetGeeGuardTokenBlocking(
        BrowserSlot slot,
        string registrationUrl,
        string appId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driver = slot.Driver ??= CreateDriver(slot, TargetSite.GG88);
        if (!string.Equals(
                slot.LastSuccessfulRegistrationUrl,
                registrationUrl,
                StringComparison.Ordinal))
        {
            driver.Navigate().GoToUrl(registrationUrl);
        }

        var javaScript = (IJavaScriptExecutor)driver;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (javaScript.ExecuteScript(
                    "return typeof window.initGeeGuard === 'function';")
                is true)
            {
                var token = javaScript.ExecuteAsyncScript(
                        """
                        const appId = arguments[0];
                        const done = arguments[arguments.length - 1];
                        let completed = false;
                        const finish = value => {
                            if (completed) return;
                            completed = true;
                            clearTimeout(timer);
                            done(value || null);
                        };
                        const timer = setTimeout(() => finish(null), 4000);
                        try {
                            window.initGeeGuard({ appId }, result => {
                                finish(result?.data?.gee_token || null);
                            });
                        } catch {
                            finish(null);
                        }
                        """,
                        appId)
                    ?.ToString();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    slot.LastSuccessfulRegistrationUrl = registrationUrl;
                    return token;
                }
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static bool WaitForInitBotion(
        IJavaScriptExecutor javaScript,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (javaScript.ExecuteScript(
                    "return typeof window.initBotion === 'function';")
                is true)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return javaScript.ExecuteScript(
                   "return typeof window.initBotion === 'function';")
               is true;
    }

    private static string? BootstrapGeeTestSdk(
        IJavaScriptExecutor javaScript,
        BrowserGeetestTokenRequest request)
    {
        return javaScript.ExecuteAsyncScript(
                """
                const sdkCdn = arguments[0];
                const sdkVersion = arguments[1];
                const done = arguments[arguments.length - 1];
                const finish = value => done(value || 'failed');

                if (typeof window.initBotion === 'function') {
                    finish('ready');
                    return;
                }

                let completed = false;
                const complete = value => {
                    if (completed) return;
                    completed = true;
                    finish(value);
                };
                const timer = setTimeout(() => complete('timeout'), 8000);
                const base = sdkCdn.endsWith('/') ? sdkCdn : sdkCdn + '/';
                const load = path => new Promise((resolve, reject) => {
                    const source = base + path + '?v=' + encodeURIComponent(sdkVersion);
                    const existing = Array.from(document.scripts)
                        .find(script => script.src === source);
                    if (existing) {
                        if (typeof window.initBotion === 'function') resolve();
                        else existing.addEventListener('load', resolve, { once: true });
                        return;
                    }

                    const script = document.createElement('script');
                    script.async = true;
                    script.src = source;
                    script.onload = resolve;
                    script.onerror = reject;
                    document.head.appendChild(script);
                });

                (async () => {
                    try {
                        await load('TCG_PROD_IMAGES/B2C/libs/g5/gd.js');
                    } catch {
                        // GeeGuard is optional on a subset of deployments.
                    }

                    try {
                        await load('TCG_PROD_IMAGES/B2C/libs/gt/boc.js');
                        clearTimeout(timer);
                        complete(typeof window.initBotion === 'function' ? 'ready' : 'sdk-loaded-without-init');
                    } catch {
                        clearTimeout(timer);
                        complete('sdk-load-failed');
                    }
                })();
                """,
                request.GeetestSdkCdn,
                request.GeetestSdkVersion)
            ?.ToString();
    }

    private static IWebDriver CreateDriver(BrowserSlot slot, TargetSite site)
    {
        if (slot.Engine == BrowserEngine.Edge)
        {
            return DriverFactory.CreateEdgeDriver(
                slot.ProfilePath,
                slot.Proxy,
                deleteProfileOnDispose: true);
        }

        return DriverFactory.CreateDriver(
            slot.ProfilePath,
            userAgent: MobileUserAgent,
            proxy: slot.Proxy?.ConnectionString,
            headless: true,
            site: site,
            verifyProxy: false,
            deleteProfileOnDispose: true,
            forceMobileEmulation: true);
    }

    private List<BrowserSlot> RemoveExpiredSlotsLocked(DateTimeOffset now)
    {
        var toDispose = _slots.Values
            .Where(slot => slot.ActiveLeases == 0
                           && now - slot.LastReleasedAt
                              >= TimeSpan.FromSeconds(_options.LocalBrowserIdleSeconds))
            .ToList();
        foreach (var slot in toDispose)
        {
            _slots.Remove(slot.Key);
        }

        return toDispose;
    }

    private static bool IsUsableToken(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "lot_number", "captcha_output", "pass_token", "gen_time" })
        {
            if (!value.TryGetProperty(name, out var field)
                || field.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(field.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateProxyKey(
        ProxyConfiguration? proxy,
        BrowserEngine engine)
    {
        var raw = $"{engine}|{proxy?.ConnectionString ?? "direct"}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            throw new ObjectDisposedException(nameof(BrowserGeetestTokenPool));
        }
    }

    private void SignalAvailability()
    {
        try
        {
            _availability.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void DisposeSlots(IEnumerable<BrowserSlot>? slots)
    {
        if (slots is null)
        {
            return;
        }

        foreach (var slot in slots)
        {
            slot.Dispose();
        }
    }

    private void TryDeletePoolRoot()
    {
        try
        {
            if (Directory.Exists(_poolRoot))
            {
                Directory.Delete(_poolRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class BrowserLease : IAsyncDisposable
    {
        private readonly BrowserGeetestTokenPool _owner;
        private readonly bool _reusedBrowser;
        private int _gateHeld = 1;
        private int _successfulUse;
        private int _invalidate;

        public BrowserLease(
            BrowserGeetestTokenPool owner,
            BrowserSlot slot,
            bool reusedBrowser)
        {
            _owner = owner;
            Slot = slot;
            _reusedBrowser = reusedBrowser;
        }

        public BrowserSlot Slot { get; }

        public bool ReusedBrowser => _reusedBrowser;

        public void MarkSuccessfulUse() => Interlocked.Exchange(ref _successfulUse, 1);

        public void Invalidate() => Interlocked.Exchange(ref _invalidate, 1);

        public async ValueTask DisposeAsync()
        {
            var gateHeld = Interlocked.Exchange(ref _gateHeld, 0) != 0;
            await _owner.ReturnAsync(
                    Slot,
                    gateHeld,
                    Volatile.Read(ref _successfulUse) != 0,
                    Volatile.Read(ref _invalidate) != 0)
                .ConfigureAwait(false);
        }
    }

    private sealed record BrowserTokenAttempt(
        JsonElement? Token,
        string? FailureReason);

    private sealed class BrowserSlot : IDisposable
    {
        private int _disposed;

        public BrowserSlot(
            string key,
            ProxyConfiguration? proxy,
            string profilePath,
            BrowserEngine engine,
            DateTimeOffset createdAt)
        {
            Key = key;
            Proxy = proxy;
            ProfilePath = profilePath;
            Engine = engine;
            LastReleasedAt = createdAt;
        }

        public string Key { get; }

        public ProxyConfiguration? Proxy { get; }

        public string ProfilePath { get; }

        public BrowserEngine Engine { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IWebDriver? Driver { get; set; }

        public int ActiveLeases { get; set; }

        public int SuccessfulUses { get; set; }

        public bool RecycleRequested { get; set; }

        public string? LastSuccessfulRegistrationUrl { get; set; }

        public DateTimeOffset LastReleasedAt { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                Driver?.Dispose();
            }
            catch
            {
            }
        }
    }

    private enum BrowserEngine
    {
        Chrome,
        Edge
    }
}

public sealed record BrowserGeetestPoolSnapshot(
    int SessionCount,
    int StartedBrowserCount,
    int ActiveLeaseCount,
    int SuccessfulTokenCount);
