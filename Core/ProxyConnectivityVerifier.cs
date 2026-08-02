using System;
using System.Net;

namespace BAOCODETOOL.Core;

public sealed record ProxyConnectivityCheckResult(
    bool IsSuccess,
    string? ObservedIp,
    string Message)
{
    public static ProxyConnectivityCheckResult Success(string observedIp) =>
        new(true, observedIp, $"Proxy returned external IP {observedIp}.");

    public static ProxyConnectivityCheckResult Failure(string message) =>
        new(false, null, message);
}

public static class ProxyConnectivityVerifier
{
    private static readonly Uri[] DefaultEndpoints =
    [
        new("https://api.ipify.org/"),
        new("http://api.ipify.org/")
    ];

    public static async Task<ProxyConnectivityCheckResult> CheckAsync(
        string? rawProxy,
        CancellationToken cancellationToken = default)
    {
        if (!ProxyConfigurationParser.TryParse(rawProxy, out var proxyConfiguration, out var parseError)
            || proxyConfiguration is null)
        {
            return ProxyConnectivityCheckResult.Failure(parseError ?? "Proxy is required.");
        }

        return await CheckAsync(proxyConfiguration, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProxyConnectivityCheckResult> CheckAsync(
        ProxyConfiguration proxyConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxyConfiguration);

        if (proxyConfiguration.IsSocks)
        {
            return ProxyConnectivityCheckResult.Failure(
                "SOCKS5 proxy is not supported for the registration API path. Use HTTP/HTTPS proxy.");
        }

        var lastError = "Proxy verification did not run.";
        foreach (var endpoint in DefaultEndpoints)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    Proxy = CreateWebProxy(proxyConfiguration),
                    UseProxy = true
                };

                using var httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };

                using var response = await httpClient
                    .GetAsync(endpoint, cancellationToken)
                    .ConfigureAwait(false);
                var body = (await response.Content
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false))
                    .Trim();

                if (response.IsSuccessStatusCode && LooksLikeIp(body))
                {
                    return ProxyConnectivityCheckResult.Success(body);
                }

                lastError = $"Proxy test via {endpoint.Host} returned {(int)response.StatusCode}: {Trim(body)}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                lastError = $"Proxy test via {endpoint.Host} timed out.";
            }
            catch (Exception exception)
            {
                lastError = $"Proxy test via {endpoint.Host} failed: {exception.Message}";
            }
        }

        return ProxyConnectivityCheckResult.Failure(lastError);
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

    private static bool LooksLikeIp(string value) =>
        IPAddress.TryParse(value, out _);

    private static string Trim(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= 180 ? value : value[..180] + "...";
    }
}
