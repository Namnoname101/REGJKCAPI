using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Threading.Tasks;
using VibecodeReg.Core;
using VibecodeReg.Models;

namespace VibecodeReg.Strategies;

public sealed class RR88Strategy : IRegistrationStrategy
{
    private const string TargetUrl = "http://gioithieu.rr3311.com/m/register?referralCode=mrh6472";

    public bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Opening registration page...");
        driver.Navigate().GoToUrl(TargetUrl);

        try
        {
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(60));

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Waiting for form to appear...");

            wait.Until(ExpectedConditions.ElementIsVisible(
                By.CssSelector("input.form-control.username")));

            executionContext.Checkpoint();
            var usernameInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input.form-control.username")));
            usernameInput.SendKeysLikeHuman(driver, account.Username);

            executionContext.Checkpoint();
            var passwordInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input.form-control[type='password']")));
            passwordInput.SendKeysLikeHuman(driver, account.Password);

            executionContext.Checkpoint();
            var formattedPhoneNumber = PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber);
            var phoneNumberInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input.form-control.form-mobileNum")));
            phoneNumberInput.SendKeysLikeHuman(driver, formattedPhoneNumber);

            executionContext.Checkpoint();
            var fullNameInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input.form-control.reg-payeeName")));
            fullNameInput.SendKeysLikeHuman(driver, account.FullName);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Submitting registration form...");
            var preSubmitUrl = driver.Url;
            wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("button.submit-btn"))).ClickLikeHuman(driver);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Verifying registration result...");
            var outcome = RegistrationResultWatcher.WaitForResult(
                driver,
                executionContext,
                new RegistrationResultWatchOptions(
                    "RR88",
                    preSubmitUrl,
                    TimeSpan.FromSeconds(15),
                    [By.CssSelector("input.form-control.username"), By.CssSelector("form")], // FormSelectors
                    [By.CssSelector(".registerModal")], // ModalSelectors
                    [
                        By.CssSelector(".errorMsg:not(.created)")
                    ], // FailureMessageSelectors
                    [By.CssSelector(".register-success"), By.CssSelector(".am-navbar-title"), By.CssSelector(".am-toast-text")], // StatusMessageSelectors
                    [By.CssSelector("form .is-invalid"), By.CssSelector("form .error")]) // InvalidFieldSelectors
                );

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

        return BankSetupWorkflow.Execute(driver, account, siteRun, executionContext);
    }
}
