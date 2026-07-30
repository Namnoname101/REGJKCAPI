using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BAOCODETOOL.Models;
using OpenQA.Selenium;

namespace BAOCODETOOL.Core;

internal static partial class LlwinBrowserBankWorkflow
{
    private const string RegistrationUrl = "https://llwin.app/home/register?id=924999213";
    private const string DefaultApiOrigin = "https://llwin47.com/hall/";
    private const string DefaultSiteCode = "3239";
    private const string DefaultAppVersion = "7.3.268";
    private const string DefaultStaticToken = "b2e3d672-9d88-47a7-81b4-9d7ffc62054f";

    public static async Task<DirectBankSetupOutcome> SetupAsync(
        IWebDriver driver,
        AccountModel account,
        CancellationToken cancellationToken)
    {
        if (driver is not IJavaScriptExecutor javaScript)
        {
            return Failed("LLWIN bank setup requires a JavaScript-capable Edge session.");
        }

        try
        {
            driver.SwitchTo().DefaultContent();
            var snapshot = javaScript.ExecuteScript(SessionSnapshotScript)?.ToString();
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                return Failed("LLWIN authenticated session metadata was empty.");
            }

            if (!TryCreateSession(driver, snapshot, out var session, out var error))
            {
                return Failed(error);
            }

            return await GgDirectWorkflow.SetupBankAsync(account, session!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is WebDriverException or JsonException or UriFormatException)
        {
            return Failed($"LLWIN authenticated bank session could not be read: {exception.Message}");
        }
    }

    private static bool TryCreateSession(
        IWebDriver driver,
        string snapshot,
        out DirectRegistrationSession? session,
        out string error)
    {
        session = null;
        error = string.Empty;

        var values = ReadNamedValues(snapshot);
        var sessionKey = FindValue(values, "session_key", "sessionKey");
        var jwtToken = FindValue(values, "jwt_token", "jwtToken", "newJwt");
        var userKey = FindValue(values, "userkey", "user_key", "userKey");
        var cryptoIdentity = FindValue(values, "username", "userName", "userid", "userId", "uid");
        if (string.IsNullOrWhiteSpace(sessionKey)
            || string.IsNullOrWhiteSpace(jwtToken)
            || string.IsNullOrWhiteSpace(userKey)
            || string.IsNullOrWhiteSpace(cryptoIdentity))
        {
            error = "LLWIN login completed, but its authenticated API identity was not available in the Edge session.";
            return false;
        }

        if (!IsAscii(sessionKey) || !IsAscii(jwtToken) || !IsAscii(cryptoIdentity))
        {
            error = "LLWIN returned a login session in an unsupported header format.";
            return false;
        }

        var siteOrigin = FindValue(values, "siteOrigin", "origin") ?? "https://llwin.app";
        if (!Uri.TryCreate(siteOrigin, UriKind.Absolute, out var siteUri))
        {
            siteUri = new Uri("https://llwin.app");
        }

        var apiOrigin = FindApiOrigin(values) ?? DefaultApiOrigin;
        var siteCode = FindValue(values, "siteCode", "site_code") ?? DefaultSiteCode;
        var appVersion = NormalizeAppVersion(
            FindValue(values, "appVersion", "x-version", "version"));
        var deviceId = FindValue(values, "deviceId", "device_id", "loginId", "device");
        if (string.IsNullOrWhiteSpace(deviceId)
            || !IsAscii(deviceId)
            || deviceId.Length > 128)
        {
            deviceId = Guid.NewGuid().ToString();
        }
        var staticToken = FindStaticToken(values, sessionKey) ?? DefaultStaticToken;
        var cookies = CreateCookieContainer(driver);
        long? customerId = long.TryParse(cryptoIdentity, out var parsedIdentity)
            ? parsedIdentity
            : null;

        session = new DirectRegistrationSession(
            sessionKey,
            cryptoIdentity,
            customerId,
            siteCode,
            cookies,
            new GgDirectSession(
                EnsureTrailingSlash(apiOrigin),
                siteUri.GetLeftPart(UriPartial.Authority),
                RegistrationUrl,
                staticToken,
                siteCode,
                deviceId,
                appVersion,
                siteUri.Host,
                "LLWIN",
                jwtToken));
        return true;
    }

    private static Dictionary<string, List<string>> ReadNamedValues(string snapshot)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string? Name, string Value, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((null, snapshot, 0));

        while (queue.Count > 0)
        {
            var (name, rawValue, depth) = queue.Dequeue();
            if (depth > 8 || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = rawValue.Trim();
            var seenKey = $"{name}\n{value}";
            if (!seen.Add(seenKey))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                AddValue(values, name, value);
            }

            foreach (Match match in NamedValueRegex().Matches(value))
            {
                AddValue(values, match.Groups[1].Value, match.Groups[2].Value);
            }

            TryQueueJson(value, depth, queue, values);

            var decoded = TryDecodeTransportValue(value);
            if (!string.IsNullOrWhiteSpace(decoded)
                && !decoded.Equals(value, StringComparison.Ordinal))
            {
                queue.Enqueue((name, decoded, depth + 1));
            }

            foreach (Match match in TiParameterRegex().Matches(value))
            {
                var tiValue = Uri.UnescapeDataString(match.Groups[1].Value);
                var tiJson = TryDecodeBase64(tiValue);
                if (!string.IsNullOrWhiteSpace(tiJson))
                {
                    queue.Enqueue(("_ti", tiJson, depth + 1));
                }
            }
        }

