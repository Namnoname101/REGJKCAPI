using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public enum DirectBankSetupStatus
{
    NotSupported,
    Success,
    Failed
}

public sealed record DirectBankSetupOutcome(
    DirectBankSetupStatus Status,
    string Message,
    string? RawResponse = null);

public static class DirectBankSetupWorkflow
{
    private const string Language = "VI";
    private const string BankModuleId = "BINDCARD3";
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsSupported(TargetSite site) =>
        GetProfile(site) is not null;

    public static async Task<DirectBankSetupOutcome> TrySetupAsync(
        AccountModel account,
        TargetSite site,
        DirectRegistrationSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(session);

        var profile = GetProfile(site);
        if (profile is null)
        {
            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.NotSupported,
                $"{site} does not have a direct HTTP bank profile.");
        }

        if (string.IsNullOrWhiteSpace(session.Token))
        {
            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                "Direct bank setup cannot run because registration did not return an auth token.");
        }

        var validationError = ValidateBankInput(account);
        if (validationError is not null)
        {
            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                validationError);
        }

        try
        {
            if (!ProxyConfigurationParser.TryParse(account.Proxy, out var proxyConfiguration, out var proxyError))
            {
                return new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    proxyError ?? "Proxy configuration is invalid.");
            }

            using var handler = CreateHttpClientHandler(proxyConfiguration);
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(profile.Origin),
                Timeout = TimeSpan.FromSeconds(20)
            };

            ConfigureDefaultHeaders(httpClient, profile, session);

            var bankList = await GetBankListAsync(httpClient, profile, cancellationToken)
                .ConfigureAwait(false);
            if (bankList.Outcome is not null)
            {
                return bankList.Outcome;
            }

            var bank = FindMatchingBank(bankList.Banks, account.BankName);
            if (bank is null)
            {
                return new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    $"Direct bank setup could not find bank '{account.BankName}'.");
            }

            var unsupportedReason = GetUnsupportedBankReason(bank, account.Branch);
            if (unsupportedReason is not null)
            {
                return new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    unsupportedReason);
            }

            var template = await GetBankTemplateAsync(
                    httpClient,
                    bank.BankCode,
                    cancellationToken)
                .ConfigureAwait(false);
            if (template.Outcome is not null)
            {
                return template.Outcome;
            }

            var templateAnalysis = AnalyzeTemplate(
                template.Template,
                account.AccountNumber,
                account.FullName);
            if (templateAnalysis.UnsupportedReason is not null)
            {
                return new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    templateAnalysis.UnsupportedReason);
            }

            var payload = BuildBindPayload(
                account,
                profile,
                bank,
                templateAnalysis);
            var bindOutcome = await BindBankCardAsync(
                    httpClient,
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);
            if (bindOutcome.Status != DirectBankSetupStatus.Success)
            {
                return bindOutcome;
            }

            var verified = await VerifyBankCardLinkedAsync(
                    httpClient,
                    account.AccountNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verified)
            {
                return bindOutcome;
            }

            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                "Direct bank setup submitted but could not verify the linked card.",
                bindOutcome.RawResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                $"Direct bank setup exception: {exception.Message}");
        }
    }

    private static string? ValidateBankInput(AccountModel account)
    {
        if (string.IsNullOrWhiteSpace(account.BankName)
            || string.IsNullOrWhiteSpace(account.AccountNumber)
            || string.IsNullOrWhiteSpace(account.Level2Password))
        {
            return "Bank setup data missing. Required: Bank, Account Number, Level 2 Password.";
        }

        if (string.IsNullOrWhiteSpace(account.FullName))
        {
            return "Bank setup data missing. Required: Full Name.";
        }

        return null;
    }

    private static void ConfigureDefaultHeaders(
        HttpClient httpClient,
        DirectBankSiteProfile profile,
        DirectRegistrationSession session)
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
        httpClient.DefaultRequestHeaders.Referrer = new Uri(profile.AddBankUrl);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Language", Language);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Merchant", profile.MerchantCode);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", session.Token);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Real-UA",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(MobileUserAgent)));
    }

    private static async Task<BankListResult> GetBankListAsync(
        HttpClient httpClient,
        DirectBankSiteProfile profile,
        CancellationToken cancellationToken)
    {
        var path = "/wps/relay/MCSFE_getBanks?merchantCode=" +
                   Uri.EscapeDataString(profile.MerchantCode);
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new BankListResult(
                [],
                new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    $"Direct bank list returned {(int)response.StatusCode}.",
                    responseText));
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (!IsSuccess(root)
                || !root.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return new BankListResult(
                    [],
                    new DirectBankSetupOutcome(
                        DirectBankSetupStatus.Failed,
                        ReadMessage(root, "Direct bank list was rejected."),
                        responseText));
            }

            var banks = value.EnumerateArray()
                .Select(BankInfo.FromJson)
                .Where(bank => !string.IsNullOrWhiteSpace(bank.BankCode))
                .ToArray();
            return new BankListResult(banks, null);
        }
        catch (JsonException)
        {
            return new BankListResult(
                [],
                new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    "Direct bank list returned non-JSON response.",
                    responseText));
        }
    }

    private static async Task<BankTemplateResult> GetBankTemplateAsync(
        HttpClient httpClient,
        string bankCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bankCode))
        {
            return new BankTemplateResult(null, null);
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            "/wps/relay/MCSFE_getBankTemplate?bankId=" + Uri.EscapeDataString(bankCode));
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new BankTemplateResult(
                null,
                new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    $"Direct bank template returned {(int)response.StatusCode}.",
                    responseText));
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (!IsSuccess(root))
            {
                return new BankTemplateResult(
                    null,
                    new DirectBankSetupOutcome(
                        DirectBankSetupStatus.Failed,
                        ReadMessage(root, "Direct bank template was rejected."),
                        responseText));
            }

            return root.TryGetProperty("value", out var value)
                ? new BankTemplateResult(value.Clone(), null)
                : new BankTemplateResult(null, null);
        }
        catch (JsonException)
        {
            return new BankTemplateResult(
                null,
                new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Failed,
                    "Direct bank template returned non-JSON response.",
                    responseText));
        }
    }

    private static async Task<DirectBankSetupOutcome> BindBankCardAsync(
        HttpClient httpClient,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var rsaKey = await GetRsaKeyAsync(httpClient, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rsaKey))
        {
            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                "Direct bank setup could not fetch RSA key.");
        }

        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var encrypted = DirectRegistrationCrypto.Encrypt(payloadJson, rsaKey);

        using var request = CreateRequest(HttpMethod.Put, "/wps/v2/member/bankCards");
        request.Headers.TryAddWithoutValidation("Encryption", encrypted.Rsa);
        request.Headers.TryAddWithoutValidation("X-Digest", encrypted.Des);
        request.Headers.TryAddWithoutValidation("X-RSA", rsaKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["value"] = encrypted.Des
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                $"Direct bank bind returned {(int)response.StatusCode}.",
                responseText);
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (IsSuccess(root))
            {
                return new DirectBankSetupOutcome(
                    DirectBankSetupStatus.Success,
                    "Direct bank setup completed.",
                    responseText);
            }

            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                ReadMessage(root, "Direct bank bind was rejected."),
                responseText);
        }
        catch (JsonException)
        {
            return new DirectBankSetupOutcome(
                DirectBankSetupStatus.Failed,
                "Direct bank bind returned non-JSON response.",
                responseText);
        }
    }

    private static async Task<bool> VerifyBankCardLinkedAsync(
        HttpClient httpClient,
        string bankAccount,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/wps/relay/MCSFE_getWithdrawAccountInfo");
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (responseText.Contains(bankAccount, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            return ContainsCardSuffix(document.RootElement, bankAccount);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsCardSuffix(JsonElement element, string bankAccount)
    {
        var expectedSuffix = bankAccount.Length >= 4
            ? bankAccount[^4..]
            : bankAccount;

        return ContainsCardSuffix(element, expectedSuffix, string.Empty);
    }

    private static bool ContainsCardSuffix(
        JsonElement element,
        string expectedSuffix,
        string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsCardSuffix(property.Value, expectedSuffix, property.Name))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                return element.EnumerateArray()
                    .Any(item => ContainsCardSuffix(item, expectedSuffix, propertyName));

            case JsonValueKind.String:
                if (!LooksLikeCardProperty(propertyName))
                {
                    return false;
                }

                var digits = DigitsOnly(element.GetString());
                return digits.Length >= expectedSuffix.Length
                       && digits.EndsWith(expectedSuffix, StringComparison.Ordinal);

            default:
                return false;
        }
    }

    private static bool LooksLikeCardProperty(string propertyName)
    {
        var compact = Compact(propertyName);
        return compact.Contains("cardnumber", StringComparison.Ordinal)
               || compact.Contains("accountnumber", StringComparison.Ordinal)
               || compact is "card" or "bankcard";
    }

    private static async Task<string> GetRsaKeyAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var request = CreateRequest(HttpMethod.Get, $"/wps/session/key/rsa?_={timestamp}");
        using var response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        return (await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false))
            .Trim();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("ModuleId", BankModuleId);
        request.Headers.TryAddWithoutValidation(
            "X-Timestamp",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return request;
    }

    private static Dictionary<string, object?> BuildBindPayload(
        AccountModel account,
        DirectBankSiteProfile profile,
        BankInfo bank,
        BankTemplateAnalysis templateAnalysis)
    {
        var phoneNumber = profile.Site == TargetSite.XX88
            ? PhoneNumberFormatter.FormatTenDigitWithLeadingZero(account.PhoneNumber)
            : PhoneNumberFormatter.FormatNineDigitWithoutLeadingZero(account.PhoneNumber);
        var payeeName = account.FullName.Trim().ToUpperInvariant();

        var payload = new Dictionary<string, object?>
        {
            ["area"] = account.Branch.Trim(),
            ["paymentPassword"] = account.Level2Password.Trim(),
            ["validatePaymentPassword"] = account.Level2Password.Trim(),
            ["verificationCode"] = string.Empty,
            ["firebaseIdToken"] = string.Empty,
            ["mobileNum"] = phoneNumber,
            ["cardType"] = "B",
            ["countryDialingCode"] = "84",
            ["playerId"] = templateAnalysis.PlayerId,
            ["customField"] = string.Empty,
            ["email"] = string.Empty,
            ["mobileNo"] = string.Empty,
            ["bankName"] = bank.DisplayName,
            ["bankCode"] = bank.BankCode,
            ["payeeName"] = payeeName,
            ["cardNumber"] = account.AccountNumber.Trim()
        };

        if (templateAnalysis.IncludeFullName)
        {
            payload["fullName"] = payeeName;
        }

        return payload;
    }

    private static BankTemplateAnalysis AnalyzeTemplate(
        JsonElement? template,
        string accountNumber,
        string fullName)
    {
        if (template is null
            || template.Value.ValueKind != JsonValueKind.Object
            || !template.Value.TryGetProperty("bankCardTemplateSettings", out var settings)
            || settings.ValueKind != JsonValueKind.Array)
        {
            return new BankTemplateAnalysis(string.Empty, false, null);
        }

        var playerId = string.Empty;
        var includeFullName = false;
        foreach (var setting in settings.EnumerateArray())
        {
            var cardItem = TryGetString(setting, "cardItem") ?? string.Empty;
            switch (cardItem)
            {
                case "":
                case "BANK_BRANCH":
                    break;

                case "BANK_ACCOUNT_NUMBER":
                    playerId = accountNumber.Trim();
                    break;

                case "LEGAL_NAME":
                    if (IsRequired(TryGetValue(setting, "isLegalNameSeparate")))
                    {
                        return new BankTemplateAnalysis(
                            string.Empty,
                            false,
                            "Direct bank setup does not support separate legal-name templates.");
                    }

                    includeFullName = !string.IsNullOrWhiteSpace(fullName);
                    break;

                default:
                    return new BankTemplateAnalysis(
                        string.Empty,
                        false,
                        $"Direct bank setup does not support bank template field '{cardItem}'.");
            }
        }

        return new BankTemplateAnalysis(playerId, includeFullName, null);
    }

    private static string? GetUnsupportedBankReason(BankInfo bank, string branch)
    {
        if (IsRequired(bank.IsProvinceRequired))
        {
            return $"Direct bank setup does not support province-required bank '{bank.DisplayName}'.";
        }

        if (IsRequired(bank.IsCityRequired))
        {
            return $"Direct bank setup does not support city-required bank '{bank.DisplayName}'.";
        }

        if (IsRequired(bank.IsPlayerIdRequired)
            || IsRequired(bank.IsPlayerEmailRequired)
            || IsRequired(bank.IsPlayerPhoneRequired)
            || IsRequired(bank.IsCustomizeRequired))
        {
            return $"Direct bank setup does not support extra-field bank '{bank.DisplayName}'.";
        }

        if (IsRequired(bank.IsBranchRequired) && string.IsNullOrWhiteSpace(branch))
        {
            return $"Direct bank setup requires branch for bank '{bank.DisplayName}'.";
        }

        return null;
    }

    private static BankInfo? FindMatchingBank(
        IReadOnlyList<BankInfo> banks,
        string targetBankName)
    {
        var target = Compact(targetBankName);
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        return banks.FirstOrDefault(bank =>
        {
            var candidate = Compact(
                $"{bank.BankLocaleName} {bank.BankEngName} {bank.BankChName} {bank.BankCode}");
            return candidate.Contains(target, StringComparison.Ordinal)
                   || target.Contains(candidate, StringComparison.Ordinal);
        });
    }

    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("success", out var successElement)
        && successElement.ValueKind == JsonValueKind.True;

    private static string ReadMessage(JsonElement root, string fallback) =>
        TryGetString(root, "message")
        ?? TryGetString(root, "errorCode")
        ?? fallback;

    private static string? TryGetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static string? TryGetValue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ToString();
    }

    private static bool IsRequired(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "y", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string Compact(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string DigitsOnly(string? value) =>
        Regex.Replace(value ?? string.Empty, "\\D", string.Empty, RegexOptions.CultureInvariant);

    private static HttpClientHandler CreateHttpClientHandler(ProxyConfiguration? proxyConfiguration)
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

    private static DirectBankSiteProfile? GetProfile(TargetSite site) =>
        site switch
        {
            TargetSite.RR88 => new DirectBankSiteProfile(
                site,
                "https://gioithieu.rr3311.com",
                "https://gioithieu.rr3311.com/m/securityCenter/addBankCard?type=B",
                "rr88f4"),
            TargetSite.MM88 => new DirectBankSiteProfile(
                site,
                "https://gioithieu.mm88p.com",
                "https://gioithieu.mm88p.com/m/securityCenter/addBankCard?type=B",
                "mm88vndkf3"),
            TargetSite.XX88 => new DirectBankSiteProfile(
                site,
                "https://gioithieu.xx88.fun",
                "https://gioithieu.xx88.fun/m/securityCenter/addBankCard?type=B",
                "xx88vndkf1"),
            _ => null
        };

    private sealed record DirectBankSiteProfile(
        TargetSite Site,
        string Origin,
        string AddBankUrl,
        string MerchantCode);

    private sealed record BankListResult(
        IReadOnlyList<BankInfo> Banks,
        DirectBankSetupOutcome? Outcome);

    private sealed record BankTemplateResult(
        JsonElement? Template,
        DirectBankSetupOutcome? Outcome);

    private sealed record BankTemplateAnalysis(
        string PlayerId,
        bool IncludeFullName,
        string? UnsupportedReason);

    private sealed record BankInfo(
        string BankCode,
        string BankEngName,
        string BankChName,
        string BankLocaleName,
        string? IsProvinceRequired,
        string? IsCityRequired,
        string? IsBranchRequired,
        string? IsPlayerIdRequired,
        string? IsPlayerEmailRequired,
        string? IsPlayerPhoneRequired,
        string? IsCustomizeRequired)
    {
        public string DisplayName =>
            !string.IsNullOrWhiteSpace(BankLocaleName)
                ? BankLocaleName
                : !string.IsNullOrWhiteSpace(BankEngName)
                    ? BankEngName
                    : !string.IsNullOrWhiteSpace(BankChName)
                        ? BankChName
                        : BankCode;

        public static BankInfo FromJson(JsonElement element) =>
            new(
                TryGetString(element, "bankCode") ?? string.Empty,
                TryGetString(element, "bankEngName") ?? string.Empty,
                TryGetString(element, "bankChName") ?? string.Empty,
                TryGetString(element, "bankLocaleName") ?? string.Empty,
                TryGetValue(element, "isProvinceRequired"),
                TryGetValue(element, "isCityRequired"),
                TryGetValue(element, "isBranchRequired"),
                TryGetValue(element, "isPlayerIdRequired"),
                TryGetValue(element, "isPlayerEmailRequired"),
                TryGetValue(element, "isPlayerPhoneRequired"),
                TryGetValue(element, "isCustomizeRequired"));
    }
}
