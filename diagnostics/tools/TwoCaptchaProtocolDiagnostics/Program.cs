using System.Reflection;
using System.Text.Json;
using BAOCODETOOL.Core;

var solverType = typeof(DirectRegistrationWorkflow).Assembly.GetType(
                     "BAOCODETOOL.Core.TwoCaptchaGeetestSolver")
                 ?? throw new InvalidOperationException(
                     "TwoCaptchaGeetestSolver was not found.");
var flags = BindingFlags.NonPublic | BindingFlags.Static;
var parseSubmission = solverType.GetMethod("ParseSubmissionResponse", flags)
                      ?? throw new InvalidOperationException(
                          "Submission parser was not found.");
var parseResult = solverType.GetMethod("ParseResultResponse", flags)
                  ?? throw new InvalidOperationException(
                      "Result parser was not found.");

const string token =
    """
    {
      "captcha_id": "2e3984aebc5904671a2a0a7c284b8c79",
      "lot_number": "lot",
      "pass_token": "pass",
      "gen_time": "1785350000",
      "captcha_output": "output"
    }
    """;

AssertStatus(parseSubmission, """{"status":1,"request":"123456789"}""", "Pending");
AssertStatus(parseSubmission, "OK|987654321", "Pending");
AssertStatus(
    parseSubmission,
    """{"status":0,"request":"ERROR_ZERO_BALANCE"}""",
    "Error");

AssertReady(parseResult, token);
AssertReady(parseResult, $$"""{"status":1,"request":{{token}}}""");
AssertReady(
    parseResult,
    JsonSerializer.Serialize(new
    {
        status = 1,
        request = token
    }));
AssertStatus(
    parseResult,
    """{"status":0,"request":"CAPCHA_NOT_READY"}""",
    "Pending");
AssertStatus(
    parseResult,
    """{"status":0,"request":"ERROR_CAPTCHA_UNSOLVABLE"}""",
    "Error");

Console.WriteLine(
    "PASS 2Captcha GeeTest v4 protocol parsing: task IDs, pending responses, token variants, and errors.");
return 0;

static void AssertReady(MethodInfo parser, string response)
{
    var parsed = Invoke(parser, response);
    var status = ReadStatus(parsed);
    if (!status.Equals("Ready", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Expected Ready but got {status}. Response={response}");
    }

    var tokenValue = parsed.GetType().GetProperty("Token")?.GetValue(parsed);
    if (tokenValue is not JsonElement token
        || token.ValueKind != JsonValueKind.Object
        || token.GetProperty("captcha_id").GetString()
        != "2e3984aebc5904671a2a0a7c284b8c79")
    {
        throw new InvalidOperationException("Parsed token was missing or invalid.");
    }
}

static void AssertStatus(MethodInfo parser, string response, string expected)
{
    var parsed = Invoke(parser, response);
    var actual = ReadStatus(parsed);
    if (!actual.Equals(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Expected {expected} but got {actual}. Response={response}");
    }
}

static object Invoke(MethodInfo parser, string response)
{
    try
    {
        return parser.Invoke(null, [response])
               ?? throw new InvalidOperationException("Parser returned null.");
    }
    catch (TargetInvocationException exception)
    {
        throw exception.InnerException ?? exception;
    }
}

static string ReadStatus(object parsed) =>
    parsed.GetType().GetProperty("Status")?.GetValue(parsed)?.ToString()
    ?? throw new InvalidOperationException("Parser status was not found.");
