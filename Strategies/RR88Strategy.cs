using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Threading.Tasks;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

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
            var preSubmitUrl = driver.Url;
            
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Injecting data via JS (Max Speed)...");
            var js = (IJavaScriptExecutor)driver;
            
            string jsSnippet = $@"
                var maxAttempts = 150;
                var attempts = 0;
                var t = setInterval(function() {{
                    attempts++;
                    // Xóa popup ngáng đường
                    document.querySelectorAll('.van-overlay, .modal-backdrop, .overlay').forEach(e => e.remove());
                    document.querySelectorAll('button, div, span, a').forEach(el => {{
                        if (el.innerText && (el.innerText.trim() === 'Huỷ' || el.innerText.trim() === 'Cancel')) {{
                            el.click();
                        }}
                    }});

                    function sv(el, val) {{
                        if(el) {{
                            el.value = val;
                            el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                            el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                            el.dispatchEvent(new Event('blur', {{ bubbles: true }}));
                        }}
                    }}
                    
                    var inputs = Array.from(document.querySelectorAll('input')).filter(i => i.type !== 'hidden' && i.type !== 'checkbox' && i.type !== 'radio' && i.offsetWidth > 0);
                    var btn = document.querySelector('button.submit-btn, button[type=""submit""]');
                    
                    // RR88 thường có 4 ô
                    if(inputs.length >= 3 && btn) {{
                        clearInterval(t);
                        
                        sv(inputs[0], '{account.GetUsername(siteRun.Site)}');
                        sv(inputs[1], '{account.GetPassword(siteRun.Site)}');
                        
                        // Tìm ô Phone
                        var phoneEl = document.querySelector('.form-mobileNum, input[type=""tel""]');
                        if (phoneEl) {{
                            sv(phoneEl, '{PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber)}');
                            var otherInputs = inputs.filter(i => i !== inputs[0] && i !== inputs[1] && i !== phoneEl);
                            if (otherInputs.length > 0) sv(otherInputs[0], '{account.FullName}');
                        }} else {{
                            if (inputs.length >= 4) {{
                                sv(inputs[2], '{PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber)}');
                                sv(inputs[3], '{account.FullName}');
                            }} else {{
                                sv(inputs[2], '{PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber)}');
                            }}
                        }}
                        
                        setTimeout(function() {{ btn.click(); }}, 300);
                    }}
                    
                    if(attempts >= maxAttempts) clearInterval(t);
                }}, 100);
            ";
            
            js.ExecuteScript(jsSnippet);

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

        return BankSetupWorkflow.Execute(driver, account, siteRun, executionContext);
    }
}
