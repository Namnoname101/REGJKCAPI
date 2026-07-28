using System.Net;
using System.Reflection;
using System.Text;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

var workflowType = typeof(DirectBankSetupWorkflow);
var ggWorkflowType = typeof(DirectRegistrationWorkflow).Assembly.GetType(
                         "BAOCODETOOL.Core.GgDirectWorkflow")
                     ?? throw new InvalidOperationException("GgDirectWorkflow was not found.");
var nonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

Run("Bank payload uses server member info", VerifyPayloadUsesMemberInfo);
RunAsync("Bank bind sends raw DES ciphertext", VerifyRawCiphertextProtocolAsync);
RunAsync("Bank verification checks both card APIs", VerifyBankVerificationAsync);
RunAsync("RR88/MM88 recover from first bind 400", VerifyFullRecoveryFlowAsync);
Run("HTTP 400 details remain diagnosable", VerifyFailureDescription);
Run("Registration session carries cookies", VerifySessionCookies);
Run("GG88 bank payload follows dynamic rules", VerifyGgBankPayload);
Run("GG88 bank payload supports keyed useType rules", VerifyGgKeyedRulesPayload);
Run("GG88 encrypted request matches browser content type", VerifyGgRequestTransport);
Run("GG88 bank list recognizes masked account", VerifyGgMaskedAccount);
Run("GG88 Vietnamese bank aliases match live codes", VerifyGgVietnameseBankCodes);
Run("GG88 live bank cache extracts short aliases", VerifyGgLiveBankAliasCache);

Console.WriteLine("DIRECT BANK PROTOCOL DIAGNOSTICS: PASS");
return;

void VerifyPayloadUsesMemberInfo()
{
    var account = new AccountModel
    {
        FullName = "Tran Thi B",
        PhoneNumber = "0987654321",
        BankName = "Vietcombank",
        AccountNumber = "1234567890",
        Branch = "HA NOI",
        Level2Password = "112233"
    };

    var profile = CreateNested(
        "DirectBankSiteProfile",
        TargetSite.RR88,
        "https://example.test",
        "https://example.test/add-bank",
        "rr88f4");
    var bank = CreateNested(
        "BankInfo",
        "VCB",
        "Vietcombank",
        "VCB",
        "Ngân hàng Vietcombank",
        null,
        null,
        null,
        null,
        null,
        null,
        null);
    var template = CreateNested("BankTemplateAnalysis", "1234567890", true, null);
    var member = CreateNested("MemberInfoSnapshot", "912345678", "84", "SERVER PAYEE");

    var buildPayload = workflowType.GetMethod("BuildBindPayload", nonPublicStatic)
                       ?? throw new InvalidOperationException("BuildBindPayload was not found.");
    var payload = (Dictionary<string, object?>?)buildPayload.Invoke(
        null,
        [account, profile, bank, template, member])
        ?? throw new InvalidOperationException("BuildBindPayload returned null.");

    Equal("912345678", payload["mobileNum"], "mobileNum");
    Equal("84", payload["countryDialingCode"], "countryDialingCode");
    Equal("SERVER PAYEE", payload["payeeName"], "payeeName");
    Equal("SERVER PAYEE", payload["fullName"], "fullName");
    Equal("Ngân hàng Vietcombank", payload["bankName"], "bankName");
    Equal("VCB", payload["bankCode"], "bankCode");
    Equal("1234567890", payload["cardNumber"], "cardNumber");
    Equal("112233", payload["paymentPassword"], "paymentPassword");
    Equal("112233", payload["validatePaymentPassword"], "validatePaymentPassword");
}

