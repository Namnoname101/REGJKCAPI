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
            .SelectMany(account => account.SiteRuns
                .Where(siteRun => siteRun.IsSelected
                    && siteRun.Status != AccountStatus.Running
                    && siteRun.Status != AccountStatus.Success)
                .Select(siteRun => new SiteRunJob(account, siteRun)))
            .ToList();

        if (jobs.Count == 0)
        {
            return;
        }

        maxThreads = Math.Max(1, maxThreads);

        using var semaphore = new SemaphoreSlim(maxThreads);
        var tasks = jobs.Select(job => RunJobAsync(job, semaphore, executionContext, onSiteCompleted));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    finally
    {
        EndRun(cancellationTokenSource);
    }
}

private async Task RunJobAsync(
    SiteRunJob job,
    SemaphoreSlim semaphore,
    AutomationExecutionContext executionContext,
    Action? onSiteCompleted)
{
    var slotAcquired = false;

    try
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(job.Account, job.SiteRun, AccountStatus.Running, "Queued. Waiting for browser slot...");

        await semaphore.WaitAsync(executionContext.CancellationToken).ConfigureAwait(false);
        slotAcquired = true;

        executionContext.Checkpoint();
        await Task.Run(
                () => RunSingleRegistration(job.Account, job.SiteRun, executionContext),
                executionContext.CancellationToken)
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (executionContext.CancellationToken.IsCancellationRequested)
    {
        AutomationUi.SetSiteStatus(job.Account, job.SiteRun, AccountStatus.Stopped, "Stopped by operator.");
    }
    finally
    {
        if (slotAcquired)
        {
            semaphore.Release();
        }
        onSiteCompleted?.Invoke();
    }
}

    private static void RunSingleRegistration(
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        try
        {
            executionContext.Checkpoint();
            var strategy = StrategyFactory.Create(siteRun.Site);

            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Running, "Launching isolated browser profile...");
            executionContext.Checkpoint();
            using IWebDriver driver = DriverFactory.CreateDriver(account, siteRun.Site);

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

    private sealed record SiteRunJob(AccountModel Account, SiteRunModel SiteRun);
}
