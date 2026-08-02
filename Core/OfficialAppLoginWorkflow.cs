using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public enum OfficialAppLoginStatus
{
    NotSupported,
    Success,
    Failed
}

public sealed record OfficialAppLoginOutcome(
    OfficialAppLoginStatus Status,
    string Message,
    string? RawResponse = null);

/// <summary>
/// Replays the one-time PWA sign-in performed by the official RR88/MM88
/// mobile clients. The official bundles mark this request with Device=PWA
/// and isOfficialAppLogin=true.
/// </summary>
public static class OfficialAppLoginWorkflow
{
    private const string Language = "VI";
    private const string PwaDevice = "PWA";
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsSupported(TargetSite site) => GetProfile(site) is not null;

    public static async Task<OfficialAppLoginOutcome> TryLoginAsync(
        AccountModel account,
        TargetSite site,
        CancellationToken cancellationToken,
        CaptchaSolverOptions? captchaSolverOptions = null,
        IGeetestBrowserTokenService? browserGeetestTokenService = null)
    {
        ArgumentNullException.ThrowIfNull(account);

        var profile = GetProfile(site);
        if (profile is null)
        {
            return new OfficialAppLoginOutcome(
                OfficialAppLoginStatus.NotSupported,
                $"{site} does not use the RR88/MM88 PWA login protocol.");
        }

        if (!ProxyConfigurationParser.TryParse(
                account.Proxy,
                out var proxyConfiguration,
                out var proxyError))
        {
            return new OfficialAppLoginOutcome(
                OfficialAppLoginStatus.Failed,
                proxyError ?? "Proxy configuration is invalid.");
        }

        try
        {
            using var handler = CreateHttpClientHandler(proxyConfiguration);
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(profile.Origin),
                Timeout = TimeSpan.FromSeconds(20)
            };

            ConfigureDefaultHeaders(httpClient, profile);

            using (var pageResponse = await httpClient.GetAsync(
                       profile.PwaLoginUrl,
                       cancellationToken).ConfigureAwait(false))
            {
                pageResponse.EnsureSuccessStatusCode();
            }

            var rsaKey = await GetRsaKeyAsync(httpClient, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rsaKey))
            {
                return new OfficialAppLoginOutcome(
                    OfficialAppLoginStatus.Failed,
                    "PWA login could not fetch the session RSA key.");
            }

            var geetest = await DirectRegistrationWorkflow.TryAcquireGeetestChallengeAsync(
                    httpClient,
                    site,
                    proxyConfiguration,
                    captchaSolverOptions,
                    browserGeetestTokenService,
                    cancellationToken)
                .ConfigureAwait(false);
            if (geetest is null)
            {
                return new OfficialAppLoginOutcome(
                    OfficialAppLoginStatus.Failed,
                    "PWA login could not acquire a fresh GeeTest verification token.");
            }

            var payload = new Dictionary<string, object?>
            {
                ["username"] = account.GetUsername(site),
                ["password"] = account.GetPassword(site),
                ["loginDeviceId"] = Guid.NewGuid().ToString(),
                ["isOfficialAppLogin"] = true,
                ["device"] = PwaDevice,
                ["geetestValidateV4"] = geetest.Value
            };
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var encrypted = DirectRegistrationCrypto.Encrypt(payloadJson, rsaKey);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/wps/session/login")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new Dictionary<string, string> { ["value"] = encrypted.Des },
                        JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.TryAddWithoutValidation("Device", PwaDevice);
            request.Headers.TryAddWithoutValidation("Encryption", encrypted.Rsa);
            request.Headers.TryAddWithoutValidation("X-Digest", encrypted.Des);
            request.Headers.TryAddWithoutValidation("X-RSA", rsaKey);

            using var response = await httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new OfficialAppLoginOutcome(
                    OfficialAppLoginStatus.Failed,
                    DescribeFailure(response.StatusCode, responseText),
                    responseText);
            }

            try
            {
                using var document = JsonDocument.Parse(responseText);
                var root = document.RootElement;
                if (root.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.True)
                {
                    return new OfficialAppLoginOutcome(
                        OfficialAppLoginStatus.Success,
                        "Official PWA login completed.",
                        responseText);
                }

                return new OfficialAppLoginOutcome(
                    OfficialAppLoginStatus.Failed,
                    ReadMessage(root, "Official PWA login was rejected."),
                    responseText);
            }
            catch (JsonException)
            {
                return new OfficialAppLoginOutcome(
                    OfficialAppLoginStatus.Failed,
                    "Official PWA login returned a non-JSON response.",
                    responseText);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new OfficialAppLoginOutcome(
                OfficialAppLoginStatus.Failed,
                $"Official PWA login failed: {exception.Message}");
        }
    }

    private static void ConfigureDefaultHeaders(
        HttpClient httpClient,
        OfficialAppLoginProfile profile)
    {
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/plain"));
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("*/*"));
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd(
            "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(MobileUserAgent);
        httpClient.DefaultRequestHeaders.Referrer = new Uri(profile.PwaLoginUrl);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Merchant", profile.MerchantCode);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Language", Language);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Real-UA",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(MobileUserAgent)));
    }

    private static async Task<string> GetRsaKeyAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var response = await httpClient.GetAsync(
                $"/wps/session/key/rsa?_={timestamp}",
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        return (await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false))
            .Trim();
    }

    private static HttpClientHandler CreateHttpClientHandler(
        ProxyConfiguration? proxyConfiguration)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer()
        };

        if (proxyConfiguration is null)
        {
            return handler;
        }

        handler.UseProxy = true;
        var proxy = new WebProxy(new Uri(proxyConfiguration.ProxyServerArgument), false);
        if (proxyConfiguration.RequiresAuthentication)
        {
            proxy.Credentials = new NetworkCredential(
                proxyConfiguration.Username,
                proxyConfiguration.Password);
        }

        handler.Proxy = proxy;
        return handler;
    }

    private static string DescribeFailure(HttpStatusCode statusCode, string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            return $"Official PWA login returned {(int)statusCode}: " +
                   ReadMessage(document.RootElement, "request rejected");
        }
        catch (JsonException)
        {
            return $"Official PWA login returned {(int)statusCode}.";
        }
    }

    private static string ReadMessage(JsonElement root, string fallback)
    {
        foreach (var propertyName in new[] { "message", "errorCode", "code", "msg" })
        {
            if (root.TryGetProperty(propertyName, out var property))
            {
                var value = property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : property.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return fallback;
    }

    private static OfficialAppLoginProfile? GetProfile(TargetSite site) =>
        site switch
        {
            TargetSite.RR88 => new OfficialAppLoginProfile(
                "https://www.rr3311.com",
                "https://www.rr3311.com/m/login",
                "rr88f4"),
            TargetSite.MM88 => new OfficialAppLoginProfile(
                "https://www.mm88o.com",
                "https://www.mm88o.com/m/login",
                "mm88vndkf3"),
            _ => null
        };

    private sealed record OfficialAppLoginProfile(
        string Origin,
        string PwaLoginUrl,
        string MerchantCode);
}
