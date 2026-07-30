using System.Text.Json;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

const string registrationUrl = "https://www.gg88v.com/home/register?id=456510325";
const string sourceUrl =
    "https://static.geetest.com/g5/v1/static/v1.2.4/js/geeGuard.a624a606c405df21c58408882e276ff8.js";
const string appId = "9ia4hndgblg9xihxcwgdjt9ztg8sjwaf";
const string captureAnchor = "function gr(t){";
const string wrapperAnchor = "}function br(g){";
const string cryptoAnchor =
    "while(!n||128!==n[VTzcy.$_CO(7)])e=i(32),n=new Z()[VTzcy.$_CO(315)](e);return $[VTzcy.$_CO(279)](t,e)[VTzcy.$_CO(75)](n);";
const string cryptoCapture =
    "while(!n||128!==n[VTzcy.$_CO(7)])e=i(32),n=new Z()[VTzcy.$_CO(315)](e);" +
    "window.__ggAesKey=e;window.__ggRsaCipher=n;" +
    "var a=$[VTzcy.$_CO(279)](t,e);window.__ggAesCipher=a;return a[VTzcy.$_CO(75)](n);";

var proxy = Environment.GetEnvironmentVariable("VIBECODE_PROXY") ?? string.Empty;
var profileRoot = Path.Combine(
    Path.GetTempPath(),
    "VibecodeGgFingerprint",
    Guid.NewGuid().ToString("N"));

using var driver = DriverFactory.CreateDriver(
    profileRoot,
    userAgent: null,
    proxy,
    headless: true,
    site: TargetSite.GG88,
    verifyProxy: true,
    deleteProfileOnDispose: true,
    enablePerformanceLogging: true,
    disableWebSecurity: string.Equals(
        Environment.GetEnvironmentVariable("VIBECODE_HEADER_ORDER_PROBE"),
        "1",
        StringComparison.Ordinal));

if (driver is not ChromeDriver chromeDriver)
{
    throw new InvalidOperationException("GG fingerprint probe requires ChromeDriver.");
}

if (string.Equals(
        Environment.GetEnvironmentVariable("VIBECODE_TLS_PROBE"),
        "1",
        StringComparison.Ordinal))
{
    driver.Navigate().GoToUrl("https://tls.peet.ws/api/all");
    await Task.Delay(1000);
    Console.WriteLine(driver.FindElement(By.TagName("body")).Text);
    return;
}

if (string.Equals(
        Environment.GetEnvironmentVariable("VIBECODE_HEADER_ORDER_PROBE"),
        "1",
        StringComparison.Ordinal))
{
    driver.Navigate().GoToUrl(registrationUrl);
    await Task.Delay(1000);
    var response = ((IJavaScriptExecutor)driver).ExecuteAsyncScript(
        """
        const done = arguments[arguments.length - 1];
        fetch('https://tls.peet.ws/api/all', {
          method: 'POST',
          headers: {
            'Client-Type': '3',
            'AppID': '9ia4hndgblg9xihxcwgdjt9ztg8sjwaf',
            'API-Version': '1',
            'Content-Type': 'application/x-www-form-urlencoded'
          },
          body: 'probe=1'
        }).then(value => value.text()).then(done).catch(error => done('ERROR:' + String(error)));
        """)?.ToString();
    Console.WriteLine(response);
    return;
}

chromeDriver.ExecuteCdpCommand(
    "Network.setBlockedURLs",
    new Dictionary<string, object>
    {
        ["urls"] = new[] { "*geeGuard*.js" }
    });

driver.Navigate().GoToUrl(registrationUrl);
await Task.Delay(1500);

var bootstrapSource = ((IJavaScriptExecutor)driver).ExecuteAsyncScript(
    """
    const done = arguments[arguments.length - 1];
    fetch('/libs/rc/gt@5/gt.js', { credentials: 'include' })
      .then(response => response.text())
      .then(text => done(text))
      .catch(error => done('FETCH_ERROR:' + String(error)));
    """)?.ToString() ?? string.Empty;
