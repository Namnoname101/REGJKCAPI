using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VibecodeReg.Core;
using VibecodeReg.Models;

namespace VibecodeReg.Strategies;

internal enum RegistrationOutcomeKind
{
    Success,
    Failure,
    Timeout
}

internal sealed record RegistrationOutcome(RegistrationOutcomeKind Kind, string Message)
{
    public static RegistrationOutcome Success(string message) => new(RegistrationOutcomeKind.Success, message);

    public static RegistrationOutcome Failure(string message) => new(RegistrationOutcomeKind.Failure, message);

    public static RegistrationOutcome Timeout(string message) => new(RegistrationOutcomeKind.Timeout, message);
}

internal sealed record RegistrationResultWatchOptions(
    string SiteName,
    string PreSubmitUrl,
    TimeSpan Timeout,
    IReadOnlyList<By> FormSelectors,
    IReadOnlyList<By> ModalSelectors,
    IReadOnlyList<By> FailureMessageSelectors,
    IReadOnlyList<By> StatusMessageSelectors,
    IReadOnlyList<By> InvalidFieldSelectors);

internal static class RegistrationResultWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string[] SuccessKeywords =
    [
        "success",
        "successful",
        "thanh cong",
        "dang ky thanh cong",
        "chuc mung",
        "hoan tat"
    ];

    private static readonly string[] FailureKeywords =
    [
        "error",
        "failed",
        "failure",
        "invalid",
        "duplicate",
        "already",
        "exist",
        "server",
        "captcha",
        "verification",
        "loi",
        "sai",
        "ton tai",
        "that bai",
        "tu choi"
    ];

    public static RegistrationOutcome WaitForResult(
        IWebDriver driver,
        AutomationExecutionContext executionContext,
        RegistrationResultWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(options);

        var remaining = options.Timeout;
        var lastUrl = SafeGetUrl(driver);
        var lastTitle = SafeGetTitle(driver);
        var lastMessage = string.Empty;
        var formVisible = false;
        var modalVisible = false;

        while (remaining > TimeSpan.Zero)
        {
            executionContext.Checkpoint();

            var failureMessages = GetVisibleTexts(driver, options.FailureMessageSelectors);
            var explicitFailure = failureMessages.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(explicitFailure))
            {
                return RegistrationOutcome.Failure($"Website validation: {TrimForLog(explicitFailure)}");
            }

            var statusMessages = GetVisibleTexts(driver, options.StatusMessageSelectors);
            foreach (var statusMessage in statusMessages)
            {
                lastMessage = statusMessage;

                if (LooksLikeFailure(statusMessage))
                {
                    return RegistrationOutcome.Failure($"Website message: {TrimForLog(statusMessage)}");
                }

                if (LooksLikeSuccess(statusMessage))
                {
                    return RegistrationOutcome.Success($"Website success message: {TrimForLog(statusMessage)}");
                }
            }

            if (HasAnyVisible(driver, options.InvalidFieldSelectors))
            {
                return RegistrationOutcome.Failure("Website marked one or more registration fields as invalid.");
            }

            lastUrl = SafeGetUrl(driver);
            lastTitle = SafeGetTitle(driver);
            formVisible = HasAnyVisible(driver, options.FormSelectors);
            modalVisible = HasAnyVisible(driver, options.ModalSelectors);

            if (UrlChanged(options.PreSubmitUrl, lastUrl) && !formVisible && !modalVisible)
            {
                return RegistrationOutcome.Success(
                    $"URL changed and registration form/modal disappeared. Url={TrimForLog(lastUrl)}");
            }

            var delay = remaining < PollInterval ? remaining : PollInterval;
            executionContext.Delay(delay);
            remaining -= delay;
        }

        return RegistrationOutcome.Timeout(
            $"No verified website result within {options.Timeout.TotalSeconds:0}s. " +
            $"Url={TrimForLog(lastUrl)}; Title={TrimForLog(lastTitle)}; " +
            $"FormVisible={formVisible}; ModalVisible={modalVisible}; " +
            $"LastMessage={(string.IsNullOrWhiteSpace(lastMessage) ? "none" : TrimForLog(lastMessage))}");
    }

    public static bool ReportOutcome(
        AccountModel account,
        SiteRunModel siteRun,
        RegistrationOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case RegistrationOutcomeKind.Success:
                AutomationUi.SetSiteStatus(account, siteRun, logMessage: $"Registration verified: {outcome.Message}");
                return true;

            case RegistrationOutcomeKind.Failure:
                AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Registration failed by website: {outcome.Message}");
                return false;

            case RegistrationOutcomeKind.Timeout:
                AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, $"Registration result timeout: {outcome.Message}");
                return false;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "Unknown registration outcome.");
        }
    }

    private static IReadOnlyList<string> GetVisibleTexts(IWebDriver driver, IReadOnlyList<By> selectors)
    {
        if (selectors.Count == 0)
        {
            return [];
        }

        var messages = new List<string>();

        foreach (var selector in selectors)
        {
            foreach (var element in FindElementsForPolling(driver, selector))
            {
                if (TryGetVisibleText(element, out var text))
                {
                    messages.Add(text);
                }
            }
        }

        return messages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasAnyVisible(IWebDriver driver, IReadOnlyList<By> selectors)
    {
        foreach (var selector in selectors)
        {
            foreach (var element in FindElementsForPolling(driver, selector))
            {
                if (TryIsDisplayed(element))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<IWebElement> FindElementsForPolling(IWebDriver driver, By selector)
    {
        try
        {
            return driver.FindElements(selector).ToArray();
        }
        catch (NoSuchElementException)
        {
            return [];
        }
        catch (StaleElementReferenceException)
        {
            return [];
        }
        catch (ElementNotInteractableException)
        {
            return [];
        }
    }

    private static bool TryGetVisibleText(IWebElement element, out string text)
    {
        text = string.Empty;

        try
        {
            if (!element.Displayed)
            {
                return false;
            }

            text = element.Text?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text);
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

    private static bool TryIsDisplayed(IWebElement element)
    {
        try
        {
            return element.Displayed;
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

    private static bool UrlChanged(string referenceUrl, string currentUrl)
    {
        return !string.Equals(
            referenceUrl?.Trim(),
            currentUrl?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSuccess(string message)
    {
        var normalized = NormalizeForKeywordMatch(message);
        return SuccessKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeFailure(string message)
    {
        var normalized = NormalizeForKeywordMatch(message);
        return FailureKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForKeywordMatch(string value)
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

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static string SafeGetUrl(IWebDriver driver)
    {
        return driver.Url ?? string.Empty;
    }

    private static string SafeGetTitle(IWebDriver driver)
    {
        return driver.Title ?? string.Empty;
    }

    private static string TrimForLog(string value)
    {
        const int maxLength = 180;

        var normalized = (value ?? string.Empty)
            .ReplaceLineEndings(" ")
            .Trim();

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }
}
