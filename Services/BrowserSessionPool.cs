using System.Collections.Concurrent;
using System.Threading.Channels;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace BAOCODETOOL.Services;

public sealed class BrowserSessionPool : IBrowserSessionPool, IHostedService, IDisposable
{
    private const string BlankPage = "about:blank";

    private readonly AutomationOptions _options;
    private readonly ILogger<BrowserSessionPool> _logger;
    private readonly Channel<PooledBrowserSlot> _available;
    private readonly ConcurrentDictionary<int, PooledBrowserSlot> _slots = new();
    private readonly ConcurrentDictionary<int, byte> _replenishing = new();
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleSync = new();
    private readonly string _poolRoot;
    private int _activeLeases;
    private int _stopping;
    private int _disposed;

    public BrowserSessionPool(
        IOptions<AutomationOptions> options,
        ILogger<BrowserSessionPool> logger)
    {
        _options = options.Value;
        _logger = logger;

        var capacity = Math.Max(1, _options.PrewarmedBrowserPoolSize);
        _available = Channel.CreateBounded<PooledBrowserSlot>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _poolRoot = Path.Combine(
            AppContext.BaseDirectory,
            "BrowserPool",
            Guid.NewGuid().ToString("N"));
    }

    public bool CanServe(AccountModel account, bool headless)
    {
        ArgumentNullException.ThrowIfNull(account);

        return _options.EnablePrewarmedBrowserPool
               && headless == _options.Headless
               && string.IsNullOrWhiteSpace(account.Proxy)
               && string.IsNullOrWhiteSpace(account.UserAgent)
               && Volatile.Read(ref _stopping) == 0;
    }

