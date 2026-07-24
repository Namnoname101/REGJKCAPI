using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Threading.Tasks;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

public sealed class MMOOStrategy : IRegistrationStrategy
{
    private const string TargetUrl = "https://m.mmoo.team/?r=QWP70O&app=1";

    public bool ExecuteBankSetup(IWebDriver driver, AccountModel account, SiteRunModel siteRun, AutomationExecutionContext executionContext) => false;

    public bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Opening MMOO landing page...");
        driver.Navigate().GoToUrl(TargetUrl);

        try
        {
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(60));

            // Fast loop: try to close popup and click Register simultaneously until form appears
            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Bypass popups & opening registration form...");
            
            for (int i = 0; i < 30; i++)
            {
                // Try to close popup
                try
                {
                    var closeBtns = driver.FindElements(By.XPath("//*[@translate='Common_Closed' or contains(@ng-click, 'ok')] | //div[contains(text(), 'Đóng')] | //button[contains(text(), 'Đóng')]"));
                    foreach (var btn in closeBtns)
                    {
                        try { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", btn); } catch { }
                    }
                }
                catch { }

                // Try to click Register button
                try
                {
                    var btns = driver.FindElements(By.XPath("//button[contains(@routerlink, 'Register') or contains(@translate, 'CheckIn') or contains(@class, 'btn-reg') or contains(@translate, 'Register') or contains(@ng-click, 'openRegister')] | //a[contains(@routerlink, 'Register') or contains(@class, 'btn-reg')]"));
                    foreach (var b in btns)
                    {
                        if (b.Text != null && b.Text.Contains("trải nghiệm", StringComparison.OrdinalIgnoreCase)) continue;
                        try { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", b); } catch { }
                    }
                }
                catch { }

                // Check if form appeared
                try
                {
                    if (driver.FindElements(By.CssSelector("input[ng-model='$ctrl.user.account.value'], input[formcontrolname='account'], input[type='password']")).Count >= 2)
                    {
                        break; // Form opened!
                    }
                }
                catch { }
                
                System.Threading.Thread.Sleep(500);
            }

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Waiting for form to appear...");

            var usernameInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input[ng-model='$ctrl.user.account.value'], input[formcontrolname='account'], input[placeholder*='2-15'], input[placeholder*='chữ cái']")));
            usernameInput.SendKeysLikeHuman(driver, account.GetUsername(siteRun.Site));

            executionContext.Checkpoint();
            var passwordInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input[ng-model='$ctrl.user.password.value'], input[formcontrolname='password'], input[placeholder*='6 ký tự'], input[type='password']:first-of-type")));
            passwordInput.SendKeysLikeHuman(driver, account.GetPassword(siteRun.Site));

            executionContext.Checkpoint();
            var confirmPasswordInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input[ng-model='$ctrl.user.confirmPassword.value'], input[formcontrolname='confirmPassword'], input[placeholder*='xác nhận lại'], input[placeholder*='nhập lại']")));
            confirmPasswordInput.SendKeysLikeHuman(driver, account.Password);

            executionContext.Checkpoint();
            var moneyPasswordInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input[ng-model='$ctrl.user.moneyPassword.value'], input[formcontrolname='moneyPassword'], input[placeholder*='Rút Tiền'], input[placeholder*='rút tiền']")));
            moneyPasswordInput.SendKeysLikeHuman(driver, account.Password);

            executionContext.Checkpoint();
            var fullNameInput = wait.Until(ExpectedConditions.ElementToBeClickable(
                By.CssSelector("input[ng-model='$ctrl.user.name.value'], input[formcontrolname='name'], input[placeholder*='tài khoản ngân hàng'], input[placeholder*='Họ Tên']")));
            fullNameInput.SendKeysLikeHuman(driver, account.FullName);

            executionContext.Checkpoint();
            try
            {
                var termsCheckbox = driver.FindElement(By.CssSelector("input[type='checkbox'][ng-model='$ctrl.user.agree.value'], input[type='checkbox'][formcontrolname='agree']"));
                if (!termsCheckbox.Selected)
                {
                    ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", termsCheckbox);
                }
            }
            catch { /* Ignore */ }

            // Handle Captcha if present
            executionContext.Checkpoint();
            try
            {
                var captchaInput = driver.FindElement(By.CssSelector("input[ng-model='$ctrl.code'], input[formcontrolname='code'], input[placeholder*='xác minh']"));
                if (captchaInput.Displayed)
                {
                    AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Vui lòng nhập mã xác minh (Captcha) bằng tay...");
                    wait.Until(d => captchaInput.GetAttribute("value")?.Length >= 4);
                }
            }
            catch (NoSuchElementException)
            {
                // Captcha might not be present or already solved
            }

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Submitting registration form...");
            
            var submitButton = driver.FindElement(By.XPath("//button[contains(@ng-click, 'submit()') or contains(@class, 'reg') or contains(@translate, 'Register')]"));
            try 
            {
                submitButton.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", submitButton);
            }

            // Wait for result
            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Waiting for registration response...");

            var options = new RegistrationResultWatchOptions(
                "MMOO",
                driver.Url,
                TimeSpan.FromSeconds(15),
                Array.Empty<By>(),
                Array.Empty<By>(),
                new[] { By.XPath("//*[contains(text(), 'không khả dụng') or contains(text(), 'chăm sóc') or contains(text(), 'đã tồn tại')]") },
                new[] { By.XPath("//*[contains(text(), 'thành công') or contains(text(), 'success')]") },
                Array.Empty<By>()
            );
            var outcome = RegistrationResultWatcher.WaitForResult(driver, executionContext, options);
            return RegistrationResultWatcher.ReportOutcome(account, siteRun, outcome);
        }
        catch (Exception ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Error: {ex.Message}");
            return false;
        }
    }
}
