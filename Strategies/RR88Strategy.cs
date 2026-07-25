using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;

namespace BAOCODETOOL.Strategies;

public sealed class RR88Strategy : IRegistrationStrategy
{
    private const string TargetUrl = "http://gioithieu.rr3311.com/m/register?referralCode=mrh6472";
    private const string BankSetupUrl = "https://gioithieu.rr3311.com/m/securityCenter/addBankCard?type=B";

    public bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        try
        {
            if (!FastRegistrationWorkflow.FillAndSubmit(
                    driver,
                    account,
                    siteRun,
                    executionContext,
                    TargetUrl,
                    PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber),
                    FastRegistrationFallbackOrder.PhoneThenFullName))
            {
                return false;
            }

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Verifying registration result...");
            var outcome = RegistrationResultWatcher.AggressiveAssumeSuccess(driver, executionContext);

            return RegistrationResultWatcher.ReportOutcome(account, siteRun, outcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebDriverTimeoutException)
        {
            return false;
        }
        catch (NoSuchElementException)
        {
            return false;
        }
        catch (ElementClickInterceptedException)
        {
            return false;
        }
        catch (ElementNotInteractableException)
        {
            return false;
        }
        catch (StaleElementReferenceException)
        {
            return false;
        }
    }

    public bool ExecuteBankSetup(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Preparing bank setup...");

        return BankSetupWorkflow.Execute(driver, account, siteRun, executionContext, BankSetupUrl);
    }
}
