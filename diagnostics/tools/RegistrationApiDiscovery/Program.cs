using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

var site = args.Length > 0 && Enum.TryParse<TargetSite>(args[0], ignoreCase: true, out var parsedSite)
    ? parsedSite
    : TargetSite.RR88;
var proxy = Environment.GetEnvironmentVariable("VIBECODE_PROXY")
            ?? (args.Length > 1 ? args[1] : string.Empty);

var account = CreateRandomAccount(proxy);

if (site == TargetSite.GG88
    && string.Equals(
        Environment.GetEnvironmentVariable("VIBECODE_DIRECT_GG"),
        "1",
        StringComparison.Ordinal))
{
    var benchmarkCount = int.TryParse(
        Environment.GetEnvironmentVariable("VIBECODE_GG_BENCHMARK_COUNT"),
        out var parsedCount)
        ? Math.Clamp(parsedCount, 1, 10)
        : 1;
    var accounts = Enumerable.Range(0, benchmarkCount)
        .Select(index => index == 0 ? account : CreateRandomAccount(proxy))
        .ToArray();
    object[] runs;
    if (string.Equals(
            Environment.GetEnvironmentVariable("VIBECODE_GG_BENCHMARK_PARALLEL"),
            "1",
            StringComparison.Ordinal))
    {
        runs = await Task.WhenAll(accounts.Select((currentAccount, index) =>
            RunDirectAccountAsync(currentAccount, site, index + 1)));
    }
    else
    {
        runs = new object[accounts.Length];
        for (var index = 0; index < accounts.Length; index++)
        {
            runs[index] = await RunDirectAccountAsync(accounts[index], site, index + 1);
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new { mode = "direct-api", runs }));
    return;
}

var profilePath = Path.Combine(
    AppContext.BaseDirectory,
    "DiscoveryProfiles",
    Guid.NewGuid().ToString("N"));

var assetScan = string.Equals(
    Environment.GetEnvironmentVariable("VIBECODE_GG_ASSET_SCAN"),
    "1",
    StringComparison.Ordinal);
