using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Svg.Skia;

var site = args.Length > 0
           && Enum.TryParse<TargetSite>(args[0], ignoreCase: true, out var parsedSite)
    ? parsedSite
    : TargetSite.RR88;

if (site is not (TargetSite.RR88 or TargetSite.MM88))
{
    Console.Error.WriteLine("Usage: AppLoginEligibilityDiagnostics <RR88|MM88>");
    return 2;
}

var account = new AccountModel
{
    Username = $"d{Random.Shared.Next(1_000_000, 9_999_999)}",
    Password = $"Dt{Random.Shared.Next(100000, 999999)}x",
    PhoneNumber = $"052{Random.Shared.Next(1_000_000, 9_999_999):D7}",
    FullName = "NGUYEN VAN AN",
    Level2Password = Random.Shared.Next(100000, 999999).ToString(),
    AccountNumber = $"000{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}",
    BankName = "VIETCOMBANK",
    Branch = "HA NOI",
    Proxy = Environment.GetEnvironmentVariable("VIBECODE_PROXY") ?? string.Empty
};

var omoOptions = new OmoCaptchaOptions();
var imageSolver = new OmoImageCaptchaSolver(
    new SingleHttpClientFactory(),
    Options.Create(omoOptions),
    NullLogger<OmoImageCaptchaSolver>.Instance);

Console.WriteLine($"site={site}");
Console.WriteLine($"username={account.GetUsername(site)}");
Console.WriteLine($"password={account.GetPassword(site)}");

var registration = await DirectRegistrationWorkflow.TryRegisterAsync(
    account,
    site,
    CancellationToken.None,
    imageCaptchaSolver: imageSolver);
Console.WriteLine($"registrationStatus={registration.Status}");
Console.WriteLine($"registrationMessage={registration.Message}");
if (registration.Status != DirectRegistrationStatus.Success)
{
    Console.WriteLine($"registrationResponse={Summarize(registration.RawResponse)}");
    return 1;
}

await Task.Delay(TimeSpan.FromSeconds(2));
var checker = new PromotionChecker(site, imageSolver);
var before = await checker.CheckAsync(account.GetUsername(site), CancellationToken.None);
Console.WriteLine($"beforeCheckStatus={before.StatusCode}");
Console.WriteLine($"beforeAppSignals={string.Join(" | ", before.AppSignals)}");
Console.WriteLine($"beforeResponse={Summarize(before.RawResponse)}");

if (registration.Session is null)
{
    Console.WriteLine("bankStatus=Failed");
    Console.WriteLine("bankMessage=Registration did not return a session token.");
    return 1;
}

var bank = await DirectBankSetupWorkflow.TrySetupAsync(
    account,
    site,
    registration.Session,
    CancellationToken.None);
Console.WriteLine($"bankStatus={bank.Status}");
Console.WriteLine($"bankMessage={bank.Message}");
Console.WriteLine($"bankResponse={Summarize(bank.RawResponse)}");
if (bank.Status != DirectBankSetupStatus.Success)
{
    return 1;
}

var appLogin = await OfficialAppLoginWorkflow.TryLoginAsync(
    account,
    site,
    CancellationToken.None);
Console.WriteLine($"appLoginStatus={appLogin.Status}");
Console.WriteLine($"appLoginMessage={appLogin.Message}");
Console.WriteLine($"appLoginResponse={Summarize(appLogin.RawResponse)}");
if (appLogin.Status != OfficialAppLoginStatus.Success)
{
    return 1;
}

await Task.Delay(TimeSpan.FromSeconds(3));
var after = await checker.CheckAsync(account.GetUsername(site), CancellationToken.None);
Console.WriteLine($"afterCheckStatus={after.StatusCode}");
Console.WriteLine($"afterAppSignals={string.Join(" | ", after.AppSignals)}");
Console.WriteLine($"afterResponse={Summarize(after.RawResponse)}");

return after.StatusCode == 200 && after.AppSignals.Count == 0 ? 0 : 1;

