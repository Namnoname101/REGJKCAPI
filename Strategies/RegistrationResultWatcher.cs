using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

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
    IReadOnlyList<By> InvalidFieldSelectors,
    bool AssumeSuccessOnTimeout = false);

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
        "tu choi",
        "khong dung",
        "khong hop le",
        "da duoc dang ky",
        "thu lai",
        "vuot qua",
        "khac",
        "trung lap",
        "dang ky roi",
        "da su dung"
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

            if (lastUrl != null && (lastUrl.Contains("/m/home", StringComparison.OrdinalIgnoreCase) || 
                                    lastUrl.Contains("/m/member", StringComparison.OrdinalIgnoreCase) ||
                                    lastUrl.Contains("/home", StringComparison.OrdinalIgnoreCase) ||
                                    lastUrl.Contains("/user/center", StringComparison.OrdinalIgnoreCase)))
            {
                return RegistrationOutcome.Success($"Redirected to authenticated area: {TrimForLog(lastUrl)}");
            }

            formVisible = HasAnyVisible(driver, options.FormSelectors);
            modalVisible = HasAnyVisible(driver, options.ModalSelectors);

            if (UrlChanged(options.PreSubmitUrl, lastUrl ?? string.Empty) && !formVisible && !modalVisible)
            {
                return RegistrationOutcome.Success(
                    $"URL changed and registration form/modal disappeared. Url={TrimForLog(lastUrl ?? string.Empty)}");
            }

            var delay = remaining < PollInterval ? remaining : PollInterval;
            executionContext.Delay(delay);
            remaining -= delay;
        }

        if (options.AssumeSuccessOnTimeout)
        {
            return RegistrationOutcome.Success(
                $"Assumed success because no errors appeared within {options.Timeout.TotalSeconds:0}s. " +
                $"Url={TrimForLog(lastUrl ?? string.Empty)}; Title={TrimForLog(lastTitle ?? string.Empty)}");
        }

        return RegistrationOutcome.Timeout(
            $"No verified website result within {options.Timeout.TotalSeconds:0}s. " +
            $"Url={TrimForLog(lastUrl ?? string.Empty)}; Title={TrimForLog(lastTitle ?? string.Empty)}; " +
            $"FormVisible={formVisible}; ModalVisible={modalVisible}; " +
            $"LastMessage={(string.IsNullOrWhiteSpace(lastMessage) ? "none" : TrimForLog(lastMessage))}");
    }

    public static RegistrationOutcome AggressiveAssumeSuccess(
        IWebDriver driver,
        AutomationExecutionContext executionContext)
    {
        // Keep the outer timeout long enough for slow APIs, but exit as soon
        // as the site has created an authenticated session.
        var pollEndTime = DateTime.Now.AddSeconds(15);
        var initialUrl = driver.Url;
        
        if (driver is IJavaScriptExecutor js)
        {
            while (DateTime.Now < pollEndTime)
            {
                if (executionContext.CancellationToken.IsCancellationRequested) break;

                if (TryGetAuthenticatedRedirect(driver, initialUrl, out var redirectMessage))
                {
                    return RegistrationOutcome.Success(redirectMessage);
                }

                try
                {
                    var resultMessage = js.ExecuteScript(@"
                        // 1. Kiểm tra URL thay đổi (Đăng ký thành công thường chuyển hướng sang home)
                        if (window.location.href !== arguments[0] && 
                            (window.location.href.includes('/m/home') || window.location.href.includes('/m/member/home'))) {
                            return 'SUCCESS:Redirected';
                        }

                        // 2. Quét thông báo lỗi hoặc thành công trên màn hình
                        var allEls = document.querySelectorAll(
                            '.van-toast__text, .van-dialog__message, .van-dialog, ' +
                            '.el-message-box, .dialog-content, [role=""alert""], [role=""status""], ' +
                            '.toast, .toast-message, .modal-body');
                        for(var i=0; i<allEls.length; i++) {
                            var el = allEls[i];
                            // Tăng giới hạn text length lên 300 để bắt trọn cả Dialog to
                            if (el.offsetWidth > 0 && el.offsetHeight > 0 && el.innerText && el.innerText.trim().length < 300) {
                                var text = el.innerText.toLowerCase();
                                
                                // Phát hiện thành công sớm
                                if (text.includes('thành công') || text.includes('success')) {
                                    return 'SUCCESS:' + el.innerText.trim();
                                }
                                
                                // Phát hiện lỗi
                                if (text.includes('tồn tại') || 
                                    text.includes('không đúng') || 
                                    text.includes('chưa chính xác') || 
                                    text.includes('invalid') || 
                                    text.includes('đã được đăng ký') || 
                                    text.includes('đã được sử dụng') || 
                                    text.includes('sai định dạng') || 
                                    text.includes('vui lòng thử lại') ||
                                    text.includes('thất bại') ||
                                    text.includes('lỗi')) {
                                    return 'ERROR:' + el.innerText.trim();
                                }
                            }
                        }
                        return null;
                    ", initialUrl) as string;

                    if (!string.IsNullOrEmpty(resultMessage))
                    {
                        if (resultMessage.StartsWith("ERROR:"))
                        {
                            return RegistrationOutcome.Failure($"Error immediately after form submission: {resultMessage.Substring(6)}");
                        }
                        else if (resultMessage.StartsWith("SUCCESS:"))
                        {
                            // Đợi thêm 1 chút xíu để trang load hẳn sau khi báo thành công
                            executionContext.Delay(TimeSpan.FromSeconds(1));
                            return RegistrationOutcome.Success($"Verified success: {resultMessage.Substring(8)}");
                        }
                    }

                    if (TryGetAuthenticatedSession(js, out var sessionMessage))
                    {
                        return RegistrationOutcome.Success(sessionMessage);
                    }
                }
                catch { }

                executionContext.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
        else
        {
            // Fallback nếu không hỗ trợ JS
            executionContext.Delay(TimeSpan.FromSeconds(10));
        }

        // A slow response is only safe to treat as success when the site has
        // already established an authenticated session. Without this check a
        // stalled registration page could fall through to bank binding and
        // submit data against the wrong or unauthenticated state.
        if (driver is IJavaScriptExecutor sessionScript
            && TryGetAuthenticatedSession(sessionScript, out var lateSessionMessage))
        {
            return RegistrationOutcome.Success(lateSessionMessage);
        }

        return RegistrationOutcome.Timeout(
            "No verified success or authenticated session after the 15-second result window.");
    }

    private static bool TryGetAuthenticatedRedirect(
        IWebDriver driver,
        string initialUrl,
        out string message)
    {
        message = string.Empty;

        try
        {
            var currentUrl = SafeGetUrl(driver);
            if (UrlChanged(initialUrl, currentUrl) && LooksLikeAuthenticatedUrl(currentUrl))
            {
                message = $"Redirected to authenticated area: {TrimForLog(currentUrl)}";
                return true;
            }
        }
        catch (WebDriverException)
        {
        }

        return false;
    }

    private static bool TryGetAuthenticatedSession(
        IJavaScriptExecutor javaScript,
        out string message)
    {
        message = string.Empty;

        try
        {
            var sessionState = javaScript.ExecuteScript(
                """
                const path = String(window.location.pathname || '').toLowerCase();
                const keys = [
                    ...Object.keys(window.localStorage || {}),
                    ...Object.keys(window.sessionStorage || {})
                ];
                const authenticatedPath =
                    path.includes('/home')
                    || path.includes('/member')
                    || path.includes('/user/center');
                const authenticatedStorage = keys.some(key =>
                    /^(mc_session_info|sessioninfo|token|auth)/i.test(key));
                return {
                    authenticatedPath,
                    authenticatedStorage,
                    path
                };
                """);

            if (sessionState is IReadOnlyDictionary<string, object> state)
            {
                if (IsTruthy(state, "authenticatedPath"))
                {
                    message = "Authenticated path detected after registration submit.";
                    return true;
                }

                if (IsTruthy(state, "authenticatedStorage"))
                {
                    message = "Authenticated browser storage detected after registration submit.";
                    return true;
                }
            }
        }
        catch (WebDriverException)
        {
        }

        return false;
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

    private static bool LooksLikeAuthenticatedUrl(string url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.Contains("/m/home", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/m/member", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/home", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/member", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/user/center", StringComparison.OrdinalIgnoreCase));

    private static bool IsTruthy(
        IReadOnlyDictionary<string, object> values,
        string key)
    {
        return values.TryGetValue(key, out var value)
               && value is bool boolean
               && boolean;
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