using var driver = DriverFactory.CreateDriver(
    profilePath,
    userAgent: null,
    proxy: account.Proxy,
    headless: assetScan,
    site);

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
if (site == TargetSite.GG88 && assetScan)
{
    var assetDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (DateTimeOffset.UtcNow < assetDeadline)
    {
        if (js.ExecuteScript(
                "return document.readyState === 'complete' && Boolean(window.initGeeGuard) && String(document.body?.innerText || '').length > 0;")
            is true)
        {
            break;
        }
        Thread.Sleep(100);
    }

    Console.WriteLine("ASSET_PAGE=" + js.ExecuteScript(
        "return JSON.stringify({url:location.href,title:document.title,readyState:document.readyState,head:Array.from(document.querySelectorAll('script[src],link[href]')).map(el=>({tag:el.tagName,rel:el.rel||'',url:el.src||el.href})),body:String(document.body?.innerText||'').slice(0,500)});"));
    var assetReport = js.ExecuteAsyncScript(
        """
        const done = arguments[arguments.length - 1];
        const needles = [
            'withdrawMethodId',
            'bankInfo',
            'getUserWithdrawAccountRules',
            'bindWithdrawAccount',
            'useTypeId',
            'withdrawInfoV3',
            'checkAddWithdrawAccountStatus',
            'addWithdrawAccountType',
            'withdrawalPassword',
            'formBody',
            'twoStepVerifyInfo',
            'withdrawPassword',
            'paymentMethodId',
            'payBindCardV2'
            ,'submitRechargeBank'
            ,'bankMatchTrigger'
            ,'cardNumber'
            ,'bindBank'
            ,'bankCard'
            ,'as hT'
            ,'as hU'
            ,'ome='
            ,'nme='
            ,'CG='
            ,'wG='
            ,'_P='
            ,'getBankListByCurrency'
            ,'bankInfoV2'
            ,'eV='
            ,'as bZ'
            ,'OF='
            ,'as hR'
            ,'as hS'
            ,'staticOnly'
            ,'x-data-mode'
            ,'chipher'
            ,'omitKey'
            ,'useCacheOnError'
            ,'oss:'
        ];
        const seeds = [
            ...Array.from(document.scripts).map(script => script.src),
            ...Array.from(document.querySelectorAll('link[href]')).map(link => link.href),
            ...performance.getEntriesByType('resource').map(entry => entry.name)
        ].filter(url => /\.(?:js|json)(?:\?|$)/i.test(url));

        (async () => {
            const queue = [...new Set(seeds)];
            const visited = new Set();
            const files = [];
            const hits = [];
            while (queue.length > 0 && visited.size < 80) {
                const url = queue.shift();
                if (!url || visited.has(url)) continue;
                visited.add(url);

                let source;
                try {
                    const response = await fetch(url, { credentials: 'include' });
                    if (!response.ok) {
                        files.push({ url, status: response.status, length: 0, references: [] });
                        continue;
                    }
                    source = await response.text();
                } catch (error) {
                    files.push({ url, error: String(error), length: 0, references: [] });
                    continue;
                }

                const references = source.match(/(?:https?:\/\/[^"'`\s]+|(?:\.\.\/|\.\/|\/)?(?:assets\/)?[\w./-]+\.(?:json|js))(?=$|[?"'`\s])(?:\?[^"'`\s]*)?/gi) || [];
                files.push({
                    url,
                    length: source.length,
                    references: [...new Set(references)].slice(0, 100),
                    preview: source.length <= 2000
                        ? source
                        : /0_WithdrawChunk/i.test(url)
                            ? source.slice(0, 8000)
                            : undefined
                });
                for (const reference of references) {
                    try {
                        const child = new URL(
                            reference.startsWith('assets/') ? '/' + reference : reference,
                            url).href;
                        if (child.startsWith(location.origin) && !visited.has(child)) {
                            if (/Withdraw/i.test(child)) queue.unshift(child);
                            else queue.push(child);
                        }
                    } catch {
                    }
                }

                for (const needle of needles) {
                    let index = source.indexOf(needle);
                    let count = 0;
                    while (index >= 0 && count++ < 6) {
                        const start = Math.max(0, index - 900);
                        hits.push({
                            url,
                            needle,
                            snippet: source.slice(start, Math.min(source.length, index + needle.length + 900))
                        });
                        index = source.indexOf(needle, index + needle.length);
                    }
                }

                for (const alias of ['hR', 'hS']) {
                    const exportMatch = source.match(
                        new RegExp('([A-Za-z_$][\\w$]*)\\s+as\\s+' + alias + '(?:,|})'));
                    if (!exportMatch) continue;
                    const symbol = exportMatch[1];
                    const index = source.indexOf(symbol + '=');
                    if (index < 0) continue;
                    hits.push({
                        url,
                        needle: alias + 'Definition',
                        snippet: source.slice(
                            Math.max(0, index - 200),
                            Math.min(source.length, index + 1600))
                    });
                }

                if (/commonChunk/i.test(url)) {
                    try {
                        const module = await import(url);
                        const store = module.bX();
                        await store.getWithdrawSetting(true);
                        const banks = store.getBankListByCurrency('VND');
                        const requests = (window.__vibeRequests || [])
                            .filter(item => /withdrawSettingV3/i.test(String(item.url || '')))
                            .slice(-10);
                        hits.push({
                            url,
                            needle: 'bankSettingsResult',
                            snippet: JSON.stringify({ banks, requests })
                        });
                    } catch (error) {
                        hits.push({
                            url,
                            needle: 'bankSettingsResult',
                            snippet: JSON.stringify({ error: String(error) })
                        });
                    }
                }
            }
            done(JSON.stringify({ files, hits }));
        })().catch(error => done(JSON.stringify({ error: String(error) })));
        """)?.ToString() ?? "{}";
    Console.WriteLine("ASSET_REPORT=" + assetReport);
    return;
}

var result = site == TargetSite.GG88
    ? SubmitGgForm(driver, account, stopwatch)
    : js.ExecuteAsyncScript(
        FillAndSubmitScript(),
        account.GetUsername(site),
        account.GetPassword(site),
        account.FullName,
        FormatPhone(site, account.PhoneNumber),
        site == TargetSite.RR88,
        20_000);
Console.WriteLine($"DISCOVERY submit result: {JsonSerializer.Serialize(result)}");

var waitUntil = DateTimeOffset.UtcNow.AddSeconds(site == TargetSite.GG88 ? 8 : 25);
while (DateTimeOffset.UtcNow < waitUntil)
{
    var count = Convert.ToInt64(js.ExecuteScript("return (window.__vibeRequests || []).length;") ?? 0);
    var href = driver.Url;
    Console.WriteLine($"DISCOVERY +{stopwatch.Elapsed.TotalSeconds:0.000}s url={href} requests={count}");

    var registrationRequestSeen = js.ExecuteScript(
        "return (window.__vibeRequests || []).some(r => /\\/api\\/member\\/(?:check\\/)?register/i.test(String(r.url || '')));") is true;
    if (registrationRequestSeen)
    {
        break;
    }

    if (site != TargetSite.GG88
        && (href.Contains("/home", StringComparison.OrdinalIgnoreCase)
        || href.Contains("/member", StringComparison.OrdinalIgnoreCase))
       )
    {
        break;
    }

    Thread.Sleep(1_000);
}

