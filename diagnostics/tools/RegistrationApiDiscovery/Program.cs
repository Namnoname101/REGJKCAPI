using System.Diagnostics;
using System.Text.Json;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

var site = args.Length > 0 && Enum.TryParse<TargetSite>(args[0], ignoreCase: true, out var parsedSite)
    ? parsedSite
    : TargetSite.RR88;

var account = new AccountModel
{
    Username = $"c{Random.Shared.Next(1_000_000, 9_999_999)}",
    Password = $"Ct{Random.Shared.Next(100000, 999999)}x",
    PhoneNumber = $"09{Random.Shared.NextInt64(10_000_000, 99_999_999)}",
    FullName = "NGUYEN VAN TEST",
    Level2Password = Random.Shared.Next(100000, 999999).ToString(),
    AccountNumber = $"000{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}",
    BankName = "VIETCOMBANK",
    Branch = "HA NOI",
    Proxy = string.Empty,
    UserAgent = string.Empty
};

var profilePath = Path.Combine(
    AppContext.BaseDirectory,
    "DiscoveryProfiles",
    Guid.NewGuid().ToString("N"));

using var driver = DriverFactory.CreateDriver(
    profilePath,
    userAgent: null,
    proxy: null,
    headless: false);

if (driver is ChromeDriver chromeDriver)
{
    chromeDriver.ExecuteCdpCommand(
        "Page.addScriptToEvaluateOnNewDocument",
        new Dictionary<string, object>
        {
            ["source"] = NetworkHookScript()
        });
}

var stopwatch = Stopwatch.StartNew();
driver.Navigate().GoToUrl(GetRegistrationUrl(site));
Console.WriteLine($"DISCOVERY opened {site} in {stopwatch.Elapsed.TotalSeconds:0.000}s");

var js = (IJavaScriptExecutor)driver;
var result = js.ExecuteAsyncScript(
    FillAndSubmitScript(),
    account.GetUsername(site),
    account.GetPassword(site),
    account.FullName,
    FormatPhone(site, account.PhoneNumber),
    site == TargetSite.RR88,
    20_000);
Console.WriteLine($"DISCOVERY submit result: {JsonSerializer.Serialize(result)}");

