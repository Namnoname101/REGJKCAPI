using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VibecodeReg.Core;
using VibecodeReg.Models;

namespace VibecodeReg.Strategies;

internal static class BankSetupWorkflow
{
    private static readonly By[] PopupCloseSelectors =
    [
        By.CssSelector(".am-navbar-title.close-btn"),
        By.CssSelector(".am-navbar-title"),
        By.CssSelector(".deposit-close"),
        By.CssSelector(".deposit-guide-close"),
        By.CssSelector(".modal-close"),
        By.CssSelector(".popup_modal_close"),
        By.CssSelector(".close"),
        By.CssSelector(".am-icon-cross"),
        By.CssSelector(".dialog-close"),
        By.CssSelector(".van-icon-cross"),
        By.CssSelector(".home-popup-close"),
        By.CssSelector(".modal-close-btn"),
        By.CssSelector(".close-btn"),
        By.CssSelector(".dialog-tips-close")
    ];

    private static readonly By[] FooterNavigationSelectors =
    [
        By.CssSelector("div.footer-menu-item:nth-child(5)"),
        By.CssSelector(".footer-menu-item:nth-child(5)"),
        By.CssSelector("a.footer-menu-item:last-child"),
        By.CssSelector("a.footer-item:last-child"),
        By.CssSelector(".footer-menu-item:last-child")
    ];

    private static readonly By[] SecondaryNavigationSelectors =
    [
        By.CssSelector("a.footer-item:nth-child(2)"),
        By.CssSelector(".default-member-footer a.footer-item:nth-child(2)"),
        By.CssSelector(".footer-item:nth-child(2)")
    ];

    private static readonly By[] AddBankSelectors =
    [
        By.CssSelector(".withdraw-bkadd"),
        By.CssSelector(".bank-add"),
        By.CssSelector(".add-bank")
    ];

    private static readonly By[] BankDropdownSelectors =
    [
        By.CssSelector(".select-bank-type .inputCon"),
        By.CssSelector(".inputCon")
    ];

    private static readonly By[] BankListItemSelectors =
    [
        By.CssSelector(".popUps-list-item")
    ];

    private static readonly By[] AccountNumberSelectors =
    [
        By.CssSelector("div.account-form-group:nth-child(2) > div:nth-child(1) > input:nth-child(2)"),
        By.CssSelector(".account-form-group:nth-child(2) input:nth-of-type(2)"),
        By.CssSelector(".account-form-group:nth-child(2) input")
    ];

    private static readonly By[] BranchSelectors =
    [
        By.CssSelector("div.account-form-group:nth-child(3) > div:nth-child(1) > input:nth-child(2)"),
        By.CssSelector(".account-form-group:nth-child(3) input:nth-of-type(2)"),
        By.CssSelector(".account-form-group:nth-child(3) input")
    ];

    private static readonly By[] PasswordSelectors =
    [
        By.CssSelector(".payment-password input"),
        By.CssSelector("div.mc-account-list:nth-child(2) > input:nth-child(2)"),
        By.CssSelector("div.account-form-group:nth-child(5) > div:nth-child(1) > input:nth-child(2)"),
        By.CssSelector("input[type='password']")
    ];

    private static readonly By[] SubmitSelectors =
    [
        By.CssSelector("span.am-button"),
        By.CssSelector(".am-button"),
        By.CssSelector("button")
    ];