async Task VerifyRawCiphertextProtocolAsync()
{
    var handler = new CaptureHandler();
    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://example.test")
    };
    var payload = new Dictionary<string, object?>
    {
        ["bankCode"] = "VCB",
        ["cardNumber"] = "1234567890",
        ["paymentPassword"] = "112233"
    };

    var bind = workflowType.GetMethod("BindBankCardAsync", nonPublicStatic)
               ?? throw new InvalidOperationException("BindBankCardAsync was not found.");
    var task = (Task<DirectBankSetupOutcome>?)bind.Invoke(
        null,
        [client, payload, CancellationToken.None])
        ?? throw new InvalidOperationException("BindBankCardAsync returned null.");
    var outcome = await task.ConfigureAwait(false);

    Equal(DirectBankSetupStatus.Success, outcome.Status, "bind status");
    True(handler.RsaPath?.Contains("/wps/session/key/rsa?t=", StringComparison.Ordinal) == true,
        "RSA key route must use the member-center ?t= protocol.");
    True(!string.IsNullOrWhiteSpace(handler.BindBody), "Bind request body was empty.");
    True(!handler.BindBody!.TrimStart().StartsWith('{'),
        "Bind ciphertext must not be wrapped in a JSON object.");
    _ = Convert.FromBase64String(handler.BindBody);
    True(handler.HasEncryptionHeader, "Encryption header was missing.");
    True(!handler.HasDigestHeader, "X-Digest must not be sent to the bind-card endpoint.");
    True(!handler.HasRsaHeader, "X-RSA must not be sent to the bind-card endpoint.");
}

async Task VerifyBankVerificationAsync()
{
    var handler = new VerificationHandler();
    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://example.test")
    };
    var verify = workflowType.GetMethod("VerifyBankCardLinkedAsync", nonPublicStatic)
                 ?? throw new InvalidOperationException("VerifyBankCardLinkedAsync was not found.");
    var task = (Task<bool>?)verify.Invoke(
        null,
        [client, "1234567890", CancellationToken.None])
        ?? throw new InvalidOperationException("VerifyBankCardLinkedAsync returned null.");

    True(await task.ConfigureAwait(false), "Masked bank card was not recognized.");
    True(handler.WithdrawInfoCalls > 0, "Withdraw-account API was not checked.");
    True(handler.BankCardCalls > 0, "Bank-card list fallback was not checked.");
}

async Task VerifyFullRecoveryFlowAsync()
{
    foreach (var site in new[] { TargetSite.RR88, TargetSite.MM88 })
    {
        var account = new AccountModel
        {
            FullName = "Tran Thi B",
            PhoneNumber = "0987654321",
            BankName = "Techcombank",
            AccountNumber = "1234567890",
            Branch = "HA NOI",
            Level2Password = "112233"
        };
        var merchant = site == TargetSite.RR88 ? "rr88f4" : "mm88vndkf3";
        var profile = CreateNested(
            "DirectBankSiteProfile",
            site,
            "https://example.test",
            "https://example.test/add-bank",
            merchant);
        var handler = new FullFlowHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test")
        };
        var core = workflowType.GetMethod("TrySetupCoreAsync", nonPublicStatic)
                   ?? throw new InvalidOperationException("TrySetupCoreAsync was not found.");
        var task = (Task<DirectBankSetupOutcome>?)core.Invoke(
            null,
            [account, profile, client, CancellationToken.None])
            ?? throw new InvalidOperationException("TrySetupCoreAsync returned null.");
        var outcome = await task.ConfigureAwait(false);

        Equal(DirectBankSetupStatus.Success, outcome.Status, $"{site} full-flow status");
        Equal(2, handler.BindCalls, $"{site} bind attempts");
        True(handler.BindBodies.All(body => !body.TrimStart().StartsWith('{')),
            $"{site} wrapped a bind ciphertext in JSON.");
        True(handler.BindBodies.Distinct(StringComparer.Ordinal).Count() == 2,
            $"{site} retry did not use fresh ciphertext.");
        True(handler.BindEncryptionHeaders.Distinct(StringComparer.Ordinal).Count() == 2,
            $"{site} retry did not use a fresh RSA encryption header.");
    }
}