if (string.IsNullOrWhiteSpace(bootstrapSource)
    || bootstrapSource.StartsWith("FETCH_ERROR:", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"GeeGuard bootstrap fetch failed: {bootstrapSource}");
}
bootstrapSource = bootstrapSource.Replace(
    "var type = newConfig.type",
    "var type = newConfig.type || 'slide'",
    StringComparison.Ordinal);

using var sourceClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(20)
};
var source = await sourceClient.GetStringAsync(sourceUrl);
if (!source.Contains(captureAnchor, StringComparison.Ordinal))
{
    throw new InvalidOperationException("GeeGuard payload capture anchor was not found.");
}

var patchedSource = source.Replace(
    captureAnchor,
    "function __ggOriginalGr(t){",
    StringComparison.Ordinal);
if (!patchedSource.Contains(cryptoAnchor, StringComparison.Ordinal))
{
    throw new InvalidOperationException("GeeGuard crypto capture anchor was not found.");
}
patchedSource = patchedSource.Replace(
    cryptoAnchor,
    cryptoCapture,
    StringComparison.Ordinal);
if (!patchedSource.Contains(wrapperAnchor, StringComparison.Ordinal))
{
    throw new InvalidOperationException("GeeGuard crypto wrapper anchor was not found.");
}
patchedSource = patchedSource.Replace(
    wrapperAnchor,
    "}function gr(t){" +
    "var token=__ggOriginalGr(t);" +
    "(window.__ggCaptures||(window.__ggCaptures=[])).push({" +
    "token:token,payload:t,key:window.__ggAesKey,aes:window.__ggAesCipher,rsa:window.__ggRsaCipher});" +
    "return token;}function br(g){",
    StringComparison.Ordinal);
var resultJson = ((IJavaScriptExecutor)driver).ExecuteAsyncScript(
    """
    const source = arguments[0];
    const bootstrap = arguments[1];
    const appId = arguments[2];
    const captureOnly = arguments[3] === true;
    const done = arguments[arguments.length - 1];
    try {
      window.__ggNetwork = [];
      const originalOpen = XMLHttpRequest.prototype.open;
      const originalSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;
      const originalSend = XMLHttpRequest.prototype.send;
      XMLHttpRequest.prototype.open = function(method, url) {
        this.__ggNetworkEntry = { method, url: new URL(url, location.href).href, headers: {} };
        window.__ggNetwork.push(this.__ggNetworkEntry);
        return originalOpen.apply(this, arguments);
      };
      XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
        if (this.__ggNetworkEntry) this.__ggNetworkEntry.headers[name] = value;
        return originalSetRequestHeader.apply(this, arguments);
      };
      XMLHttpRequest.prototype.send = function(body) {
        if (this.__ggNetworkEntry) {
          this.__ggNetworkEntry.body = typeof body === 'string' ? body : String(body || '');
          this.addEventListener('loadend', () => {
            this.__ggNetworkEntry.status = this.status;
            this.__ggNetworkEntry.response = this.responseText;
            this.__ggNetworkEntry.responseHeaders = this.getAllResponseHeaders();
          });
        }
        if (captureOnly && /riskct\.geetest\.com\/g2\/api\/v1\/client_report/.test(this.__ggNetworkEntry?.url || '')) {
          return;
        }
        return originalSend.apply(this, arguments);
      };
      (0, eval)(source);
      (0, eval)(bootstrap);
      if (typeof window.initGeeGuard !== 'function') {
        done(JSON.stringify({
          error: 'window.initGeeGuard is not a function after patched bundle evaluation',
          globals: Object.keys(window).filter(key => /gee|guard|captcha/i.test(key)).sort(),
          resources: performance.getEntriesByType('resource')
            .map(entry => entry.name)
            .filter(name => /gt@5|gee|guard|riskct/i.test(name))
        }));
        return;
      }
      if (captureOnly) {
        window.initGeeGuard({ appId }, () => {});
        const started = Date.now();
        const timer = setInterval(() => {
          const capture = (window.__ggCaptures || []).at(-1);
          if (capture) {
            clearInterval(timer);
            const request = (window.__ggNetwork || [])
              .find(entry => /riskct\.geetest\.com\/g2\/api\/v1\/client_report/.test(entry.url));
            done(JSON.stringify({
              payloadJson: JSON.stringify(capture.payload),
              requestBody: request?.body || null,
              crypto: {
                key: Array.from(capture.key || []),
                aes: Array.from(capture.aes || []),
                rsa: Array.from(capture.rsa || [])
              }
            }));
          } else if (Date.now() - started > 5000) {
            clearInterval(timer);
            done(JSON.stringify({ error: 'Timed out waiting for an unsent GeeGuard payload.' }));
          }
        }, 20);
        return;
      }
      window.initGeeGuard({ appId }, result => {
        setTimeout(() => {
          const token = result?.data?.gee_token || null;
          const localId = result?.data?.local_id || null;
          const capture = (window.__ggCaptures || [])
            .slice()
            .reverse()
            .find(item => !localId || item?.payload?.local_id === localId) || null;
          done(JSON.stringify({
            tokenLength: token?.length || 0,
            result,
            payload: capture?.payload || null,
            crypto: {
              key: Array.from(capture?.key || []),
              aes: Array.from(capture?.aes || []),
              rsa: Array.from(capture?.rsa || [])
            },
            network: window.__ggNetwork,
            environment: {
              userAgent: navigator.userAgent,
              platform: navigator.platform,
              webdriver: navigator.webdriver,
              plugins: navigator.plugins?.length,
              mimeTypes: navigator.mimeTypes?.length,
              outerWidth: window.outerWidth,
              outerHeight: window.outerHeight,
              innerWidth: window.innerWidth,
              innerHeight: window.innerHeight,
              devicePixelRatio: window.devicePixelRatio
            }
          }));
        }, 100);
      });
    } catch (error) {
      done(JSON.stringify({ error: error?.stack || String(error) }));
    }
    """,
    patchedSource,
    bootstrapSource,
    appId,
    string.Equals(
        Environment.GetEnvironmentVariable("VIBECODE_GG_CAPTURE_ONLY"),
        "1",
        StringComparison.Ordinal))?.ToString();