var requestsJson = js.ExecuteScript(
    "return JSON.stringify((window.__vibeRequests || []).filter(r => /register|member|withdraw|bank|finance|certify|security|captcha|geetest|botion|gee/i.test(String(r.url || ''))).slice(-120));")
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
var formStateJson = js.ExecuteScript(
    """
    return JSON.stringify({
        inputs: Array.from(document.querySelectorAll('input')).filter(el => {
            const rect = el.getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
        }).map(el => ({
            type: el.type,
            value: el.value,
            placeholder: el.placeholder,
            className: String(el.className || ''),
            parentClass: String(el.parentElement?.className || '')
        })),
        buttons: Array.from(document.querySelectorAll('button, [role="button"], input[type="submit"]'))
            .filter(el => {
                const rect = el.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            }).map(el => ({
                text: String(el.innerText || el.textContent || el.value || '').trim(),
                className: String(el.className || ''),
                parentClass: String(el.parentElement?.className || ''),
                disabled: Boolean(el.disabled),
                rect: [Math.round(el.getBoundingClientRect().x), Math.round(el.getBoundingClientRect().y)]
            })).filter(item => item.text.length > 0 && item.text.length < 100),
        messages: Array.from(document.querySelectorAll(
            '[role="alert"], [role="status"], [class*="message" i], [class*="error" i], [class*="toast" i]'))
            .filter(el => {
                const rect = el.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            }).map(el => String(el.innerText || el.textContent || '').trim())
            .filter(Boolean).slice(0, 30)
    });
    """)?.ToString() ?? "{}";
Console.WriteLine("DISCOVERY form state:");
Console.WriteLine(formStateJson);
if (driver is ITakesScreenshot screenshotDriver)
{
    var screenshotPath = Path.Combine(Path.GetTempPath(), "vibecode-gg-mobile.png");
    screenshotDriver.GetScreenshot().SaveAsFile(screenshotPath);
    Console.WriteLine($"DISCOVERY screenshot: {screenshotPath}");
}

static object SubmitGgForm(
    IWebDriver driver,
    AccountModel account,
    Stopwatch stopwatch)
{
    var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(
        driver,
        TimeSpan.FromSeconds(20));
    var openerJs = (IJavaScriptExecutor)driver;
    openerJs.ExecuteScript(
        """
        const visible = element => {
            const rect = element.getBoundingClientRect();
            return rect.width > 0
                && rect.height > 0
                && rect.left >= 0
                && rect.right <= window.innerWidth
                && rect.top >= 0
                && rect.bottom <= window.innerHeight;
        };
        const hasVisibleForm = Array.from(document.querySelectorAll('input'))
            .filter(input => input.type !== 'checkbox')
            .some(visible);
        if (!hasVisibleForm) {
            const opener = Array.from(document.querySelectorAll('#insideRegisterSubmitClick'))
                .find(button => visible(button) && !button.disabled);
            opener?.click();
        }
        """);
    Thread.Sleep(700);
    var fields = wait.Until(browser =>
    {
        var visible = browser.FindElements(By.CssSelector("input"))
            .Where(input => input.Displayed && input.Enabled)
            .ToArray();
        return visible.Length >= 4 ? visible : null;
    }) ?? throw new WebDriverTimeoutException("GG88 registration fields did not appear.");

    static IWebElement FindByPlaceholder(
        IEnumerable<IWebElement> inputs,
        params string[] fragments) =>
        inputs.First(input => fragments.Any(fragment =>
            (input.GetAttribute("placeholder") ?? string.Empty)
                .Contains(fragment, StringComparison.OrdinalIgnoreCase)));

    var usernameInput = FindByPlaceholder(fields, "tai khoan", "tài khoản");
    var passwordInput = FindByPlaceholder(fields, "mat khau", "mật khẩu");
    var phoneInput = FindByPlaceholder(fields, "SDT", "SĐT");
    var nameInput = FindByPlaceholder(fields, "Ho va Ten", "Họ và Tên");

    foreach (var (input, value) in new[]
             {
                 (usernameInput, account.GetUsername(TargetSite.GG88)),
                 (passwordInput, account.GetPassword(TargetSite.GG88)),
                 (phoneInput, FormatPhone(TargetSite.GG88, account.PhoneNumber)),
                 (nameInput, account.FullName.ToUpperInvariant())
             })
    {
        input.Clear();
        input.SendKeys(value);
        input.SendKeys(Keys.Tab);
    }

    var js = (IJavaScriptExecutor)driver;
    js.ExecuteScript(
        """
        const values = Array.from(arguments);
        const visible = element => {
            const rect = element.getBoundingClientRect();
            return rect.width > 0
                && rect.height > 0
                && rect.left >= 0
                && rect.right <= window.innerWidth
                && rect.top >= 0
                && rect.bottom <= window.innerHeight;
        };
        const inputs = Array.from(document.querySelectorAll('input'))
            .filter(input => visible(input) && input.type !== 'checkbox');
        const valueSetter = Object.getOwnPropertyDescriptor(
            HTMLInputElement.prototype,
            'value').set;
        inputs.slice(0, values.length).forEach((input, index) => {
            valueSetter.call(input, values[index]);
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
            input.dispatchEvent(new Event('blur', { bubbles: true }));
        });
        document.querySelectorAll('input[type="checkbox"]').forEach(input => {
            if (input.checked) return;
            input.click();
        });
        """,
        account.GetUsername(TargetSite.GG88),
        account.GetPassword(TargetSite.GG88),
        FormatPhone(TargetSite.GG88, account.PhoneNumber),
        account.FullName.ToUpperInvariant());

    var submit = wait.Until(browser => browser.FindElements(By.Id("insideRegisterSubmitClick"))
            .Where(button => button.Displayed && button.Enabled)
            .OrderByDescending(button => button.Location.Y)
            .FirstOrDefault())
        ?? throw new WebDriverTimeoutException("GG88 visible registration submit button did not appear.");
    submit.Click();

    return new
    {
        ok = true,
        message = "selenium-clicked",
        elapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds)
    };
}

