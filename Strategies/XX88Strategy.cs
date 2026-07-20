using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Threading.Tasks;
using VibecodeReg.Core;
using VibecodeReg.Models;

namespace VibecodeReg.Strategies;

public sealed class XX88Strategy : IRegistrationStrategy
{
    private const string TargetUrl = "http://gioithieu.xx88.fun/m/register?referralCode=gql2969";

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
            var genericInputs = wait.Until(d => {
                var els = d.FindElements(By.CssSelector(".form-page-container input.form-control:not(.form-mobileNum)"));
                return els.Count >= 3 ? els : null;
            });

            executionContext.Checkpoint();
            var usernameInput = genericInputs[0];
            usernameInput.SendKeysLikeHuman(driver, account.Username);

            executionContext.Checkpoint();
            var passwordInput = genericInputs[1];
            passwordInput.SendKeysLikeHuman(driver, account.Password);

            executionContext.Checkpoint();
            var fullNameInput = genericInputs[2];
            fullNameInput.SendKeysLikeHuman(driver, account.FullName);

            executionContext.Checkpoint();
            var formattedPhoneNumber = PhoneNumberFormatter.FormatTenDigitWithLeadingZero(account.PhoneNumber);
            var phoneNumberInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input.form-mobileNum")));
            phoneNumberInput.SendKeysLikeHuman(driver, formattedPhoneNumber);

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
                    "XX88",
                    preSubmitUrl,
                    TimeSpan.FromSeconds(15),
                    [By.CssSelector("input.form-control"), By.CssSelector("form")], // FormSelectors
                    [By.CssSelector(".registerModal")], // ModalSelectors
                    [
                        By.CssSelector(".errorMsg:not(.created)")
                    ], // FailureMessageSelectors
                    [
                        By.CssSelector(".register-success"), By.CssSelector(".am-navbar-title"), By.CssSelector(".am-toast-text"),
                        By.CssSelector(".toast_bg #toast.dialog-tips p"),
                        By.CssSelector("#toast.dialog-tips p")
                    ], // StatusMessageSelectors
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
