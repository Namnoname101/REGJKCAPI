using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

const string origin = "https://gioithieu.xx88.fun";
const string registrationUrl =
    "https://gioithieu.xx88.fun/m/register?referralCode=gql2969&r=gql2969";
const string fallbackCaptchaId = "2e3984aebc5904671a2a0a7c284b8c79";
const string mobileUserAgent =
    "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";

var rawProxy = Environment.GetEnvironmentVariable("VIBECODE_PROXY");
if (!ProxyConfigurationParser.TryParse(rawProxy, out var proxy, out var proxyError)
    || proxy is null
    || proxy.IsSocks)
{
    Console.Error.WriteLine(
        $"PROBE FAILED: VIBECODE_PROXY must contain an HTTP/HTTPS proxy. {proxyError ?? string.Empty}".Trim());
    return 2;
}

var connectivity = await ProxyConnectivityVerifier.CheckAsync(proxy, CancellationToken.None);
if (!connectivity.IsSuccess)
{
    Console.Error.WriteLine($"PROBE FAILED: proxy connection failed. {connectivity.Message}");
    return 3;
}
Console.WriteLine("PROBE stage=proxy-connected");

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = true,
    AutomaticDecompression = DecompressionMethods.All,
    UseProxy = true,
    Proxy = CreateWebProxy(proxy)
};
using var client = new HttpClient(handler)
{
    BaseAddress = new Uri(origin),
    Timeout = TimeSpan.FromSeconds(20)
};
client.DefaultRequestHeaders.UserAgent.ParseAdd(mobileUserAgent);
client.DefaultRequestHeaders.Accept.Add(
    new MediaTypeWithQualityHeaderValue("application/json"));
client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
client.DefaultRequestHeaders.Referrer = new Uri(registrationUrl);
client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
client.DefaultRequestHeaders.TryAddWithoutValidation("Language", "VI");

using (var pageResponse = await client.GetAsync(registrationUrl))
{
    if (!pageResponse.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"PROBE FAILED: registration page returned {(int)pageResponse.StatusCode}.");
        return 4;
    }
}
Console.WriteLine("PROBE stage=registration-page-ready");

string captchaId;
var usedFallbackCaptchaId = false;
using (var settingsResponse = await client.GetAsync("/wps/system/settings/consolidated"))
{
    if (!settingsResponse.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"PROBE FAILED: settings endpoint returned {(int)settingsResponse.StatusCode}.");
        return 5;
    }

    var settingsJson = await settingsResponse.Content.ReadAsStringAsync();
    if (!TryReadCaptchaId(settingsJson, out captchaId))
    {
        // Mirror the production workflow: some XX88 deployments omit gw
        // from this endpoint while the deployed captcha ID still works.
        captchaId = fallbackCaptchaId;
        usedFallbackCaptchaId = true;
    }
}
Console.WriteLine(
    $"PROBE stage=captcha-id-ready source={(usedFallbackCaptchaId ? "fallback" : "live-settings")}");

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Warning);
    builder.AddSimpleConsole(options => options.SingleLine = true);
});
var options = Options.Create(new CaptchaSolverOptions
{
    LocalGeeTestMode = CaptchaSolverOptions.BrowserFirstMode,
    LocalBrowserMaxSessions = 1,
    LocalBrowserMaxUsesPerSession = 20,
    LocalBrowserIdleSeconds = 120
});
using var pool = new BrowserGeetestTokenPool(
    options,
    loggerFactory.CreateLogger<BrowserGeetestTokenPool>());
await pool.StartAsync(CancellationToken.None);

try
{
    var request = new BrowserGeetestTokenRequest(
        TargetSite.XX88,
        registrationUrl,
        captchaId,
        proxy);
    var first = await pool.GetTokenAsync(request, CancellationToken.None);
    if (first is null)
    {
        Console.Error.WriteLine("PROBE FAILED: browser did not receive a usable first GeeTest v4 token.");
        return 7;
    }

    var second = await pool.GetTokenAsync(request, CancellationToken.None);

    if (second is null)
    {
        Console.Error.WriteLine("PROBE FAILED: browser did not receive a reusable GeeTest v4 token.");
        return 7;
    }

    Console.WriteLine("PROBE PASS: live XX88 GeeTest tokens were acquired through the registration proxy.");
    Console.WriteLine($"firstTokenMs={first.ElapsedMilliseconds}");
    Console.WriteLine($"reusedTokenMs={second.ElapsedMilliseconds}");
    Console.WriteLine($"secondReusedBrowser={second.ReusedBrowser}");
    Console.WriteLine($"startedBrowsers={pool.GetSnapshot().StartedBrowserCount}");
    Console.WriteLine($"captchaIdSource={(usedFallbackCaptchaId ? "fallback" : "live-settings")}");
    return 0;
}
finally
{
    await pool.StopAsync(CancellationToken.None);
}

static IWebProxy CreateWebProxy(ProxyConfiguration proxy)
{
    var webProxy = new WebProxy(proxy.ProxyServerArgument);
    if (proxy.RequiresAuthentication)
    {
        webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
    }

    return webProxy;
}

static bool TryReadCaptchaId(string json, out string captchaId)
{
    captchaId = string.Empty;
    try
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("gw", out var gateway)
            || gateway.ValueKind != JsonValueKind.Object
            || !gateway.TryGetProperty("GEETEST_VERSION", out var version)
            || !string.Equals(version.GetString(), "v4", StringComparison.OrdinalIgnoreCase)
            || !gateway.TryGetProperty("GEETEST_ID", out var id))
        {
            return false;
        }

        captchaId = id.GetString() ?? string.Empty;
        return captchaId.Length >= 16;
    }
    catch (JsonException)
    {
        return false;
    }
}
