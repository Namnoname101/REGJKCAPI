using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(options => options.SingleLine = true));

var options = Options.Create(new CaptchaSolverOptions
{
    LocalGeeTestMode = CaptchaSolverOptions.BrowserFirstMode,
    LocalBrowserMaxSessions = 1,
    LocalBrowserMaxUsesPerSession = 3,
    LocalBrowserIdleSeconds = 60
});

using var pool = new BrowserGeetestTokenPool(
    options,
    loggerFactory.CreateLogger<BrowserGeetestTokenPool>());
await pool.StartAsync(CancellationToken.None);

try
{
    var request = new BrowserGeetestTokenRequest(
        TargetSite.XX88,
        CreateTestPageUrl(),
        "local-pool-smoke",
        Proxy: null);

    var first = await RequireTokenAsync(pool, request);
    var firstSnapshot = pool.GetSnapshot();
    Ensure(firstSnapshot.SessionCount == 1, "The first token did not create exactly one session.");
    Ensure(firstSnapshot.StartedBrowserCount == 1, "The first token did not start Chromium.");
    Ensure(!first.ReusedBrowser, "The first token cannot report a reused browser.");

    var second = await RequireTokenAsync(pool, request);
    var secondSnapshot = pool.GetSnapshot();
    Ensure(second.ReusedBrowser, "The second token did not reuse the existing Chromium session.");
    Ensure(secondSnapshot.SessionCount == 1, "A second browser was created for the same proxy.");
    Ensure(secondSnapshot.StartedBrowserCount == 1, "The pool no longer has exactly one active Chromium.");
    Ensure(secondSnapshot.SuccessfulTokenCount == 2, "The pool did not record both successful tokens.");

    var third = await RequireTokenAsync(pool, request);
    var recycledSnapshot = pool.GetSnapshot();
    Ensure(third.ReusedBrowser, "The session was not reused before its configured recycle limit.");
    Ensure(recycledSnapshot.SessionCount == 0, "The session was not recycled at its configured use limit.");

    var fourth = await RequireTokenAsync(pool, request);
    var fourthSnapshot = pool.GetSnapshot();
    Ensure(!fourth.ReusedBrowser, "A recycled session was incorrectly reported as reused.");
    Ensure(fourthSnapshot.SessionCount == 1, "The pool did not create a replacement session after recycle.");

    Console.WriteLine(
        $"PASS: local headless GeeTest pool reused one Chromium session, recycled it at 3 tokens, and kept {fourthSnapshot.SessionCount} active session. FirstTokenMs={first.ElapsedMilliseconds}; ReusedTokenMs={second.ElapsedMilliseconds}; ReplacementTokenMs={fourth.ElapsedMilliseconds}.");
}
finally
{
    await pool.StopAsync(CancellationToken.None);
}

static async Task<BrowserGeetestTokenResult> RequireTokenAsync(
    BrowserGeetestTokenPool pool,
    BrowserGeetestTokenRequest request)
{
    var token = await pool.GetTokenAsync(request, CancellationToken.None);
    if (token is null)
    {
        throw new InvalidOperationException("The local test page did not yield a GeeTest-shaped token.");
    }

    using var document = JsonDocument.Parse(token.Value.GetRawText());
    foreach (var name in new[] { "lot_number", "captcha_output", "pass_token", "gen_time" })
    {
        if (!document.RootElement.TryGetProperty(name, out var value)
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Token field '{name}' was missing.");
        }
    }

    return token;
}

static string CreateTestPageUrl()
{
    const string html = """
        <!doctype html>
        <html>
        <body>
        <script>
        (() => {
          let sequence = 0;
          window.initBotion = (options, ready) => {
            let success = null;
            const token = {
              lot_number: 'lot-' + options.captchaId + '-' + (++sequence),
              captcha_output: 'output-' + sequence,
              pass_token: 'pass-' + sequence,
              gen_time: String(Date.now())
            };
            ready({
              onReady: callback => setTimeout(callback, 0),
              onSuccess: callback => { success = callback; },
              onError: () => {},
              getValidate: () => token,
              verify: () => setTimeout(() => success && success(), 10)
            });
          };
        })();
        </script>
        </body>
        </html>
        """;

    return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
}

static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
