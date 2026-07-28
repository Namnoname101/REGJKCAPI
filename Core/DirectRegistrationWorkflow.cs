using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Models;
using OpenQA.Selenium;

namespace BAOCODETOOL.Core;

public enum DirectRegistrationStatus
{
    NotSupported,
    Success,
    Failed,
    FallbackToBrowser
}

public sealed record DirectRegistrationOutcome(
    DirectRegistrationStatus Status,
    string Message,
    bool UsernameConflict = false,
    string? RawResponse = null,
    DirectRegistrationSession? Session = null);

public sealed record DirectRegistrationSession(
    string Token,
    string UserName,
    long? CustomerId,
    string MerchantCode,
    CookieContainer? Cookies = null,
    GgDirectSession? Gg = null);

public static class DirectRegistrationWorkflow
{
    private const string Language = "VI";
    private const string RegisterModuleId = "REG3";
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, DirectEgressThrottle> EgressThrottles = new();
    private static readonly TimeSpan MinimumEgressStartSpacing = TimeSpan.FromMilliseconds(1200);
    private static readonly SemaphoreSlim BrowserGeetestConcurrency = new(4, 4);
    private static readonly ConcurrentDictionary<TargetSite, GeetestSettingsCacheEntry> GeetestSettingsCache = new();
    private static readonly ConcurrentDictionary<TargetSite, SemaphoreSlim> GeetestSettingsGates = new();
    private static readonly TimeSpan GeetestSettingsCacheLifetime = TimeSpan.FromMinutes(10);

    public static bool IsSupported(TargetSite site) =>
        site == TargetSite.GG88 || GetProfile(site) is not null;

    public static async Task<DirectRegistrationOutcome> TryRegisterAsync(
        AccountModel account,
        TargetSite site,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (site == TargetSite.GG88)
        {
            return await GgDirectWorkflow.RegisterAsync(account, cancellationToken)
                .ConfigureAwait(false);
        }

        var profile = GetProfile(site);
        if (profile is null)
        {
            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.NotSupported,
                $"{site} does not have a direct HTTP registration profile.");
        }

        try
        {
            if (!ProxyConfigurationParser.TryParse(account.Proxy, out var proxyConfiguration, out var proxyError))
            {
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.Failed,
                    proxyError ?? "Proxy configuration is invalid.");
            }