void VerifyFailureDescription()
{
    var describe = workflowType.GetMethod("DescribeHttpFailure", nonPublicStatic)
                   ?? throw new InvalidOperationException("DescribeHttpFailure was not found.");
    var message = (string?)describe.Invoke(
        null,
        ["Direct bank bind", HttpStatusCode.BadRequest, """{"errorCode":1234}"""]);

    True(message?.Contains("400", StringComparison.Ordinal) == true,
        "HTTP status was not retained.");
    True(message?.Contains("1234", StringComparison.Ordinal) == true,
        "Provider error code was not retained.");
}

void VerifySessionCookies()
{
    var cookies = new CookieContainer();
    var original = new DirectRegistrationOutcome(
        DirectRegistrationStatus.Success,
        "ok",
        Session: new DirectRegistrationSession("token", "user", 1, "merchant"));
    var attach = typeof(DirectRegistrationWorkflow).GetMethod(
                     "AttachCookies",
                     BindingFlags.NonPublic | BindingFlags.Static)
                 ?? throw new InvalidOperationException("AttachCookies was not found.");
    var attached = (DirectRegistrationOutcome?)attach.Invoke(null, [original, cookies])
                   ?? throw new InvalidOperationException("AttachCookies returned null.");
    True(ReferenceEquals(cookies, attached.Session?.Cookies),
        "Registration response cookies were not attached to its session.");

    var createHandler = workflowType.GetMethod("CreateHttpClientHandler", nonPublicStatic)
                        ?? throw new InvalidOperationException("CreateHttpClientHandler was not found.");
    using var handler = (HttpClientHandler?)createHandler.Invoke(null, [null, cookies])
                        ?? throw new InvalidOperationException("CreateHttpClientHandler returned null.");
    True(ReferenceEquals(cookies, handler.CookieContainer),
        "Bank HTTP handler did not reuse registration cookies.");
}

void VerifyGgVietnameseBankCodes()
{
    var resolve = ggWorkflowType.GetMethod(
                      "FindKnownVietnameseBankCode",
                      nonPublicStatic)
                  ?? throw new InvalidOperationException(
                      "FindKnownVietnameseBankCode was not found.");

    Equal("VIETCOMB", resolve.Invoke(null, ["Vietcom Bank"]), "Vietcombank code");
    Equal("MBBANK", resolve.Invoke(null, ["MB BANK"]), "MB Bank code");
    Equal("ACB", resolve.Invoke(null, ["ACB"]), "ACB code");
    Equal("TECHCOMB", resolve.Invoke(null, ["TECHCOMBANK"]), "Techcombank code");
    Equal("MARIBANK", resolve.Invoke(null, ["MSB"]), "MSB code");
    Equal("EXIM", resolve.Invoke(null, ["EIB"]), "Eximbank code");
    Equal("SBANK", resolve.Invoke(null, ["STB"]), "Sacombank code");
    Equal("LVPBANK", resolve.Invoke(null, ["LPB"]), "LPBank code");
}

void VerifyGgLiveBankAliasCache()
{
    var cache = ggWorkflowType.GetMethod("CacheBankCodes", nonPublicStatic)
                ?? throw new InvalidOperationException("CacheBankCodes was not found.");
    var lookup = ggWorkflowType.GetMethod("TryGetCachedBankCode", nonPublicStatic)
                 ?? throw new InvalidOperationException("TryGetCachedBankCode was not found.");
    using var document = System.Text.Json.JsonDocument.Parse(
        """
        {
          "data": {
            "bankInfo": [
              { "Img": "msb.png", "Name": "MARITIMEBANK(MSB)", "en": "MARIBANK" },
              { "Img": "eib.png", "Name": "EXIMBANK(EIB)", "en": "EXIM" },
              { "Img": "stb.png", "Name": "SACOMBANK(STB)", "en": "SBANK" },
              { "Img": "lpb.png", "Name": "LIENVIETPOSTBANK(LPB)", "en": "LVPBANK" },
              { "Img": "dab.png", "Name": "DONGABANK (DAB/EAB)", "en": "DAB" },
              { "Img": "vikki.png", "Name": "Vikki Bank", "en": "DONGABANK" }
            ]
          }
        }
        """);
    Equal(6, cache.Invoke(null, [document.RootElement]), "cached live bank count");

    AssertCached("msb", "MARIBANK");
    AssertCached("maritimebank", "MARIBANK");
    AssertCached("eib", "EXIM");
    AssertCached("stb", "SBANK");
    AssertCached("lpb", "LVPBANK");
    AssertCached("eab", "DAB");
    AssertCached("dongabank", "DONGABANK");

    void AssertCached(string alias, string expected)
    {
        object?[] arguments = [alias, null];
        True(lookup.Invoke(null, arguments) is true, $"GG alias '{alias}' was not cached.");
        Equal(expected, arguments[1], $"GG alias '{alias}'");
    }
}

