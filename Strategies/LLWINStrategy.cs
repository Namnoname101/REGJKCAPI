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
        return LobbyMobileWorkflow.ExecuteBankSetup(
            driver,
            account,
            siteRun,
            executionContext);
    }
}
