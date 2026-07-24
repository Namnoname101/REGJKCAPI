using OpenQA.Selenium;
using System;
using System.Threading.Tasks;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

public sealed class GG88Strategy : ISiteAutomationStrategy
{
    private const string TargetUrl = "https://www.gg88v.com/?id=456510325&fixed.iswebclip=2";

    public bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Opening registration page...");
        driver.Navigate().GoToUrl(TargetUrl);

        // TODO: Fill GG88 registration fields after the target HTML DOM is available.
        executionContext.Delay(TimeSpan.FromSeconds(2));
        return true;
    }

    public bool ExecuteBankSetup(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Preparing bank setup...");

        // TODO: Fill GG88 bank setup fields after the target HTML DOM is available.
        executionContext.Delay(TimeSpan.FromSeconds(2));
        return true;
    }
}