        return values;
    }

    private static void TryQueueJson(
        string value,
        int depth,
        Queue<(string? Name, string Value, int Depth)> queue,
        Dictionary<string, List<string>> values)
    {
        if (!value.StartsWith('{') && !value.StartsWith('['))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            WalkJson(document.RootElement, depth, queue, values);
        }
        catch (JsonException)
        {
        }
    }

    private static void WalkJson(
        JsonElement element,
        int depth,
        Queue<(string? Name, string Value, int Depth)> queue,
        Dictionary<string, List<string>> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        AddValue(values, property.Name, value);
                        queue.Enqueue((property.Name, value, depth + 1));
                    }
                }
                else if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    AddValue(values, property.Name, property.Value.GetRawText());
                }
                else
                {
                    WalkJson(property.Value, depth + 1, queue, values);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                WalkJson(item, depth + 1, queue, values);
            }
        }
    }

    private static void AddValue(
        Dictionary<string, List<string>> values,
        string name,
        string value)
    {
        if (!values.TryGetValue(name, out var entries))
        {
            entries = [];
            values[name] = entries;
        }

        if (!entries.Contains(value, StringComparer.Ordinal))
        {
            entries.Add(value);
        }
    }

    private static string? FindValue(
        IReadOnlyDictionary<string, List<string>> values,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var entries))
            {
                var value = entries.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? FindApiOrigin(IReadOnlyDictionary<string, List<string>> values)
    {
        foreach (var name in new[] { "resources", "href", "apiOrigin", "apiUrl", "apiDomain" })
        {
            if (!values.TryGetValue(name, out var entries))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                foreach (Match match in AbsoluteUrlRegex().Matches(entry))
                {
                    if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                        || !uri.AbsolutePath.Contains("/hall/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return uri.GetLeftPart(UriPartial.Authority) + "/hall/";
                }
            }
        }

        return null;
    }

    private static string? FindStaticToken(
        IReadOnlyDictionary<string, List<string>> values,
        string sessionKey)
    {
        foreach (var name in new[] { "staticToken", "static_token", "token" })
        {
            if (!values.TryGetValue(name, out var entries))
            {
                continue;
            }

            var token = entries.FirstOrDefault(value =>
                !value.Equals(sessionKey, StringComparison.Ordinal)
                && Guid.TryParse(value, out _));
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static string NormalizeAppVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultAppVersion;
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out _)
            ? normalized
            : DefaultAppVersion;
    }

    private static CookieContainer CreateCookieContainer(IWebDriver driver)
    {
        var container = new CookieContainer();
        foreach (var cookie in driver.Manage().Cookies.AllCookies)
        {
            if (!IsAscii(cookie.Name) || !IsAscii(cookie.Value))
            {
                continue;
            }

            try
            {
                var domain = string.IsNullOrWhiteSpace(cookie.Domain)
                    ? "llwin.app"
                    : cookie.Domain.TrimStart('.');
                container.Add(new System.Net.Cookie(
                    cookie.Name,
                    cookie.Value,
                    string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                    domain));
            }
            catch (CookieException)
            {
            }
        }

        return container;
    }

    private static string? TryDecodeTransportValue(string value)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma >= 0 && comma + 1 < value.Length)
            {
                return Uri.UnescapeDataString(value[(comma + 1)..]);
            }
        }

        if (value.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(value);
            if (!decoded.Equals(value, StringComparison.Ordinal))
            {
                return decoded;
            }
        }

        return value.Length >= 24 && value.All(character =>
            char.IsLetterOrDigit(character) || character is '+' or '/' or '=' or '-' or '_')
            ? TryDecodeBase64(value)
            : null;
    }

    private static string? TryDecodeBase64(string value)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return decoded.TrimStart().StartsWith('{') || decoded.TrimStart().StartsWith('[')
                ? decoded
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    private static bool IsAscii(string value) => value.All(character => character <= 0x7f);

    private static DirectBankSetupOutcome Failed(string message) =>
        new(DirectBankSetupStatus.Failed, message);

    private const string SessionSnapshotScript =
        """
        return (() => {
            const safeClone = value => {
                try { return JSON.parse(JSON.stringify(value || {})); }
                catch { return {}; }
            };
            return JSON.stringify({
                href: String(location.href || ''),
                origin: String(location.origin || ''),
                cookie: String(document.cookie || ''),
                localStorage: Object.fromEntries(Object.entries(window.localStorage || {})),
                sessionStorage: Object.fromEntries(Object.entries(window.sessionStorage || {})),
                manifests: Array.from(document.querySelectorAll('link[rel="manifest"]'))
                    .map(link => String(link.href || '')),
                resources: performance.getEntriesByType('resource')
                    .map(entry => String(entry.name || ''))
                    .filter(url => /\/hall\/|llwin\d+\./i.test(url)),
                lobbySiteConfig: safeClone(window.LOBBY_SITE_CONFIG),
                lobbyClientConfig: safeClone(window.LOBBY_CLIENT_CONFIG)
            });
        })();
        """;

    [GeneratedRegex("[\\\"'](session_key|sessionKey|jwt_token|jwtToken|newJwt|userkey|user_key|userKey|username|userName|userid|userId|uid|siteCode|site_code|appVersion|deviceId|loginId|staticToken|static_token|token)[\\\"']\\s*:\\s*[\\\"']([^\\\"']+)[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex NamedValueRegex();

    [GeneratedRegex("(?:[?&]|\\\"|')_ti=([^&#\\\"']+)", RegexOptions.IgnoreCase)]
    private static partial Regex TiParameterRegex();

    [GeneratedRegex("https?://[^\\s\\\"'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlRegex();
}