var waitUntil = DateTimeOffset.UtcNow.AddSeconds(25);
while (DateTimeOffset.UtcNow < waitUntil)
{
    var count = Convert.ToInt64(js.ExecuteScript("return (window.__vibeRequests || []).length;") ?? 0);
    var href = driver.Url;
    Console.WriteLine($"DISCOVERY +{stopwatch.Elapsed.TotalSeconds:0.000}s url={href} requests={count}");

    if (href.Contains("/home", StringComparison.OrdinalIgnoreCase)
        || href.Contains("/member", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Thread.Sleep(1_000);
}

var requestsJson = js.ExecuteScript(
    "return JSON.stringify((window.__vibeRequests || []).filter(r => /rsa|register|member|captcha|affiliate|botion|geetest|bcaptcha|gcaptcha/i.test(String(r.url || ''))).slice(-80));")
    ?.ToString()
    ?? "[]";
var cryptoJson = js.ExecuteScript(
    "return JSON.stringify((window.__vibeCrypto || []).slice(-50));")
    ?.ToString()
    ?? "[]";

Console.WriteLine("DISCOVERY requests:");
Console.WriteLine(requestsJson);
Console.WriteLine("DISCOVERY crypto:");
Console.WriteLine(cryptoJson);

static string GetRegistrationUrl(TargetSite site) =>
    site switch
    {
        TargetSite.RR88 => "http://gioithieu.rr3311.com/m/register?referralCode=mrh6472",
        TargetSite.MM88 => "http://gioithieu.mm88p.com/m/register?referralCode=mgr0794",
        TargetSite.XX88 => "http://gioithieu.xx88.fun/m/register?referralCode=gql2969",
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, "No registration URL for discovery.")
    };

static string FormatPhone(TargetSite site, string phoneNumber) =>
    site switch
    {
        TargetSite.XX88 => PhoneNumberFormatter.FormatTenDigitWithLeadingZero(phoneNumber),
        TargetSite.RR88 or TargetSite.MM88 => PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(phoneNumber),
        _ => PhoneNumberFormatter.FormatTenDigitWithLeadingZero(phoneNumber)
    };

static string NetworkHookScript() =>
    """
    (() => {
        if (window.__vibeNetworkHooked) return;
        window.__vibeNetworkHooked = true;
        window.__vibeRequests = [];
        const trim = value => {
            const text = String(value ?? '');
            return text.length > 4000 ? text.slice(0, 4000) + '...[trimmed]' : text;
        };
        const push = entry => {
            try {
                window.__vibeRequests.push({
                    at: Date.now(),
                    ...entry,
                    body: trim(entry.body),
                    response: trim(entry.response)
                });
            } catch {}
        };
        window.__vibeCrypto = [];

        const cryptoPush = entry => {
            try {
                const stringify = window.__vibeOriginalJsonStringify || JSON.stringify;
                window.__vibeCrypto.push({
                    at: Date.now(),
                    ...entry,
                    args: trim(stringify.call(JSON, entry.args)),
                    result: trim(stringify.call(JSON, entry.result))
                });
            } catch {}
        };

        const wrapCrypto = name => {
            const fn = window[name];
            if (typeof fn !== 'function' || fn.__vibeWrapped) return;
            const wrapped = function(...args) {
                const started = Date.now();
                try {
                    const value = fn.apply(this, args);
                    if (value && typeof value.then === 'function') {
                        return value.then(result => {
                            cryptoPush({ name, started, elapsedMs: Date.now() - started, args, result });
                            return result;
                        }, error => {
                            cryptoPush({ name, started, elapsedMs: Date.now() - started, args, error: String(error && error.message ? error.message : error) });
                            throw error;
                        });
                    }
                    cryptoPush({ name, started, elapsedMs: Date.now() - started, args, result: value });
                    return value;
                } catch (error) {
                    cryptoPush({ name, started, elapsedMs: Date.now() - started, args, error: String(error && error.message ? error.message : error) });
                    throw error;
                }
            };
            Object.defineProperty(wrapped, '__vibeWrapped', { value: true });
            try { window[name] = wrapped; } catch {}
        };

        setInterval(() => {
            ['rsaEncrypt', 'rsaEncryptV2', 'reRsa', 'reRsaV2'].forEach(wrapCrypto);
        }, 10);

        const originalFetch = window.fetch;
        const originalAppendChild = Element.prototype.appendChild;
        Element.prototype.appendChild = function(child) {
            try {
                if (child && String(child.tagName || '').toUpperCase() === 'SCRIPT' && child.src) {
                    push({ kind: 'script', method: 'GET', url: child.src, body: '' });
                }
            } catch {}
            return originalAppendChild.apply(this, arguments);
        };

        const originalStringify = JSON.stringify;
        window.__vibeOriginalJsonStringify = originalStringify;
        JSON.stringify = function(value, replacer, space) {
            try {
                if (value && typeof value === 'object' && (
                    Object.prototype.hasOwnProperty.call(value, 'username') ||
                    Object.prototype.hasOwnProperty.call(value, 'mobileNum') ||
                    Object.prototype.hasOwnProperty.call(value, 'geetestSeccode') ||
                    Object.prototype.hasOwnProperty.call(value, 'geetestValidateV4'))) {
                    cryptoPush({ name: 'JSON.stringify', args: [value], result: value });
                }
            } catch {}
            return originalStringify.apply(this, arguments);
        };

        const originalSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;
        XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
            try {
                this.__vibeRequest = this.__vibeRequest || { kind: 'xhr' };
                this.__vibeRequest.headers = this.__vibeRequest.headers || {};
                this.__vibeRequest.headers[name] = value;
            } catch {}
            return originalSetRequestHeader.apply(this, arguments);
        };

        if (originalFetch) {
            window.fetch = async function(input, init) {
                const url = typeof input === 'string' ? input : input && input.url;
                const method = init && init.method ? init.method : 'GET';
                const body = init && init.body ? init.body : '';
                const entry = { kind: 'fetch', url, method, body, headers: init && init.headers ? init.headers : {} };
                try {
                    const response = await originalFetch.apply(this, arguments);
                    entry.status = response.status;
                    try { entry.response = await response.clone().text(); } catch {}
                    push(entry);
                    return response;
                } catch (error) {
                    entry.error = error && error.message ? error.message : String(error);
                    push(entry);
                    throw error;
                }
            };
        }

        const originalOpen = XMLHttpRequest.prototype.open;
        const originalSend = XMLHttpRequest.prototype.send;
        XMLHttpRequest.prototype.open = function(method, url) {
            this.__vibeRequest = { kind: 'xhr', method, url };
            return originalOpen.apply(this, arguments);
        };
        XMLHttpRequest.prototype.send = function(body) {
            const entry = this.__vibeRequest || { kind: 'xhr' };
            entry.body = body || '';
            this.addEventListener('loadend', () => {
                entry.status = this.status;
                try { entry.response = this.responseText; } catch {}
                push(entry);
            });
            return originalSend.apply(this, arguments);
        };
    })();
    """;

static string FillAndSubmitScript() =>
    """
    const username = String(arguments[0] || '');
    const password = String(arguments[1] || '');
    const fullName = String(arguments[2] || '');
    const phoneNumber = String(arguments[3] || '');
    const phoneFirst = Boolean(arguments[4]);
    const timeoutMs = Number(arguments[5] || 20000);
    const done = arguments[arguments.length - 1];
    const started = performance.now();
    let timer = null;
    let completed = false;

    function finish(ok, message) {
        if (completed) return;
        completed = true;
        if (timer) clearTimeout(timer);
        done({ ok, message, elapsedMs: Math.round(performance.now() - started) });
    }

    function normalize(text) {
        return String(text || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
    }

    function visible(el) {
        if (!el) return false;
        const style = getComputedStyle(el);
        const rect = el.getBoundingClientRect();
        return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
    }

    function setValue(el, value) {
        const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
        if (descriptor && descriptor.set) descriptor.set.call(el, value);
        else el.value = value;
        el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: value }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        el.dispatchEvent(new Event('blur', { bubbles: true }));
    }

    function tick() {
        document.querySelectorAll('.van-overlay, .modal-backdrop, .overlay').forEach(el => el.remove());
        const inputs = Array.from(document.querySelectorAll('input')).filter(input => {
            const type = String(input.type || '').toLowerCase();
            return !input.disabled && type !== 'hidden' && type !== 'checkbox' && type !== 'radio' && visible(input);
        });
        const button = Array.from(document.querySelectorAll('button, .submit_btn, .register-btn'))
            .find(el => visible(el) && !['huy', 'cancel', 'dong'].includes(normalize(el.innerText || el.textContent).trim()));

        if (inputs.length >= 3 && button) {
            const passwords = inputs.filter(input => String(input.type || '').toLowerCase() === 'password');
            const usernameInput = inputs.find(input => !passwords.includes(input)) || inputs[0];
            setValue(usernameInput, username);
            passwords.forEach(input => setValue(input, password));
            const remaining = inputs.filter(input => input !== usernameInput && !passwords.includes(input));
            const phoneInput = remaining.find(input =>
                /mobile|phone|tel/i.test(`${input.name || ''} ${input.className || ''} ${input.placeholder || ''}`));
            if (phoneInput) {
                setValue(phoneInput, phoneNumber);
                const nameInput = remaining.find(input => input !== phoneInput);
                if (nameInput) setValue(nameInput, fullName);
            } else {
                if (phoneFirst) {
                    if (remaining[0]) setValue(remaining[0], phoneNumber);
                    if (remaining[1]) setValue(remaining[1], fullName);
                } else {
                    if (remaining[0]) setValue(remaining[0], fullName);
                    if (remaining[1]) setValue(remaining[1], phoneNumber);
                }
            }

            timer = setTimeout(() => {
                try {
                    button.disabled = false;
                    button.removeAttribute('disabled');
                    button.click();
                    finish(true, 'clicked');
                } catch (error) {
                    finish(false, error && error.message ? error.message : String(error));
                }
            }, 100);
            return;
        }

        if (performance.now() - started >= timeoutMs) {
            finish(false, `timeout: inputs=${inputs.length}`);
            return;
        }

        timer = setTimeout(tick, 50);
    }

    tick();
    """;
