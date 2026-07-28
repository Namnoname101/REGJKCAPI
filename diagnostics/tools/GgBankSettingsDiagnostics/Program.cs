using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string apiOrigin = "https://www.gg8839.com/hall/";
const string staticToken = "b2e3d672-9d88-47a7-81b4-9d7ffc62054f";
const string siteCode = "2365";
const string appVersion = "7.3.254";
const string registrationUrl = "https://www.gg88v.com/home/register?id=456510325";
const string mobileUserAgent =
    "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) "
    + "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 "
    + "Mobile/15E148 Safari/604.1";

using var client = new HttpClient
{
    BaseAddress = new Uri(apiOrigin),
    Timeout = TimeSpan.FromSeconds(20)
};
client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
client.DefaultRequestHeaders.UserAgent.ParseAdd(mobileUserAgent);
client.DefaultRequestHeaders.Referrer = new Uri(registrationUrl);
client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.gg88v.com");
client.DefaultRequestHeaders.TryAddWithoutValidation("platformType", "5");
client.DefaultRequestHeaders.TryAddWithoutValidation("x-device", "3-1");
client.DefaultRequestHeaders.TryAddWithoutValidation("accept-language", "vi");
client.DefaultRequestHeaders.TryAddWithoutValidation("device", Guid.NewGuid().ToString());
client.DefaultRequestHeaders.TryAddWithoutValidation("currency", "VND");
client.DefaultRequestHeaders.TryAddWithoutValidation("siteCode", siteCode);
client.DefaultRequestHeaders.TryAddWithoutValidation("language", "vi");
client.DefaultRequestHeaders.TryAddWithoutValidation("token", staticToken);
client.DefaultRequestHeaders.TryAddWithoutValidation("domain", "www.gg88v.com");
client.DefaultRequestHeaders.TryAddWithoutValidation("x-version", appVersion);
client.DefaultRequestHeaders.TryAddWithoutValidation("appVersion", $"v{appVersion}");

var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var path = "api/finance/certify/withdrawSettingV3"
           + $"?siteCode={Uri.EscapeDataString(siteCode)}"
           + $"&token={Uri.EscapeDataString(staticToken)}"
           + "&currency=VND&language=vi";
using var request = new HttpRequestMessage(HttpMethod.Get, path)
{
    Content = new ByteArrayContent([])
};
request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
request.Headers.TryAddWithoutValidation("x-data-mode", "chipher");
request.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
request.Headers.TryAddWithoutValidation("timestamp", timestamp.ToString());

using var response = await client.SendAsync(request);
var responseText = await response.Content.ReadAsStringAsync();
Console.WriteLine($"HttpStatus={(int)response.StatusCode}");
var decoded = DecodeResponse(responseText, DeriveStaticKey(staticToken));
using var document = JsonDocument.Parse(decoded);
var bankArrays = EnumerateBankArrays(document.RootElement, "$", []).ToArray();
Console.WriteLine($"BankArrayCount={bankArrays.Length}");
foreach (var bankArray in bankArrays)
{
    Console.WriteLine(
        $"BankArrayPath={bankArray.Path}; Count={bankArray.Codes.Length}; "
        + $"Codes={string.Join(',', bankArray.Codes)}");
}
var rows = EnumerateObjects(document.RootElement)
    .Select(CreateCandidate)
    .Where(candidate => candidate is not null)
    .Cast<BankCandidate>()
    .GroupBy(
        candidate => $"{candidate.Code}|{string.Join('|', candidate.Names)}",
        StringComparer.OrdinalIgnoreCase)
    .Select(group => group.First())
    .ToArray();

Console.WriteLine($"CandidateCount={rows.Length}");
foreach (var row in rows)
{
    Console.WriteLine(
        $"Code={row.Code ?? "-"}; Names={string.Join(" | ", row.Names)}; "
        + $"Keys={string.Join(',', row.Keys)}");
}

