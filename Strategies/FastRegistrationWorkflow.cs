using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;

namespace BAOCODETOOL.Strategies;

internal enum FastRegistrationFallbackOrder
{
    PhoneThenFullName,
    FullNameThenPhone
}

internal static class FastRegistrationWorkflow
{
    private const int FormTimeoutMs = 7_000;

    private const string FillAndSubmitScript =
        """
        const username = String(arguments[0] || '');
        const password = String(arguments[1] || '');
        const fullName = String(arguments[2] || '');
        const phoneNumber = String(arguments[3] || '');
        const phoneFirst = Boolean(arguments[4]);
        const timeoutMs = Number(arguments[5] || 7000);
        const done = arguments[arguments.length - 1];
        const started = performance.now();

        let completed = false;
        let timer = null;
        let lastInputCount = 0;

        function finish(ok, message, extra) {
            if (completed) return;
            completed = true;
            if (timer) clearTimeout(timer);
            done(Object.assign({
                ok,
                message,
                elapsedMs: Math.round(performance.now() - started),
                inputCount: lastInputCount
            }, extra || {}));
        }

        function normalize(text) {
            return String(text || '')
                .normalize('NFD')
                .replace(/[\u0300-\u036f]/g, '')
                .trim()
                .toLowerCase();
        }

        function visible(el) {
            if (!el) return false;
            const style = window.getComputedStyle(el);
            if (style.display === 'none' || style.visibility === 'hidden') return false;
            const rect = el.getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
        }

        function visibleInputs() {
            return Array.from(document.querySelectorAll('input'))
                .filter(input => {
                    const type = String(input.type || '').toLowerCase();
                    return !input.disabled
                        && type !== 'hidden'
                        && type !== 'checkbox'
                        && type !== 'radio'
                        && visible(input);
                });
        }

        function isCaptchaInput(input) {
            const hint = normalize([
                input.id,
                input.name,
                input.placeholder,
                input.getAttribute('aria-label'),
                input.getAttribute('autocomplete')
            ].filter(Boolean).join(' '));

            return hint.includes('captcha')
                || hint.includes('ma xac minh')
                || hint.includes('verification code')
                || hint.includes('verify code')
                || hint.includes('security code');
        }

        function setValue(el, value) {
            if (!el) return;
            const prototype = el.tagName === 'TEXTAREA'
                ? HTMLTextAreaElement.prototype
                : HTMLInputElement.prototype;
            const descriptor = Object.getOwnPropertyDescriptor(prototype, 'value');
            if (descriptor && descriptor.set) {
                descriptor.set.call(el, value);
            } else {
                el.value = value;
            }

            try {
                el.dispatchEvent(new InputEvent('input', {
                    bubbles: true,
                    inputType: 'insertText',
                    data: value
                }));
            } catch {
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }

            el.dispatchEvent(new Event('change', { bubbles: true }));
            el.dispatchEvent(new Event('blur', { bubbles: true }));
        }

        function closeBlockingPopups() {
            document
                .querySelectorAll('.van-overlay, .modal-backdrop, .overlay')
                .forEach(el => el.remove());

            document
                .querySelectorAll('button, [role="button"]')
                .forEach(el => {
                    const text = normalize(el.innerText || el.textContent);
                    if (visible(el) && (text === 'huy' || text === 'cancel' || text === 'dong')) {
                        try { el.click(); } catch {}
                    }
                });
        }

        function firstVisible(selector) {
            return Array.from(document.querySelectorAll(selector)).find(visible) || null;
        }

        function findSubmitButton() {
            const buttons = Array.from(document.querySelectorAll(
                'button.submit-btn, button[type="submit"], .submit-btn, button'));

            return buttons.find(button => {
                if (!visible(button)) return false;

                const text = normalize(button.innerText || button.textContent);
                if (text === 'huy' || text === 'cancel' || text === 'dong') return false;

                return button.matches('button.submit-btn, button[type="submit"], .submit-btn')
                    || text.includes('dang ky')
                    || text.includes('register');
            }) || null;
        }

        function findPhoneInput() {
            return firstVisible(
                'input.form-mobileNum, .form-mobileNum input, input[type="tel"], ' +
                'input[name*="phone" i], input[name*="mobile" i], input[autocomplete="tel"]');
        }

        function tick() {
            try {
                closeBlockingPopups();

                const allInputs = visibleInputs();
                const captchaInputs = allInputs.filter(isCaptchaInput);
                const inputs = allInputs.filter(input => !captchaInputs.includes(input));
                lastInputCount = allInputs.length;
                const submitButton = findSubmitButton();

                if (inputs.length >= 3 && submitButton) {
                    const passwordInputs = inputs.filter(input =>
                        String(input.type || '').toLowerCase() === 'password');
                    const usernameInput = inputs.find(input => !passwordInputs.includes(input)) || inputs[0];

                    setValue(usernameInput, username);
                    passwordInputs.forEach(input => setValue(input, password));

                    const remainingInputs = inputs.filter(input =>
                        input !== usernameInput && !passwordInputs.includes(input));
                    const phoneInput = findPhoneInput();

                    if (phoneInput) {
                        setValue(phoneInput, phoneNumber);
                        const nameInput = remainingInputs.find(input => input !== phoneInput);
                        if (nameInput) setValue(nameInput, fullName);
                    } else {
                        const first = remainingInputs[0];
                        const second = remainingInputs[1];
                        if (phoneFirst) {
                            if (first) setValue(first, phoneNumber);
                            if (second) setValue(second, fullName);
                        } else {
                            if (first) setValue(first, fullName);
                            if (second) setValue(second, phoneNumber);
                        }
                    }

                    if (captchaInputs.length > 0) {
                        finish(true, 'filled', { requiresImageCaptcha: true });
                        return;
                    }

                    timer = setTimeout(() => {
                        try {
                            const finalButton = findSubmitButton() || submitButton;
                            finalButton.disabled = false;
                            finalButton.removeAttribute('disabled');
                            finalButton.click();
                            finish(true, 'submitted');
                        } catch (error) {
                            finish(false, `submit click failed: ${error && error.message ? error.message : error}`);
                        }
                    }, 80);
                    return;
                }
            } catch (error) {
                if (performance.now() - started >= timeoutMs) {
                    finish(false, `form fill failed: ${error && error.message ? error.message : error}`);
                    return;
                }
            }

            if (performance.now() - started >= timeoutMs) {
                finish(false, 'registration form not ready');
                return;
            }

            timer = setTimeout(tick, 50);
        }

        tick();
        """;