static AccountModel CreateRandomAccount(string proxy)
{
    var naturalUsername = $"minhan{Random.Shared.Next(10_000, 99_999)}";
    return new AccountModel
    {
        Username = naturalUsername,
        Password = naturalUsername + "1",
        PhoneNumber = $"09{Random.Shared.NextInt64(10_000_000, 99_999_999)}",
        FullName = Environment.GetEnvironmentVariable("VIBECODE_TEST_FULL_NAME")
                   ?? "NGUYEN VAN AN",
        Level2Password = Random.Shared.Next(100000, 999999).ToString(),
        AccountNumber = Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString(),
        BankName = "VIETCOMBANK",
        Branch = "HA NOI",
        Proxy = proxy,
        UserAgent = string.Empty
    };
}

static async Task<object> RunDirectAccountAsync(
    AccountModel account,
    TargetSite site,
    int index)
{
    var totalWatch = Stopwatch.StartNew();
    var registrationWatch = Stopwatch.StartNew();
    var outcome = await DirectRegistrationWorkflow.TryRegisterAsync(
        account,
        site,
        CancellationToken.None);
    registrationWatch.Stop();

    DirectBankSetupOutcome? bankOutcome = null;
    var bankElapsedMs = 0L;
    if (outcome.Status == DirectRegistrationStatus.Success
        && outcome.Session is not null
        && string.Equals(
            Environment.GetEnvironmentVariable("VIBECODE_DIRECT_GG_BANK"),
            "1",
            StringComparison.Ordinal))
    {
        var bankWatch = Stopwatch.StartNew();
        bankOutcome = await DirectBankSetupWorkflow.TrySetupAsync(
            account,
            site,
            outcome.Session,
            CancellationToken.None);
        bankWatch.Stop();
        bankElapsedMs = bankWatch.ElapsedMilliseconds;
    }
    totalWatch.Stop();

    return new
    {
        index,
        elapsedMs = totalWatch.ElapsedMilliseconds,
        registrationElapsedMs = registrationWatch.ElapsedMilliseconds,
        bankElapsedMs,
        outcome.Status,
        outcome.Message,
        outcome.UsernameConflict,
        hasSession = outcome.Session is not null,
        bankStatus = bankOutcome?.Status.ToString(),
        bankMessage = bankOutcome?.Message
    };
}

static string GetRegistrationUrl(TargetSite site) =>
    site switch
    {
        TargetSite.RR88 => "https://www.rr3311.com/register?referralCode=hug0888&affiliateCode=gioithieu",
        TargetSite.MM88 => "https://www.mm88o.com/register?referralCode=zfr8673&affiliateCode=gioithieu",
        TargetSite.XX88 => "http://gioithieu.xx88.fun/m/register?referralCode=gql2969",
        TargetSite.GG88 => "https://www.gg88v.com/home/register?id=456510325",
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, "No registration URL for discovery.")
    };

