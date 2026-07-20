using OpenQA.Selenium;
using VibecodeReg.Core;
using VibecodeReg.Models;

namespace VibecodeReg.Strategies;

public interface ISiteAutomationStrategy
{
    bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext);

    bool ExecuteBankSetup(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext);
}
