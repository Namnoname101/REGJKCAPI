using System;
using System.Text.Json;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

public sealed class ProxyVnService
{
    private const int MaxUsableProxyAttempts = 3;
    private static readonly string[] SupportedRegistrationProxyCategories =
    [
        "VNPT",
        "Viettel",
        "FPT"
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<ProxyVnService> _logger;

    public ProxyVnService(HttpClient httpClient, ILogger<ProxyVnService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<string> BuyProxyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var defaultPackage = new ProxyPackage
        {
            Name = "Proxy tinh 1 ngay",
            PeriodDays = 1,
            ProxyType = 0,
            Category = "VNPT"
        };

        return BuyProxyPackageAsync(defaultPackage, apiKey, cancellationToken);
    }

    public async Task<string> BuyProxyPackageAsync(
        ProxyPackage package,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var proxies = await BuyProxyPackagesAsync(package, apiKey, 1, cancellationToken)
            .ConfigureAwait(false);
        return proxies[0];
    }

    public async Task<IReadOnlyList<string>> BuyProxyPackagesAsync(
        ProxyPackage package,
        string apiKey,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "Proxy.vn API key is missing. Configure ProxyVn:ApiKey.",
                nameof(apiKey));
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                quantity,
                "Proxy quantity must be greater than zero.");
        }

        if (package.ProxyType == 1)
        {
            throw new InvalidOperationException(
                "SOCKS5 Proxy.vn packages are not supported for registration. Use an HTTP/HTTPS package.");
        }

        var proxyCategory = ResolveProxyCategory(package);
        const string type = "HTTP";

        _logger.LogInformation(
            "Calling Proxy.vn API to buy package {PackageName}. ISP={Isp}, Type={Type}, Days={PeriodDays}, Quantity={Quantity}",
            package.Name,
            proxyCategory,
            type,
            package.PeriodDays,
            quantity);

        var proxyResults = await CallProxyVnBatchAsync(
                BuildBuyUrl(apiKey, proxyCategory, package.PeriodDays, type, quantity),
                cancellationToken)
            .ConfigureAwait(false);

        var verifiedProxies = new List<string>(Math.Min(quantity, proxyResults.Count));
        foreach (var proxyResult in proxyResults.Take(quantity))
        {
            var verifiedProxy = await TryGetUsableProxyAsync(
                    proxyResult,
                    apiKey,
                    proxyCategory,
                    type,
                    cancellationToken)
                .ConfigureAwait(false);

            if (verifiedProxy is not null
                && !verifiedProxies.Contains(verifiedProxy, StringComparer.OrdinalIgnoreCase))
            {
                verifiedProxies.Add(verifiedProxy);
            }
        }

        if (verifiedProxies.Count == 0)
        {
            throw new InvalidOperationException(
                $"Proxy.vn did not return any usable proxy for the requested quantity ({quantity}).");
        }

        if (verifiedProxies.Count < quantity)
        {
            _logger.LogWarning(
                "Proxy.vn purchase was only partially fulfilled. Requested={Requested}, Usable={Usable}",
                quantity,
                verifiedProxies.Count);
        }

        return verifiedProxies;
    }

    private async Task<string?> TryGetUsableProxyAsync(
        ProxyVnProxyResult initialProxyResult,
        string apiKey,
        string proxyCategory,
        string type,
        CancellationToken cancellationToken)
    {
        var proxyResult = initialProxyResult;
        ProxyConnectivityCheckResult? lastCheck = null;
        string? lastProxy = null;
        for (var attempt = 1; attempt <= MaxUsableProxyAttempts; attempt++)
        {
            foreach (var candidateProxy in proxyResult.CandidateProxies)
            {
                lastProxy = candidateProxy;
                lastCheck = await ProxyConnectivityVerifier
                    .CheckAsync(candidateProxy, cancellationToken)
                    .ConfigureAwait(false);

                if (lastCheck.IsSuccess)
                {
                    _logger.LogInformation(
                        "Proxy.vn proxy verified. Attempt={Attempt}, Proxy={Proxy}, ObservedIp={ObservedIp}",
                        attempt,
                        MaskProxy(candidateProxy),
                        lastCheck.ObservedIp);
                    return candidateProxy;
                }

                _logger.LogWarning(
                    "Proxy.vn returned unusable proxy endpoint. Attempt={Attempt}, Proxy={Proxy}, Reason={Reason}",
                    attempt,
                    MaskProxy(candidateProxy),
                    lastCheck.Message);
            }

            if (attempt >= MaxUsableProxyAttempts || proxyResult.IdProxy is null)
            {
                break;
            }

            try
            {
                proxyResult = await CallProxyVnAsync(
                        BuildChangeUrl(apiKey, proxyCategory, proxyCategory, type, proxyResult.IdProxy.Value),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A failed replacement for one endpoint must not discard the
                // other proxies from the same batch purchase.
                _logger.LogWarning(
                    exception,
                    "Proxy.vn could not replace an unusable proxy. ProxyId={ProxyId}",
                    proxyResult.IdProxy);
                break;
            }
        }

        _logger.LogWarning(
            "Proxy.vn proxy remained unusable after {Attempts} attempt(s). Proxy={Proxy}, Reason={Reason}",
            MaxUsableProxyAttempts,
            lastProxy is null ? "<missing>" : MaskProxy(lastProxy),
            lastCheck?.Message ?? "unknown verification failure");
        return null;
    }

    private async Task<ProxyVnProxyResult> CallProxyVnAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var results = await CallProxyVnBatchAsync(url, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    private async Task<IReadOnlyList<ProxyVnProxyResult>> CallProxyVnBatchAsync(
        string url,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "HTTP request to Proxy.vn failed.");
            throw new InvalidOperationException(
                $"Could not connect to Proxy.vn API: {exception.Message}",
                exception);
        }

        using var responseToDispose = response;
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Proxy.vn response received. StatusCode={StatusCode}, ContentLength={ContentLength}",
            (int)response.StatusCode,
            json.Length);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Proxy.vn API returned HTTP {(int)response.StatusCode}: {json}");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadProxyResults(document.RootElement, json);
        }
        catch (JsonException firstException)
        {
            // Some versions of the Proxy.vn documentation show multiple root
            // objects separated by commas rather than a JSON array. Accept
            // that response shape as a compatibility fallback.
            try
            {
                using var wrappedDocument = JsonDocument.Parse($"[{json}]");
                return ReadProxyResults(wrappedDocument.RootElement, json);
            }
            catch (JsonException)
            {
                _logger.LogError(
                    firstException,
                    "Failed to parse Proxy.vn JSON. ContentLength={ContentLength}",
                    json.Length);
                throw new InvalidOperationException("Proxy.vn returned invalid JSON.", firstException);
            }
        }
    }

    private static ProxyVnProxyResult ReadProxyResult(JsonElement root, string rawJson)
    {
        return ReadProxyResults(root, rawJson)[0];
    }

    private static IReadOnlyList<ProxyVnProxyResult> ReadProxyResults(
        JsonElement root,
        string rawJson)
    {
        var results = new List<ProxyVnProxyResult>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && HasProxyData(item))
                {
                    results.Add(ReadProxyObject(item, rawJson));
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    ValidateNonProxyStatus(item, rawJson);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (HasProxyData(root))
            {
                results.Add(ReadProxyObject(root, rawJson));
            }
            else
            {
                ValidateNonProxyStatus(root, rawJson);
            }
        }
        else
        {
            throw new InvalidOperationException($"Unexpected Proxy.vn response format: {rawJson}");
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException(
                $"Proxy.vn response did not contain any purchased proxy: {ReadErrorMessage(root, rawJson)}");
        }

        return results;
    }

    private static ProxyVnProxyResult ReadProxyObject(JsonElement root, string rawJson)
    {
        var status = TryGetInt(root, "status");
        if (status is not null && status != 100)
        {
            throw new InvalidOperationException(
                $"Proxy.vn API error status={status}: {ReadErrorMessage(root, rawJson)}");
        }

        var type = TryGetString(root, "type");
        if (string.Equals(type, "SOCKS5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Proxy.vn returned SOCKS5 proxy, but registration requires HTTP/HTTPS proxy.");
        }

        var candidateProxies = new List<string>();
        var candidateErrors = new List<string>();

        // Proxy.vn may expose two different connection hosts:
        // - the structured ip/port/user/password fields contain the original
        //   proxy endpoint;
        // - the legacy "proxy" string can contain an intermediary endpoint.
        // Prefer the original endpoint requested by the customer, but retain
        // the intermediary as a verified fallback when the original host is
        // temporarily unreachable.
        AddCandidate(candidateProxies, candidateErrors, BuildProxyFromFields(root));
        AddCandidate(candidateProxies, candidateErrors, TryGetString(root, "proxy"));

        if (candidateProxies.Count == 0)
        {
            var reason = candidateErrors.Count == 0
                ? "the response did not include complete connection fields."
                : string.Join(" ", candidateErrors);
            throw new InvalidOperationException($"Proxy.vn response did not contain a usable proxy: {reason}");
        }

        return new ProxyVnProxyResult(
            candidateProxies,
            TryGetInt(root, "idproxy"));
    }

    private static void ValidateNonProxyStatus(JsonElement element, string rawJson)
    {
        var status = TryGetInt(element, "status");
        if (status is null or 100 or 200 or 201)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Proxy.vn API error status={status}: {ReadErrorMessage(element, rawJson)}");
    }

    private static bool HasProxyData(JsonElement element) =>
        element.TryGetProperty("proxy", out _)
        || (element.TryGetProperty("ip", out _) && element.TryGetProperty("port", out _));

    private static string? BuildProxyFromFields(JsonElement element)
    {
        var ip = TryGetString(element, "ip");
        var port = TryGetInt(element, "port");
        if (string.IsNullOrWhiteSpace(ip) || port is null)
        {
            return null;
        }

        var user = TryGetString(element, "user");
        var password = TryGetString(element, "password");
        if (string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(password))
        {
            return $"{ip}:{port.Value}";
        }

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return $"{ip}:{port.Value}:{user}:{password}";
    }

    private static void AddCandidate(
        List<string> candidates,
        List<string> errors,
        string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = NormalizeHttpProxy(proxy);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add(exception.Message);
            return;
        }

        if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalized);
        }
    }

    private static string NormalizeHttpProxy(string proxy)
    {
        var value = proxy.Trim();
        if (!ProxyConfigurationParser.TryParse(value, out var proxyConfiguration, out var parseError)
            || proxyConfiguration is null)
        {
            throw new InvalidOperationException(parseError ?? $"Invalid proxy returned by Proxy.vn: {proxy}");
        }

        if (proxyConfiguration.IsSocks)
        {
            throw new InvalidOperationException(
                "SOCKS5 proxy is not supported for registration. Use HTTP/HTTPS proxy.");
        }

        if (!proxyConfiguration.RequiresAuthentication)
        {
            return proxyConfiguration.Server;
        }

        return $"{proxyConfiguration.Host}:{proxyConfiguration.Port}:{proxyConfiguration.Username}:{proxyConfiguration.Password}";
    }

    private static string ResolveProxyCategory(ProxyPackage package)
    {
        var category = package.Category?.Trim();
        if (string.IsNullOrWhiteSpace(category)
            || category.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedRegistrationProxyCategories[
                Random.Shared.Next(SupportedRegistrationProxyCategories.Length)];
        }

        var canonicalCategory = SupportedRegistrationProxyCategories.FirstOrDefault(
            supported => supported.Equals(category, StringComparison.OrdinalIgnoreCase));
        return canonicalCategory
            ?? throw new InvalidOperationException(
                $"Proxy.vn category '{category}' is not supported for registration. " +
                $"Use {string.Join(", ", SupportedRegistrationProxyCategories)}, or ALL.");
    }

    private static string BuildBuyUrl(
        string apiKey,
        string proxyCategory,
        int days,
        string type,
        int quantity = 1) =>
        "https://proxy.vn/apiv2/muaproxy.php" +
        $"?key={Uri.EscapeDataString(apiKey)}" +
        $"&loaiproxy={Uri.EscapeDataString(proxyCategory)}" +
        $"&soluong={Math.Max(1, quantity)}" +
        $"&ngay={Math.Max(1, days)}" +
        $"&type={Uri.EscapeDataString(type)}" +
        "&user=random&password=random";

    private static string BuildChangeUrl(
        string apiKey,
        string currentProxyCategory,
        string targetProxyCategory,
        string type,
        int idProxy) =>
        "https://proxy.vn/apiv2/doiproxy.php" +
        $"?key={Uri.EscapeDataString(apiKey)}" +
        $"&loaiproxy={Uri.EscapeDataString(currentProxyCategory)}" +
        $"&loaiproxynhan={Uri.EscapeDataString(targetProxyCategory)}" +
        $"&type={Uri.EscapeDataString(type)}" +
        "&user=random&password=random" +
        $"&idproxy={idProxy}";

    private static int? TryGetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            _ => null
        };
    }

    private static string ReadErrorMessage(JsonElement element, string rawJson) =>
        TryGetString(element, "comen")
        ?? TryGetString(element, "message")
        ?? TryGetString(element, "error")
        ?? rawJson;

    private static string MaskProxy(string proxy)
    {
        if (!ProxyConfigurationParser.TryParse(proxy, out var proxyConfiguration, out _)
            || proxyConfiguration is null)
        {
            return "<invalid-proxy>";
        }

        return proxyConfiguration.Masked;
    }

    private sealed record ProxyVnProxyResult(
        IReadOnlyList<string> CandidateProxies,
        int? IdProxy);
}
