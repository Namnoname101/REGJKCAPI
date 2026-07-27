using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BAOCODETOOL.Models;
using BAOCODETOOL.Strategies;

namespace BAOCODETOOL.Core;

public sealed class BotManager
{
    private readonly object _runLock = new();
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private CancellationTokenSource? _cts;

    public void Pause()
    {
        lock (_runLock)
        {
            if (_cts is not null && !_cts.IsCancellationRequested)
            {
                _pauseEvent.Reset();
            }
        }
    }

    public void Resume()
    {
        _pauseEvent.Set();
    }

    public void Stop()
    {
        CancellationTokenSource? cancellationTokenSource;

        lock (_runLock)
        {
            cancellationTokenSource = _cts;
        }

        _pauseEvent.Set();
        cancellationTokenSource?.Cancel();
    }

    public async Task RunMassRegistrationAsync(
        IEnumerable<AccountModel> profiles,
        int maxThreads,
        Action? onSiteCompleted = null,
        CancellationToken cancellationToken = default,
        SemaphoreSlim? browserStartupGate = null,
        bool headless = false,
        bool enableDirectHttpRegistration = true,
        bool enableBrowserFallback = false,
        int maxConcurrentSitesPerAccount = 1,
        IBrowserSessionPool? browserSessionPool = null,
        IBrowserConcurrencyGovernor? browserConcurrencyGovernor = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var cancellationTokenSource = BeginRun(cancellationToken);
        var executionContext = new AutomationExecutionContext(cancellationTokenSource.Token, _pauseEvent);

        try
        {
        var activeAccounts = profiles
            .Where(a => a.SiteRuns.Any(sr => sr.IsSelected && sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Phase1_RegDone))
            .ToList();

        if (activeAccounts.Count == 0)
        {
            return;
        }

        maxThreads = Math.Max(1, maxThreads);
        maxConcurrentSitesPerAccount = Math.Max(1, maxConcurrentSitesPerAccount);

        using var semaphore = new SemaphoreSlim(maxThreads);
        var tasks = new List<Task>();
        
        foreach (var account in activeAccounts)
        {
            tasks.Add(RunAccountSitesConcurrentlyAsync(
                account,
                semaphore,
                Math.Min(maxThreads, maxConcurrentSitesPerAccount),
                executionContext,
                onSiteCompleted,
                browserStartupGate,
                headless,
                enableDirectHttpRegistration,
                enableBrowserFallback,
                browserSessionPool,
                browserConcurrencyGovernor));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    finally
    {
        EndRun(cancellationTokenSource);
    }
}

private async Task RunAccountSitesConcurrentlyAsync(
    AccountModel account,
    SemaphoreSlim semaphore,
    int maxConcurrentSitesPerAccount,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted,
    SemaphoreSlim? browserStartupGate,
    bool headless,
    bool enableDirectHttpRegistration,
    bool enableBrowserFallback,
    IBrowserSessionPool? browserSessionPool,
    IBrowserConcurrencyGovernor? browserConcurrencyGovernor)
{
    var siteRuns = account.SiteRuns
        .Where(sr => sr.IsSelected && sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Phase1_RegDone)
        .ToList();

    if (siteRuns.Count == 0)
    {
        return;
    }

    // A proxy is shared by all sites of one account. Verify it once before
    // fan-out so RR88/MM88 do not each spend a health-check round trip and so
    // direct API jobs can start immediately after the single preflight.
    var proxyCheckSlotAcquired = false;
    try
    {
        await semaphore.WaitAsync(executionContext.CancellationToken).ConfigureAwait(false);
        proxyCheckSlotAcquired = true;
        if (!await TryVerifyRequiredProxyAsync(
                account,
                siteRuns,
                executionContext,
                onSiteCompleted)
            .ConfigureAwait(false))
        {
            return;
        }
    }
    finally
    {
        if (proxyCheckSlotAcquired)
        {
            semaphore.Release();
        }
    }

    using var accountSemaphore = new SemaphoreSlim(maxConcurrentSitesPerAccount);
    var tasks = siteRuns
        .Select(siteRun => RunJobAsync(
            account,
            siteRun,
            semaphore,
            accountSemaphore,
            executionContext,
            onSiteCompleted,
            browserStartupGate,
            headless,
            enableDirectHttpRegistration,
            enableBrowserFallback,
            browserSessionPool,
            browserConcurrencyGovernor))
        .ToArray();

    await Task.WhenAll(tasks).ConfigureAwait(false);
}

private async Task RunJobAsync(
    AccountModel account,
    SiteRunModel siteRun,
    SemaphoreSlim semaphore,
    SemaphoreSlim accountSemaphore,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted,
    SemaphoreSlim? browserStartupGate,
    bool headless,
    bool enableDirectHttpRegistration,
    bool enableBrowserFallback,
    IBrowserSessionPool? browserSessionPool,
    IBrowserConcurrencyGovernor? browserConcurrencyGovernor)
{
    var accountSlotAcquired = false;
    var slotAcquired = false;

    try
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Running, "Queued. Waiting for site slot...");

        // Acquire the per-account slot before the global slot. This prevents
        // queued sites from consuming global slots while waiting for their
        // own account's concurrency limit.
        await accountSemaphore.WaitAsync(executionContext.CancellationToken).ConfigureAwait(false);
        accountSlotAcquired = true;

        if (enableDirectHttpRegistration
            && DirectRegistrationWorkflow.IsSupported(siteRun.Site))
        {
            if (await TryRunDirectHttpRegistrationAsync(
                    account,
                    siteRun,
                    executionContext,
                    onSiteCompleted,
                    enableBrowserFallback)
                .ConfigureAwait(false))
            {
                return;
            }

            if (!enableBrowserFallback)
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Failed,
                    "Direct HTTP registration did not complete. Chrome fallback is disabled.");
                NotifySiteCompleted(onSiteCompleted);
                return;
            }
        }
        else if (!enableBrowserFallback
                 && !LobbyMobileWorkflow.IsSupported(siteRun.Site))
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                enableDirectHttpRegistration
                    ? $"Direct HTTP registration is not supported for {siteRun.Site}. Chrome fallback is disabled."
                    : "Direct HTTP registration is disabled and Chrome fallback is disabled.");
            NotifySiteCompleted(onSiteCompleted);
            return;
        }
        else if (LobbyMobileWorkflow.IsSupported(siteRun.Site))
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Running,
                "Direct API requires interactive GeeTest. Switching to the mobile browser workflow...");
        }

        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Running, "Waiting for browser slot...");
        await semaphore.WaitAsync(executionContext.CancellationToken).ConfigureAwait(false);
        slotAcquired = true;

        executionContext.Checkpoint();
        await Task.Run(
                () => RunSingleSiteExecution(
                    account,
                    siteRun,
                    executionContext,
                    onSiteCompleted,
                    browserStartupGate,
                    headless,
                    browserSessionPool,
                    browserConcurrencyGovernor),
                executionContext.CancellationToken)
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (executionContext.CancellationToken.IsCancellationRequested)
    {
        if (siteRun.Status == AccountStatus.Running || siteRun.Status == AccountStatus.Ready)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Stopped, "Stopped by operator.");
        }
    }
    finally
    {
        if (slotAcquired)
        {
            semaphore.Release();
        }

        if (accountSlotAcquired)
        {
            accountSemaphore.Release();
        }
    }
}