void VerifyGgBankPayload()
{
    const string rules =
        """
        {
          "code": 0,
          "data": {
            "withdrawMethods": [
              { "withdrawMethodId": 9, "currencyCode": "USDT", "name": "Crypto" },
              { "withdrawMethodId": 12, "currencyCode": "VND", "name": "Bank transfer" }
            ],
            "bankInfo": [
              { "Name": "Vietcombank", "en": "VCB" },
              { "Name": "Techcombank", "en": "TCB" }
            ],
            "templates": [
              { "dbField": "realName" },
              { "dbField": "account" },
              { "dbField": "bankCode" }
            ]
          }
        }
        """;
    var account = new AccountModel
    {
        FullName = "Nguyen Van An",
        BankName = "VIETCOMBANK",
        AccountNumber = "1234567890",
        Level2Password = "112233"
    };
    var build = ggWorkflowType.GetMethod("TryBuildBankPayload", nonPublicStatic)
                ?? throw new InvalidOperationException("TryBuildBankPayload was not found.");
    object?[] arguments = [rules, account, account.Level2Password, null, null];
    True((bool)(build.Invoke(null, arguments) ?? false), arguments[4]?.ToString() ?? "GG payload failed.");

    var payload = (Dictionary<string, object?>?)arguments[3]
                  ?? throw new InvalidOperationException("GG payload was null.");
    Equal(12L, payload["withdrawMethodId"], "GG withdrawMethodId");
    Equal("112233", payload["withdrawPassword"], "GG withdrawPassword");

    var form = (Dictionary<string, string>[]?)payload["formBody"]
               ?? throw new InvalidOperationException("GG formBody was null.");
    var values = form.ToDictionary(item => item["fieldKey"], item => item["fieldVal"]);
    Equal("NGUYEN VAN AN", values["realName"], "GG realName");
    Equal("1234567890", values["account"], "GG account");
    Equal("VCB", values["bankCode"], "GG bankCode");
}

void VerifyGgKeyedRulesPayload()
{
    const string rules =
        """
        {
          "code": 1,
          "data": {
            "17293822569103000001": {
              "useTypeId": "17293822569103000001",
              "currencyCode": "USDT",
              "fields": [
                { "dbField": "walletAddress" }
              ]
            },
            "17293822569103005563": {
              "useTypeId": "17293822569103005563",
              "currencyCode": "VND",
              "fields": [
                { "dbField": "realName", "fieldName": "Account name" },
                { "dbField": "account", "fieldName": "Account number" },
                { "dbField": "bankCode", "fieldId": "30009", "fieldName": "Bank" }
              ]
            }
          }
        }
        """;
    var account = new AccountModel
    {
        FullName = "Nguyen Van An",
        BankName = "VIETCOMBANK",
        AccountNumber = "1234567890",
        Level2Password = "112233"
    };
    var build = ggWorkflowType.GetMethod("TryBuildBankPayload", nonPublicStatic)
                ?? throw new InvalidOperationException("TryBuildBankPayload was not found.");
    object?[] arguments = [rules, account, account.Level2Password, null, null];
    True((bool)(build.Invoke(null, arguments) ?? false), arguments[4]?.ToString() ?? "GG keyed payload failed.");

    var payload = (Dictionary<string, object?>?)arguments[3]
                  ?? throw new InvalidOperationException("GG keyed payload was null.");
    Equal("17293822569103005563", payload["withdrawMethodId"], "GG keyed withdrawMethodId");

    var form = (Dictionary<string, string>[]?)payload["formBody"]
               ?? throw new InvalidOperationException("GG keyed formBody was null.");
    var values = form.ToDictionary(item => item["fieldKey"], item => item["fieldVal"]);
    Equal(3, values.Count, "GG keyed form field count");
    Equal("NGUYEN VAN AN", values["realName"], "GG keyed realName");
    Equal("1234567890", values["account"], "GG keyed account");
    Equal("VIETCOMB", values["bankCode"], "GG keyed bankCode");
}