static BankCandidate? CreateCandidate(JsonElement item)
{
    var keys = item.EnumerateObject().Select(property => property.Name).ToArray();
    var code = GetString(item, "bankCode")
               ?? GetString(item, "code")
               ?? GetString(item, "value")
               ?? GetString(item, "en")
               ?? GetString(item, "id");
    var names = new[]
        {
            GetString(item, "Name"),
            GetString(item, "name"),
            GetString(item, "bankName"),
            GetString(item, "bankEngName"),
            GetString(item, "bankChName"),
            GetString(item, "bankLocaleName"),
            GetString(item, "label"),
            GetString(item, "vi"),
            GetString(item, "en")
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var looksLikeBank = keys.Contains("Img", StringComparer.OrdinalIgnoreCase)
                        && keys.Contains("Name", StringComparer.OrdinalIgnoreCase)
                        && keys.Contains("en", StringComparer.OrdinalIgnoreCase)
                        || keys.Any(key => key.Contains("bank", StringComparison.OrdinalIgnoreCase))
                        || names.Any(name => name.Contains("bank", StringComparison.OrdinalIgnoreCase))
                        || names.Any(name => name.Contains("ngan hang", StringComparison.OrdinalIgnoreCase))
                        || names.Any(name => name.Contains("ngân hàng", StringComparison.OrdinalIgnoreCase));
    return looksLikeBank && names.Length > 0
        ? new BankCandidate(code, names, keys)
        : null;
}

static string? GetString(JsonElement item, string name)
{
    foreach (var property in item.EnumerateObject())
    {
        if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        return property.Value.ValueKind switch
        {
            JsonValueKind.String => property.Value.GetString(),
            JsonValueKind.Number => property.Value.GetRawText(),
            _ => null
        };
    }
    return null;
}

static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
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

static IEnumerable<BankArray> EnumerateBankArrays(
    JsonElement element,
    string path,
    HashSet<string> visitedPaths)
{
    if (!visitedPaths.Add(path))
    {
        yield break;
    }

    if (element.ValueKind == JsonValueKind.Array)
    {
        var items = element.EnumerateArray().ToArray();
        var codes = items
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => GetString(item, "en"))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .ToArray();
        if (codes.Length > 0)
        {
            yield return new BankArray(path, codes);
        }

        for (var index = 0; index < items.Length; index++)
        {
            foreach (var nested in EnumerateBankArrays(
                         items[index],
                         $"{path}[{index}]",
                         visitedPaths))
            {
                yield return nested;
            }
        }
        yield break;
    }

    if (element.ValueKind != JsonValueKind.Object)
    {
        yield break;
    }

    foreach (var property in element.EnumerateObject())
    {
        foreach (var nested in EnumerateBankArrays(
                     property.Value,
                     $"{path}.{property.Name}",
                     visitedPaths))
        {
            yield return nested;
        }
    }
}

static string DecodeResponse(string responseText, string key)
{
    if (responseText.TrimStart().StartsWith('{'))
    {
        using var document = JsonDocument.Parse(responseText);
        if (document.RootElement.TryGetProperty("encryptString", out var encrypted))
        {
            return Decrypt(encrypted.GetString() ?? string.Empty, key);
        }
        return responseText;
    }
    return Decrypt(responseText, key);
}

static string Decrypt(string ciphertext, string key)
{
    var encrypted = Convert.FromBase64String(ciphertext.Trim().Trim('"'));
    using var aes = Aes.Create();
    aes.Key = Encoding.UTF8.GetBytes(key);
    aes.IV = Encoding.UTF8.GetBytes("5421698523412578");
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.None;
    using var decryptor = aes.CreateDecryptor();
    return Encoding.UTF8.GetString(
            decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length))
        .TrimEnd('\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007',
            '\b', '\t', '\n', '\u000B', '\f', '\r', '\u000E', '\u000F', '\u0010');
}

static string DeriveStaticKey(string token)
{
    var inner = Md5(token);
    return Md5(token + inner).Substring(2, 16);
}

static string Md5(string value) =>
    Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

internal sealed record BankCandidate(string? Code, string[] Names, string[] Keys);

internal sealed record BankArray(string Path, string[] Codes);