            if (profile.RequiresProxyForDirect
                && (proxyConfiguration is null || proxyConfiguration.IsSocks))
            {
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.FallbackToBrowser,
                    $"{profile.Site} direct HTTP requires an HTTP/HTTPS proxy.");
            }

            var egressKey = CreateEgressKey(profile, proxyConfiguration);
            var throttle = EgressThrottles.GetOrAdd(egressKey, _ => new DirectEgressThrottle());
            using (await throttle.AcquireAsync(
                    MinimumEgressStartSpacing,
                    cancellationToken)
                .ConfigureAwait(false))
            {
            }

            // Keep the authenticated cookies set during registration.  The
            // bank-card API uses the token and these cookies together on
            // current RR88/MM88 deployments.
            var cookies = new CookieContainer();
            using var handler = CreateHttpClientHandler(proxyConfiguration, cookies);

            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(profile.Origin),
                Timeout = TimeSpan.FromSeconds(20)
            };

            ConfigureDefaultHeaders(httpClient, profile);

            var geetestTask = GetGeetestValidateV4Async(
                httpClient,
                profile,
                proxyConfiguration,
                cancellationToken);

            await WarmUpSessionAsync(httpClient, profile, cancellationToken)
                .ConfigureAwait(false);

            var rsaKey = await GetRsaKeyAsync(httpClient, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rsaKey))
            {
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.FallbackToBrowser,
                    "Direct HTTP could not fetch RSA key.");
            }

            var geetest = await geetestTask.ConfigureAwait(false);
            if (geetest is null)
            {
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.FallbackToBrowser,
                    "Direct HTTP could not solve GeeTest token.");
            }

            var registerResponse = await SendRegisterRequestAsync(
                    httpClient,
                    account,
                    profile,
                    rsaKey,
                    geetest.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            if (profile.Site == TargetSite.XX88
                && IsGeetestRejected(registerResponse.ResponseText))
            {
                var activeCaptchaId = geetest.CaptchaId;
                var refreshedCaptchaId = await ResolveGeetestCaptchaIdAsync(
                        httpClient,
                        profile,
                        rejectedCaptchaId: activeCaptchaId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        refreshedCaptchaId,
                        activeCaptchaId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var refreshedToken = await GetGeetestValidateV4ForCaptchaIdAsync(
                            profile,
                            refreshedCaptchaId,
                            proxyConfiguration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (refreshedToken is not null)
                    {
                        activeCaptchaId = refreshedCaptchaId;
                        registerResponse = await SendRegisterRequestAsync(
                                httpClient,
                                account,
                                profile,
                                rsaKey,
                                refreshedToken.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (IsGeetestRejected(registerResponse.ResponseText))
                {
                    var browserToken = await GetBrowserGeetestValidateV4Async(
                            profile,
                            activeCaptchaId,
                            proxyConfiguration,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (browserToken is not null)
                    {
                        registerResponse = await SendRegisterRequestAsync(
                                httpClient,
                                account,
                                profile,
                                rsaKey,
                                browserToken.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            if (!registerResponse.IsSuccessStatusCode)
            {
                var rejectedOutcome = ParseRejectedRegisterResponse(
                    registerResponse.ResponseText,
                    profile);
                if (rejectedOutcome is not null)
                {
                    return AttachCookies(rejectedOutcome, cookies);
                }

                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.FallbackToBrowser,
                    $"Direct HTTP register returned {(int)registerResponse.StatusCode}.",
                    RawResponse: registerResponse.ResponseText);
            }

            return AttachCookies(
                ParseRegisterResponse(registerResponse.ResponseText, profile),
                cookies);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.FallbackToBrowser,
                $"Direct HTTP exception: {exception.Message}");
        }
    }

    private static async Task<RegisterHttpResponse> SendRegisterRequestAsync(
        HttpClient httpClient,
        AccountModel account,
        DirectRegistrationSiteProfile profile,
        string rsaKey,
        JsonElement geetestValidateV4,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(account, profile, geetestValidateV4);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var encrypted = DirectRegistrationCrypto.Encrypt(payloadJson, rsaKey);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/wps/member/register")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["value"] = encrypted.Des
                }, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.TryAddWithoutValidation("Language", Language);
        request.Headers.TryAddWithoutValidation("ModuleId", RegisterModuleId);
        request.Headers.TryAddWithoutValidation("Encryption", encrypted.Rsa);
        request.Headers.TryAddWithoutValidation("X-Digest", encrypted.Des);
        request.Headers.TryAddWithoutValidation("X-RSA", rsaKey);
        request.Headers.TryAddWithoutValidation(
            "X-Real-UA",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(MobileUserAgent)));
        if (!string.IsNullOrWhiteSpace(profile.MerchantCode))
        {
            request.Headers.TryAddWithoutValidation("Merchant", profile.MerchantCode);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return new RegisterHttpResponse(
            response.IsSuccessStatusCode,
            response.StatusCode,
            responseText);
    }

    private static bool IsGeetestRejected(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            return string.Equals(
                TryGetString(document.RootElement, "errorCode"),
                "geetest_captcha_err",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DirectRegistrationOutcome? ParseRejectedRegisterResponse(
        string responseText,
        DirectRegistrationSiteProfile profile)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var errorCode = TryGetString(root, "errorCode");

            if (string.Equals(errorCode, "geetest_captcha_err", StringComparison.OrdinalIgnoreCase))
            {
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.FallbackToBrowser,
                    "Direct HTTP GeeTest token was rejected.",
                    RawResponse: responseText);
            }

            return ParseRegisterResponse(responseText, profile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ConfigureDefaultHeaders(
        HttpClient httpClient,
        DirectRegistrationSiteProfile profile)
    {
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/plain"));
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("*/*"));
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(MobileUserAgent);
        httpClient.DefaultRequestHeaders.Referrer = new Uri(profile.RegistrationUrl);

        if (!string.IsNullOrWhiteSpace(profile.MerchantCode))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Merchant", profile.MerchantCode);
        }

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Language", Language);
    }

    private static async Task WarmUpSessionAsync(
        HttpClient httpClient,
        DirectRegistrationSiteProfile profile,
        CancellationToken cancellationToken)
    {
        using var pageResponse = await httpClient.GetAsync(profile.RegistrationUrl, cancellationToken)
            .ConfigureAwait(false);
        pageResponse.EnsureSuccessStatusCode();

        var affiliatePath = $"/wps/agent/affiliate?domain={Uri.EscapeDataString(profile.AffiliateDomain)}" +
                            $"&code={Uri.EscapeDataString(profile.AffiliateCode)}" +
            $"&referralCode={Uri.EscapeDataString(profile.ReferralCode)}";
        using var affiliateResponse = await httpClient.GetAsync(affiliatePath, cancellationToken)
            .ConfigureAwait(false);
        _ = await affiliateResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static DirectRegistrationOutcome AttachCookies(
        DirectRegistrationOutcome outcome,
        CookieContainer cookies)
    {
        return outcome.Session is null
            ? outcome
            : outcome with
            {
                Session = outcome.Session with
                {
                    Cookies = cookies
                }
            };
    }

    private static HttpClientHandler CreateHttpClientHandler(
        ProxyConfiguration? proxyConfiguration,
        CookieContainer? cookies = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = cookies ?? new CookieContainer()
        };

        if (proxyConfiguration is null)
        {
            return handler;
        }

        handler.UseProxy = true;
        handler.Proxy = CreateWebProxy(proxyConfiguration);
        return handler;
    }

    private static IWebProxy CreateWebProxy(ProxyConfiguration proxyConfiguration)
    {
        var proxy = new WebProxy(new Uri(proxyConfiguration.ProxyServerArgument), false);
        if (proxyConfiguration.RequiresAuthentication)
        {
            proxy.Credentials = new NetworkCredential(
                proxyConfiguration.Username,
                proxyConfiguration.Password);
        }

        return proxy;
    }

    private static string CreateProxyEnvironmentValue(ProxyConfiguration proxyConfiguration)
    {
        var builder = new UriBuilder(
            proxyConfiguration.Scheme,
            proxyConfiguration.Host,
            proxyConfiguration.Port);
        if (proxyConfiguration.RequiresAuthentication)
        {
            builder.UserName = proxyConfiguration.Username;
            builder.Password = proxyConfiguration.Password;
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string CreateEgressKey(
        DirectRegistrationSiteProfile profile,
        ProxyConfiguration? proxyConfiguration)
    {
        if (proxyConfiguration is null)
        {
            return $"{profile.Site}|direct";
        }

        var credentials = proxyConfiguration.RequiresAuthentication
            ? $"{proxyConfiguration.Username}:{proxyConfiguration.Password}@"
            : string.Empty;
        return $"{profile.Site}|{proxyConfiguration.Scheme}://{credentials}{proxyConfiguration.Server}";
    }

    private static async Task<string> GetRsaKeyAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var response = await httpClient.GetAsync($"/wps/session/key/rsa?_={timestamp}", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        return (await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false))
            .Trim();
    }

    private static async Task<GeetestTokenResult?> GetGeetestValidateV4Async(
        HttpClient httpClient,
        DirectRegistrationSiteProfile profile,
        ProxyConfiguration? proxyConfiguration,
        CancellationToken cancellationToken)
    {
        var captchaId = await ResolveGeetestCaptchaIdAsync(
                httpClient,
                profile,
                rejectedCaptchaId: null,
                cancellationToken)
            .ConfigureAwait(false);
        var token = await GetGeetestValidateV4ForCaptchaIdAsync(
                profile,
                captchaId,
                proxyConfiguration,
                cancellationToken)
            .ConfigureAwait(false);
        return token is null
            ? null
            : new GeetestTokenResult(captchaId, token.Value);
    }

    private static async Task<string> ResolveGeetestCaptchaIdAsync(
        HttpClient httpClient,
        DirectRegistrationSiteProfile profile,
        string? rejectedCaptchaId,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedGeetestCaptchaId(profile.Site, rejectedCaptchaId, out var cachedCaptchaId))
        {
            return cachedCaptchaId;
        }

        var gate = GeetestSettingsGates.GetOrAdd(profile.Site, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedGeetestCaptchaId(profile.Site, rejectedCaptchaId, out cachedCaptchaId))
            {
                return cachedCaptchaId;
            }

            try
            {
                using var response = await httpClient.GetAsync(
                        "/wps/system/settings/consolidated",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var document = JsonDocument.Parse(responseText);
                    if (TryReadGeetestCaptchaId(document.RootElement, out var liveCaptchaId))
                    {
                        GeetestSettingsCache[profile.Site] = new GeetestSettingsCacheEntry(
                            liveCaptchaId,
                            DateTimeOffset.UtcNow.Add(GeetestSettingsCacheLifetime));
                        return liveCaptchaId;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            return profile.GeetestCaptchaId;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryGetCachedGeetestCaptchaId(
        TargetSite site,
        string? rejectedCaptchaId,
        out string captchaId)
    {
        captchaId = string.Empty;
        if (!GeetestSettingsCache.TryGetValue(site, out var cached)
            || cached.ExpiresAt <= DateTimeOffset.UtcNow
            || (!string.IsNullOrWhiteSpace(rejectedCaptchaId)
                && string.Equals(
                    cached.CaptchaId,
                    rejectedCaptchaId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        captchaId = cached.CaptchaId;
        return true;
    }

    private static bool TryReadGeetestCaptchaId(JsonElement root, out string captchaId)
    {
        captchaId = string.Empty;
        if (!root.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("gw", out var gateway)
            || gateway.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var version = TryGetString(gateway, "GEETEST_VERSION");
        var candidate = TryGetString(gateway, "GEETEST_ID");
        if (!string.Equals(version, "v4", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(candidate)
            || candidate.Length is < 16 or > 128
            || candidate.Any(character => !char.IsLetterOrDigit(character)
                                          && character is not '-' and not '_'))
        {
            return false;
        }

        captchaId = candidate;
        return true;
    }

    private static async Task<JsonElement?> GetGeetestValidateV4ForCaptchaIdAsync(
        DirectRegistrationSiteProfile profile,
        string captchaId,
        ProxyConfiguration? proxyConfiguration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(captchaId))
        {
            return null;
        }

        var scriptPath = ResolveGeetestScriptPath();
        if (scriptPath is null)
        {
            return null;
        }

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var token = await RunGeetestTokenProcessAsync(
                    profile,
                    captchaId,
                    scriptPath,
                    proxyConfiguration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (token is not null)
            {
                return token;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return null;
    }

    private static async Task<JsonElement?> RunGeetestTokenProcessAsync(
        DirectRegistrationSiteProfile profile,
        string captchaId,
        string scriptPath,
        ProxyConfiguration? proxyConfiguration,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--use-env-proxy");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add(captchaId);
        process.StartInfo.ArgumentList.Add(profile.GeetestSdkCdn);
        process.StartInfo.ArgumentList.Add(profile.GeetestSdkVersion);
        process.StartInfo.ArgumentList.Add(profile.Origin);
        process.StartInfo.ArgumentList.Add(profile.RegistrationUrl);
        process.StartInfo.ArgumentList.Add(
            profile.Site == TargetSite.XX88
                ? MobileUserAgent
                : string.Empty);

        if (proxyConfiguration is not null && !proxyConfiguration.IsSocks)
        {
            var proxy = CreateProxyEnvironmentValue(proxyConfiguration);
            process.StartInfo.Environment["HTTP_PROXY"] = proxy;
            process.StartInfo.Environment["HTTPS_PROXY"] = proxy;
            // The GeeTest helper is a separate Node process. Clear inherited
            // bypass lists so a machine-level NO_PROXY value cannot silently
            // send captcha traffic directly while the registration request
            // itself uses the purchased proxy.
            process.StartInfo.Environment["NO_PROXY"] = string.Empty;
            process.StartInfo.Environment["no_proxy"] = string.Empty;
        }

        if (!process.Start())
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return null;
        }

        var output = (await outputTask.ConfigureAwait(false)).Trim();
        _ = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            return IsUsableGeetestValidateV4(document.RootElement)
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonElement?> GetBrowserGeetestValidateV4Async(
        DirectRegistrationSiteProfile profile,
        string captchaId,
        ProxyConfiguration? proxyConfiguration,
        CancellationToken cancellationToken)
    {
        await BrowserGeetestConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run<JsonElement?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profileRoot = Path.Combine(
                    Path.GetTempPath(),
                    "VibecodeGeetestToken",
                    Guid.NewGuid().ToString("N"));

                try
                {
                    using var driver = DriverFactory.CreateDriver(
                        profileRoot,
                        userAgent: null,
                        proxy: proxyConfiguration?.ConnectionString,
                        headless: true,
                        site: profile.Site,
                        verifyProxy: false,
                        deleteProfileOnDispose: true);
                    driver.Navigate().GoToUrl(profile.RegistrationUrl);

                    var javaScript = (IJavaScriptExecutor)driver;
                    var initDeadline = DateTimeOffset.UtcNow.AddSeconds(12);
                    while (DateTimeOffset.UtcNow < initDeadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (javaScript.ExecuteScript(
                                "return typeof window.initBotion === 'function';")
                            is true)
                        {
                            break;
                        }

                        Thread.Sleep(100);
                    }

                    if (javaScript.ExecuteScript(
                            "return typeof window.initBotion === 'function';")
                        is not true)
                    {
                        return null;
                    }

                    var tokenJson = javaScript.ExecuteAsyncScript(
                        """
                        const captchaId = arguments[0];
                        const done = arguments[arguments.length - 1];
                        let completed = false;
                        let poll = null;
                        let timeout = null;
                        const finish = value => {
                            if (completed) return;
                            completed = true;
                            if (poll) clearInterval(poll);
                            if (timeout) clearTimeout(timeout);
                            done(value ? JSON.stringify(value) : null);
                        };
                        timeout = setTimeout(() => finish(null), 8000);

                        try {
                            window.initBotion({
                                captchaId,
                                product: 'bind',
                                language: 'vie',
                                hideSuccess: true,
                                timeout: 5000,
                                protocol: 'https://'
                            }, captcha => {
                                const capture = () => {
                                    try {
                                        const value = captcha.getValidate?.();
                                        if (value && typeof value === 'object') finish(value);
                                    } catch {}
                                };
                                captcha.onSuccess?.(capture);
                                captcha.onError?.(() => finish(null));
                                const verify = () => {
                                    try {
                                        captcha.verify();
                                        poll = setInterval(capture, 50);
                                    } catch {
                                        finish(null);
                                    }
                                };
                                if (captcha.onReady) captcha.onReady(verify);
                                else verify();
                            });
                        } catch {
                            finish(null);
                        }
                        """,
                        captchaId)?.ToString();

                    if (string.IsNullOrWhiteSpace(tokenJson))
                    {
                        return null;
                    }

                    using var document = JsonDocument.Parse(tokenJson);
                    return IsUsableGeetestValidateV4(document.RootElement)
                        ? document.RootElement.Clone()
                        : null;
                }
                catch (WebDriverException)
                {
                    return null;
                }
                catch (JsonException)
                {
                    return null;
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(profileRoot))
                        {
                            Directory.Delete(profileRoot, recursive: true);
                        }
                    }
                    catch
                    {
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            BrowserGeetestConcurrency.Release();
        }
    }

    private static bool IsUsableGeetestValidateV4(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "lot_number", "captcha_output", "pass_token", "gen_time" })
        {
            if (!value.TryGetProperty(name, out var field)
                || field.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(field.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ResolveGeetestScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Core", "geetest_token.js"),
            Path.Combine(AppContext.BaseDirectory, "geetest_token.js"),
            Path.Combine(Directory.GetCurrentDirectory(), "Core", "geetest_token.js")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static Dictionary<string, object?> BuildPayload(
        AccountModel account,
        DirectRegistrationSiteProfile profile,
        JsonElement geetestValidateV4)
    {
        var phoneNumber = profile.Site == TargetSite.XX88
            ? PhoneNumberFormatter.FormatTenDigitWithLeadingZero(account.PhoneNumber)
            : PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber);

        return new Dictionary<string, object?>
        {
            ["countryDialingCode"] = "84",
            ["registerUrl"] = profile.RegisterUrlForPayload,
            ["registerMethod"] = "H5",
            ["username"] = account.GetUsername(profile.Site),
            ["password"] = account.GetPassword(profile.Site),
            ["confirmPassword"] = account.GetPassword(profile.Site),
            ["affiliateCode"] = profile.AffiliateCode,
            ["paymentPassword"] = string.Empty,
            ["recommenderId"] = string.Empty,
            ["nickname"] = string.Empty,
            ["payeeName"] = account.FullName.ToUpperInvariant(),
            ["email"] = string.Empty,
            ["mobileNum"] = phoneNumber,
            ["idNumber"] = string.Empty,
            ["qqNum"] = string.Empty,
            ["facebook"] = string.Empty,
            ["wechat"] = string.Empty,
            ["line"] = string.Empty,
            ["whatsapp"] = string.Empty,
            ["zalo"] = string.Empty,
            ["birthday"] = string.Empty,
            ["captcha"] = string.Empty,
            ["type"] = 0,
            ["domain"] = profile.AffiliateDomain,
            ["verificationCode"] = string.Empty,
            ["login"] = true,
            ["geetestValidateV4"] = geetestValidateV4,
            ["lineSessionToken"] = null,
            ["loginDeviceId"] = Guid.NewGuid().ToString(),
            ["isOfficialAppLogin"] = false,
            ["referralCode"] = profile.ReferralCode,
            ["pixelTrack"] = null
        };
    }

    private static DirectRegistrationOutcome ParseRegisterResponse(
        string responseText,
        DirectRegistrationSiteProfile profile)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.FallbackToBrowser,
                "Direct HTTP register returned an empty response.");
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var success = root.TryGetProperty("success", out var successElement)
                          && successElement.ValueKind == JsonValueKind.True;

            if (success)
            {
                var session = TryReadRegistrationSession(root, profile);
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.Success,
                    "Direct HTTP registration completed.",
                    RawResponse: responseText,
                    Session: session);
            }

            var errorCode = TryGetString(root, "errorCode");
            if (string.Equals(errorCode, "geetest_captcha_err", StringComparison.OrdinalIgnoreCase))
            {
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.FallbackToBrowser,
                    "Direct HTTP GeeTest token was rejected.",
                    RawResponse: responseText);
            }

            var message = TryGetString(root, "message")
                          ?? errorCode
                          ?? "Direct HTTP registration was rejected.";
            var rawLower = responseText.ToLowerInvariant();
            var usernameConflict = rawLower.Contains("username", StringComparison.Ordinal)
                                   && (rawLower.Contains("exist", StringComparison.Ordinal)
                                       || rawLower.Contains("already", StringComparison.Ordinal))
                                   || rawLower.Contains("user.name.exists", StringComparison.Ordinal)
                                   || rawLower.Contains("customer.exists", StringComparison.Ordinal);

            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.Failed,
                message,
                usernameConflict,
                responseText);
        }
        catch (JsonException)
        {
            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.FallbackToBrowser,
                "Direct HTTP register returned non-JSON response.",
                RawResponse: responseText);
        }
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static DirectRegistrationSession? TryReadRegistrationSession(
        JsonElement root,
        DirectRegistrationSiteProfile profile)
    {
        if (!root.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var token = TryGetString(value, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var userName = TryGetString(value, "userName") ?? string.Empty;
        long? customerId = null;
        if (value.TryGetProperty("id", out var idElement)
            && idElement.TryGetInt64(out var parsedId))
        {
            customerId = parsedId;
        }

        return new DirectRegistrationSession(
            token,
            userName,
            customerId,
            profile.MerchantCode);
    }

    private static DirectRegistrationSiteProfile? GetProfile(TargetSite site) =>
        site switch
        {
            TargetSite.RR88 => new DirectRegistrationSiteProfile(
                site,
                "https://gioithieu.rr3311.com",
                "https://gioithieu.rr3311.com/m/register?referralCode=mrh6472&r=mrh6472",
                "https://gioithieu.rr3311.com/m/register?referralCode=mrh6472&r=mrh6472",
                "rr88f4",
                "rr3311",
                "gioithieu",
                "mrh6472",
                "fa2202091f2d9086161947f672579b1d",
                "https://images.0998455.com/",
                "1",
                false),
            TargetSite.MM88 => new DirectRegistrationSiteProfile(
                site,
                "https://gioithieu.mm88p.com",
                "https://gioithieu.mm88p.com/m/register?referralCode=mgr0794&r=mgr0794",
                "https://gioithieu.mm88p.com/m/register?referralCode=mgr0794&r=mgr0794",
                "mm88vndkf3",
                "mm88p",
                "gioithieu",
                "mgr0794",
                "fa2202091f2d9086161947f672579b1d",
                "https://images.0998455.com/",
                "1",
                false),
            TargetSite.XX88 => new DirectRegistrationSiteProfile(
                site,
                "https://gioithieu.xx88.fun",
                "https://gioithieu.xx88.fun/m/register?referralCode=gql2969&r=gql2969",
                "https://gioithieu.xx88.fun/m/register?referralCode=gql2969&r=gql2969",
                "xx88vndkf1",
                "xx88",
                "gioithieu",
                "gql2969",
                "2e3984aebc5904671a2a0a7c284b8c79",
                "https://images.0998455.com/",
                "1",
                true),
            _ => null
        };

    private sealed record DirectRegistrationSiteProfile(
        TargetSite Site,
        string Origin,
        string RegistrationUrl,
        string RegisterUrlForPayload,
        string MerchantCode,
        string AffiliateDomain,
        string AffiliateCode,
        string ReferralCode,
        string GeetestCaptchaId,
        string GeetestSdkCdn,
        string GeetestSdkVersion,
        bool RequiresProxyForDirect);

    private sealed record RegisterHttpResponse(
        bool IsSuccessStatusCode,
        HttpStatusCode StatusCode,
        string ResponseText);

    private sealed record GeetestTokenResult(string CaptchaId, JsonElement Value);

    private sealed record GeetestSettingsCacheEntry(
        string CaptchaId,
        DateTimeOffset ExpiresAt);
}

internal sealed class DirectEgressThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastStartTimestamp;

    public async Task<IDisposable> AcquireAsync(
        TimeSpan minimumStartSpacing,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var lastStartTimestamp = Volatile.Read(ref _lastStartTimestamp);
        if (lastStartTimestamp != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(lastStartTimestamp);
            var remaining = minimumStartSpacing - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }

        Volatile.Write(ref _lastStartTimestamp, Stopwatch.GetTimestamp());
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

internal static class DirectRegistrationCrypto
{
    private const string RandomChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXTZabcdefghiklmnopqrstuvwxyz";
    private static readonly BigInteger PublicExponent = new(65537);

    public static DirectEncryptedPayload Encrypt(string plaintextJson, string rsaModulusHex)
    {
        var randomKey = CreateRandomKey();
        var rsa = EncryptRsaKey(rsaModulusHex, new string(randomKey.Reverse().ToArray()));
        var des = EncryptDesEcbPkcs7(plaintextJson, randomKey);

        return new DirectEncryptedPayload(rsaModulusHex, rsa, des);
    }

    private static string CreateRandomKey()
    {
        Span<char> chars = stackalloc char[16];
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = RandomChars[RandomNumberGenerator.GetInt32(RandomChars.Length)];
        }

        return new string(chars);
    }

    private static string EncryptRsaKey(string modulusHex, string plaintext)
    {
        var modulus = BigInteger.Parse(
            "00" + modulusHex,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
        var modulusDigitCount = (modulusHex.Length + 3) / 4;
        var chunkSize = 2 * Math.Max(0, modulusDigitCount - 1);
        if (chunkSize <= 0)
        {
            throw new InvalidOperationException("Invalid RSA modulus.");
        }

        var source = plaintext.Select(ch => (byte)ch).ToList();
        while (source.Count % chunkSize != 0)
        {
            source.Add(0);
        }

        var encryptedChunks = new List<string>(source.Count / chunkSize);
        for (var offset = 0; offset < source.Count; offset += chunkSize)
        {
            var block = BigInteger.Zero;
            for (var index = 0; index < chunkSize; index += 2)
            {
                var digit = source[offset + index] + (source[offset + index + 1] << 8);
                block += new BigInteger(digit) << (8 * index);
            }

            var encrypted = BigInteger.ModPow(block, PublicExponent, modulus);
            encryptedChunks.Add(ToJavaScriptBigIntHex(encrypted));
        }

        return string.Join(' ', encryptedChunks);
    }

    private static string ToJavaScriptBigIntHex(BigInteger value)
    {
        if (value.Sign == 0)
        {
            return "0000";
        }

        var digits = new List<ushort>();
        var current = value;
        while (current.Sign > 0)
        {
            digits.Add((ushort)(current & 0xffff));
            current >>= 16;
        }

        var builder = new StringBuilder(digits.Count * 4);
        for (var index = digits.Count - 1; index >= 0; index--)
        {
            builder.Append(digits[index].ToString("x4", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string EncryptDesEcbPkcs7(string plaintext, string randomKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(randomKey[..8]);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.PKCS7;
        des.Key = keyBytes;

        using var encryptor = des.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(
            plaintextBytes,
            0,
            plaintextBytes.Length);
        return Convert.ToBase64String(encrypted);
    }
}

internal sealed record DirectEncryptedPayload(
    string RsaKey,
    string Rsa,
    string Des);