    public static bool FillAndSubmit(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun,
        AutomationExecutionContext executionContext,
        string targetUrl,
        string phoneNumber,
        FastRegistrationFallbackOrder fallbackOrder)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(siteRun);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUrl);

        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Opening registration page...");
        driver.Navigate().GoToUrl(targetUrl);

        executionContext.Checkpoint();
        AutomationUi.SetSiteStatus(account, siteRun, logMessage: "Injecting registration data...");

        if (driver is not IJavaScriptExecutor javaScript)
        {
            AutomationUi.SetSiteStatus(account, siteRun, AccountStatus.Failed, "Browser does not support JavaScript execution.");
            return false;
        }

        var result = javaScript.ExecuteAsyncScript(
            FillAndSubmitScript,
            account.GetUsername(siteRun.Site),
            account.GetPassword(siteRun.Site),
            account.FullName,
            phoneNumber,
            fallbackOrder == FastRegistrationFallbackOrder.PhoneThenFullName,
            FormTimeoutMs);

        if (TryReadResult(
                result,
                out var ok,
                out var message,
                out var elapsedMs,
                out var inputCount,
                out var requiresImageCaptcha)
            && ok)
        {
            if (requiresImageCaptcha)
            {
                var solver = executionContext.ImageCaptchaSolver;
                if (solver?.IsConfigured != true)
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        "Image captcha detected but OMOCaptcha is not configured.");
                    return false;
                }

                AutomationUi.SetSiteStatus(
                    account,
                    siteRun,
                    logMessage: "Solving image captcha with OMOCaptcha...");
                var captchaResult = ImageCaptchaBrowserWorkflow.SolveAndFillAsync(
                        driver,
                        solver,
                        executionContext.CancellationToken)
                    .GetAwaiter()
                    .GetResult();
                if (!captchaResult.IsSuccess)
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        $"Image captcha failed: {captchaResult.Message}");
                    return false;
                }

                executionContext.Checkpoint();
                executionContext.Delay(TimeSpan.FromMilliseconds(80));
                if (!SubmitRegistrationForm(javaScript))
                {
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        AccountStatus.Failed,
                        "Captcha was filled but the registration button could not be submitted.");
                    return false;
                }
            }

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: requiresImageCaptcha
                    ? $"Image captcha solved and registration form submitted ({inputCount} inputs)."
                    : $"Registration form submitted in {elapsedMs}ms ({inputCount} inputs).");
            return true;
        }

        message = string.IsNullOrWhiteSpace(message) ? "unknown script result" : message;
        AutomationUi.SetSiteStatus(
            account,
            siteRun,
            AccountStatus.Failed,
            $"Registration form automation failed: {message}");
        return false;
    }

    private static bool TryReadResult(
        object? result,
        out bool ok,
        out string message,
        out long elapsedMs,
        out long inputCount,
        out bool requiresImageCaptcha)
    {
        ok = false;
        message = string.Empty;
        elapsedMs = 0;
        inputCount = 0;
        requiresImageCaptcha = false;

        if (result is not IReadOnlyDictionary<string, object> values)
        {
            message = result?.ToString() ?? "no result";
            return false;
        }

        ok = values.TryGetValue("ok", out var okValue) && okValue is bool boolean && boolean;
        message = values.TryGetValue("message", out var messageValue)
            ? messageValue?.ToString() ?? string.Empty
            : string.Empty;
        elapsedMs = ReadInt64(values, "elapsedMs");
        inputCount = ReadInt64(values, "inputCount");
        requiresImageCaptcha = values.TryGetValue(
                                   "requiresImageCaptcha",
                                   out var captchaValue)
                               && captchaValue is bool captchaBoolean
                               && captchaBoolean;
        return true;
    }

    private static bool SubmitRegistrationForm(IJavaScriptExecutor javaScript)
    {
        var result = javaScript.ExecuteScript(
            """
            const normalize = text => String(text || '')
                .normalize('NFD')
                .replace(/[\u0300-\u036f]/g, '')
                .trim()
                .toLowerCase();
            const visible = element => {
                if (!element) return false;
                const style = window.getComputedStyle(element);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const rect = element.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            };
            const buttons = Array.from(document.querySelectorAll(
                'button.submit-btn, button[type="submit"], .submit-btn, button'));
            const button = buttons.find(candidate => {
                if (!visible(candidate)) return false;
                const text = normalize(candidate.innerText || candidate.textContent);
                if (text === 'huy' || text === 'cancel' || text === 'dong') return false;
                return candidate.matches('button.submit-btn, button[type="submit"], .submit-btn')
                    || text.includes('dang ky')
                    || text.includes('register');
            });
            if (!button) return false;
            button.disabled = false;
            button.removeAttribute('disabled');
            button.click();
            return true;
            """);

        return result is bool submitted && submitted;
    }

    private static long ReadInt64(IReadOnlyDictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            double doubleValue => Convert.ToInt64(doubleValue),
            decimal decimalValue => Convert.ToInt64(decimalValue),
            _ when long.TryParse(value.ToString(), out var parsed) => parsed,
            _ => 0
        };
    }
}
