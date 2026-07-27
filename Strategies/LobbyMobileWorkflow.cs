using System.Collections.Generic;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;

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

            AutomationUi.SetSiteStatus(
                account,
                siteRun,
                logMessage: "Registration submitted. Waiting for GeeTest and login confirmation...");

            var deadline = DateTime.UtcNow.AddSeconds(RegistrationResultTimeoutSeconds);
            var captchaWasSeen = false;
            while (DateTime.UtcNow < deadline)
            {
                executionContext.Checkpoint();

                var state = ReadRegistrationState(javaScript);
                if (state.Equals("success", StringComparison.OrdinalIgnoreCase))
                {
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

                if (state.Equals("captcha", StringComparison.OrdinalIgnoreCase)
                    && !captchaWasSeen)
                {
                    captchaWasSeen = true;
                    AutomationUi.SetSiteStatus(
                        account,
                        siteRun,
                        logMessage: "GeeTest is waiting in the mobile page. Complete it to continue...");
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
                    if (/(captcha|geetest|botion|xac minh|verification)/i.test(bodyText)
                        || document.querySelector(
                            '[id*="captcha" i], [class*="captcha" i], [id*="geetest" i], [class*="geetest" i], [id*="botion" i], [class*="botion" i]'))
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
            const candidates = elements('button, [role="button"], a, input[type="submit"]');
            return candidates.find(el => {
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
                        submit.removeAttribute('disabled');
                        submit.click();
                        finish(true, 'submitted');
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
