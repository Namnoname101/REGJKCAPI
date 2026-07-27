using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BAOCODETOOL.Models;
using OpenQA.Selenium;

namespace BAOCODETOOL.Core;

public sealed record GgDirectSession(
    string ApiOrigin,
    string SiteOrigin,
    string RegistrationUrl,
    string StaticToken,
    string SiteCode,
    string DeviceId,
    string AppVersion);

internal static partial class GgDirectWorkflow
{
    private const string SiteOrigin = "https://www.gg88v.com";
    private const string RegistrationUrl = "https://www.gg88v.com/home/register?id=456510325";
    private const string DefaultApiOrigin = "https://www.gg8839.com/hall/";
    private const string DefaultStaticToken = "b2e3d672-9d88-47a7-81b4-9d7ffc62054f";
    private const string DefaultSiteCode = "2365";
    private const string DefaultGeeGuardAppId = "9ia4hndgblg9xihxcwgdjt9ztg8sjwaf";
    private const string DefaultAppVersion = "7.3.254";
    private const string AgentCode = "456510325";
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) " +
        "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 " +
        "Mobile/15E148 Safari/604.1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly ConcurrentDictionary<string, DirectEgressThrottle> EgressThrottles = new();
    private static readonly ConcurrentDictionary<string, string> BankCodeCache = new();
    private static readonly SemaphoreSlim BankSettingsGate = new(1, 1);
    private static int BankSettingsLoaded;
    private static readonly TimeSpan MinimumEgressStartSpacing = TimeSpan.FromMilliseconds(1200);
    private static readonly SemaphoreSlim GeeGuardConcurrency = new(8, 8);

    public static async Task<DirectRegistrationOutcome> RegisterAsync(
        AccountModel account,
        CancellationToken cancellationToken)
    {
        if (!ProxyConfigurationParser.TryParse(account.Proxy, out var proxy, out var proxyError))
        {
            return Failed(proxyError ?? "GG88 proxy configuration is invalid.");
        }

        if (proxy?.IsSocks == true)
        {
            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.FallbackToBrowser,
                "GG88 direct API does not support SOCKS proxies.");
        }

        try
        {
            var throttle = EgressThrottles.GetOrAdd(
                proxy?.ProxyServerArgument ?? "direct",
                _ => new DirectEgressThrottle());
            using (await throttle.AcquireAsync(
                    MinimumEgressStartSpacing,
                    cancellationToken)
                .ConfigureAwait(false))
            {
            }

            var cookies = new CookieContainer();
            using var handler = CreateHandler(proxy, cookies);
            using var pageClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            ConfigurePageHeaders(pageClient);

            var pageTask = TryGetPageHtmlAsync(pageClient, cancellationToken);
            var geeTokenTask = GetGeeGuardTokenAsync(proxy, cancellationToken);
            await Task.WhenAll((Task)pageTask, geeTokenTask).ConfigureAwait(false);

            var pageHtml = await pageTask.ConfigureAwait(false) ?? string.Empty;
            var geeToken = await geeTokenTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(geeToken))
            {
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.FallbackToBrowser,
                    "GG88 GeeGuard token could not be generated.");
            }

            var staticToken = FindValue(pageHtml, StaticTokenRegex()) ?? DefaultStaticToken;
            var siteCode = FindValue(pageHtml, SiteCodeRegex()) ?? DefaultSiteCode;
            var deviceId = Guid.NewGuid().ToString();
            var username = account.GetUsername(TargetSite.GG88);
            var password = account.GetPassword(TargetSite.GG88);
            var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = BuildRegistrationPayload(
                account,
                username,
                password,
                deviceId,
                staticToken,
                geeToken,
                unixTime);
            var plaintext = JsonSerializer.Serialize(payload, JsonOptions);
            var key = GgCrypto.DeriveStaticKey(staticToken);
            var encrypted = GgCrypto.Encrypt(plaintext, key);

            using var apiHandler = CreateHandler(proxy, cookies);
            using var apiClient = new HttpClient(apiHandler)
            {
                BaseAddress = new Uri(DefaultApiOrigin),
                Timeout = TimeSpan.FromSeconds(20)
            };
            ConfigureApiHeaders(
                apiClient,
                staticToken,
                siteCode,
                deviceId,
                DefaultAppVersion,
                unixTime);

            using var request = CreateEncryptedRequest(
                "api/member/register",
                encrypted,
                unixTime);

            using var response = await apiClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var decodedResponse = GgCrypto.TryDecryptResponse(responseText, key) ?? responseText;

            var responseToken = TryGetResponseHeader(
                response,
                "token",
                "session_key",
                "newjwt",
                "authorization");
            return ParseRegistrationResponse(
                decodedResponse,
                response.StatusCode,
                username,
                cookies,
                responseToken,
                new GgDirectSession(
                    DefaultApiOrigin,
                    SiteOrigin,
                    RegistrationUrl,
                    staticToken,
                    siteCode,
                    deviceId,
                    DefaultAppVersion));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.FallbackToBrowser,
                $"GG88 direct API exception: {exception.Message}");
        }
    }

    public static async Task<DirectBankSetupOutcome> SetupBankAsync(
        AccountModel account,
        DirectRegistrationSession session,
        CancellationToken cancellationToken)
    {
        if (session.Gg is null)
        {
            return BankFailed("GG88 direct bank setup is missing its registration session metadata.");
        }

        if (!ProxyConfigurationParser.TryParse(account.Proxy, out var proxy, out var proxyError))
        {
            return BankFailed(proxyError ?? "GG88 proxy configuration is invalid.");
        }

        if (proxy?.IsSocks == true)
        {
            return BankFailed("GG88 direct bank setup does not support SOCKS proxies.");
        }

        try
        {
            var gg = session.Gg;
            var cookies = session.Cookies ?? new CookieContainer();
            using var handler = CreateHandler(proxy, cookies);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(gg.ApiOrigin),
                Timeout = TimeSpan.FromSeconds(20)
            };
            ConfigureApiHeaders(
                client,
                gg.StaticToken,
                gg.SiteCode,
                gg.DeviceId,
                gg.AppVersion,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                session.UserName);

            client.DefaultRequestHeaders.Remove("platformType");
            var bankResolution = await ResolveBankCodeAsync(
                    client,
                    gg.StaticToken,
                    gg.SiteCode,
                    account.BankName,
                    cancellationToken)
                .ConfigureAwait(false);
            var bankCode = bankResolution.Code;
            client.DefaultRequestHeaders.Remove("token");
            client.DefaultRequestHeaders.TryAddWithoutValidation("token", session.Token);
            client.DefaultRequestHeaders.TryAddWithoutValidation("platformType", "5");

            if (string.IsNullOrWhiteSpace(bankCode))
            {
                return BankFailed($"GG88 does not recognize bank '{account.BankName}'.");
            }

            var key = GgCrypto.DeriveLoginKey(session.Token, session.UserName);
            var withdrawPassword = account.Level2Password.Trim();

            var verifyPassword = await SendEncryptedApiAsync(
                    client,
                    "api/member/user/security/verifyWithdrawPass",
                    new Dictionary<string, object?>
                    {
                        ["withdraw_pass"] = withdrawPassword
                    },
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryValidateApiResponse(verifyPassword, out var verifyError))
            {
                return BankFailed(
                    $"GG88 withdraw-password verification failed: {verifyError}",
                    verifyPassword.Text);
            }

            var modifyPassword = await SendEncryptedApiAsync(
                    client,
                    "api/member/user/security/modifyWithdrawPass",
                    new Dictionary<string, object?>
                    {
                        ["withdraw_pass"] = withdrawPassword,
                        ["secondVerify"] = new Dictionary<string, object?>()
                    },
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryValidateApiResponse(modifyPassword, out var modifyError))
            {
                return BankFailed(
                    $"GG88 withdraw-password setup failed: {modifyError}",
                    modifyPassword.Text);
            }
            ApplyRotatedSessionToken(client, modifyPassword, session.UserName, ref key);

            var bankPayload = new Dictionary<string, object?>
            {
                ["name"] = RemoveDiacritics(account.FullName).Trim().ToUpperInvariant(),
                ["number"] = account.AccountNumber.Trim(),
                ["bank"] = bankCode,
                ["withdrawPassword"] = withdrawPassword,
                ["pinNumberType"] = 0,
                ["accountType"] = 1,
                ["currencyCode"] = "VND",
                ["extendedField"] = new Dictionary<string, object?>
                {
                    ["bankCardType"] = 0,
                    ["CCINumber"] = string.Empty
                }
            };
            var bind = await SendEncryptedApiAsync(
                    client,
                    "api/finance/certify/payBindCard",
                    bankPayload,
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryValidateApiResponse(bind, out var bindError))
            {
                return BankFailed(
                    $"GG88 bank bind failed for bank code '{bankCode}' "
                    + $"({bankResolution.Source}): {bindError}",
                    bind.Text);
            }

            var linkedAccounts = await SendEncryptedApiAsync(
                    client,
                    "api/finance/certify/getUserBankCardList",
                    new Dictionary<string, object?>(),
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryValidateApiResponse(linkedAccounts, out var listError))
            {
                return BankFailed(
                    $"GG88 bank verification failed: {listError}",
                    linkedAccounts.Text);
            }

            if (!ContainsAccountNumber(linkedAccounts.Text, account.AccountNumber))
            {
                return BankFailed(
                    "GG88 accepted the bank bind but the linked account could not be verified.",
                    linkedAccounts.Text);
            }

            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Success,
                "GG88 direct bank setup completed.",
                bind.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return BankFailed($"GG88 direct bank setup exception: {exception.Message}");
        }
    }

    private static async Task<GgApiResponse> SendEncryptedApiAsync(
        HttpClient client,
        string path,
        Dictionary<string, object?> payload,
        string key,
        CancellationToken cancellationToken)
    {
        var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestPayload = new Dictionary<string, object?>(payload)
        {
            ["time"] = unixTime
        };
        var plaintext = JsonSerializer.Serialize(requestPayload, JsonOptions);
        var encrypted = GgCrypto.Encrypt(plaintext, key);

        using var request = CreateEncryptedRequest(path, encrypted, unixTime);
        using var response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        var decoded = GgCrypto.TryDecryptResponse(responseText, key) ?? responseText;
        var rotatedToken = TryGetResponseHeader(
                               response,
                               "token",
                               "session_key",
                               "newjwt")
                           ?? TryFindResponseToken(decoded);
        return new GgApiResponse(response.StatusCode, decoded, rotatedToken);
    }

    private static async Task<BankCodeResolution> ResolveBankCodeAsync(
        HttpClient client,
        string staticToken,
        string siteCode,
        string bankName,
        CancellationToken cancellationToken)
    {
        var cacheKey = Compact(bankName);
        if (TryGetCachedBankCode(cacheKey, out var cached))
        {
            return new BankCodeResolution(cached, "live settings cache");
        }

        await BankSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedBankCode(cacheKey, out cached))
            {
                return new BankCodeResolution(cached, "live settings cache");
            }

            if (Volatile.Read(ref BankSettingsLoaded) == 1)
            {
                return new BankCodeResolution(
                    FindKnownVietnameseBankCode(bankName),
                    "fallback mapping");
            }

            var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var settingsPath = "api/finance/certify/withdrawSettingV3"
                               + $"?siteCode={Uri.EscapeDataString(siteCode)}"
                               + $"&token={Uri.EscapeDataString(staticToken)}"
                               + "&currency=VND&language=vi";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                settingsPath)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            request.Headers.TryAddWithoutValidation("x-data-mode", "chipher");
            request.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
            request.Headers.TryAddWithoutValidation("timestamp", unixTime.ToString());

            using var response = await client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var key = GgCrypto.DeriveStaticKey(staticToken);
            var decoded = TryDecryptJsonCipherResponse(responseText, key) ?? responseText;
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(decoded))
            {
                using var document = JsonDocument.Parse(decoded);
                if (CacheBankCodes(document.RootElement) > 0)
                {
                    Volatile.Write(ref BankSettingsLoaded, 1);
                }

                if (TryGetCachedBankCode(cacheKey, out cached))
                {
                    return new BankCodeResolution(cached, "live settings");
                }

                var resolved = FindBankCode(document.RootElement, bankName);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    BankCodeCache[cacheKey] = resolved;
                    return new BankCodeResolution(resolved, "live settings");
                }
            }

            return new BankCodeResolution(
                FindKnownVietnameseBankCode(bankName),
                "fallback mapping");
        }
        catch (JsonException)
        {
            return new BankCodeResolution(
                FindKnownVietnameseBankCode(bankName),
                "fallback mapping after unreadable settings");
        }
        finally
        {
            BankSettingsGate.Release();
        }
    }

    private static int CacheBankCodes(JsonElement root)
    {
        var cached = 0;
        foreach (var item in EnumerateObjects(root))
        {
            var name = TryGetObjectString(item, "Name");
            var code = TryGetObjectString(item, "en");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            BankCodeCache[Compact(name)] = code;
            BankCodeCache[Compact(code)] = code;
            cached++;
        }

        return cached;
    }

    private static bool TryGetCachedBankCode(string cacheKey, out string code)
    {
        if (BankCodeCache.TryGetValue(cacheKey, out code!))
        {
            return true;
        }

        if (cacheKey.Length >= 5)
        {
            var partial = BankCodeCache
                .Where(item => item.Key.Length >= 5
                               && (item.Key.Contains(cacheKey, StringComparison.Ordinal)
                                   || cacheKey.Contains(item.Key, StringComparison.Ordinal)))
                .OrderBy(item => Math.Abs(item.Key.Length - cacheKey.Length))
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(partial.Value))
            {
                code = partial.Value;
                return true;
            }
        }

        code = string.Empty;
        return false;
    }

    private static string? TryDecryptJsonCipherResponse(string responseText, string key)
    {
        var direct = GgCrypto.TryDecryptResponse(responseText, key);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && TryGetProperty(document.RootElement, "encryptString", out var encrypted)
                   && encrypted.ValueKind == JsonValueKind.String
                ? GgCrypto.TryDecryptResponse(encrypted.GetString() ?? string.Empty, key)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryFindResponseToken(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            return TryFindString(
                document.RootElement,
                "token",
                "session_key",
                "newjwt",
                "jwt_token");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ApplyRotatedSessionToken(
        HttpClient client,
        GgApiResponse response,
        string userName,
        ref string key)
    {
        if (string.IsNullOrWhiteSpace(response.RotatedToken))
        {
            return;
        }

        client.DefaultRequestHeaders.Remove("token");
        client.DefaultRequestHeaders.TryAddWithoutValidation("token", response.RotatedToken);
        key = GgCrypto.DeriveLoginKey(response.RotatedToken, userName);
    }

    private static bool TryValidateApiResponse(GgApiResponse response, out string error)
    {
        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            error = $"HTTP {(int)response.StatusCode}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            error = "empty response";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && TryGetProperty(root, "success", out var success)
                && success.ValueKind == JsonValueKind.False)
            {
                error = TryFindString(root, "msg", "message", "errorMessage")
                        ?? "request was rejected";
                return false;
            }

            var code = TryFindInt64(root, "code", "errorCode");
            if (!IsSuccessCode(code))
            {
                error = TryFindString(root, "msg", "message", "errorMessage")
                        ?? $"provider code {code}";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "unreadable response";
            return false;
        }
    }

    private static bool TryBuildBankPayload(
        string rulesJson,
        AccountModel account,
        string withdrawPassword,
        out Dictionary<string, object?> payload,
        out string error) =>
        TryBuildBankPayloadFromConfig(
            rulesJson,
            rulesJson,
            account,
            withdrawPassword,
            out payload,
            out error);

    private static bool TryBuildBankPayloadFromConfig(
        string rulesJson,
        string withdrawInfoJson,
        AccountModel account,
        string withdrawPassword,
        out Dictionary<string, object?> payload,
        out string error)
    {
        payload = [];
        try
        {
            using var rulesDocument = JsonDocument.Parse(rulesJson);
            using var withdrawInfoDocument = JsonDocument.Parse(withdrawInfoJson);
            var rulesRoot = rulesDocument.RootElement;
            var withdrawInfoRoot = withdrawInfoDocument.RootElement;
            var methodDetails = FindBankWithdrawMethodDetails(withdrawInfoRoot)
                                ?? FindBankWithdrawMethodDetails(rulesRoot);
            var template = FindBankFormTemplate(
                rulesRoot,
                methodDetails?.UseTypeId,
                out var templateMethod);
            var method = methodDetails?.MethodId ?? templateMethod;
            if (method is null)
            {
                error = "GG88 bank rules did not contain a VND bank-withdraw method.";
                return false;
            }

            var bankCode = FindBankCode(template, account.BankName)
                           ?? FindBankCode(withdrawInfoRoot, account.BankName)
                           ?? FindBankCode(rulesRoot, account.BankName)
                           ?? FindKnownVietnameseBankCode(account.BankName);
            if (string.IsNullOrWhiteSpace(bankCode))
            {
                error = $"GG88 bank rules did not contain bank '{account.BankName}'.";
                return false;
            }

            var formValues = BuildBankFormValues(template, account, bankCode);
            payload = new Dictionary<string, object?>
            {
                ["formBody"] = formValues.Select(item => new Dictionary<string, string>
                {
                    ["fieldKey"] = item.Key,
                    ["fieldVal"] = item.Value
                }).ToArray(),
                ["twoStepVerifyInfo"] = new Dictionary<string, object?>(),
                ["withdrawMethodId"] = method,
                ["withdrawPassword"] = withdrawPassword
            };
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "GG88 bank rules returned unreadable JSON.";
            return false;
        }
    }

    private static JsonElement FindBankFormTemplate(
        JsonElement root,
        string? preferredUseTypeId,
        out object? methodId)
    {
        var candidates = new List<(int Score, JsonElement Template, object? MethodId)>();
        CollectBankFormTemplates(root, preferredUseTypeId, candidates);
        var selected = candidates
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (selected.Template.ValueKind != JsonValueKind.Undefined)
        {
            methodId = selected.MethodId;
            return selected.Template;
        }

        methodId = null;
        return root;
    }

    private static void CollectBankFormTemplates(
        JsonElement element,
        string? preferredUseTypeId,
        ICollection<(int Score, JsonElement Template, object? MethodId)> candidates)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectBankFormTemplates(item, preferredUseTypeId, candidates);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var explicitMethod = FindExplicitMethodId(property.Value);
                var keyedMethod = IsProviderId(property.Name) ? property.Name : null;
                var method = explicitMethod ?? keyedMethod;
                if (method is not null)
                {
                    var score = ScoreBankFormTemplate(property.Value);
                    if (score > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(preferredUseTypeId)
                            && string.Equals(
                                Convert.ToString(
                                    method,
                                    System.Globalization.CultureInfo.InvariantCulture),
                                preferredUseTypeId,
                                StringComparison.Ordinal))
                        {
                            score += 1_000;
                        }
                        candidates.Add((score, property.Value.Clone(), method));
                    }
                }

                CollectBankFormTemplates(property.Value, preferredUseTypeId, candidates);
            }
        }
    }

    private static object? FindExplicitMethodId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "withdrawMethodId", "useTypeId" })
        {
            if (TryGetProperty(element, name, out var id)
                && id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                return ToJsonValue(id);
            }
        }
        return null;
    }

    private static int ScoreBankFormTemplate(JsonElement template)
    {
        var categories = EnumerateObjects(template)
            .Select(item => TryGetObjectString(item, "dbField")
                            ?? TryGetObjectString(item, "fieldKey"))
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => ClassifyBankField(field!))
            .ToHashSet(StringComparer.Ordinal);
        if (!categories.Contains("account") || !categories.Contains("bank"))
        {
            return 0;
        }

        var text = RemoveDiacritics(template.GetRawText()).ToLowerInvariant();
        var score = 50;
        if (categories.Contains("name"))
        {
            score += 20;
        }
        if (text.Contains("vnd", StringComparison.Ordinal)
            || text.Contains("ngan hang", StringComparison.Ordinal)
            || text.Contains("bankcode", StringComparison.Ordinal))
        {
            score += 15;
        }
        if (text.Contains("usdt", StringComparison.Ordinal)
            || text.Contains("crypto", StringComparison.Ordinal)
            || text.Contains("momo", StringComparison.Ordinal)
            || text.Contains("zalopay", StringComparison.Ordinal))
        {
            score -= 40;
        }
        return score;
    }

    private static bool IsProviderId(string value) =>
        value.Length >= 6 && value.All(char.IsDigit);

    private static object? FindBankWithdrawMethod(JsonElement root) =>
        FindBankWithdrawMethodDetails(root)?.MethodId;

    private static BankWithdrawMethod? FindBankWithdrawMethodDetails(JsonElement root)
    {
        var candidates = new List<(int Score, BankWithdrawMethod Method)>();
        foreach (var item in EnumerateObjects(root))
        {
            if (!TryGetProperty(item, "withdrawMethodId", out var id))
            {
                continue;
            }

            var text = BuildProviderSearchText(item);
            var score = 0;
            if (text.Contains("vnd", StringComparison.Ordinal))
            {
                score += 20;
            }
            if (text.Contains("bank", StringComparison.Ordinal)
                || text.Contains("ngan hang", StringComparison.Ordinal)
                || text.Contains("the ngan hang", StringComparison.Ordinal))
            {
                score += 120;
            }
            if (TryGetObjectBoolean(item, "bindEnabled") is true)
            {
                score += 10;
            }
            if (TryGetObjectBoolean(item, "requiredSelectBank") is true)
            {
                score += 30;
            }
            if (text.Contains("usdt", StringComparison.Ordinal)
                || text.Contains("crypto", StringComparison.Ordinal)
                || text.Contains("momo", StringComparison.Ordinal)
                || text.Contains("zalo", StringComparison.Ordinal)
                || text.Contains("qr code", StringComparison.Ordinal)
                || text.Contains("wallet", StringComparison.Ordinal))
            {
                score -= 100;
            }
            if (text.Contains("no wallet", StringComparison.Ordinal)
                || text.Contains("no vi", StringComparison.Ordinal))
            {
                score -= 40;
            }

            var methodId = ToJsonValue(id);
            if (methodId is null)
            {
                continue;
            }
            var useTypeId = TryGetObjectString(item, "useTypeId");
            candidates.Add((
                score,
                new BankWithdrawMethod(methodId, useTypeId, item.Clone())));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Method)
            .FirstOrDefault();
    }

    private static string BuildProviderSearchText(JsonElement item)
    {
        var values = new[]
        {
            TryGetObjectString(item, "currencyCode"),
            TryGetObjectString(item, "paymentMethodName"),
            TryGetObjectString(item, "paymentTypeName"),
            TryGetObjectString(item, "withdrawTypeName"),
            TryGetObjectString(item, "typeName"),
            TryGetObjectString(item, "name")
        }.Where(value => !string.IsNullOrWhiteSpace(value));
        return RemoveDiacritics(string.Join(' ', values)).ToLowerInvariant();
    }

    private static string? FindBankCode(JsonElement root, string targetBankName)
    {
        var target = Compact(targetBankName);
        var bestScore = 0;
        string? bestCode = null;
        foreach (var item in EnumerateObjects(root))
        {
            var labels = new[]
            {
                TryGetObjectString(item, "Name"),
                TryGetObjectString(item, "name"),
                TryGetObjectString(item, "bankName"),
                TryGetObjectString(item, "bankLocaleName"),
                TryGetObjectString(item, "label"),
                TryGetObjectString(item, "vi"),
                TryGetObjectString(item, "en")
            }.Where(value => !string.IsNullOrWhiteSpace(value));

            var score = labels.Select(label => Compact(label!))
                .Select(label => label == target ? 100 : label.Contains(target, StringComparison.Ordinal) || target.Contains(label, StringComparison.Ordinal) ? 50 : 0)
                .DefaultIfEmpty(0)
                .Max();
            if (score <= bestScore)
            {
                continue;
            }

            var code = TryGetObjectString(item, "bankCode")
                       ?? TryGetObjectString(item, "code")
                       ?? TryGetObjectString(item, "value")
                       ?? TryGetObjectString(item, "en")
                       ?? TryGetObjectString(item, "id");
            if (!string.IsNullOrWhiteSpace(code))
            {
                bestScore = score;
                bestCode = code;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestCode))
        {
            return bestCode;
        }

        var trimmed = targetBankName.Trim();
        return trimmed.Length <= 5 && trimmed.All(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? trimmed
            : null;
    }

    private static string? FindKnownVietnameseBankCode(string bankName)
    {
        var normalized = Compact(bankName);
        return normalized switch
        {
            "vietcombank" or "nganhangngoaithuongvietnam" or "vcb" => "VIETCOMB",
            "techcombank" or "nganhangkythuongvietnam" or "tcb" => "TECHCOMB",
            "vietinbank" or "nganhangcongthuongvietnam" or "ctg" => "CTG",
            "bidv" or "nganhangdautuvaphattrienvietnam" => "BIDV",
            "acb" or "nganhangachau" => "ACB",
            "mbbank" or "mb" or "nganhangquandoi" or "mbb" => "MBBANK",
            "vpbank" or "vpb" => "VPB",
            "vib" => "VIB",
            "tpbank" or "tpb" => "TPB",
            "sacombank" or "stb" => "STB",
            "hdbank" or "hdb" => "HDB",
            "shb" => "SHB",
            "eximbank" or "eib" => "EIB",
            "msb" => "MSB",
            "ocb" => "OCB",
            "seabank" or "ssb" => "SSB",
            "abbank" or "abb" => "ABB",
            "namabank" or "nab" => "NAB",
            "pvcombank" or "pvcb" => "PVCB",
            "vietabank" or "vab" => "VAB",
            "bacabank" or "bab" => "BAB",
            "kienlongbank" or "klb" => "KLB",
            "ncb" => "NCB",
            "lpbank" or "lienvietpostbank" or "lpb" => "LPB",
            _ => null
        };
    }

    private static IReadOnlyDictionary<string, string> BuildBankFormValues(
        JsonElement root,
        AccountModel account,
        string bankCode)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in EnumerateObjects(root))
        {
            var fieldKey = TryGetObjectString(item, "dbField")
                           ?? TryGetObjectString(item, "fieldKey");
            if (string.IsNullOrWhiteSpace(fieldKey))
            {
                continue;
            }

            var category = ClassifyBankField(fieldKey);
            var value = category switch
            {
                "name" => account.FullName.Trim().ToUpperInvariant(),
                "account" => account.AccountNumber.Trim(),
                "bank" => bankCode,
                "branch" when !string.IsNullOrWhiteSpace(account.Branch) => account.Branch.Trim(),
                "currency" => "VND",
                "phone" => PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber),
                _ => null
            };
            if (value is null || !categories.Add(category))
            {
                continue;
            }
            values[fieldKey] = value;
        }

        AddDefaultField(values, categories, "name", "realName", account.FullName.Trim().ToUpperInvariant());
        AddDefaultField(values, categories, "account", "account", account.AccountNumber.Trim());
        AddDefaultField(values, categories, "bank", "bankCode", bankCode);
        return values;
    }

    private static void AddDefaultField(
        IDictionary<string, string> values,
        ISet<string> categories,
        string category,
        string fieldKey,
        string fieldValue)
    {
        if (categories.Add(category))
        {
            values[fieldKey] = fieldValue;
        }
    }

    private static string ClassifyBankField(string fieldKey)
    {
        var key = Compact(fieldKey);
        if (key.Contains("realname", StringComparison.Ordinal)
            || key.Contains("fullname", StringComparison.Ordinal)
            || key.Contains("accountname", StringComparison.Ordinal)
            || key.Contains("payee", StringComparison.Ordinal))
        {
            return "name";
        }
        if (key.Contains("bankcode", StringComparison.Ordinal)
            || key.Contains("bankid", StringComparison.Ordinal)
            || key == "bank")
        {
            return "bank";
        }
        if (key.Contains("account", StringComparison.Ordinal)
            || key.Contains("cardnumber", StringComparison.Ordinal))
        {
            return "account";
        }
        if (key.Contains("branch", StringComparison.Ordinal))
        {
            return "branch";
        }
        if (key.Contains("currency", StringComparison.Ordinal))
        {
            return "currency";
        }
        if (key.Contains("phone", StringComparison.Ordinal)
            || key.Contains("mobile", StringComparison.Ordinal))
        {
            return "phone";
        }
        return "unknown:" + key;
    }

    private static bool ContainsAccountNumber(string responseText, string accountNumber)
    {
        var expected = DigitsOnly(accountNumber);
        if (expected.Length < 4)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            foreach (var property in Walk(document.RootElement))
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidate = DigitsOnly(property.Value.GetString() ?? string.Empty);
                if (candidate == expected
                    || candidate.Length >= 4
                    && candidate.EndsWith(expected[^4..], StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static string BuildBankTrace(
        string withdrawInfoJson,
        string rulesJson,
        IReadOnlyDictionary<string, object?> payload,
        string bindResponse)
    {
        try
        {
            using var infoDocument = JsonDocument.Parse(withdrawInfoJson);
            using var rulesDocument = JsonDocument.Parse(rulesJson);
            var methods = EnumerateObjects(infoDocument.RootElement)
                .Where(item => TryGetProperty(item, "withdrawMethodId", out _))
                .Select(item => item.Clone())
                .ToArray();
            var banks = EnumerateObjects(infoDocument.RootElement)
                .Concat(EnumerateObjects(rulesDocument.RootElement))
                .Where(item => TryGetProperty(item, "bankCode", out _))
                .Select(item => item.Clone())
                .Take(200)
                .ToArray();
            var templates = EnumerateObjects(rulesDocument.RootElement)
                .Where(item => TryGetProperty(item, "useTypeId", out _)
                               || TryGetProperty(item, "fields", out _))
                .Select(item => item.Clone())
                .Take(100)
                .ToArray();
            return JsonSerializer.Serialize(
                new { payload, bindResponse, methods, banks, templates },
                JsonOptions);
        }
        catch (JsonException)
        {
            return bindResponse;
        }
    }

    private static bool UsesV2BankBind(
        string withdrawInfoJson,
        IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("withdrawMethodId", out var selectedMethod)
            || selectedMethod is null)
        {
            return false;
        }

        var selected = Convert.ToString(
            selectedMethod,
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var document = JsonDocument.Parse(withdrawInfoJson);
            foreach (var item in EnumerateObjects(document.RootElement))
            {
                var methodId = TryGetObjectString(item, "withdrawMethodId");
                if (!string.Equals(methodId, selected, StringComparison.Ordinal))
                {
                    continue;
                }

                return !string.IsNullOrWhiteSpace(
                    TryGetObjectString(item, "paymentMethodId"));
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private static Dictionary<string, object?> BuildBankPreflightPayload(
        string withdrawInfoJson,
        IReadOnlyDictionary<string, object?> payload,
        string withdrawPassword)
    {
        var result = new Dictionary<string, object?>();
        if (!payload.TryGetValue("withdrawMethodId", out var selectedMethod)
            || selectedMethod is null)
        {
            return result;
        }

        var selected = Convert.ToString(
            selectedMethod,
            System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var document = JsonDocument.Parse(withdrawInfoJson);
            foreach (var item in EnumerateObjects(document.RootElement))
            {
                if (!string.Equals(
                        TryGetObjectString(item, "withdrawMethodId"),
                        selected,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var accountType = TryGetObjectString(item, "withdrawTypeId")
                                  ?? TryGetObjectString(item, "typeId")
                                  ?? "1";
                result["addWithdrawAccountType"] = long.TryParse(
                    accountType,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var numericType)
                    ? numericType
                    : accountType;
                if (string.IsNullOrWhiteSpace(
                        TryGetObjectString(item, "paymentMethodId")))
                {
                    result["withdrawalPassword"] = withdrawPassword;
                }
                break;
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateObjects(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateObjects(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static object? ToJsonValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            _ => element.GetRawText()
        };

    private static string? TryGetObjectString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool? TryGetObjectBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var boolean) => boolean,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number != 0,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string Compact(string value) =>
        new(RemoveDiacritics(value)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string DigitsOnly(string value) =>
        new(value.Where(char.IsDigit).ToArray());

    private static DirectBankSetupOutcome BankFailed(string message, string? rawResponse = null) =>
        new(DirectBankSetupStatus.Failed, message, rawResponse);

    private static DirectRegistrationOutcome ParseRegistrationResponse(
        string responseText,
        HttpStatusCode statusCode,
        string username,
        CookieContainer cookies,
        string? responseToken,
        GgDirectSession ggSession)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Failed($"GG88 register returned HTTP {(int)statusCode} with an empty response.");
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var code = TryFindInt64(root, "code", "errorCode");
            var message = TryFindString(root, "msg", "message", "errorMessage") ?? string.Empty;
            var token = TryFindString(
                            root,
                            "token",
                            "session_key",
                            "loginToken",
                            "accessToken",
                            "sessionToken",
                            "jwt_token",
                            "newjwt")
                        ?? responseToken;

            if (statusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
                && IsSuccessCode(code)
                && !string.IsNullOrWhiteSpace(token))
            {
                var customerId = TryFindInt64(root, "userid", "userId", "uid", "id");
                var cryptoIdentity = customerId?.ToString(
                                         System.Globalization.CultureInfo.InvariantCulture)
                                     ?? TryFindString(root, "username", "uid")
                                     ?? username;
                return new DirectRegistrationOutcome(
                    DirectRegistrationStatus.Success,
                    "GG88 direct API registration completed.",
                    RawResponse: responseText,
                    Session: new DirectRegistrationSession(
                        token,
                        cryptoIdentity,
                        customerId,
                        DefaultSiteCode,
                        cookies,
                        ggSession));
            }

            var usernameConflict = IsUsernameConflict(code, message);
            var detail = string.IsNullOrWhiteSpace(message)
                ? $"GG88 register rejected the request (code {code?.ToString() ?? "unknown"})."
                : message;
            return new DirectRegistrationOutcome(
                DirectRegistrationStatus.Failed,
                detail,
                usernameConflict,
                responseText);
        }
        catch (JsonException)
        {
            return Failed(
                $"GG88 register returned an unreadable HTTP {(int)statusCode} response.",
                responseText);
        }
    }

    private static bool IsSuccessCode(long? code) =>
        code is null or 0 or 1 or 200;

    private static bool IsUsernameConflict(long? code, string message)
    {
        if (code is 1003 or 1004 or 1101 or 1102)
        {
            return true;
        }

        var normalized = RemoveDiacritics(message).ToLowerInvariant();
        return (normalized.Contains("tai khoan", StringComparison.Ordinal)
                || normalized.Contains("username", StringComparison.Ordinal)
                || normalized.Contains("account", StringComparison.Ordinal))
               && (normalized.Contains("ton tai", StringComparison.Ordinal)
                   || normalized.Contains("da duoc", StringComparison.Ordinal)
                   || normalized.Contains("exists", StringComparison.Ordinal));
    }

    private static string RemoveDiacritics(string value)
    {
        value = value.Replace('\u0111', 'd').Replace('\u0110', 'D');
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character is 'đ' or 'Đ' ? 'd' : character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static Dictionary<string, object?> BuildRegistrationPayload(
        AccountModel account,
        string username,
        string password,
        string deviceId,
        string staticToken,
        string geeToken,
        long unixTime)
    {
        var phone = PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber);
        return new Dictionary<string, object?>
        {
            ["domain"] = SiteOrigin,
            ["clientType"] = 5,
            ["jpush_id"] = string.Empty,
            ["loginId"] = deviceId,
            ["os_type"] = 5,
            ["deviceOsType"] = 5,
            ["deviceModel"] = "Mobile Safari v16.0",
            ["platformType"] = "1001",
            ["operationId"] = 0,
            ["pkgId"] = 1,
            ["fromClub"] = 2,
            ["token"] = staticToken,
            ["version"] = 9,
            ["realName"] = account.FullName.ToUpperInvariant(),
            ["currency"] = "VND",
            ["inviterId"] = long.Parse(AgentCode, System.Globalization.CultureInfo.InvariantCulture),
            ["registerLink"] = RegistrationUrl,
            ["agent_type"] = 2,
            ["agent_code"] = AgentCode,
            ["inviterIdSource"] = 1,
            ["mobilePhoneNumber"] = $"+84-{phone}",
            ["platformId"] = username,
            ["passwd"] = password,
            ["registerType"] = 0,
            ["geeToken"] = geeToken,
            ["time"] = unixTime
        };
    }

    internal static void ConfigureApiHeaders(
        HttpClient client,
        string token,
        string siteCode,
        string deviceId,
        string appVersion,
        long unixTime,
        string? username = null)
    {
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MobileUserAgent);
        client.DefaultRequestHeaders.Referrer = new Uri(RegistrationUrl);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", SiteOrigin);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        client.DefaultRequestHeaders.TryAddWithoutValidation("platformType", "5");
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-device", "3-1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept-language", "vi");
        client.DefaultRequestHeaders.TryAddWithoutValidation("device", deviceId);
        client.DefaultRequestHeaders.TryAddWithoutValidation("currency", "VND");
        client.DefaultRequestHeaders.TryAddWithoutValidation("siteCode", siteCode);
        client.DefaultRequestHeaders.TryAddWithoutValidation("language", "vi");
        client.DefaultRequestHeaders.TryAddWithoutValidation("token", token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("deviceModel", "Mobile Safari v16.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("physicalDeviceModel", "unknown");
        client.DefaultRequestHeaders.TryAddWithoutValidation("operatingSystem", "iOS");
        client.DefaultRequestHeaders.TryAddWithoutValidation("deviceBrand", "iPhone");
        client.DefaultRequestHeaders.TryAddWithoutValidation("browserType", "Mobile Safari v16.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("appSystem", "iOS 16.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("domain", "www.gg88v.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation("webauthnDomain", "www.gg88v.com");
        client.DefaultRequestHeaders.TryAddWithoutValidation("clienttimezone", "+7");
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-custom-referer", RegistrationUrl);
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-version", appVersion);
        client.DefaultRequestHeaders.TryAddWithoutValidation("appVersion", $"v{appVersion}");
        client.DefaultRequestHeaders.TryAddWithoutValidation("browserfingerid", string.Empty);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "x-object-id",
            JsonSerializer.Serialize(new
            {
                uid = username ?? string.Empty,
                browserLanguage = "en-US",
                init = new
                {
                    device = string.Empty,
                    created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    version = 1784857024000L
                }
            }, JsonOptions));
    }

    private static HttpRequestMessage CreateEncryptedRequest(
        string path,
        string encryptedBody,
        long unixTime)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(encryptedBody));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        request.Headers.TryAddWithoutValidation("x-data-mode", "chipher");
        request.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("timestamp", unixTime.ToString());
        return request;
    }

    private static string? TryGetResponseHeader(
        HttpResponseMessage response,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                var value = values.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? value[7..].Trim()
                        : value.Trim();
                }
            }
        }

        return null;
    }

    internal static HttpClientHandler CreateHandler(
        ProxyConfiguration? proxy,
        CookieContainer cookies)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = cookies,
            UseCookies = true,
            UseProxy = proxy is not null
        };

        if (proxy is not null)
        {
            var webProxy = new WebProxy(new Uri(proxy.ProxyServerArgument), false);
            if (proxy.RequiresAuthentication)
            {
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            }
            handler.Proxy = webProxy;
        }

        return handler;
    }

    private static void ConfigurePageHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MobileUserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    private static async Task<string?> TryGetPageHtmlAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(RegistrationUrl, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<string?> GetGeeGuardTokenAsync(
        ProxyConfiguration? proxy,
        CancellationToken cancellationToken)
    {
        await GeeGuardConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VIBECODE_GG_NODE_TOKEN"),
                    "1",
                    StringComparison.Ordinal))
            {
                return await GetGeeGuardTokenCoreAsync(proxy, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await GetGeeGuardTokenWithChromeAsync(proxy, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            GeeGuardConcurrency.Release();
        }
    }

    private static Task<string?> GetGeeGuardTokenWithChromeAsync(
        ProxyConfiguration? proxy,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profileRoot = Path.Combine(
                Path.GetTempPath(),
                "VibecodeGgToken",
                Guid.NewGuid().ToString("N"));
            var proxyValue = proxy is null
                ? null
                : proxy.RequiresAuthentication
                    ? $"{proxy.Host}:{proxy.Port}:{proxy.Username}:{proxy.Password}"
                    : $"{proxy.Scheme}://{proxy.Host}:{proxy.Port}";

            try
            {
                using var driver = DriverFactory.CreateDriver(
                    profileRoot,
                    userAgent: null,
                    proxy: proxyValue,
                    headless: true,
                    TargetSite.GG88);
                driver.Navigate().GoToUrl(RegistrationUrl);
                var js = (IJavaScriptExecutor)driver;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (js.ExecuteScript("return typeof window.initGeeGuard === 'function';") is true)
                    {
                        var token = js.ExecuteAsyncScript(
                            """
                            const appId = arguments[0];
                            const done = arguments[arguments.length - 1];
                            try {
                                window.initGeeGuard({ appId }, result => {
                                    done(result?.data?.gee_token || null);
                                });
                            } catch {
                                done(null);
                            }
                            """,
                            DefaultGeeGuardAppId)?.ToString();
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            return token;
                        }
                    }
                    Thread.Sleep(100);
                }

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
        }, cancellationToken);

    private static async Task<string?> GetGeeGuardTokenCoreAsync(
        ProxyConfiguration? proxy,
        CancellationToken cancellationToken)
    {
        var scriptPath = ResolveGeeGuardScriptPath();
        if (scriptPath is null)
        {
            return null;
        }

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
        process.StartInfo.ArgumentList.Add(DefaultGeeGuardAppId);
        process.StartInfo.ArgumentList.Add(SiteOrigin);
        process.StartInfo.ArgumentList.Add(RegistrationUrl);

        if (proxy is not null)
        {
            var proxyUri = new UriBuilder(proxy.Scheme, proxy.Host, proxy.Port)
            {
                UserName = proxy.Username,
                Password = proxy.Password
            }.Uri.AbsoluteUri;
            process.StartInfo.Environment["HTTP_PROXY"] = proxyUri;
            process.StartInfo.Environment["HTTPS_PROXY"] = proxyUri;
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
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
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
            return document.RootElement.TryGetProperty("token", out var token)
                ? token.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolveGeeGuardScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Core", "gg_geetest_token.js"),
            Path.Combine(AppContext.BaseDirectory, "gg_geetest_token.js"),
            Path.Combine(Directory.GetCurrentDirectory(), "Core", "gg_geetest_token.js")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryKill(Process process)
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
            // Best effort process cleanup.
        }
    }

    private static string? FindValue(string input, Regex regex)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? TryFindString(JsonElement element, params string[] names)
    {
        foreach (var property in Walk(element))
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static long? TryFindInt64(JsonElement element, params string[] names)
    {
        foreach (var property in Walk(element))
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.TryGetInt64(out var number))
            {
                return number;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && long.TryParse(property.Value.GetString(), out number))
            {
                return number;
            }
        }

        return null;
    }

    private static IEnumerable<JsonProperty> Walk(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property;
                foreach (var nested in Walk(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in Walk(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static DirectRegistrationOutcome Failed(string message, string? rawResponse = null) =>
        new(DirectRegistrationStatus.Failed, message, RawResponse: rawResponse);

    [GeneratedRegex("token:\\s*\"([0-9a-f-]{36})\"", RegexOptions.IgnoreCase)]
    private static partial Regex StaticTokenRegex();

    [GeneratedRegex("siteCode:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SiteCodeRegex();

    private sealed record GgApiResponse(
        HttpStatusCode StatusCode,
        string Text,
        string? RotatedToken = null);

    private sealed record BankCodeResolution(string? Code, string Source);

    private sealed record BankWithdrawMethod(
        object MethodId,
        string? UseTypeId,
        JsonElement Source);
}

internal static class GgCrypto
{
    private static readonly byte[] Iv = Encoding.UTF8.GetBytes("5421698523412578");

    public static string DeriveStaticKey(string staticToken)
    {
        var inner = Md5(staticToken);
        return Md5(staticToken + inner).Substring(2, 16);
    }

    public static string DeriveLoginKey(string loginToken, string username) =>
        Md5(loginToken + username).Substring(0, 16);

    public static string Encrypt(string plaintext, string key)
    {
        var input = Encoding.UTF8.GetBytes(plaintext);
        var paddedLength = input.Length % 16 == 0
            ? input.Length
            : input.Length + (16 - input.Length % 16);
        var padded = new byte[paddedLength];
        input.CopyTo(padded, 0);

        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(padded, 0, padded.Length));
    }

    public static string? TryDecryptResponse(string ciphertext, string key)
    {
        if (string.IsNullOrWhiteSpace(ciphertext)
            || ciphertext.TrimStart().StartsWith('{')
            || ciphertext.TrimStart().StartsWith('['))
        {
            return null;
        }

        try
        {
            var encrypted = Convert.FromBase64String(ciphertext.Trim().Trim('"'));
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            return Encoding.UTF8.GetString(decrypted).TrimEnd('\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\b', '\t', '\n', '\u000B', '\f', '\r', '\u000E', '\u000F', '\u0010');
        }
        catch (Exception) when (ciphertext.Length > 0)
        {
            return null;
        }
    }

    private static string Md5(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