    public static bool Execute(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(siteRun);
        ArgumentNullException.ThrowIfNull(executionContext);

        var bankName = account.BankName.Trim();
        var bankAccount = account.AccountNumber.Trim();
        var branch = account.Branch.Trim();
        var withdrawPassword = account.Level2Password.Trim();

        if (string.IsNullOrWhiteSpace(bankName)
            || string.IsNullOrWhiteSpace(bankAccount)
            || string.IsNullOrWhiteSpace(withdrawPassword))
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                "Bank setup data missing. Required: Bank, Account Number, Level 2 Password.");
            return false;
        }

        try
        {
            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Closing post-registration popups...");
            CloseOptionalPopups(driver, executionContext);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Navigating to bank setup...");
            NavigateToBankSetup(driver, executionContext);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Opening bank selector...");
            ClickAnyVisible(driver, AddBankSelectors, TimeSpan.FromSeconds(20), required: true);
            executionContext.Delay(TimeSpan.FromMilliseconds(700));
            ClickAnyVisible(driver, BankDropdownSelectors, TimeSpan.FromSeconds(15), required: true);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Selecting bank...");
            ClickBankName(driver, bankName, TimeSpan.FromSeconds(15));

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Filling bank details...");
            FillBankAccountAndBranch(driver, bankAccount, branch, executionContext);
            FillWithdrawPassword(driver, withdrawPassword, executionContext);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Submitting bank setup...");
            ClickSubmitButton(driver, TimeSpan.FromSeconds(15));

            AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Bank setup submitted. Waiting for final result flow...");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebDriverTimeoutException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup timeout: {ex.Message}");
            return false;
        }
        catch (NoSuchElementException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup element missing: {ex.Message}");
            return false;
        }
        catch (ElementClickInterceptedException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup click blocked: {ex.Message}");
            return false;
        }
        catch (ElementNotInteractableException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup element not interactable: {ex.Message}");
            return false;
        }
        catch (StaleElementReferenceException ex)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Bank setup DOM changed while interacting: {ex.Message}");
            return false;
        }
    }

    private static void CloseOptionalPopups(IWebDriver driver, AutomationExecutionContext executionContext)
    {
        executionContext.Delay(TimeSpan.FromSeconds(3));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            executionContext.Checkpoint();

            if (!ClickAnyVisible(driver, PopupCloseSelectors, TimeSpan.FromSeconds(2), required: false))
            {
                // Wait another short period just in case another popup is queued
                executionContext.Delay(TimeSpan.FromSeconds(1));
                if (attempt == 0 && !ClickAnyVisible(driver, PopupCloseSelectors, TimeSpan.FromSeconds(2), required: false))
                {
                    return;
                }
            }

            executionContext.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    private static void NavigateToBankSetup(IWebDriver driver, AutomationExecutionContext executionContext)
    {
        if (!ClickBestTextMatch(
                driver,
                [By.CssSelector(".footer-menu-item"), By.CssSelector("a.footer-item"), By.CssSelector(".footer-item")],
                ["rut", "withdraw", "bank", "vi", "wallet", "tai khoan", "account", "mine", "me", "cua toi"],
                TimeSpan.FromSeconds(8)))
        {
            ClickAnyVisible(driver, FooterNavigationSelectors, TimeSpan.FromSeconds(8), required: false);
        }

        executionContext.Delay(TimeSpan.FromSeconds(2));

        if (!ClickBestTextMatch(
                driver,
                [By.CssSelector(".default-member-footer a.footer-item"), By.CssSelector("a.footer-item"), By.CssSelector(".footer-item")],
                ["rut", "withdraw", "bank", "card", "the"],
                TimeSpan.FromSeconds(8)))
        {
            ClickAnyVisible(driver, SecondaryNavigationSelectors, TimeSpan.FromSeconds(8), required: false);
        }

        executionContext.Delay(TimeSpan.FromSeconds(2));
    }

    private static void FillBankAccountAndBranch(
        IWebDriver driver,
        string bankAccount,
        string branch,
        AutomationExecutionContext executionContext)
    {
        var groupedInputs = WaitForAnyVisibleList(
            driver,
            [By.CssSelector(".type-group-area .mc-account-list input.input-base")],
            TimeSpan.FromSeconds(5));

        if (groupedInputs.Count >= 3)
        {
            executionContext.Checkpoint();
            groupedInputs[1].SendKeysLikeHuman(driver, bankAccount);

            if (!string.IsNullOrWhiteSpace(branch))
            {
                executionContext.Checkpoint();
                groupedInputs[2].SendKeysLikeHuman(driver, branch);
            }

            return;
        }

        var accountInput = WaitForAnyVisible(driver, AccountNumberSelectors, TimeSpan.FromSeconds(10));
        executionContext.Checkpoint();
        accountInput.SendKeysLikeHuman(driver, bankAccount);

        if (!string.IsNullOrWhiteSpace(branch))
        {
            var branchInput = WaitForAnyVisible(driver, BranchSelectors, TimeSpan.FromSeconds(10));
            executionContext.Checkpoint();
            branchInput.SendKeysLikeHuman(driver, branch);
        }
    }

    private static void FillWithdrawPassword(
        IWebDriver driver,
        string withdrawPassword,
        AutomationExecutionContext executionContext)
    {
        var passwordInputs = WaitForAnyVisibleList(driver, PasswordSelectors, TimeSpan.FromSeconds(10));
        if (passwordInputs.Count < 2)
        {
            throw new WebDriverTimeoutException("Could not find two withdraw password inputs.");
        }

        var firstPasswordInput = passwordInputs[^2];
        var secondPasswordInput = passwordInputs[^1];

        executionContext.Checkpoint();
        firstPasswordInput.SendKeysLikeHuman(driver, withdrawPassword);

        executionContext.Checkpoint();
        secondPasswordInput.SendKeysLikeHuman(driver, withdrawPassword);
    }

    private static void ClickBankName(IWebDriver driver, string bankName, TimeSpan timeout)
    {
        var normalizedBankName = NormalizeForMatch(bankName);
        var bankItem = WaitForMatchingText(
            driver,
            BankListItemSelectors,
            text =>
            {
                var normalizedText = NormalizeForMatch(text);
                return !string.IsNullOrWhiteSpace(normalizedText)
                    && (normalizedText.Contains(normalizedBankName, StringComparison.OrdinalIgnoreCase)
                        || normalizedBankName.Contains(normalizedText, StringComparison.OrdinalIgnoreCase));
            },
            timeout);

        bankItem.ClickLikeHuman(driver);
    }

    private static void ClickSubmitButton(IWebDriver driver, TimeSpan timeout)
    {
        var submitButton = WaitForMatchingText(
            driver,
            SubmitSelectors,
            text =>
            {
                var normalizedText = NormalizeForMatch(text);
                return ContainsAny(normalizedText, "xac nhan", "luu", "confirm", "save");
            },
            timeout);

        submitButton.ClickLikeHuman(driver);
    }

    private static bool ClickBestTextMatch(
        IWebDriver driver,
        IReadOnlyList<By> selectors,
        IReadOnlyList<string> keywords,
        TimeSpan timeout)
    {
        try
        {
            var element = WaitForMatchingText(
                driver,
                selectors,
                text => ContainsAny(NormalizeForMatch(text), keywords),
                timeout);

            element.ClickLikeHuman(driver);
            return true;
        }
        catch (WebDriverTimeoutException)
        {
            return false;
        }
    }

    private static bool ClickAnyVisible(IWebDriver driver, IReadOnlyList<By> selectors, TimeSpan timeout, bool required)
    {
        try
        {
            var element = WaitForAnyVisible(driver, selectors, timeout);
            element.ClickLikeHuman(driver);
            return true;
        }
        catch (WebDriverTimeoutException) when (!required)
        {
            return false;
        }
    }

    private static IWebElement WaitForAnyVisible(IWebDriver driver, IReadOnlyList<By> selectors, TimeSpan timeout)
    {
        var wait = CreateWait(driver, timeout);
        var element = wait.Until(webDriver => FindVisibleElements(webDriver, selectors).FirstOrDefault());
        return element ?? throw new WebDriverTimeoutException("Could not find a visible matching element.");
    }

    private static IReadOnlyList<IWebElement> WaitForAnyVisibleList(
        IWebDriver driver,
        IReadOnlyList<By> selectors,
        TimeSpan timeout)
    {
        try
        {
            var wait = CreateWait(driver, timeout);
            return wait.Until(webDriver =>
            {
                var elements = FindVisibleElements(webDriver, selectors).ToArray();
                return elements.Length > 0 ? elements : null;
            }) ?? [];
        }
        catch (WebDriverTimeoutException)
        {
            return [];
        }
    }

    private static IWebElement WaitForMatchingText(
        IWebDriver driver,
        IReadOnlyList<By> selectors,
        Func<string, bool> isMatch,
        TimeSpan timeout)
    {
        var wait = CreateWait(driver, timeout);
        var element = wait.Until(webDriver =>
        {
            return FindVisibleElements(webDriver, selectors)
                .FirstOrDefault(element => isMatch(element.Text ?? string.Empty));
        });

        return element ?? throw new WebDriverTimeoutException("Could not find a visible element matching the expected text.");
    }

    private static IEnumerable<IWebElement> FindVisibleElements(IWebDriver driver, IReadOnlyList<By> selectors)
    {
        foreach (var selector in selectors)
        {
            IReadOnlyCollection<IWebElement> elements;

            try
            {
                elements = driver.FindElements(selector);
            }
            catch (NoSuchElementException)
            {
                continue;
            }
            catch (StaleElementReferenceException)
            {
                continue;
            }
            catch (ElementNotInteractableException)
            {
                continue;
            }

            foreach (var element in elements)
            {
                if (IsDisplayedAndEnabled(element))
                {
                    yield return element;
                }
            }
        }
    }

    private static bool IsDisplayedAndEnabled(IWebElement element)
    {
        try
        {
            return element.Displayed && element.Enabled;
        }
        catch (StaleElementReferenceException)
        {
            return false;
        }
        catch (ElementNotInteractableException)
        {
            return false;
        }
    }

    private static WebDriverWait CreateWait(IWebDriver driver, TimeSpan timeout)
    {
        var wait = new WebDriverWait(driver, timeout)
        {
            PollingInterval = TimeSpan.FromMilliseconds(250)
        };

        wait.IgnoreExceptionTypes(
            typeof(NoSuchElementException),
            typeof(StaleElementReferenceException),
            typeof(ElementNotInteractableException));

        return wait;
    }

    private static bool ContainsAny(string haystack, IReadOnlyList<string> needles)
    {
        return needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        return needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForMatch(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
    }
}
