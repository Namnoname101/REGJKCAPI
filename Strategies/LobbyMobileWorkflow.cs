using System.Collections.Generic;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Interactions;

namespace BAOCODETOOL.Strategies;

/// <summary>
/// GG88 and LLWIN use the newer lobby application.  Its registration form is
/// a client-rendered modal and its Botion captcha is intentionally interactive,
/// so the safe browser path is to open a real mobile page, fill the form, and
/// wait for the user/browser to complete the captcha.
/// </summary>
internal static class LobbyMobileWorkflow
{
    private const int FormTimeoutMs = 20_000;
    private const int RegistrationResultTimeoutSeconds = 90;
    private const int BankFormTimeoutMs = 25_000;

    public static bool IsSupported(TargetSite site) =>
        site is TargetSite.LLWIN or TargetSite.GG88;

    public static bool ExecuteRegistration(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext,
        string targetUrl,
        string phoneNumber)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(siteRun);
        ArgumentNullException.ThrowIfNull(executionContext);

        if (driver is not IJavaScriptExecutor javaScript)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                "Mobile lobby workflow requires a JavaScript-capable browser.");
            return false;
        }

        try
        {
            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: "Opening mobile registration page...");
            driver.Navigate().GoToUrl(targetUrl);

            executionContext.Checkpoint();
            EnterInteractiveLobbyDocument(driver, executionContext);

            executionContext.Checkpoint();
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: "Opening the lobby registration form...");

            var result = javaScript.ExecuteAsyncScript(
                RegistrationScript,
                account.GetUsername(siteRun.Site),
                account.GetPassword(siteRun.Site),
                account.FullName,
                phoneNumber,
                FormTimeoutMs);

            if (!TryReadResult(result, out var ok, out var message)
                || !ok)
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Failed,
                    $"Mobile lobby form could not be submitted: {message}");
                return false;
            }

            if (!TryClickRegistrationSubmit(driver, javaScript, out var clickError))
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Failed,
                    $"Mobile lobby form could not be submitted: {clickError}");
                return false;
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: "Registration submitted. Waiting for GeeTest and login confirmation...");

            var deadline = DateTime.UtcNow.AddSeconds(RegistrationResultTimeoutSeconds);
            var captchaWasSeen = false;
            var sliderAttempts = 0;
            var nextSliderAttemptAt = DateTime.MinValue;
            while (DateTime.UtcNow < deadline)
            {
                executionContext.Checkpoint();

                var state = IsRegistrationSuccessVisible(driver)
                    ? "success"
                    : IsBotionSliderVisible(driver)
                        ? "captcha"
                        : ReadRegistrationState(javaScript);
                if (state.Equals("success", StringComparison.OrdinalIgnoreCase))
                {
                    TryDismissRegistrationSuccess(driver, javaScript);
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        logMessage: "Mobile lobby registration and login session verified.");
                    return true;
                }

                if (state.Equals("failure", StringComparison.OrdinalIgnoreCase))
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        "The lobby reported a registration error. Check the form or captcha message.");
                    return false;
                }

                if (state.Equals("captcha", StringComparison.OrdinalIgnoreCase))
                {
                    if (!captchaWasSeen)
                    {
                        captchaWasSeen = true;
                        AutomationUi.SetSiteStatus(
                            account,
                            siteRun,
                            logMessage: "Slider verification is ready in the mobile page.");
                    }

                    if (siteRun.Site == TargetSite.LLWIN
                        && DateTime.UtcNow >= nextSliderAttemptAt
                        && sliderAttempts < 2)
                    {
                        sliderAttempts++;
                        var dragResult = SolveAndDragBotionSliderAsync(
                                driver,
                                executionContext.SliderCaptchaSolver,
                                executionContext.CancellationToken)
                            .GetAwaiter()
                            .GetResult();
                        if (!dragResult.IsSuccess)
                        {
                            if (sliderAttempts >= 2)
                            {
                                AutomationUi.SetSiteStatus(
                                    account,
                                    siteRun,
                                    AccountStatus.Failed,
                                    $"LLWIN slider verification failed: {dragResult.Message}");
                                return false;
                            }

                            TryRefreshBotion(javaScript);
                            nextSliderAttemptAt = DateTime.UtcNow.AddSeconds(2);
                        }
                        else
                        {
                            AutomationUi.SetSiteStatus(
                                account,
                                siteRun,
                                logMessage:
                                    $"LLWIN slider submitted with OMOCaptcha (attempt {sliderAttempts}). Waiting for registration confirmation...");
                            nextSliderAttemptAt = DateTime.UtcNow.AddSeconds(5);
                        }
                    }
                }

                executionContext.Delay(TimeSpan.FromMilliseconds(500));
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                "Timed out waiting for GeeTest or an authenticated lobby session.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebDriverException exception)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                $"Mobile lobby browser error: {exception.Message}");
            return false;
        }
    }

    private static bool TryClickRegistrationSubmit(
        IWebDriver driver,
        IJavaScriptExecutor javaScript,
        out string error)
    {
        error = string.Empty;
        try
        {
            var submit = driver.FindElements(By.Id("insideRegisterSubmitClick"))
                .LastOrDefault(IsUsableElement);
            if (submit is null)
            {
                error = "the LLWIN submit button is not visible.";
                return false;
            }

            javaScript.ExecuteScript(
                "arguments[0].scrollIntoView({block:'center', inline:'center'});",
                submit);
            submit.Click();
            return true;
        }
        catch (WebDriverException exception)
        {
            error = $"the LLWIN submit click failed: {exception.Message}";
            return false;
        }
    }

    private static async Task<SliderDragResult> SolveAndDragBotionSliderAsync(
        IWebDriver driver,
        ISliderCaptchaSolver? solver,
        CancellationToken cancellationToken)
    {
        if (solver?.IsConfigured != true)
        {
            return SliderDragResult.Failure(
                "OMOCaptcha GeetestSliderWebTask is not configured.");
        }

        IWebElement? puzzleWindow = null;
        IWebElement? sliderButton = null;
        IWebElement? sliderTrack = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                puzzleWindow = driver.FindElements(By.CssSelector(".botion_window"))
                    .LastOrDefault(IsUsableElement);
                sliderButton = driver.FindElements(By.CssSelector(".botion_btn"))
                    .LastOrDefault(IsUsableElement);
                sliderTrack = driver.FindElements(By.CssSelector(".botion_track"))
                    .LastOrDefault(IsUsableElement);
                if (puzzleWindow is not null
                    && sliderButton is not null
                    && sliderTrack is not null)
                {
                    break;
                }
            }
            catch (WebDriverException)
            {
                puzzleWindow = null;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        if (puzzleWindow is null || sliderButton is null || sliderTrack is null)
        {
            return SliderDragResult.Failure(
                "The Botion slider controls did not become visible.");
        }

        if (puzzleWindow is not ITakesScreenshot screenshotElement)
        {
            return SliderDragResult.Failure(
                "The Botion puzzle image cannot be captured by Selenium.");
        }

        byte[] imageBytes;
        try
        {
            imageBytes = screenshotElement.GetScreenshot().AsByteArray;
        }
        catch (WebDriverException exception)
        {
            return SliderDragResult.Failure(
                $"Could not capture the Botion puzzle image: {exception.Message}");
        }

        if (imageBytes.Length == 0)
        {
            return SliderDragResult.Failure("The captured Botion puzzle image is empty.");
        }

        var solution = await solver.SolveAsync(
                Convert.ToBase64String(imageBytes),
                cancellationToken)
            .ConfigureAwait(false);
        if (!solution.IsSuccess)
        {
            return SliderDragResult.Failure(solution.Message);
        }

        var screenshotWidth = ReadPngWidth(imageBytes);
        var cssWidth = Math.Max(1, puzzleWindow.Size.Width);
        var coordinateScale = screenshotWidth > 0
            ? cssWidth / (double)screenshotWidth
            : 1d;
        var requestedOffset = (int)Math.Round(solution.EndX * coordinateScale);
        var maximumOffset = Math.Max(1, sliderTrack.Size.Width - sliderButton.Size.Width);
        var dragOffset = Math.Clamp(requestedOffset, 1, maximumOffset);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var actions = new Actions(driver)
                .MoveToElement(sliderButton)
                .ClickAndHold()
                .Pause(TimeSpan.FromMilliseconds(120));
            var checkpoints = new[] { 0.12, 0.28, 0.46, 0.64, 0.79, 0.91, 1.0 };
            var verticalOffsets = new[] { 0, 1, 0, -1, 0, 1, 0 };
            var movedX = 0;
            var movedY = 0;
            for (var index = 0; index < checkpoints.Length; index++)
            {
                var targetX = index == checkpoints.Length - 1
                    ? dragOffset
                    : (int)Math.Round(dragOffset * checkpoints[index]);
                var targetY = verticalOffsets[index];
                actions.MoveByOffset(targetX - movedX, targetY - movedY)
                    .Pause(TimeSpan.FromMilliseconds(35 + index * 7));
                movedX = targetX;
                movedY = targetY;
            }

            actions.Release().Perform();
        }
        catch (WebDriverException exception)
        {
            return SliderDragResult.Failure(
                $"Could not drag the Botion slider: {exception.Message}");
        }

        return new SliderDragResult(true, string.Empty);
    }

    private static bool IsUsableElement(IWebElement element)
    {
        try
        {
            return element.Displayed
                   && element.Enabled
                   && element.Size.Width > 20
                   && element.Size.Height > 10;
        }
        catch (StaleElementReferenceException)
        {
            return false;
        }
    }

    private static bool IsBotionSliderVisible(IWebDriver driver)
    {
        try
        {
            return driver.FindElements(By.CssSelector(".botion_window, .botion_btn"))
                .Any(IsUsableElement);
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static bool IsRegistrationSuccessVisible(IWebDriver driver)
    {
        try
        {
            return driver.FindElements(By.CssSelector(
                    ".ui-dialog, [role='dialog'], [class*='registerRechargeDialog']"))
                .Any(element =>
                {
                    try
                    {
                        return element.Displayed
                               && element.Text.Contains(
                                   "đăng ký thành công",
                                   StringComparison.OrdinalIgnoreCase);
                    }
                    catch (StaleElementReferenceException)
                    {
                        return false;
                    }
                });
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private static void TryDismissRegistrationSuccess(
        IWebDriver driver,
        IJavaScriptExecutor javaScript)
    {
        try
        {
            var close = driver.FindElements(By.CssSelector(
                    ".ui-dialog-close-box__icon, .ui-dialog-close-box"))
                .LastOrDefault(IsUsableElement);
            if (close is null)
            {
                return;
            }

            javaScript.ExecuteScript(
                "arguments[0].scrollIntoView({block:'center', inline:'center'});",
                close);
            close.Click();
        }
        catch (WebDriverException)
        {
            try
            {
                javaScript.ExecuteScript(
                    "document.querySelector('.ui-dialog-close-box__icon, .ui-dialog-close-box')?.click();");
            }
            catch (WebDriverException)
            {
                // The bank workflow can still navigate if the popup closes itself.
            }
        }
    }

    private static int ReadPngWidth(byte[] pngBytes)
    {
        if (pngBytes.Length < 24
            || pngBytes[0] != 0x89
            || pngBytes[1] != 0x50
            || pngBytes[2] != 0x4E
            || pngBytes[3] != 0x47)
        {
            return 0;
        }

        return (pngBytes[16] << 24)
               | (pngBytes[17] << 16)
               | (pngBytes[18] << 8)
               | pngBytes[19];
    }

    private static void TryRefreshBotion(IJavaScriptExecutor javaScript)
    {
        try
        {
            javaScript.ExecuteScript(
                "document.querySelector('.botion_refresh')?.click();");
        }
        catch (WebDriverException)
        {
            // A failed challenge normally refreshes itself; this click is best effort.
        }
    }

    public static bool ExecuteBankSetup(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(siteRun);
        ArgumentNullException.ThrowIfNull(executionContext);

        if (driver is not IJavaScriptExecutor javaScript)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                "Mobile lobby bank setup requires a JavaScript-capable browser.");
            return false;
        }

        var bankName = account.BankName?.Trim() ?? string.Empty;
        var accountNumber = account.AccountNumber?.Trim() ?? string.Empty;
        var branch = account.Branch?.Trim() ?? string.Empty;
        var withdrawPassword = account.Level2Password?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bankName)
            || string.IsNullOrWhiteSpace(accountNumber)
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
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: "Opening the mobile withdrawal/bank page...");

            EnterInteractiveLobbyDocument(driver, executionContext);

            var result = javaScript.ExecuteAsyncScript(
                BankScript,
                bankName,
                accountNumber,
                branch,
                withdrawPassword,
                account.FullName,
                BankFormTimeoutMs);

            if (!TryReadResult(result, out var ok, out var message)
                || !ok)
            {
                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    AccountStatus.Failed,
                    $"Mobile lobby bank form could not be submitted: {message}");
                return false;
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: "Bank form submitted. Verifying the bound account...");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                executionContext.Checkpoint();
                var state = ReadBankState(javaScript);
                if (state.Equals("success", StringComparison.OrdinalIgnoreCase))
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        logMessage: "Mobile lobby bank account binding verified.");
                    return true;
                }

                if (state.Equals("failure", StringComparison.OrdinalIgnoreCase))
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        "The lobby reported a bank binding error.");
                    return false;
                }

                executionContext.Delay(TimeSpan.FromMilliseconds(500));
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                "Bank binding was not confirmed by the mobile lobby.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebDriverException exception)
        {
            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                AccountStatus.Failed,
                $"Mobile lobby bank browser error: {exception.Message}");
            return false;
        }
    }

    private static void EnterInteractiveLobbyDocument(
        IWebDriver driver,
        AutomationExecutionContext executionContext)
    {
        driver.SwitchTo().DefaultContent();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            executionContext.Checkpoint();

            IWebElement? lobbyFrame = null;
            try
            {
                lobbyFrame = driver.FindElements(
                        By.CssSelector(
                            "iframe.pwa-iframe, " +
                            "iframe[class*='pwa-iframe'], " +
                            "iframe[src*='fixed.share=1'], " +
                            "iframe[src*='restore=1']"))
                    .FirstOrDefault(element =>
                    {
                        try
                        {
                            return element.Displayed;
                        }
                        catch (StaleElementReferenceException)
                        {
                            return false;
                        }
                    });
            }
            catch (WebDriverException)
            {
                // The wrapper is still rendering. Retry until the short deadline.
            }

            if (lobbyFrame is not null)
            {
                driver.SwitchTo().Frame(lobbyFrame);
                return;
            }

            // Links without fixed.iswebclip=2 render the lobby in the top document.
            if (driver is IJavaScriptExecutor javaScript
                && javaScript.ExecuteScript(
                    """
                    return Boolean(
                        document.querySelector('#app')?.children?.length
                        && !document.querySelector('iframe.pwa-iframe, iframe[class*="pwa-iframe"]')
                    );
                    """) is bool hasDirectLobby
                && hasDirectLobby)
            {
                return;
            }

            executionContext.Delay(TimeSpan.FromMilliseconds(200));
        }

        // RegistrationScript and BankScript still have their own bounded waits and
        // will report the precise missing-form error if the site did not render.
    }

    private static string ReadRegistrationState(IJavaScriptExecutor javaScript)
    {
        try
        {
            return javaScript.ExecuteScript(
                """
                (() => {
                    const path = String(location.pathname || '').toLowerCase();
                    const initialBodyText = String(document.body?.innerText || '')
                        .normalize('NFD')
                        .replace(/[\u0300-\u036f]/g, '')
                        .replace(/[đĐ]/g, 'd')
                        .toLowerCase();
                    if (/(chuc mung dang ky thanh cong|dang ky thanh cong|registration successful|register success)/i.test(initialBodyText))
                        return 'success';
                    const storageEntries = [
                        ...Object.entries(window.localStorage || {}),
                        ...Object.entries(window.sessionStorage || {})
                    ];
                    const cookieText = String(document.cookie || '');
                    const hasSession = storageEntries.some(([key, value]) => {
                        const raw = String(value || '');
                        return /token|session|user/i.test(String(key))
                            && /"session_key"\s*:\s*"[^"]+"/i.test(raw)
                            && /"jwt_token"\s*:\s*"[^"]+"/i.test(raw);
                    }) || /session_key=[^;]+/i.test(cookieText);
                    const authenticatedPath =
                        path.includes('/mine')
                        || path.includes('/member')
                        || path.includes('/user/center');
                    if (authenticatedPath || hasSession) return 'success';
                    const visible = el => {
                        const rect = el?.getBoundingClientRect?.();
                        const style = el ? getComputedStyle(el) : null;
                        return rect && rect.width > 0 && rect.height > 0
                            && style?.display !== 'none'
                            && style?.visibility !== 'hidden';
                    };
                    const normalize = value => String(value || '')
                        .normalize('NFD')
                        .replace(/[\u0300-\u036f]/g, '')
                        .replace(/[đĐ]/g, 'd')
                        .toLowerCase();
                    const messages = Array.from(document.querySelectorAll(
                        '[role="alert"], [role="status"], .toast, [class*="toast" i], ' +
                        '[class*="message" i], [class*="dialog" i], [class*="modal" i]'))
                        .filter(visible)
                        .map(el => normalize(el.innerText || el.textContent).trim())
                        .filter(text => text.length > 0 && text.length < 300);
                    if (messages.some(text =>
                        /(dang ky thanh cong|registration successful|register success)/i.test(text)))
                        return 'success';
                    const bodyText = normalize(document.body?.innerText || '');
                    if (/(chuc mung dang ky thanh cong|dang ky thanh cong|registration successful|register success)/i.test(bodyText))
                        return 'success';
                    const visibleCaptcha = Array.from(document.querySelectorAll(
                        '[id*="captcha" i], [class*="captcha" i], [id*="geetest" i], [class*="geetest" i], [id*="botion" i], [class*="botion" i]'))
                        .some(visible);
                    if (/(captcha|geetest|botion|xac minh|verification)/i.test(bodyText)
                        || visibleCaptcha)
                        return 'captcha';
                    if (messages.some(text =>
                        /(ten dang nhap da ton tai|username.*exist|already exists|dang ky that bai|registration failed|loi dang ky)/i.test(text)))
                        return 'failure';
                    return 'pending';
                })()
                """)?.ToString() ?? "pending";
        }
        catch (WebDriverException)
        {
            return "pending";
        }
    }

    private sealed record SliderDragResult(bool IsSuccess, string Message)
    {
        public static SliderDragResult Failure(string message) => new(false, message);
    }

    private static string ReadBankState(IJavaScriptExecutor javaScript)
    {
        try
        {
            return javaScript.ExecuteScript(
                """
                (() => {
                    const visible = el => {
                        const rect = el?.getBoundingClientRect?.();
                        const style = el ? getComputedStyle(el) : null;
                        return rect && rect.width > 0 && rect.height > 0
                            && style?.display !== 'none'
                            && style?.visibility !== 'hidden';
                    };
                    const normalize = value => String(value || '')
                        .normalize('NFD')
                        .replace(/[\u0300-\u036f]/g, '')
                        .replace(/[đĐ]/g, 'd')
                        .toLowerCase();
                    const messages = Array.from(document.querySelectorAll(
                        '[role="alert"], [role="status"], .toast, [class*="toast" i], ' +
                        '[class*="message" i], [class*="dialog" i], [class*="modal" i]'))
                        .filter(visible)
                        .map(el => normalize(el.innerText || el.textContent).trim())
                        .filter(text => text.length > 0 && text.length < 300);
                    if (messages.some(text =>
                        /(them thanh cong|da lien ket|lien ket thanh cong|bind success|bank.*success)/i.test(text)))
                        return 'success';
                    if (messages.some(text =>
                        /(lien ket that bai|them that bai|bank.*error|khong hop le|loi)/i.test(text)))
                        return 'failure';
                    const account = String(window.__vibecodeBankSubmittedAccount || '');
                    if (account) {
                        const bodyText = String(document.body?.innerText || '');
                        const accountInputStillVisible = Array.from(
                            document.querySelectorAll('input, textarea'))
                            .some(el => visible(el) && String(el.value || '') === account);
                        if (!accountInputStillVisible && bodyText.includes(account))
                            return 'success';
                    }
                    return 'pending';
                })()
                """)?.ToString() ?? "pending";
        }
        catch (WebDriverException)
        {
            return "pending";
        }
    }

    private static bool TryReadResult(object? value, out bool ok, out string message)
    {
        ok = false;
        message = "unknown script result";

        if (value is IReadOnlyDictionary<string, object> dictionary)
        {
            ok = dictionary.TryGetValue("ok", out var rawOk)
                 && rawOk is bool boolOk
                 && boolOk;
            if (dictionary.TryGetValue("message", out var rawMessage)
                && rawMessage is not null)
            {
                message = rawMessage.ToString() ?? message;
            }

            return true;
        }

        if (value is IDictionary<string, object> mutableDictionary)
        {
            ok = mutableDictionary.TryGetValue("ok", out var rawOk)
                 && rawOk is bool boolOk
                 && boolOk;
            if (mutableDictionary.TryGetValue("message", out var rawMessage)
                && rawMessage is not null)
            {
                message = rawMessage.ToString() ?? message;
            }

            return true;
        }

        return false;
    }

    private const string RegistrationScript =
        """
        const username = String(arguments[0] || '');
        const password = String(arguments[1] || '');
        const fullName = String(arguments[2] || '');
        const phone = String(arguments[3] || '');
        const timeoutMs = Number(arguments[4] || 20000);
        const done = arguments[arguments.length - 1];
        const started = performance.now();
        let timer = null;
        let finished = false;

        const normalize = value => String(value || '')
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .replace(/[đĐ]/g, 'd')
            .toLowerCase()
            .replace(/\s+/g, ' ')
            .trim();
        const visible = el => {
            if (!el || el.disabled) return false;
            const rect = el.getBoundingClientRect();
            const style = window.getComputedStyle(el);
            return rect.width > 0 && rect.height > 0
                && style.display !== 'none'
                && style.visibility !== 'hidden';
        };
        const finish = (ok, message) => {
            if (finished) return;
            finished = true;
            if (timer) clearTimeout(timer);
            done({ ok, message });
        };
        const elements = selector => Array.from(document.querySelectorAll(selector)).filter(visible);
        const metadata = el => normalize([
            el.name,
            el.id,
            el.placeholder,
            el.getAttribute('aria-label'),
            el.getAttribute('data-input-name'),
            el.getAttribute('autocomplete')
        ].filter(Boolean).join(' '));
        const setValue = (el, value) => {
            if (!el) return;
            const proto = el.tagName === 'TEXTAREA'
                ? HTMLTextAreaElement.prototype
                : HTMLInputElement.prototype;
            const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
            if (descriptor?.set) descriptor.set.call(el, String(value ?? ''));
            else el.value = String(value ?? '');
            try {
                el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText' }));
            } catch {
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }
            el.dispatchEvent(new Event('change', { bubbles: true }));
            el.dispatchEvent(new Event('blur', { bubbles: true }));
        };
        const clickRegister = () => {
            const candidates = elements('button, a, [role="button"], [class*="register" i]');
            const button = candidates.find(el => {
                const text = normalize(el.innerText || el.textContent);
                return text.length < 80
                    && /(dang ky|register|sign up)/i.test(text)
                    && !/(da dang ky|already)/i.test(text);
            });
            if (button) {
                button.click();
                return true;
            }
            return false;
        };
        const findBy = (inputs, pattern) => inputs.find(input => pattern.test(metadata(input)));
        const clickSubmit = () => {
            const primarySubmit = document.querySelector('#insideRegisterSubmitClick');
            if (visible(primarySubmit)) return primarySubmit;
            const candidates = elements('button, [role="button"], a, input[type="submit"]');
            return candidates.findLast(el => {
                const text = normalize(el.innerText || el.textContent || el.value);
                return text.length < 80
                    && /(dang ky|register|sign up|submit|xac nhan|confirm)/i.test(text)
                    && !/(login|dang nhap|cancel|huy)/i.test(text);
            }) || null;
        };
        const tick = () => {
            try {
                const inputs = elements('input, textarea').filter(el => {
                    const type = String(el.type || '').toLowerCase();
                    return !['hidden', 'checkbox', 'radio', 'search'].includes(type);
                });
                if (inputs.length < 2) {
                    clickRegister();
                } else {
                    // GG88/LLWIN render their password control as type="text".
                    // Match the semantic metadata as well as the native type.
                    const passwords = inputs.filter(el =>
                        String(el.type || '').toLowerCase() === 'password'
                        || /password|passwd|mat khau/i.test(metadata(el)));
                    const usernameInput = findBy(inputs, /(platform|username|account|tai khoan|ten dang nhap|账号|用户名)/i)
                        || inputs.find(el => !passwords.includes(el));
                    const confirmInput = findBy(passwords, /(confirm|repeat|retype|xac nhan|nhap lai)/i)
                        || passwords[1];
                    const passwordInput = findBy(passwords, /password|passwd|mat khau/i) || passwords[0];
                    const phoneInput = findBy(inputs, /phone|mobile|tel|dien thoai|so dien thoai/i);
                    const nameInput = findBy(inputs, /real.?name|full.?name|ho.?va.?ten|ho ten|nickname|ten that/i);
                    if (usernameInput) setValue(usernameInput, username);
                    if (passwordInput) setValue(passwordInput, password);
                    if (confirmInput && confirmInput !== passwordInput) setValue(confirmInput, password);
                    if (phoneInput) setValue(phoneInput, phone);
                    if (nameInput) setValue(nameInput, fullName.toLocaleUpperCase('vi-VN'));

                    // The visual consent control wraps a zero-sized checkbox.
                    // Updating the native property keeps Vue's v-model in sync.
                    document.querySelectorAll('input[type="checkbox"]').forEach(checkbox => {
                        if (checkbox.disabled || checkbox.checked) return;
                        const descriptor = Object.getOwnPropertyDescriptor(
                            HTMLInputElement.prototype,
                            'checked');
                        if (descriptor?.set) descriptor.set.call(checkbox, true);
                        else checkbox.checked = true;
                        checkbox.dispatchEvent(new Event('input', { bubbles: true }));
                        checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                    });

                    const submit = clickSubmit();
                    if (submit) {
                        finish(true, 'ready-to-submit');
                        return;
                    }
                }
            } catch (error) {
                if (performance.now() - started >= timeoutMs) {
                    finish(false, error?.message || String(error));
                    return;
                }
            }

            if (performance.now() - started >= timeoutMs) {
                finish(false, 'registration form not found');
                return;
            }
            timer = setTimeout(tick, 150);
        };
        tick();
        """;

    private const string BankScript =
        """
        const bankName = String(arguments[0] || '');
        const accountNumber = String(arguments[1] || '');
        const branch = String(arguments[2] || '');
        const withdrawPassword = String(arguments[3] || '');
        const fullName = String(arguments[4] || '');
        const timeoutMs = Number(arguments[5] || 25000);
        const done = arguments[arguments.length - 1];
        const started = performance.now();
        let timer = null;
        let finished = false;
        const normalize = value => String(value || '')
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .replace(/[đĐ]/g, 'd')
            .toLowerCase()
            .replace(/\s+/g, ' ')
            .trim();
        const visible = el => {
            if (!el || el.disabled) return false;
            const rect = el.getBoundingClientRect();
            const style = window.getComputedStyle(el);
            return rect.width > 0 && rect.height > 0
                && style.display !== 'none'
                && style.visibility !== 'hidden';
        };
        const finish = (ok, message) => {
            if (finished) return;
            finished = true;
            if (timer) clearTimeout(timer);
            done({ ok, message });
        };
        const elements = selector => Array.from(document.querySelectorAll(selector)).filter(visible);
        const metadata = el => normalize([
            el.name,
            el.id,
            el.placeholder,
            el.getAttribute('aria-label'),
            el.getAttribute('data-input-name'),
            el.getAttribute('autocomplete')
        ].filter(Boolean).join(' '));
        const text = el => normalize(el.innerText || el.textContent || el.value);
        const clickText = pattern => {
            const button = elements('button, a, [role="button"], [class*="withdraw" i], [class*="bank" i]')
                .find(el => text(el).length < 100 && pattern.test(text(el)));
            if (button) {
                button.click();
                return true;
            }
            return false;
        };
        const setValue = (el, value) => {
            if (!el) return;
            if (el.tagName === 'SELECT') {
                const option = Array.from(el.options || []).find(item =>
                    normalize(item.textContent).includes(normalize(value))
                    || normalize(value).includes(normalize(item.textContent)));
                if (option) el.value = option.value;
            } else {
                const proto = el.tagName === 'TEXTAREA'
                    ? HTMLTextAreaElement.prototype
                    : HTMLInputElement.prototype;
                const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
                if (descriptor?.set) descriptor.set.call(el, String(value ?? ''));
                else el.value = String(value ?? '');
            }
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            el.dispatchEvent(new Event('blur', { bubbles: true }));
        };
        const tick = () => {
            try {
                clickText(/rut tien|withdraw|bank|ngan hang|tai khoan/);
                clickText(/them tai khoan|them ngan hang|add bank|add account|bind/);

                const inputs = elements('input, textarea, select').filter(el => {
                    const type = String(el.type || '').toLowerCase();
                    return !['hidden', 'checkbox', 'radio', 'search'].includes(type);
                });
                const cardInput = inputs.find(el => /(^|\s)number($|\s)|card|account|bank.*number|so tai khoan|so the/i.test(metadata(el)));
                const branchInput = inputs.find(el => /branch|chi nhanh|area|dia chi/i.test(metadata(el)));
                const passwordInput = inputs.find(el => /withdraw.?password|withdraw|payment|transaction|level.?2|mat khau rut/i.test(metadata(el)))
                    || inputs.filter(el => String(el.type || '').toLowerCase() === 'password').at(-1);
                const nameInput = inputs.find(el => /real.?name|full.?name|ho ten|payee/i.test(metadata(el)));
                const nativeBankSelect = inputs.find(el => el.tagName === 'SELECT');
                const bankField = inputs.find(el => /bank|ngan hang/i.test(metadata(el)));
                let bankChosen = false;

                if (nativeBankSelect) {
                    setValue(nativeBankSelect, bankName);
                    bankChosen = Boolean(nativeBankSelect.value);
                } else {
                    const customBankSelect = bankField
                        || elements(
                            '[data-input-name="bank"], [name="bank"], ' +
                            '[class*="bank" i], [class*="select" i]')
                            .find(el => {
                                const value = `${metadata(el)} ${text(el)}`;
                                return /bank|ngan hang|chon ngan hang/i.test(value)
                                    && text(el).length < 120;
                            });
                    const currentBankText = customBankSelect
                        ? normalize(text(customBankSelect))
                        : '';
                    if (currentBankText.includes(normalize(bankName))) {
                        bankChosen = true;
                    } else if (customBankSelect) {
                        customBankSelect.click();
                    }

                    const bankOption = elements(
                        '[role="option"], li, [class*="option" i], [class*="item" i]')
                        .find(el => {
                            const candidate = normalize(text(el));
                            const target = normalize(bankName);
                            return candidate
                                && (candidate.includes(target) || target.includes(candidate));
                        });
                    if (bankOption) {
                        bankOption.click();
                        bankChosen = true;
                    } else if (bankField) {
                        // Keep the model-backed input in sync for custom widgets
                        // that expose the selected bank as a hidden/readonly value.
                        setValue(bankField, bankName);
                    }
                }
                if (cardInput) setValue(cardInput, accountNumber);
                if (branchInput) setValue(branchInput, branch);
                if (passwordInput) setValue(passwordInput, withdrawPassword);
                if (nameInput) setValue(nameInput, fullName.toLocaleUpperCase('vi-VN'));

                const submit = document.querySelector('#bindWithdrawAccountNextClick')
                    || elements('button, [role="button"], a, input[type="submit"]')
                    .find(el => text(el).length < 100
                        && /(them|add|save|luu|xac nhan|confirm|bind|submit)/i.test(text(el))
                        && !/(rut tien|withdraw|cancel|huy)/i.test(text(el)));
                if (bankChosen && cardInput && passwordInput && submit) {
                    window.__vibecodeBankSubmittedAccount = accountNumber;
                    submit.removeAttribute('disabled');
                    submit.click();
                    finish(true, 'submitted');
                    return;
                }
            } catch (error) {
                if (performance.now() - started >= timeoutMs) {
                    finish(false, error?.message || String(error));
                    return;
                }
            }

            if (performance.now() - started >= timeoutMs) {
                finish(false, 'bank form not found');
                return;
            }
            timer = setTimeout(tick, 200);
        };
        tick();
        """;
}
