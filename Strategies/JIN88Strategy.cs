using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Threading.Tasks;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

public sealed class JIN88Strategy : IRegistrationStrategy
{
    private const string TargetUrl = "https://www.88jin88.com/m/register";

    public bool ExecuteBankSetup(IWebDriver driver, AccountModel account, SiteRunModel siteRun, AutomationExecutionContext executionContext) => false;

    public bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Navigating to JIN88 registration...");
        driver.Navigate().GoToUrl(TargetUrl);

        try
        {
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(40));

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Waiting for form to appear...");

            try {
                wait.Until(d => d.FindElements(By.CssSelector("input.form-control, input")).Count >= 3);
            } catch (WebDriverTimeoutException) { }

            // Chờ thêm 2 giây để các framework (Vue/React) load xong hoàn toàn sự kiện (Hydration)
            executionContext.Delay(TimeSpan.FromSeconds(2));

            executionContext.Checkpoint();
            
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Injecting data via JS (Max Speed)...");
            var js = (IJavaScriptExecutor)driver;
            
            string jsSnippet = $@"
                try {{
                    // Xóa popup ngáng đường
                    document.querySelectorAll('.van-overlay, .modal-backdrop, .overlay').forEach(e => e.remove());
                    document.querySelectorAll('button, div, span, a').forEach(el => {{
                        if (el.innerText && (el.innerText.trim() === 'Huỷ' || el.innerText.trim() === 'Cancel')) {{
                            el.click();
                        }}
                    }});

                    function setVal(sel, val) {{
                        var el = document.querySelector(sel);
                        if(el) {{
                            el.value = val;
                            el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                            el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                        }}
                    }}
                    
                    setVal(""input[ng-model='formData.member']"", '{account.GetUsername(siteRun.Site)}');
                    setVal(""input[ng-model='formData.password']"", '{account.GetPassword(siteRun.Site)}');
                    setVal(""input[ng-model='formData.name']"", '{account.FullName}');
                    setVal(""input[ng-model='formData.phone']"", '{PhoneNumberFormatter.FormatTenDigitWithLeadingZero(account.PhoneNumber)}');
                }} catch (e) {{ }}
            ";
            
            js.ExecuteScript(jsSnippet);

            // Wait for user to complete any captchas manually
            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Waiting for manual Captcha entry...");
            
            try 
            {
                var captchaInput = driver.FindElement(By.CssSelector("input[ng-model='formData.yzm']"));
                wait.Until(d => captchaInput.GetAttribute("value")?.Length >= 4);
            }
            catch (NoSuchElementException)
            {
                // Captcha might not be present or already solved
            }

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Submitting registration form...");
            
            var submitButton = wait.Until(ExpectedConditions.ElementExists(By.XPath("//button[contains(@ng-click, 'submit()') or contains(@class, 'login-btn')]")));
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

            var outcome = RegistrationResultWatcher.AggressiveAssumeSuccess(driver, executionContext);
            return RegistrationResultWatcher.ReportOutcome(account, siteRun, outcome);
        }
        catch (Exception ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Error: {ex.Message}");
            return false;
        }
    }
}