private static void NotifySiteCompleted(Action? callback)
{
    try
    {
        callback?.Invoke();
    }
    catch (Exception callbackException)
    {
        Debug.WriteLine($"Site completion callback failed: {callbackException}");
    }
}

private static async Task<bool> TryVerifyRequiredProxyAsync(
    AccountModel account,
    IReadOnlyList<SiteRunModel> siteRuns,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted)
{
    if (!ProxyConfigurationParser.TryParse(
            account.Proxy,
            out var proxyConfiguration,
            out var parseError)
        || proxyConfiguration is null)
    {
        MarkProxyVerificationFailed(
            account,
            siteRuns,
            $"Proxy is required and must be valid: {parseError ?? "proxy data is empty."}",
            onSiteCompleted);
        return false;
    }

    if (proxyConfiguration.IsSocks)
    {
        MarkProxyVerificationFailed(
            account,
            siteRuns,
            "SOCKS5 proxy is not supported for registration. Use an HTTP/HTTPS proxy.",
            onSiteCompleted);
        return false;
    }

    executionContext.Checkpoint();
    foreach (var siteRun in siteRuns)
    {
        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Running,
            "Verifying required proxy before registration...");
    }

    ProxyConnectivityCheckResult connectivity;
    try
    {
        connectivity = await ProxyConnectivityVerifier
            .CheckAsync(proxyConfiguration, executionContext.CancellationToken)
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception exception)
    {
        connectivity = ProxyConnectivityCheckResult.Failure(exception.Message);
    }

    if (!connectivity.IsSuccess)
    {
        MarkProxyVerificationFailed(
            account,
            siteRuns,
            $"Proxy connection failed: {connectivity.Message} Automation was not started to avoid direct-IP fallback.",
            onSiteCompleted);
        return false;
    }

    foreach (var siteRun in siteRuns)
    {
        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Running,
            $"Proxy verified. External IP: {connectivity.ObservedIp ?? "unknown"}.");
    }

    return true;
}