void VerifyGgRequestTransport()
{
    var create = ggWorkflowType.GetMethod("CreateEncryptedRequest", nonPublicStatic)
                 ?? throw new InvalidOperationException("CreateEncryptedRequest was not found.");
    using var request = (HttpRequestMessage?)create.Invoke(
                            null,
                            ["api/member/register", "ciphertext", 123L])
                        ?? throw new InvalidOperationException("GG request was null.");

    Equal("text/plain", request.Content?.Headers.ContentType?.MediaType, "GG content type");
    True(string.IsNullOrWhiteSpace(request.Content?.Headers.ContentType?.CharSet),
        "GG text/plain request must not append a charset.");
    Equal("chipher", request.Headers.GetValues("x-data-mode").Single(), "GG data mode");
    Equal("123", request.Headers.GetValues("timestamp").Single(), "GG timestamp");
}

void VerifyGgMaskedAccount()
{
    var contains = ggWorkflowType.GetMethod("ContainsAccountNumber", nonPublicStatic)
                   ?? throw new InvalidOperationException("ContainsAccountNumber was not found.");
    var matched = contains.Invoke(
        null,
        ["""{"code":0,"data":[{"account":"******7890"}]}""", "1234567890"]);
    True(matched is true, "GG masked bank account was not recognized.");
    True(DirectBankSetupWorkflow.IsSupported(TargetSite.GG88),
        "GG88 direct bank workflow must be enabled.");
}

object CreateNested(string nestedTypeName, params object?[] arguments)
{
    var nestedType = workflowType.GetNestedType(nestedTypeName, BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException($"{nestedTypeName} was not found.");
    return Activator.CreateInstance(
               nestedType,
               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
               binder: null,
               args: arguments,
               culture: null)
           ?? throw new InvalidOperationException($"{nestedTypeName} could not be created.");
}

static void Run(string name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS: {name}");
    }
    catch
    {
        Console.WriteLine($"FAIL: {name}");
        throw;
    }
}

static void RunAsync(string name, Func<Task> action)
{
    try
    {
        action().GetAwaiter().GetResult();
        Console.WriteLine($"PASS: {name}");
    }
    catch
    {
        Console.WriteLine($"FAIL: {name}");
        throw;
    }
}

