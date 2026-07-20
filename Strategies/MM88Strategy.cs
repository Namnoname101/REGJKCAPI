using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Threading.Tasks;
using VibecodeReg.Core;
using VibecodeReg.Models;

namespace VibecodeReg.Strategies;

public sealed class MM88Strategy : IRegistrationStrategy
{
    private const string TargetUrl = "http://gioithieu.mm88p.com/m/register?referralCode=mgr0794";

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
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(20));

            executionContext.Checkpoint();
            wait.Until(ExpectedConditions.ElementIsVisible(By.CssSelector("div.form-group:nth-child(1) > div:nth-child(1) > input:nth-child(1)")));

            executionContext.Checkpoint();
            var usernameInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("div.form-group:nth-child(1) > div:nth-child(1) > input:nth-child(1)")));
            usernameInput.SendKeysLikeHuman(driver, account.Username);

            executionContext.Checkpoint();
            var passwordInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("div.form-group:nth-child(2) > div:nth-child(1) > input:nth-child(1)")));
            passwordInput.SendKeysLikeHuman(driver, account.Password);

            executionContext.Checkpoint();
            var formattedPhoneNumber = PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber);
            var phoneNumberInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input.form-control:nth-child(2)")));
            phoneNumberInput.SendKeysLikeHuman(driver, formattedPhoneNumber);

            executionContext.Checkpoint();
            var fullNameInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("div.form-group:nth-child(3) > div:nth-child(1) > input:nth-child(1)")));
            fullNameInput.SendKeysLikeHuman(driver, account.FullName);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Submitting registration form...");
            var preSubmitUrl = driver.Url;
            wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector(".submit-btn"))).ClickLikeHuman(driver);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Verifying registration result...");
            var outcome = RegistrationResultWatcher.WaitForResult(
                driver,
                executionContext,
                new RegistrationResultWatchOptions(
                    "MM88",
                    preSubmitUrl,
                    TimeSpan.FromSeconds(15),
                    [By.CssSelector("input.form-control"), By.CssSelector("form")], // FormSelectors
                    [By.CssSelector(".registerModal")], // ModalSelectors
                    [
                        By.CssSelector(".errorMsg:not(.created)")
                    ], // FailureMessageSelectors
                    [By.CssSelector(".register-success"), By.CssSelector(".am-navbar-title"), By.CssSelector(".am-toast-text")], // StatusMessageSelectors
                    [By.CssSelector("form .is-invalid"), By.CssSelector("form .error")])); // InvalidFieldSelectors

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
