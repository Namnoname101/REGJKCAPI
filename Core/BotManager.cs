using OpenQA.Selenium;
using System;
using System.Collections.Generic;
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

    public async Task RunMassRegistrationAsync(IEnumerable<AccountModel> profiles, int maxThreads, Action? onSiteCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var cancellationTokenSource = BeginRun();
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

        using var semaphore = new SemaphoreSlim(maxThreads);
        var tasks = new List<Task>();
        
        foreach (var account in activeAccounts)
        {
            tasks.Add(RunAccountSitesConcurrentlyAsync(account, semaphore, executionContext, onSiteCompleted));
            
            try 
            {
                // Delay 4s between starting each account to stagger browser launches
                await Task.Delay(TimeSpan.FromSeconds(4), executionContext.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted)
{
    var siteRuns = account.SiteRuns
        .Where(sr => sr.IsSelected && sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Phase1_RegDone)
        .ToList();

    foreach (var siteRun in siteRuns)
    {
        if (executionContext.CancellationToken.IsCancellationRequested) break;
        await RunJobAsync(account, siteRun, semaphore, executionContext, onSiteCompleted);
    }
}

private async Task RunJobAsync(
    AccountModel account,
    SiteRunModel siteRun,
    SemaphoreSlim semaphore,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted)
{
    var slotAcquired = false;

    try
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Running, "Queued. Waiting for browser slot...");

        await semaphore.WaitAsync(executionContext.CancellationToken).ConfigureAwait(false);
        slotAcquired = true;

        executionContext.Checkpoint();
        await Task.Run(
                () => RunSingleSiteExecution(account, siteRun, executionContext, onSiteCompleted),
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
    }
}

private static void RunSingleSiteExecution(
    AccountModel account,
    SiteRunModel siteRun,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted)
{
    try
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Running, "Launching browser profile...");

        using IWebDriver driver = DriverFactory.CreateDriver(account, siteRun.Site);

        RunSingleSiteInBrowser(driver, account, siteRun, executionContext);
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
        onSiteCompleted?.Invoke();
    }
}

private static void RunSingleSiteInBrowser(
    IWebDriver driver,
    AccountModel account,
    SiteRunModel siteRun,
    AutomationExecutionContext executionContext)
{
    try
    {
        executionContext.Checkpoint();
        var strategy = StrategyFactory.Create(siteRun.Site);

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
                var rng = new Random();
                var newUsername = account.GetUsername(siteRun.Site) + rng.Next(10, 99).ToString();
                account.SpecificUsernames[siteRun.Site] = newUsername;
                
                AutomationUi.SetSiteStatus(account, siteRun, logMessage: $"Username existed. Retrying with {newUsername}...");
                try { driver.Navigate().Refresh(); } catch { }
                executionContext.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            // Lỗi khác thì thoát luôn
            break;
        }

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
        var bankSetupSucceeded = strategy.ExecuteBankSetup(driver, account, siteRun, executionContext);
        if (!bankSetupSucceeded)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, "Bank setup phase failed.");
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

    private CancellationTokenSource BeginRun()
    {
        lock (_runLock)
        {
            if (_cts is not null)
            {
                throw new InvalidOperationException("Automation is already running.");
            }

            _cts = new CancellationTokenSource();
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