private static void MarkProxyVerificationFailed(
    AccountModel account,
    IReadOnlyList<SiteRunModel> siteRuns,
    string message,
    Action? onSiteCompleted)
{
    foreach (var siteRun in siteRuns)
    {
        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, message);
        NotifySiteCompleted(onSiteCompleted);
    }
}

private static async Task<bool> TryRunDirectHttpRegistrationAsync(
    AccountModel account,
    SiteRunModel siteRun,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted,
    bool enableBrowserFallback)
{
    var metrics = new SitePerformanceMetrics();
    var totalStopwatch = Stopwatch.StartNew();
    var handled = false;

    try
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Running,
                $"Executing direct HTTP registration (Attempt {attempt})...");

            var registrationStopwatch = Stopwatch.StartNew();
            var outcome = await DirectRegistrationWorkflow.TryRegisterAsync(
                    account,
                    siteRun.Site,
                    executionContext.CancellationToken)
                .ConfigureAwait(false);
            registrationStopwatch.Stop();
            metrics.RegistrationMs += registrationStopwatch.ElapsedMilliseconds;

            if (outcome.Status == DirectRegistrationStatus.Success)
            {
                if (outcome.Session is null)
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        "Direct HTTP registration succeeded but did not return a session token for bank setup.");
                    handled = true;
                    return true;
                }

                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Phase1_RegDone,
                    "Direct HTTP registration completed. Starting direct bank setup...");

                executionContext.Checkpoint();
                var bankSetupStopwatch = Stopwatch.StartNew();
                var bankOutcome = await DirectBankSetupWorkflow.TrySetupAsync(
                        account,
                        siteRun.Site,
                        outcome.Session,
                        executionContext.CancellationToken)
                    .ConfigureAwait(false);
                bankSetupStopwatch.Stop();
                metrics.BankSetupMs += bankSetupStopwatch.ElapsedMilliseconds;

                if (bankOutcome.Status != DirectBankSetupStatus.Success)
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        $"Direct HTTP bank setup failed: {bankOutcome.Message}");
                    handled = true;
                    return true;
                }

                ResultWriter.AppendSuccess(account, siteRun.Site);
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Success,
                    "Direct HTTP registration and bank setup completed. Saved to success.txt.");
                handled = true;
                return true;
            }

            if (outcome.Status == DirectRegistrationStatus.FallbackToBrowser
                || outcome.Status == DirectRegistrationStatus.NotSupported)
            {
                if (!enableBrowserFallback)
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        $"Direct HTTP registration unavailable: {outcome.Message} Chrome fallback is disabled.");
                    handled = true;
                    return true;
                }

                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Running,
                    $"{outcome.Message} Falling back to browser workflow...");
                return false;
            }

            if (outcome.UsernameConflict && attempt < 2)
            {
                var newUsername = ShortUsernameGenerator.Next();
                account.SpecificUsernames[siteRun.Site] = newUsername;
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Running,
                    $"Direct HTTP username existed. Retrying with {newUsername}...");
                continue;
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                $"Direct HTTP registration failed: {outcome.Message}");
            handled = true;
            return true;
        }

        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Failed,
            "Direct HTTP registration failed after username retry.");
        handled = true;
        return true;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    finally
    {
        if (handled)
        {
            totalStopwatch.Stop();
            metrics.TotalMs = totalStopwatch.ElapsedMilliseconds;
            SitePerformanceMetricsWriter.Append(account, siteRun, metrics);

            try
            {
                onSiteCompleted?.Invoke();
            }
            catch (Exception callbackException)
            {
                Debug.WriteLine($"Site completion callback failed: {callbackException}");
            }
        }
    }
}

