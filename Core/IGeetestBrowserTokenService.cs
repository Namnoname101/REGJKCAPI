using System.Text.Json;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

/// <summary>
/// Provides short-lived GeeTest v4 values from a real, proxy-bound browser
/// session. The registration request must use the same proxy as the token.
/// </summary>
public interface IGeetestBrowserTokenService
{
    Task<BrowserGeetestTokenResult?> GetTokenAsync(
        BrowserGeetestTokenRequest request,
        CancellationToken cancellationToken);
}

public sealed record BrowserGeetestTokenRequest(
    TargetSite Site,
    string RegistrationUrl,
    string CaptchaId,
    ProxyConfiguration? Proxy,
    string GeetestSdkCdn = "https://images.0998455.com/",
    string GeetestSdkVersion = "1");

public sealed record BrowserGeetestTokenResult(
    JsonElement Value,
    bool ReusedBrowser,
    long ElapsedMilliseconds);