static string FormatPhone(TargetSite site, string phoneNumber) =>
    site switch
    {
        TargetSite.XX88 => PhoneNumberFormatter.FormatTenDigitWithLeadingZero(phoneNumber),
        TargetSite.RR88 or TargetSite.MM88 or TargetSite.GG88 => PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(phoneNumber),
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
                    Object.prototype.hasOwnProperty.call(value, 'platformId') ||
                    Object.prototype.hasOwnProperty.call(value, 'passwd') ||
                    Object.prototype.hasOwnProperty.call(value, 'phone') ||
                    Object.prototype.hasOwnProperty.call(value, 'mobileNum') ||
                    Object.prototype.hasOwnProperty.call(value, 'withdraw_pass') ||
                    Object.prototype.hasOwnProperty.call(value, 'formBody') ||
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
        return String(text || '')
            .normalize('NFD')
            .replace(/[\u0300-\u036f]/g, '')
            .replace(/[đĐ]/g, 'd')
            .toLowerCase();
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
        const inputs = Array.from(document.querySelectorAll('input')).filter(input => {
            const type = String(input.type || '').toLowerCase();
            return !input.disabled && type !== 'hidden' && type !== 'checkbox' && type !== 'radio' && visible(input);
        });
        const metadata = input => normalize([
            input.name,
            input.id,
            input.placeholder,
            input.getAttribute('aria-label'),
            input.getAttribute('autocomplete')
        ].filter(Boolean).join(' '));

        if (performance.now() - started >= timeoutMs) {
            const buttons = Array.from(document.querySelectorAll(
                'button, a, [role="button"], input[type="submit"]'))
                .filter(visible)
                .map(el => normalize(el.innerText || el.textContent || el.value).trim())
                .filter(Boolean)
                .slice(0, 30);
            finish(false, JSON.stringify({
                inputs: inputs.map(input => ({
                    type: input.type,
                    name: input.name,
                    id: input.id,
                    placeholder: input.placeholder,
                    aria: input.getAttribute('aria-label'),
                    metadata: metadata(input)
                })),
                buttons
            }));
            return;
        }

        if (inputs.length >= 2) {
            const passwords = inputs.filter(input =>
                String(input.type || '').toLowerCase() === 'password'
                || /password|passwd|mat khau/.test(metadata(input)));
            const usernameInput = inputs.find(input =>
                /platform|username|account|tai khoan|ten dang nhap/.test(metadata(input)))
                || inputs.find(input => !passwords.includes(input));
            const phoneInput = inputs.find(input =>
                /mobile|phone|tel|sdt|dien thoai|so dien thoai/.test(metadata(input)));
            const nameInput = inputs.find(input =>
                /real.?name|full.?name|payee|ho.*ten|ten that/.test(metadata(input)));
            const submitCandidates = Array.from(document.querySelectorAll(
                'button, [role="button"], input[type="submit"], .submit_btn, .register-btn'))
                .filter(el => {
                    const label = normalize(el.innerText || el.textContent || el.value).trim();
                    return visible(el)
                        && /dang ky|register|sign up|submit|xac nhan/.test(label)
                        && !/dang nhap|login|huy|cancel|dong/.test(label);
                });
            const button = submitCandidates[submitCandidates.length - 1];

            if (!usernameInput || passwords.length === 0 || !button) {
                timer = setTimeout(tick, 50);
                return;
            }

            setValue(usernameInput, username);
            passwords.forEach(input => setValue(input, password));
            if (phoneInput) setValue(phoneInput, phoneNumber);
            if (nameInput) setValue(nameInput, fullName);

            document.querySelectorAll('input[type="checkbox"]').forEach(checkbox => {
                if (checkbox.disabled || checkbox.checked) return;
                const descriptor = Object.getOwnPropertyDescriptor(
                    HTMLInputElement.prototype,
                    'checked');
                if (descriptor && descriptor.set) descriptor.set.call(checkbox, true);
                else checkbox.checked = true;
                checkbox.dispatchEvent(new Event('input', { bubbles: true }));
                checkbox.dispatchEvent(new Event('change', { bubbles: true }));
            });

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

        const openRegister = Array.from(document.querySelectorAll(
            'button, a, [role="button"], [class*="register" i]'))
            .find(el => {
                const label = normalize(el.innerText || el.textContent).trim();
                return visible(el)
                    && /dang ky|register|sign up/.test(label)
                    && !/da dang ky|already/.test(label);
            });
        if (openRegister) openRegister.click();

        timer = setTimeout(tick, 50);
    }

    tick();
    """;