static string Summarize(string? value)
{
    var compact = (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();
    return compact.Length <= 2400 ? compact : compact[..2400] + "...";
}

file sealed class PromotionChecker
{
    private const string Password = "RR882024RR88";
    private readonly TargetSite _site;
    private readonly IImageCaptchaSolver _solver;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _fingerprint = Guid.NewGuid().ToString("N");

    public PromotionChecker(TargetSite site, IImageCaptchaSolver solver)
    {
        _site = site;
        _solver = solver;
    }

    public async Task<PromotionCheckResult> CheckAsync(
        string username,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var captchaRequest = new HttpRequestMessage(HttpMethod.Get, BuildUrl("/captcha"));
            AddFingerprint(captchaRequest);
            using var captchaResponse = await _client.SendAsync(captchaRequest, cancellationToken);
            var captchaText = await captchaResponse.Content.ReadAsStringAsync(cancellationToken);
            captchaResponse.EnsureSuccessStatusCode();

            using var captchaDocument = JsonDocument.Parse(captchaText);
            var captchaRoot = captchaDocument.RootElement;
            var captchaData = captchaRoot.GetProperty("data");
            var captchaId = captchaData.GetProperty("captchaId").GetString() ?? string.Empty;
            var captchaSvg = captchaData.GetProperty("captcha").GetString() ?? string.Empty;
            var solution = await _solver.SolveAsync(
                Convert.ToBase64String(RenderSvgToPng(captchaSvg)),
                cancellationToken);
            if (!solution.IsSuccess)
            {
                return new PromotionCheckResult(
                    0,
                    $"OMOCaptcha failed: {solution.Message}",
                    []);
            }

            var plaintext = JsonSerializer.Serialize(new
            {
                username = username.ToLowerInvariant(),
                captcha = solution.Text.Trim(),
                captchaId
            });
            var encrypted = CryptoJsAes.Encrypt(plaintext, Password);

            using var checkRequest = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/promotion/check"))
            {
                Content = JsonContent.Create(new { data = encrypted })
            };
            AddFingerprint(checkRequest);
            using var checkResponse = await _client.SendAsync(checkRequest, cancellationToken);
            var responseText = await checkResponse.Content.ReadAsStringAsync(cancellationToken);
            var result = Parse(responseText);
            if (result.StatusCode == 200 || !LooksLikeCaptchaFailure(responseText))
            {
                return result;
            }
        }

        return new PromotionCheckResult(0, "CAPTCHA retries exhausted.", []);
    }

    private Uri BuildUrl(string path)
    {
        var origin = _site == TargetSite.MM88
            ? "https://api.mm88ttkm.com"
            : "https://api.rr88ttkm.com";
        return new Uri(origin + path);
    }

    private void AddFingerprint(HttpRequestMessage request)
    {
        if (_site == TargetSite.MM88)
        {
            request.Headers.TryAddWithoutValidation("X-Fingerprint", _fingerprint);
        }
    }

    private static PromotionCheckResult Parse(string responseText)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var statusCode = root.TryGetProperty("statusCode", out var status)
                             && status.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            var signals = new List<string>();
            CollectAppSignals(root, signals);
            return new PromotionCheckResult(statusCode, responseText, signals.Distinct().ToArray());
        }
        catch (JsonException)
        {
            return new PromotionCheckResult(0, responseText, []);
        }
    }

    private static void CollectAppSignals(JsonElement element, List<string> signals)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectAppSignals(property.Value, signals);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAppSignals(item, signals);
                }

                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (text.Contains("app", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("ứng dụng", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("đăng nhập", StringComparison.OrdinalIgnoreCase))
                {
                    signals.Add(text);
                }

                break;
        }
    }

    private static bool LooksLikeCaptchaFailure(string value) =>
        value.Contains("captcha", StringComparison.OrdinalIgnoreCase)
        && (value.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || value.Contains("incorrect", StringComparison.OrdinalIgnoreCase)
            || value.Contains("không đúng", StringComparison.OrdinalIgnoreCase));

    private static byte[] RenderSvgToPng(string svgText)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(svgText));
        var svg = new SKSvg();
        var picture = svg.Load(input)
                      ?? throw new InvalidDataException("Promotion checker returned an invalid SVG captcha.");
        var bounds = picture.CullRect;
        const float scale = 3f;
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}

file static class CryptoJsAes
{
    public static string Encrypt(string plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var keyAndIv = DeriveKeyAndIv(passwordBytes, salt, 48);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keyAndIv[..32];
        aes.IV = keyAndIv[32..48];

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(
            plaintextBytes,
            0,
            plaintextBytes.Length);

        var output = new byte[16 + ciphertext.Length];
        Encoding.ASCII.GetBytes("Salted__").CopyTo(output, 0);
        salt.CopyTo(output, 8);
        ciphertext.CopyTo(output, 16);
        return Convert.ToBase64String(output);
    }

    private static byte[] DeriveKeyAndIv(byte[] password, byte[] salt, int length)
    {
        var derived = new List<byte>(length);
        var previous = Array.Empty<byte>();
        while (derived.Count < length)
        {
            var input = new byte[previous.Length + password.Length + salt.Length];
            previous.CopyTo(input, 0);
            password.CopyTo(input, previous.Length);
            salt.CopyTo(input, previous.Length + password.Length);
            previous = MD5.HashData(input);
            derived.AddRange(previous);
        }

        return derived.Take(length).ToArray();
    }
}

file sealed record PromotionCheckResult(
    int StatusCode,
    string RawResponse,
    IReadOnlyList<string> AppSignals);

file sealed class SingleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