    public IBrowserSessionLease Rent(
        TargetSite site,
        CancellationToken cancellationToken)
    {
        if (!_options.EnablePrewarmedBrowserPool)
        {
            throw new InvalidOperationException("The prewarmed browser pool is disabled.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = _available.Reader
                .ReadAsync(cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            if (IsHealthy(slot))
            {
                lock (_lifecycleSync)
                {
                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        slot.Dispose();
                        throw new ObjectDisposedException(nameof(BrowserSessionPool));
                    }

                    // StopAsync takes the same lock before marking the pool as
                    // stopping. This closes the small race where shutdown
                    // could dispose a slot after Rent read it but before the
                    // lease became visible to the drain wait.
                    Interlocked.Increment(ref _activeLeases);
                }

                _logger.LogInformation(
                    "Prewarmed browser rented. Slot={SlotId}, Site={Site}, Uses={Uses}",
                    slot.Id,
                    site,
                    slot.UseCount);

                return new PooledBrowserLease(this, slot, site);
            }

            _logger.LogWarning(
                "Prewarmed browser was unhealthy before use. Recreating slot {SlotId}.",
                slot.Id);

            if (TryReplaceSlot(slot, out var replacement))
            {
                lock (_lifecycleSync)
                {
                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        replacement.Dispose();
                        throw new ObjectDisposedException(nameof(BrowserSessionPool));
                    }

                    Interlocked.Increment(ref _activeLeases);
                }

                return new PooledBrowserLease(this, replacement, site);
            }

            ScheduleReplenishment(slot.Id);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnablePrewarmedBrowserPool)
        {
            _logger.LogInformation("Prewarmed browser pool is disabled.");
            return;
        }

        Directory.CreateDirectory(_poolRoot);
        _logger.LogInformation(
            "Prewarming {PoolSize} direct Chrome sessions. Headless={Headless}",
            _options.PrewarmedBrowserPoolSize,
            _options.Headless);

        try
        {
            for (var id = 1; id <= _options.PrewarmedBrowserPoolSize; id++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var slot = CreateSlot(id);
                _slots[id] = slot;
                await _available.Writer.WriteAsync(slot, cancellationToken);

                _logger.LogInformation(
                    "Prewarmed browser ready. Slot={SlotId}, Ready={Ready}/{PoolSize}",
                    id,
                    id,
                    _options.PrewarmedBrowserPoolSize);
            }
        }
        catch
        {
            DisposeAllSlots();
            TryDeletePoolRoot();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            Volatile.Write(ref _stopping, 1);
        }

        _shutdown.Cancel();
        _available.Writer.TryComplete();

        // Give active workflows a short graceful-drain window before
        // disposing their Chrome instances. The host may still cancel this
        // wait through its own shutdown token; disposal remains the final
        // safety net in either case.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            while (Volatile.Read(ref _activeLeases) > 0
                   && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        DisposeAllSlots();
        TryDeletePoolRoot();
        _logger.LogInformation("Prewarmed browser pool stopped.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _shutdown.Dispose();
        _maintenanceGate.Dispose();
    }

    private PooledBrowserSlot CreateSlot(int id)
    {
        var profilePath = Path.Combine(_poolRoot, $"slot-{id:D2}");
        Directory.CreateDirectory(profilePath);

        var driver = DriverFactory.CreateDriver(
            profilePath,
            userAgent: null,
            proxy: null,
            headless: _options.Headless);

        driver.Navigate().GoToUrl(BlankPage);
        return new PooledBrowserSlot(id, profilePath, driver);
    }

    private void Return(PooledBrowserSlot slot, TargetSite site)
    {
        try
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                slot.Dispose();
                return;
            }

            slot.UseCount++;
            var shouldRecycle = slot.UseCount >= _options.BrowserPoolMaxUsesPerSession;
            var resetSucceeded = !shouldRecycle && TryReset(slot, site);

            if (shouldRecycle)
            {
                _logger.LogInformation(
                    "Recycling prewarmed browser after {Uses} uses. Slot={SlotId}",
                    slot.UseCount,
                    slot.Id);
            }
            else if (!resetSucceeded)
            {
                _logger.LogWarning(
                    "Could not fully reset prewarmed browser. Recreating slot {SlotId}.",
                    slot.Id);
            }

            if (shouldRecycle || !resetSucceeded)
            {
                if (!TryReplaceSlot(slot, out slot))
                {
                    ScheduleReplenishment(slot.Id);
                    return;
                }
            }

            if (!_available.Writer.TryWrite(slot))
            {
                slot.Dispose();
            }
            else
            {
                _logger.LogInformation(
                    "Prewarmed browser reset and returned. Slot={SlotId}, Uses={Uses}",
                    slot.Id,
                    slot.UseCount);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeLeases);
        }
    }

    private bool TryReset(PooledBrowserSlot slot, TargetSite site)
    {
        try
        {
            var driver = slot.Driver;
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                GetRegistrationOrigin(site)
            };

            var handles = driver.WindowHandles.ToArray();
            if (handles.Length == 0)
            {
                return false;
            }

            var primaryHandle = handles[0];
            foreach (var handle in handles)
            {
                try
                {
                    driver.SwitchTo().Window(handle);
                    AddCurrentOrigin(driver, origins);
                    ClearCurrentPageStorage(driver);

                    if (!string.Equals(handle, primaryHandle, StringComparison.Ordinal))
                    {
                        driver.Close();
                    }
                }
                catch (WebDriverException)
                {
                    return false;
                }
            }

            driver.SwitchTo().Window(primaryHandle);
            ClearChromeStorage(driver, origins);
            driver.Navigate().GoToUrl(BlankPage);

            return IsHealthy(slot);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Browser reset failed. Slot={SlotId}, Site={Site}",
                slot.Id,
                site);
            return false;
        }
    }

    private static void AddCurrentOrigin(
        IWebDriver driver,
        ISet<string> origins)
    {
        try
        {
            if (Uri.TryCreate(driver.Url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                origins.Add(uri.GetLeftPart(UriPartial.Authority));
            }
        }
        catch (WebDriverException)
        {
        }
    }

    private static void ClearCurrentPageStorage(IWebDriver driver)
    {
        if (driver is not IJavaScriptExecutor javaScript)
        {
            return;
        }

        try
        {
            // Keep this step synchronous and bounded. Origin-wide cache,
            // service-worker, IndexedDB and cookie cleanup is handled by the
            // CDP call below; awaiting every browser storage API here can block
            // a pooled slot for many seconds when a page is mid-navigation.
            javaScript.ExecuteScript(
                """
                try { window.localStorage.clear(); } catch {}
                try { window.sessionStorage.clear(); } catch {}
                """);
        }
        catch (WebDriverException)
        {
        }
    }

    private static void ClearChromeStorage(
        IWebDriver driver,
        IEnumerable<string> origins)
    {
        if (driver is not ChromeDriver chromeDriver)
        {
            return;
        }

        var empty = new Dictionary<string, object>();

        try
        {
            chromeDriver.ExecuteCdpCommand("Network.clearBrowserCookies", empty);
        }
        catch (WebDriverException)
        {
        }

        foreach (var origin in origins.Where(origin => !string.IsNullOrWhiteSpace(origin)))
        {
            try
            {
                chromeDriver.ExecuteCdpCommand(
                    "Storage.clearDataForOrigin",
                    new Dictionary<string, object>
                    {
                        ["origin"] = origin,
                        ["storageTypes"] = "all"
                    });
            }
            catch (WebDriverException)
            {
                // Some redirects expose opaque or no-longer-valid origins.
                // Cookies/cache were already cleared globally above.
            }
        }

        try
        {
            chromeDriver.ExecuteCdpCommand("Browser.resetPermissions", empty);
        }
        catch (WebDriverException)
        {
        }
    }

    private static bool IsHealthy(PooledBrowserSlot slot)
    {
        if (slot.IsDisposed)
        {
            return false;
        }

        try
        {
            return slot.Driver.WindowHandles.Count > 0;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private bool TryReplaceSlot(
        PooledBrowserSlot oldSlot,
        out PooledBrowserSlot replacement)
    {
        replacement = oldSlot;

        try
        {
            _maintenanceGate.Wait(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            oldSlot.Dispose();
            TryDeleteProfileDirectory(oldSlot.ProfilePath);

            if (Volatile.Read(ref _stopping) != 0)
            {
                return false;
            }

            replacement = CreateSlot(oldSlot.Id);
            _slots[oldSlot.Id] = replacement;
            return true;
        }
        catch (Exception exception)
        {
            _slots.TryRemove(oldSlot.Id, out _);
            _logger.LogError(
                exception,
                "Could not recreate prewarmed browser slot {SlotId}.",
                oldSlot.Id);
            return false;
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    private void ScheduleReplenishment(int slotId)
    {
        if (Volatile.Read(ref _stopping) != 0
            || !_replenishing.TryAdd(slotId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    try
                    {
                        await _maintenanceGate.WaitAsync(_shutdown.Token);
                        try
                        {
                            var replacement = CreateSlot(slotId);
                            _slots[slotId] = replacement;

                            if (!_available.Writer.TryWrite(replacement))
                            {
                                replacement.Dispose();
                            }

                            _logger.LogInformation(
                                "Prewarmed browser slot replenished. Slot={SlotId}",
                                slotId);
                            return;
                        }
                        finally
                        {
                            _maintenanceGate.Release();
                        }
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(
                            exception,
                            "Browser slot replenishment failed. Slot={SlotId}; retrying in 5 seconds.",
                            slotId);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                _replenishing.TryRemove(slotId, out _);
            }
        });
    }

    private void DisposeAllSlots()
    {
        foreach (var slot in _slots.Values)
        {
            slot.Dispose();
        }

        _slots.Clear();
    }

    private void TryDeleteProfileDirectory(string profilePath)
    {
        try
        {
            var root = Path.GetFullPath(_poolRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(profilePath);

            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Could not delete pooled browser profile {ProfilePath}.",
                profilePath);
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
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Could not delete browser pool directory {PoolRoot}.",
                _poolRoot);
        }
    }

    private static string GetRegistrationOrigin(TargetSite site) =>
        site switch
        {
            TargetSite.RR88 => "http://gioithieu.rr3311.com",
            TargetSite.MM88 => "http://gioithieu.mm88p.com",
            TargetSite.XX88 => "http://gioithieu.xx88.fun",
            TargetSite.LLWIN => "https://llwin.app",
            TargetSite.GG88 => "https://www.gg88v.com",
            _ => throw new ArgumentOutOfRangeException(nameof(site), site, "Unknown target site.")
        };

    private sealed class PooledBrowserLease : IBrowserSessionLease
    {
        private BrowserSessionPool? _owner;
        private PooledBrowserSlot? _slot;
        private readonly TargetSite _site;

        public PooledBrowserLease(
            BrowserSessionPool owner,
            PooledBrowserSlot slot,
            TargetSite site)
        {
            _owner = owner;
            _slot = slot;
            _site = site;
        }

        public IWebDriver Driver =>
            _slot?.Driver
            ?? throw new ObjectDisposedException(nameof(PooledBrowserLease));

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var slot = Interlocked.Exchange(ref _slot, null);

            if (owner is not null && slot is not null)
            {
                owner.Return(slot, _site);
            }
        }
    }

    private sealed class PooledBrowserSlot : IDisposable
    {
        private int _disposed;

        public PooledBrowserSlot(
            int id,
            string profilePath,
            IWebDriver driver)
        {
            Id = id;
            ProfilePath = profilePath;
            Driver = driver;
        }

        public int Id { get; }

        public string ProfilePath { get; }

        public IWebDriver Driver { get; }

        public int UseCount { get; set; }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                Driver.Dispose();
            }
            catch (WebDriverException)
            {
            }
        }
    }
}
