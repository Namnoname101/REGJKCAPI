using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VibecodeReg.Models;
using VibecodeReg.Strategies;

namespace VibecodeReg.Core;

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
        var jobs = profiles
            .Where(account => account.SiteRuns.Any(sr => sr.IsSelected && sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Phase1_RegDone))
            .ToList();

        if (jobs.Count == 0)
        {
            return;
        }

        maxThreads = Math.Max(1, maxThreads);

        using var semaphore = new SemaphoreSlim(maxThreads);
        var tasks = jobs.Select(account => RunJobAsync(account, semaphore, executionContext, onSiteCompleted));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    finally
    {
        EndRun(cancellationTokenSource);
    }
}

private async Task RunJobAsync(
    AccountModel account,
    SemaphoreSlim semaphore,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted)
{
    var slotAcquired = false;
    var pendingSites = account.SiteRuns
        .Where(sr => sr.IsSelected && sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Phase1_RegDone)
        .ToList();

    try
    {
        executionContext.Checkpoint();
        foreach (var sr in pendingSites)
        {
            AutomationUi.SetSiteStatus(account, sr, AccountStatus.Running, "Queued. Waiting for browser slot...");
        }

        await semaphore.WaitAsync(executionContext.CancellationToken).ConfigureAwait(false);
        slotAcquired = true;

        executionContext.Checkpoint();
        await Task.Run(
                () => RunAccountRegistration(account, executionContext, onSiteCompleted),
                executionContext.CancellationToken)
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (executionContext.CancellationToken.IsCancellationRequested)
    {
        foreach (var sr in pendingSites)
        {
            if (sr.Status == AccountStatus.Running || sr.Status == AccountStatus.Ready)
            {
                AutomationUi.SetSiteStatus(account, sr, AccountStatus.Stopped, "Stopped by operator.");
            }
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

private static void RunAccountRegistration(
    AccountModel account,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted)
{
    var siteRunsToRun = account.SiteRuns
        .Where(sr => sr.IsSelected && sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Phase1_RegDone)
        .ToList();

    if (siteRunsToRun.Count == 0) return;

    try
    {
        executionContext.Checkpoint();
        foreach (var sr in siteRunsToRun)
        {
            AutomationUi.SetSiteStatus(account, sr, AccountStatus.Running, "Launching browser profile...");
        }

        using IWebDriver driver = DriverFactory.CreateDriver(account);

        bool isFirstTab = true;

        foreach (var siteRun in siteRunsToRun)
        {
            try
            {
                if (executionContext.CancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(executionContext.CancellationToken);
                }

                if (!isFirstTab)
                {
                    AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Running, "Opening new tab...");
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());
                }
                isFirstTab = false;

                RunSingleSiteInBrowser(driver, account, siteRun, executionContext);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            finally
            {
                onSiteCompleted?.Invoke();
            }
        }
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        foreach (var sr in siteRunsToRun.Where(s => s.Status == AccountStatus.Running || s.Status == AccountStatus.Ready))
        {
            AutomationUi.SetSiteStatus(account, sr, AccountStatus.Failed, $"Browser error: {ex.Message}");
        }
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

        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Executing registration phase...");
        executionContext.Checkpoint();
        var registrationSucceeded = strategy.ExecuteRegistration(driver, account, siteRun, executionContext);
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