static void Equal(object? expected, object? actual, string field)
{
    if (!Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{field}: expected '{expected}', received '{actual}'.");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class CaptureHandler : HttpMessageHandler
{
    public const string RsaModulus =
        "c7e43a9c6b9a0bf5d2d2d6b975d744f39f9a1d64f25ba4ad0f766f27ea" +
        "895f0f22c31ce46dc5ae72353588dbb904bfc4d27fc9a68fda38d2b5f76" +
        "c96bfb18d9365486f2f348b9d542b3647012432c69fbf69db36cfc5368b" +
        "f7bb54dad33c5fce8a258267fa9ed9a3278f71129810491bcaf269f20fc6" +
        "e18f053393fdc2cfd37ea2511e512af7f79498a54d99152956ad929084b7" +
        "ec49346532341291b00a30e9f4e8453217f1864a9f11de62739125cb9727" +
        "72401f44d71ab438fc39d1673f87e232c6f85451e166d685db75c35172c9" +
        "3129c0df59634f0c2d5010f0bf0ce03e70d78933df97b35e98609c075bb5";

    public string? RsaPath { get; private set; }

    public string? BindBody { get; private set; }

    public bool HasEncryptionHeader { get; private set; }

    public bool HasDigestHeader { get; private set; }

    public bool HasRsaHeader { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath == "/wps/session/key/rsa")
        {
            RsaPath = request.RequestUri.PathAndQuery;
            return Text(HttpStatusCode.OK, RsaModulus);
        }

        if (request.Method == HttpMethod.Put
            && request.RequestUri?.AbsolutePath == "/wps/v2/member/bankCards")
        {
            BindBody = await request.Content!.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            HasEncryptionHeader = request.Headers.Contains("Encryption");
            HasDigestHeader = request.Headers.Contains("X-Digest");
            HasRsaHeader = request.Headers.Contains("X-RSA");
            return Text(HttpStatusCode.OK, """{"success":true}""");
        }

        return Text(HttpStatusCode.NotFound, string.Empty);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
}

sealed class FullFlowHandler : HttpMessageHandler
{
    public int BindCalls { get; private set; }

    public List<string> BindBodies { get; } = [];

    public List<string> BindEncryptionHeaders { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (request.Method == HttpMethod.Get
            && path == "/wps/v2/member/bankCards/bindConfig")
        {
            return Json(HttpStatusCode.OK, """{"success":true,"value":{}}""");
        }

        if (request.Method == HttpMethod.Get && path == "/wps/member/info")
        {
            return Json(
                HttpStatusCode.OK,
                """{"success":true,"value":{"mobile":"987654321","countryDialingCode":"84","payee":"TRAN THI B"}}""");
        }

        if (request.Method == HttpMethod.Get && path == "/wps/relay/MCSFE_getBanks")
        {
            return Json(
                HttpStatusCode.OK,
                """{"success":true,"value":[{"bankCode":"TCB","bankEngName":"Techcombank","bankChName":"TCB","bankLocaleName":"Techcombank"}]}""");
        }

        if (request.Method == HttpMethod.Get
            && path == "/wps/relay/MCSFE_getBankTemplate")
        {
            return Json(
                HttpStatusCode.OK,
                """{"success":true,"value":{"bankCardTemplateSettings":[]}}""");
        }

        if (request.Method == HttpMethod.Get && path == "/wps/session/key/rsa")
        {
            return Json(HttpStatusCode.OK, CaptureHandler.RsaModulus);
        }

        if (request.Method == HttpMethod.Put && path == "/wps/v2/member/bankCards")
        {
            BindCalls++;
            BindBodies.Add(
                await request.Content!.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false));
            BindEncryptionHeaders.Add(
                request.Headers.GetValues("Encryption").Single());
            return BindCalls == 1
                ? Json(HttpStatusCode.BadRequest, """{"errorCode":"temporary-bind-state"}""")
                : Json(HttpStatusCode.OK, """{"success":true}""");
        }

        if (request.Method == HttpMethod.Get
            && (path == "/wps/relay/MCSFE_getWithdrawAccountInfo"
                || path == "/wps/relay/MCSFE_getBankCards"))
        {
            return BindCalls >= 2
                ? Json(
                    HttpStatusCode.OK,
                    """{"success":true,"value":{"bankCards":[{"cardNumber":"******7890"}]}}""")
                : Json(HttpStatusCode.OK, """{"success":true,"value":{"bankCards":[]}}""");
        }

        return Json(HttpStatusCode.NotFound, string.Empty);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
}

sealed class VerificationHandler : HttpMessageHandler
{
    public int WithdrawInfoCalls { get; private set; }

    public int BankCardCalls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath == "/wps/relay/MCSFE_getWithdrawAccountInfo")
        {
            WithdrawInfoCalls++;
            return Task.FromResult(Json("""{"success":true,"value":{}}"""));
        }

        if (request.RequestUri?.AbsolutePath == "/wps/relay/MCSFE_getBankCards")
        {
            BankCardCalls++;
            return Task.FromResult(
                Json("""{"success":true,"value":{"bankCards":[{"cardNumber":"******7890"}]}}"""));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
}
