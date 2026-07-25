using System;
using System.Linq;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public static class AutomationUi
{
    public static event EventHandler<AutomationStatusChangedEventArgs>? StatusChanged;

    public static void SetStatus(AccountModel account, AccountStatus? status = null, string? logMessage = null)
    {
        if (status.HasValue)
        {
            account.Status = status.Value;
        }

        if (logMessage is not null)
        {
            account.LogMessage = logMessage;
        }

        StatusChanged?.Invoke(null, new AutomationStatusChangedEventArgs
        {
            Account = account,
            Status = status,
            Message = logMessage
        });
    }

    public static void SetSiteStatus(
        AccountModel account,
        SiteRunModel siteRun,
        AccountStatus? status = null,
        string? logMessage = null)
    {
        if (logMessage is not null)
        {
            siteRun.LogMessage = logMessage;
            account.LogMessage = $"{siteRun.Site}: {logMessage}";
        }

        if (status.HasValue)
        {
            siteRun.Status = status.Value;
        }

        UpdateAccountStatus(account);

        StatusChanged?.Invoke(null, new AutomationStatusChangedEventArgs
        {
            Account = account,
            SiteRun = siteRun,
            Status = status,
            Message = logMessage
        });
    }

    public static void UpdateAccountStatus(AccountModel account)
    {
        var selectedRuns = account.SiteRuns.Where(siteRun => siteRun.IsSelected).ToList();

        if (selectedRuns.Count == 0)
        {
            account.Status = AccountStatus.Ready;
            account.LogMessage = "No site selected.";
            return;
        }

        if (selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Running))
        {
            account.Status = AccountStatus.Running;
            return;
        }

        if (selectedRuns.All(siteRun => siteRun.Status == AccountStatus.Success))
        {
            account.Status = AccountStatus.Success;
            account.LogMessage = "All selected sites completed successfully.";
            return;
        }

        if (selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Failed))
        {
            account.Status = AccountStatus.Failed;
            account.LogMessage = "One or more selected sites failed.";
            return;
        }

        if (selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Stopped))
        {
            account.Status = AccountStatus.Stopped;
            account.LogMessage = "Execution stopped by operator.";
            return;
        }

        if (selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Phase1_RegDone))
        {
            account.Status = AccountStatus.Phase1_RegDone;
            return;
        }

        account.Status = AccountStatus.Ready;
    }
}