private static void RunSingleSiteExecution(
    AccountModel account,
    SiteRunModel siteRun,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted,
    SemaphoreSlim? browserStartupGate,
    bool headless,
    IBrowserSessionPool? browserSessionPool,
    IBrowserConcurrencyGovernor? browserConcurrencyGovernor)
{
    IBrowserSessionLease? pooledLease = null;
    IBrowserConcurrencyLease? concurrencyLease = null;
    IWebDriver? dedicatedDriver = null;
    var metrics = new SitePerformanceMetrics();
    var totalStopwatch = Stopwatch.StartNew();

    try
    {
        executionContext.Checkpoint();
        var capacityStopwatch = Stopwatch.StartNew();
        if (browserConcurrencyGovernor is not null)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Running,
                "Waiting for CPU/browser capacity...");
            concurrencyLease = browserConcurrencyGovernor.Acquire(
                executionContext.CancellationToken);
        }
        capacityStopwatch.Stop();
        metrics.CapacityWaitMs = capacityStopwatch.ElapsedMilliseconds;

        IWebDriver driver;
        var browserAcquireStopwatch = Stopwatch.StartNew();

        if (!LobbyMobileWorkflow.IsSupported(siteRun.Site)
            && browserSessionPool?.CanServe(account, headless) == true)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Running,
                "Renting prewarmed browser...");

            pooledLease = browserSessionPool.Rent(
                siteRun.Site,
                executionContext.CancellationToken);
            driver = pooledLease.Driver;
        }
        else
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Running,
                "Launching dedicated browser profile...");

            // The gate is released as soon as the driver is created. It must
            // not cover the registration workflow.
            dedicatedDriver = CreateDriverWithStartupGate(
                account,
                siteRun.Site,
                headless,
                browserStartupGate,
                executionContext.CancellationToken);
            driver = dedicatedDriver;
        }
        browserAcquireStopwatch.Stop();
        metrics.BrowserAcquireMs = browserAcquireStopwatch.ElapsedMilliseconds;

        RunSingleSiteInBrowser(driver, account, siteRun, executionContext, metrics);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Browser error: {ex.Message}");
    }
    finally
    {
        var browserResetStopwatch = Stopwatch.StartNew();
        try
        {
            var diagnosticDriver = pooledLease?.Driver ?? dedicatedDriver;
            if (siteRun.Status == AccountStatus.Failed && diagnosticDriver is not null)
            {
                BrowserFailureArtifactWriter.TryCapture(diagnosticDriver, account, siteRun);
            }

            if (pooledLease is not null)
            {
                pooledLease.Dispose();
            }
            else
            {
                dedicatedDriver?.Dispose();
            }
        }
        finally
        {
            browserResetStopwatch.Stop();
            metrics.BrowserResetMs = browserResetStopwatch.ElapsedMilliseconds;
            totalStopwatch.Stop();
            metrics.TotalMs = totalStopwatch.ElapsedMilliseconds;
            SitePerformanceMetricsWriter.Append(account, siteRun, metrics);
            concurrencyLease?.Dispose();

            // Persistence/telemetry callbacks are deliberately outside the
            // browser result path. A transient disk/IO failure must not turn
            // an otherwise successful site into an unhandled Task exception
            // that poisons the whole account job.
            try
            {
                onSiteCompleted?.Invoke();
            }
            catch (Exception callbackException)
            {
                Debug.WriteLine($"Site completion callback failed: {callbackException}");
            }
        }
    }
}