if (string.IsNullOrWhiteSpace(resultJson))
{
    throw new InvalidOperationException("GG fingerprint probe returned no result.");
}

if (string.Equals(
        Environment.GetEnvironmentVariable("VIBECODE_GG_NETWORK_ONLY"),
        "1",
        StringComparison.Ordinal))
{
    var messages = chromeDriver.Manage().Logs.GetLog(LogType.Performance)
        .Select(entry => JsonDocument.Parse(entry.Message))
        .ToList();
    var requestIds = messages
        .Where(message => message.RootElement.GetProperty("message").GetProperty("method").GetString()
                          == "Network.requestWillBeSent")
        .Select(message => message.RootElement.GetProperty("message").GetProperty("params"))
        .Where(parameters => parameters.GetProperty("request").GetProperty("url").GetString()
                             == "https://riskct.geetest.com/g2/api/v1/client_report")
        .Select(parameters => parameters.GetProperty("requestId").GetString())
        .Where(requestId => requestId is not null)
        .ToHashSet(StringComparer.Ordinal);

    foreach (var message in messages)
    {
        var payload = message.RootElement.GetProperty("message");
        var parameters = payload.GetProperty("params");
        if (parameters.TryGetProperty("requestId", out var requestId)
            && requestIds.Contains(requestId.GetString()))
        {
            Console.WriteLine(JsonSerializer.Serialize(payload));
        }
    }

    foreach (var message in messages)
    {
        message.Dispose();
    }
    return;
}

using var result = JsonDocument.Parse(resultJson);
if (string.Equals(
        Environment.GetEnvironmentVariable("VIBECODE_GG_TOKEN_ONLY"),
        "1",
        StringComparison.Ordinal)
    && result.RootElement.TryGetProperty("result", out var geeResult)
    && geeResult.TryGetProperty("data", out var geeData)
    && geeData.TryGetProperty("gee_token", out var tokenElement))
{
    Console.WriteLine($"GG_TOKEN:{tokenElement.GetString()}");
    return;
}
Console.WriteLine(JsonSerializer.Serialize(
    result.RootElement,
    new JsonSerializerOptions { WriteIndented = true }));
