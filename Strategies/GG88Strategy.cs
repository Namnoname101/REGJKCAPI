using OpenQA.Selenium;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

public sealed class GG88Strategy : ISiteAutomationStrategy
{
    private const string TargetUrl = "https://www.gg88v.com/home/register?id=456510325";

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
