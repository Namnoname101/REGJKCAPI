using OpenQA.Selenium;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

public sealed class LLWINStrategy : ISiteAutomationStrategy
{
    private const string TargetUrl = "https://llwin.app/home/register?id=924999213";

    public bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        return LobbyMobileWorkflow.ExecuteRegistration(
            driver,
            account,
            siteRun,
            executionContext,
            TargetUrl,
            PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber));
    }

    public bool ExecuteBankSetup(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            logMessage: "Using the authenticated LLWIN session for direct bank setup...");

        var outcome = LlwinBrowserBankWorkflow.SetupAsync(
                driver,
                account,
                executionContext.CancellationToken)
            .GetAwaiter()
            .GetResult();
        if (outcome.Status == DirectBankSetupStatus.Success)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: outcome.Message);
            return true;
        }

        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Failed,
            outcome.Message);
        return false;
    }
}
