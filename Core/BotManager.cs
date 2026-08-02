using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Models;
using BAOCODETOOL.Strategies;

namespace BAOCODETOOL.Core;

public sealed class BotManager
{
    private const string ProxyDeadMessage = "Proxy die.";
    private const string PhoneAlreadyRegisteredMessage =
        "Số điện thoại đã được đăng ký, vui lòng thử số điện thoại khác.";
    private const string BankAlreadyUsedMessage =
        "Tài khoản đã đăng ký nhưng ngân hàng này đã được sử dụng. TK/MK đã được xuất để bạn đăng nhập và đổi ngân hàng.";
    private static readonly SemaphoreSlim LlwinExecutionGate = new(2, 2);
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
        IBrowserConcurrencyGovernor? browserConcurrencyGovernor = null,
        CaptchaSolverOptions? captchaSolverOptions = null,
        IGeetestBrowserTokenService? browserGeetestTokenService = null,
        IImageCaptchaSolver? imageCaptchaSolver = null,
        IGgGeeGuardTokenService? ggGeeGuardTokenService = null,
        ISliderCaptchaSolver? sliderCaptchaSolver = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var cancellationTokenSource = BeginRun(cancellationToken);
        var executionContext = new AutomationExecutionContext(
            cancellationTokenSource.Token,
            _pauseEvent,
            imageCaptchaSolver,
            ggGeeGuardTokenService,
            sliderCaptchaSolver);

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
                browserConcurrencyGovernor,
                captchaSolverOptions,
                browserGeetestTokenService));
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
    IBrowserConcurrencyGovernor? browserConcurrencyGovernor,
    CaptchaSolverOptions? captchaSolverOptions,
    IGeetestBrowserTokenService? browserGeetestTokenService)
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
            browserConcurrencyGovernor,
            captchaSolverOptions,
            browserGeetestTokenService))
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
    IBrowserConcurrencyGovernor? browserConcurrencyGovernor,
    CaptchaSolverOptions? captchaSolverOptions,
    IGeetestBrowserTokenService? browserGeetestTokenService)
{
    var accountSlotAcquired = false;
    var slotAcquired = false;

    try
    {
        executionContext.Checkpoint();
        siteRun.RegistrationCompleted = false;
        siteRun.RequiresManualBankChange = false;
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
                    enableBrowserFallback,
                    captchaSolverOptions,
                    browserGeetestTokenService)
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
                    "Direct HTTP registration did not complete. Legacy browser fallback is disabled.");
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
                    ? $"Direct HTTP registration is not supported for {siteRun.Site}. Legacy browser fallback is disabled."
                    : "Direct HTTP registration is disabled and legacy browser fallback is disabled.");
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
            ProxyDeadMessage,
            onSiteCompleted);
        Debug.WriteLine($"Proxy preflight rejected configuration: {parseError ?? "proxy data is empty."}");
        return false;
    }

    if (proxyConfiguration.IsSocks)
    {
        MarkProxyVerificationFailed(
            account,
            siteRuns,
            ProxyDeadMessage,
            onSiteCompleted);
        Debug.WriteLine("Proxy preflight rejected SOCKS5 configuration.");
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
        Debug.WriteLine($"Proxy preflight failed: {connectivity.Message}");
        MarkProxyVerificationFailed(
            account,
            siteRuns,
            ProxyDeadMessage,
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
    bool enableBrowserFallback,
    CaptchaSolverOptions? captchaSolverOptions,
    IGeetestBrowserTokenService? browserGeetestTokenService)
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
                    executionContext.CancellationToken,
                    captchaSolverOptions,
                    browserGeetestTokenService,
                    executionContext.ImageCaptchaSolver,
                    executionContext.GgGeeGuardTokenService)
                .ConfigureAwait(false);
            registrationStopwatch.Stop();
            metrics.RegistrationMs += registrationStopwatch.ElapsedMilliseconds;
            metrics.VerificationMs += outcome.VerificationMs;
            metrics.VerificationProvider = outcome.VerificationProvider;

            if (outcome.Status == DirectRegistrationStatus.Success)
            {
                siteRun.RegistrationCompleted = true;
                if (outcome.Session is null)
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        "Tài khoản đã đăng ký nhưng  thể tự động liên kết ngân hàng. Vui lòng đăng nhập để kiểm tra.");
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
                    var bankAlreadyUsed = IsBankAccountAlreadyUsed(
                        bankOutcome.Message,
                        bankOutcome.RawResponse);
                    siteRun.RequiresManualBankChange = bankAlreadyUsed;
                    if (bankAlreadyUsed)
                    {
                        ResultWriter.AppendRegisteredAccount(account, siteRun.Site);
                    }

                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        bankAlreadyUsed
                            ? BankAlreadyUsedMessage
                            : "Tài khoản đã đăng ký nhưng chưa hoàn tất liên kết ngân hàng. Vui lòng đăng nhập để kiểm tra.");
                    handled = true;
                    return true;
                }

                if (OfficialAppLoginWorkflow.IsSupported(siteRun.Site))
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Phase1_RegDone,
                        "Direct bank setup completed. Confirming official PWA login...");
                    executionContext.Checkpoint();
                    var appLoginStopwatch = Stopwatch.StartNew();
                    var appLoginOutcome = await OfficialAppLoginWorkflow.TryLoginAsync(
                            account,
                            siteRun.Site,
                            executionContext.CancellationToken,
                            captchaSolverOptions,
                            browserGeetestTokenService)
                        .ConfigureAwait(false);
                    appLoginStopwatch.Stop();
                    metrics.RegistrationMs += appLoginStopwatch.ElapsedMilliseconds;

                    if (appLoginOutcome.Status != OfficialAppLoginStatus.Success)
                    {
                        AutomationUi.SetSiteStatus(
                            account,
                            siteRun,
                            AccountStatus.Failed,
                            $"Official PWA login failed: {appLoginOutcome.Message}");
                        handled = true;
                        return true;
                    }
                }

                ResultWriter.AppendSuccess(account, siteRun.Site);
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Success,
                    OfficialAppLoginWorkflow.IsSupported(siteRun.Site)
                        ? "Direct HTTP registration, bank setup, and official PWA login completed. Saved to success.txt."
                        : "Direct HTTP registration and bank setup completed. Saved to success.txt.");
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
                        BuildRegistrationFailureMessage(outcome));
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
                BuildRegistrationFailureMessage(outcome));
            handled = true;
            return true;
        }

        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Failed,
            "Tên đăng nhập đã tồn tại, vui lòng thử lại.");
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
    var llwinGateAcquired = false;
    var metrics = new SitePerformanceMetrics();
    var totalStopwatch = Stopwatch.StartNew();

    try
    {
        executionContext.Checkpoint();
        if (siteRun.Site == TargetSite.LLWIN)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Running,
                "Waiting for the LLWIN browser slot...");
            LlwinExecutionGate.Wait(executionContext.CancellationToken);
            llwinGateAcquired = true;
        }

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
            try
            {
                concurrencyLease?.Dispose();
            }
            finally
            {
                if (llwinGateAcquired)
                {
                    LlwinExecutionGate.Release();
                }
            }
            SitePerformanceMetricsWriter.Append(account, siteRun, metrics);

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
            // Browser startup is CPU/IO heavy. Gate only this creation phase so
            // already-running browsers can execute in parallel.
            browserStartupGate.Wait(cancellationToken);
            startupSlotAcquired = true;
        }

        if (site == TargetSite.LLWIN)
        {
            if (!ProxyConfigurationParser.TryParse(
                    account.Proxy,
                    out var proxyConfiguration,
                    out var proxyError))
            {
                throw new FormatException(proxyError ?? "LLWIN proxy configuration is invalid.");
            }

            var profileFolderPath = Path.Combine(
                Path.GetTempPath(),
                "BAOCODETOOL",
                "EdgeProfiles",
                Guid.NewGuid().ToString("N"));
            return DriverFactory.CreateEdgeDriver(profileFolderPath, proxyConfiguration);
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
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                BuildRegistrationFailureMessage(siteRun.LogMessage));

            return;
        }

        siteRun.RegistrationCompleted = true;

        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Phase1_RegDone,
            "Registration completed. Starting bank setup...");

        executionContext.Checkpoint();
        var bankSetupStopwatch = Stopwatch.StartNew();
        var bankSetupSucceeded = strategy.ExecuteBankSetup(driver, account, siteRun, executionContext);
        bankSetupStopwatch.Stop();
        metrics.BankSetupMs = bankSetupStopwatch.ElapsedMilliseconds;
        if (!bankSetupSucceeded)
        {
            var bankAlreadyUsed = IsBankAccountAlreadyUsed(siteRun.LogMessage);
            siteRun.RequiresManualBankChange = bankAlreadyUsed;
            if (bankAlreadyUsed)
            {
                ResultWriter.AppendRegisteredAccount(account, siteRun.Site);
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                bankAlreadyUsed
                    ? BankAlreadyUsedMessage
                    : "Tài khoản đã đăng ký nhưng chưa hoàn tất liên kết ngân hàng. Vui lòng đăng nhập để kiểm tra.");

            return;
        }

        if (OfficialAppLoginWorkflow.IsSupported(siteRun.Site))
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Phase1_RegDone,
                "Bank setup completed. Confirming official PWA login...");
            executionContext.Checkpoint();
            var appLoginOutcome = OfficialAppLoginWorkflow.TryLoginAsync(
                    account,
                    siteRun.Site,
                    executionContext.CancellationToken)
                .GetAwaiter()
                .GetResult();
            if (appLoginOutcome.Status != OfficialAppLoginStatus.Success)
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Failed,
                    $"Official PWA login failed: {appLoginOutcome.Message}");
                return;
            }
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
        Debug.WriteLine($"Browser workflow failed for {siteRun.Site}: {ex}");
        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Failed,
            siteRun.RegistrationCompleted
                ? "Tài khoản đã đăng ký nhưng chưa hoàn tất xử lý tự động. Vui lòng đăng nhập để kiểm tra."
                : "Đăng ký chưa hoàn tất, vui lòng thử lại.");
    }
}

    private static string BuildRegistrationFailureMessage(DirectRegistrationOutcome outcome)
    {
        var combined = $"{outcome.Message}\n{outcome.RawResponse}";
        return BuildRegistrationFailureMessage(combined, outcome.UsernameConflict);
    }

    private static string BuildRegistrationFailureMessage(
        string? message,
        bool usernameConflict = false)
    {
        var value = message ?? string.Empty;
        if (IsPhoneAlreadyRegistered(value))
        {
            return PhoneAlreadyRegisteredMessage;
        }

        if (ContainsAny(value, "proxy", "connection", "network", "timed out", "timeout"))
        {
            return ProxyDeadMessage;
        }

        if (ContainsAny(value, "captcha", "geetest", "verification"))
        {
            return "Xác minh chưa hoàn tất, vui lòng thử lại.";
        }

        if (usernameConflict)
        {
            return "Tên đăng nhập đã tồn tại, vui lòng thử lại.";
        }

        return "Đăng ký chưa hoàn tất, vui lòng thử lại.";
    }

    private static bool IsPhoneAlreadyRegistered(string value)
    {
        var identifiesPhone = ContainsAny(
            value,
            "phone",
            "mobile",
            "số điện thoại",
            "so dien thoai");
        var identifiesConflict = ContainsAny(
            value,
            "already",
            "exists",
            "registered",
            "used",
            "đã được đăng ký",
            "đã được đăng kí",
            "đã tồn tại");
        return identifiesPhone && identifiesConflict;
    }

    private static bool IsBankAccountAlreadyUsed(string? message, string? rawResponse = null)
    {
        var value = $"{message}\n{rawResponse}";
        return ContainsAny(
            value,
            "đã được liên kết bởi một thành viên khác",
            "tài khoản rút tiền đó đã được liên kết",
            "already linked by another",
            "already bound to another",
            "used by another member",
            "bank account already used",
            "bank card already used",
            "duplicate bank account");
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

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
