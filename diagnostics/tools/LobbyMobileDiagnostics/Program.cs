using System.Collections;
using System.Text.Json;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

var targets = new[]
{
    (TargetSite.GG88, "https://www.gg88v.com/home/register?id=456510325"),
    (TargetSite.LLWIN, "https://llwin.app/home/register?id=924999213")
};

foreach (var (site, url) in targets)
{
    RunMobileFormProbe(site, url);
}

Console.WriteLine("LOBBY MOBILE DIAGNOSTICS: PASS");
return;

static void RunMobileFormProbe(TargetSite site, string url)
{
    var profilePath = Path.Combine(
        Path.GetTempPath(),
        $"vibecode-lobby-mobile-{site}-{Guid.NewGuid():N}");
    IWebDriver? driver = null;

    try
    {
        driver = DriverFactory.CreateDriver(
            profilePath,
            userAgent: null,
            proxy: null,
            headless: false,
            site);
        driver.Navigate().GoToUrl(url);

        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));
        wait.Until(browser =>
        {
            var source = browser.PageSource;
            return !string.IsNullOrWhiteSpace(source)
                   && source.Contains("<body", StringComparison.OrdinalIgnoreCase);
        });

        driver.SwitchTo().DefaultContent();
        var pwaFrame = driver.FindElements(
                By.CssSelector(
                    "iframe.pwa-iframe, iframe[class*='pwa-iframe'], " +
                    "iframe[src*='fixed.share=1'], iframe[src*='restore=1']"))
            .FirstOrDefault();
        if (pwaFrame is not null)
        {
            driver.SwitchTo().Frame(pwaFrame);
        }

        var javaScript = (IJavaScriptExecutor)driver;
        var title = driver.Title;
        var userAgent = javaScript.ExecuteScript("return navigator.userAgent")?.ToString() ?? string.Empty;
        if (!userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{site}: mobile user agent was not applied.");
        }

        if (title.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
            || driver.PageSource.Contains("cf-error-details", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{site}: Cloudflare blocked the mobile browser.");
        }

        var result = javaScript.ExecuteAsyncScript(
            """
            const done = arguments[arguments.length - 1];
            const started = performance.now();
            const normalize = value => String(value || '')
                .normalize('NFD')
                .replace(/[\u0300-\u036f]/g, '')
                .toLowerCase()
                .replace(/\s+/g, ' ')
                .trim();
            const visible = el => {
                if (!el) return false;
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return rect.width > 0 && rect.height > 0
                    && style.display !== 'none'
                    && style.visibility !== 'hidden';
            };
            const inspect = () => {
                const inputs = Array.from(document.querySelectorAll('input, textarea, select'))
                    .filter(visible);
                const passwords = inputs.filter(el =>
                    String(el.type || '').toLowerCase() === 'password'
                    || /password|passwd|mat khau/i.test(normalize([
                        el.name,
                        el.id,
                        el.placeholder,
                        el.getAttribute('aria-label')
                    ].filter(Boolean).join(' '))));
                if (inputs.length >= 2 && passwords.length >= 1) {
                    done({
                        ok: true,
                        url: location.href,
                        inputs: inputs.map(el => ({
                            type: String(el.type || el.tagName || ''),
                            name: String(el.name || ''),
                            id: String(el.id || ''),
                            placeholder: String(el.placeholder || ''),
                            aria: String(el.getAttribute('aria-label') || '')
                        })),
                        buttons: Array.from(document.querySelectorAll(
                            'button, [role="button"], a, input[type="submit"]'))
                            .filter(visible)
                            .map(el => ({
                                text: normalize(el.innerText || el.textContent || el.value),
                                id: String(el.id || ''),
                                disabled: Boolean(el.disabled)
                            }))
                            .filter(item => item.text.length > 0 && item.text.length < 100)
                    });
                    return;
                }

                const register = Array.from(document.querySelectorAll(
                    'button, a, [role="button"], [class*="register" i]'))
                    .filter(visible)
                    .find(el => {
                        const text = normalize(el.innerText || el.textContent);
                        return text.length < 80
                            && /(dang ky|register|sign up)/i.test(text)
                            && !/(da dang ky|already)/i.test(text);
                    });
                if (register) register.click();

                if (performance.now() - started >= 20000) {
                    done({
                        ok: false,
                        url: location.href,
                        body: String(document.body?.innerText || '').slice(0, 500)
                    });
                    return;
                }
                setTimeout(inspect, 200);
            };
            inspect();
            """);

        var dictionary = result as IDictionary
                         ?? throw new InvalidOperationException($"{site}: diagnostic result was invalid.");
        var ok = dictionary["ok"] is bool value && value;
        if (!ok)
        {
            var runtime = javaScript.ExecuteScript(
                """
                return {
                    title: document.title,
                    readyState: document.readyState,
                    sourceLength: document.documentElement?.outerHTML?.length || 0,
                    bodyChildren: document.body?.children?.length || 0,
                    appChildren: document.querySelector('#app')?.children?.length || 0,
                    appHtml: String(document.querySelector('#app')?.innerHTML || '').slice(0, 3000),
                    retry: sessionStorage.getItem('__ensure-entry-retry'),
                    inputs: Array.from(document.querySelectorAll('input, textarea, select'))
                        .map(el => ({
                            type: el.type || el.tagName,
                            name: el.name || '',
                            id: el.id || '',
                            placeholder: el.placeholder || '',
                            className: el.className || '',
                            disabled: Boolean(el.disabled),
                            rect: [Math.round(el.getBoundingClientRect().width), Math.round(el.getBoundingClientRect().height)]
                        })),
                    resources: performance.getEntriesByType('resource')
                        .slice(-30)
                        .map(item => ({
                            name: item.name,
                            status: item.responseStatus || 0,
                            duration: Math.round(item.duration)
                        }))
                };
                """);
            throw new InvalidOperationException(
                $"{site}: registration form not found. Page: {dictionary["url"]}; " +
                $"Body: {dictionary["body"]}; Runtime: {JsonSerializer.Serialize(runtime)}");
        }

        Console.WriteLine($"{site}: mobile page and registration form are reachable.");
        Console.WriteLine($"{site}: {FormatInputs(dictionary["inputs"])}");
        Console.WriteLine($"{site}: {FormatButtons(dictionary["buttons"])}");
    }
    finally
    {
        try
        {
            driver?.Quit();
        }
        catch
        {
            // The probe result above is more useful than a Chrome shutdown error.
        }

        if (Directory.Exists(profilePath)
            && profilePath.StartsWith(
                Path.GetFullPath(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(profilePath).StartsWith(
                $"vibecode-lobby-mobile-{site}-",
                StringComparison.Ordinal))
        {
            try
            {
                Directory.Delete(profilePath, recursive: true);
            }
            catch (IOException)
            {
                // Chrome can briefly retain chrome_debug.log after Quit().
            }
            catch (UnauthorizedAccessException)
            {
                // A locked temporary profile must not hide the probe result.
            }
        }
    }
}

static string FormatInputs(object? rawInputs)
{
    if (rawInputs is not IEnumerable inputs)
    {
        return "no input metadata";
    }

    var descriptions = new List<string>();
    foreach (var rawInput in inputs)
    {
        if (rawInput is not IDictionary input)
        {
            continue;
        }

        descriptions.Add(
            $"type={input["type"]}, name={input["name"]}, id={input["id"]}, " +
            $"placeholder={input["placeholder"]}");
    }

    return string.Join(" | ", descriptions);
}

static string FormatButtons(object? rawButtons)
{
    if (rawButtons is not IEnumerable buttons)
    {
        return "no button metadata";
    }

    var descriptions = new List<string>();
    foreach (var rawButton in buttons)
    {
        if (rawButton is not IDictionary button)
        {
            continue;
        }

        descriptions.Add(
            $"text={button["text"]}, id={button["id"]}, disabled={button["disabled"]}");
    }

    return string.Join(" | ", descriptions);
}