private static IWebDriver CreateDriverWithStartupGate(
    AccountModel account,
    TargetSite site,
    bool headless,
    SemaphoreSlim? browserStartupGate,
    CancellationToken cancellationToken)
{
    var startupSlotAcquired = false;

    try
    {
        if (browserStartupGate is not null)
        {
            // Chrome startup is CPU/IO heavy. Gate only this creation phase so
            // already-running browsers can execute in parallel.
            browserStartupGate.Wait(cancellationToken);
            startupSlotAcquired = true;
        }

        return DriverFactory.CreateDriver(account, site, headless);
    }
    finally
    {
        if (startupSlotAcquired)
        {
            browserStartupGate!.Release();
        }
    }
}

private static void RunSingleSiteInBrowser(
    IWebDriver driver,
    AccountModel account,
    SiteRunModel siteRun,
    AutomationExecutionContext executionContext,
    SitePerformanceMetrics metrics)
{
    try
    {
        executionContext.Checkpoint();
        var strategy = StrategyFactory.Create(siteRun.Site);

        var registrationStopwatch = Stopwatch.StartNew();
        bool registrationSucceeded = false;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: $"Executing registration phase (Attempt {attempt})...");
            executionContext.Checkpoint();
            registrationSucceeded = strategy.ExecuteRegistration(driver, account, siteRun, executionContext);
            
            if (registrationSucceeded) break;

            if (siteRun.Status == AccountStatus.Failed && 
                (siteRun.LogMessage.Contains("tên", StringComparison.OrdinalIgnoreCase) || 
                 siteRun.LogMessage.Contains("tài khoản", StringComparison.OrdinalIgnoreCase) ||
                 siteRun.LogMessage.Contains("tồn tại", StringComparison.OrdinalIgnoreCase)) &&
                 !siteRun.LogMessage.Contains("số điện thoại", StringComparison.OrdinalIgnoreCase) &&
                 !siteRun.LogMessage.Contains("phone", StringComparison.OrdinalIgnoreCase))
            {
                // Thay đổi username riêng cho site này
                var newUsername = ShortUsernameGenerator.Next();
                account.SpecificUsernames[siteRun.Site] = newUsername;
                
                AutomationUi.SetSiteStatus(account, siteRun, logMessage: $"Username existed. Retrying with {newUsername}...");
                try { driver.Navigate().Refresh(); } catch { }
                executionContext.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            // Lỗi khác thì thoát luôn
            break;
        }

        registrationStopwatch.Stop();
        metrics.RegistrationMs = registrationStopwatch.ElapsedMilliseconds;

        if (!registrationSucceeded)
        {
            if (siteRun.Status != AccountStatus.Failed)
            {
                AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, "Registration phase failed.");
            }

            return;
        }

        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Phase1_RegDone, "Registration completed. Starting bank setup...");
        executionContext.Checkpoint();
        var bankSetupStopwatch = Stopwatch.StartNew();
        var bankSetupSucceeded = strategy.ExecuteBankSetup(driver, account, siteRun, executionContext);
        bankSetupStopwatch.Stop();
        metrics.BankSetupMs = bankSetupStopwatch.ElapsedMilliseconds;
        if (!bankSetupSucceeded)
        {
            if (siteRun.Status != AccountStatus.Failed)
            {
                AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, "Bank setup phase failed.");
            }

            return;
        }

        executionContext.Checkpoint();
        ResultWriter.AppendSuccess(account, siteRun.Site);
        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Success, "Completed successfully. Saved to success.txt.");
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Failed: {ex.Message}");
    }
}

    private CancellationTokenSource BeginRun(CancellationToken cancellationToken)
    {
        lock (_runLock)
        {
            if (_cts is not null)
            {
                throw new InvalidOperationException("Automation is already running.");
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pauseEvent.Set();
            return _cts;
        }
    }

    private void EndRun(CancellationTokenSource cancellationTokenSource)
    {
        lock (_runLock)
        {
            if (ReferenceEquals(_cts, cancellationTokenSource))
            {
                _cts = null;
            }

            _pauseEvent.Set();
            cancellationTokenSource.Dispose();
        }
    }
}
