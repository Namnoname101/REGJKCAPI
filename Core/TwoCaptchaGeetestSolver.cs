using System.Diagnostics;
using System.Text.Json;
using BAOCODETOOL.Configuration;

namespace BAOCODETOOL.Core;

internal static class TwoCaptchaGeetestSolver
{
    private static readonly Uri SubmitEndpoint = new("https://2captcha.com/in.php");
    private static readonly Uri ResultEndpoint = new("https://2captcha.com/res.php");

    public static async Task<TwoCaptchaGeetestResult> SolveAsync(
        CaptchaSolverOptions options,
        string captchaId,
        string pageUrl,
        ProxyConfiguration? registrationProxy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsTwoCaptchaEnabled)
        {
            return TwoCaptchaGeetestResult.Failure("2Captcha is not configured.");
        }

        if (string.IsNullOrWhiteSpace(captchaId))
        {
            return TwoCaptchaGeetestResult.Failure("GeeTest captcha ID is missing.");
        }

        if (options.UseRegistrationProxy && registrationProxy is null)
        {
            return TwoCaptchaGeetestResult.Failure(
                "The registration proxy is required for 2Captcha GeeTest solving.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 15, 300))
        };

        try
        {
            var taskId = await SubmitAsync(
                    client,
                    options,
                    captchaId,
                    pageUrl,
                    registrationProxy,
                    cancellationToken)
                .ConfigureAwait(false);

            if (taskId.Status == TwoCaptchaResponseStatus.Error)
            {
                return TwoCaptchaGeetestResult.Failure(taskId.Message, stopwatch.Elapsed);
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 15, 300));
            var initialDelay = TimeSpan.FromMilliseconds(
                Math.Clamp(options.InitialPollingDelayMilliseconds, 1000, 10000));
            var pollingDelay = TimeSpan.FromMilliseconds(
                Math.Clamp(options.PollingIntervalMilliseconds, 1000, 10000));
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            await Task.Delay(initialDelay, cancellationToken).ConfigureAwait(false);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var result = await GetResultAsync(
                        client,
                        options.ApiKey,
                        taskId.Message,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.Status == TwoCaptchaResponseStatus.Ready)
                {
                    return new TwoCaptchaGeetestResult(
                        true,
                        result.Token,
                        "2Captcha solved GeeTest v4.",
                        taskId.Message,
                        stopwatch.Elapsed);
                }

                if (result.Status == TwoCaptchaResponseStatus.Error)
                {
                    return TwoCaptchaGeetestResult.Failure(result.Message, stopwatch.Elapsed);
                }

                await Task.Delay(pollingDelay, cancellationToken).ConfigureAwait(false);
            }

            return TwoCaptchaGeetestResult.Failure(
                "2Captcha GeeTest v4 timed out.",
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return TwoCaptchaGeetestResult.Failure(
                "2Captcha GeeTest v4 timed out.",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            return TwoCaptchaGeetestResult.Failure(
                $"2Captcha request failed: {exception.Message}",
                stopwatch.Elapsed);
        }
        catch (JsonException exception)
        {
            return TwoCaptchaGeetestResult.Failure(
                $"2Captcha returned invalid JSON: {exception.Message}",
                stopwatch.Elapsed);
        }
    }

    private static async Task<TwoCaptchaApiResponse> SubmitAsync(
        HttpClient client,
        CaptchaSolverOptions options,
        string captchaId,
        string pageUrl,
        ProxyConfiguration? registrationProxy,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["key"] = options.ApiKey.Trim(),
            ["method"] = "geetest_v4",
            ["captcha_id"] = captchaId,
            ["pageurl"] = pageUrl,
            ["json"] = "1"
        };

        if (options.UseRegistrationProxy && registrationProxy is not null)
        {
            fields["proxy"] = FormatProxy(registrationProxy);
            fields["proxytype"] = registrationProxy.IsSocks
                ? "SOCKS5"
                : registrationProxy.Scheme.Equals(
                    "https",
                    StringComparison.OrdinalIgnoreCase)
                    ? "HTTPS"
                    : "HTTP";
        }

        using var content = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(
                SubmitEndpoint,
                content,
                cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return TwoCaptchaApiResponse.Error(
                $"2Captcha submit returned HTTP {(int)response.StatusCode}.");
        }

        return ParseSubmissionResponse(responseText);
    }

    private static async Task<TwoCaptchaApiResponse> GetResultAsync(
        HttpClient client,
        string apiKey,
        string taskId,
        CancellationToken cancellationToken)
    {
        var query = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["key"] = apiKey.Trim(),
            ["action"] = "get",
            ["id"] = taskId,
            ["json"] = "1"
        });
        var queryString = await query.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        using var response = await client.GetAsync(
                new UriBuilder(ResultEndpoint) { Query = queryString }.Uri,
                cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return TwoCaptchaApiResponse.Error(
                $"2Captcha result returned HTTP {(int)response.StatusCode}.");
        }

        return ParseResultResponse(responseText);
    }

    private static TwoCaptchaApiResponse ParseSubmissionResponse(string responseText)
    {
        var normalized = responseText.Trim();
        if (normalized.StartsWith("OK|", StringComparison.Ordinal))
        {
            var plainTaskId = normalized[3..].Trim();
            return string.IsNullOrWhiteSpace(plainTaskId)
                ? TwoCaptchaApiResponse.Error("2Captcha did not return a task ID.")
                : TwoCaptchaApiResponse.Pending(plainTaskId);
        }

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;
        if (ReadStatus(root) == 1
            && TryReadRequestString(root, out var jsonTaskId)
            && !string.IsNullOrWhiteSpace(jsonTaskId))
        {
            return TwoCaptchaApiResponse.Pending(jsonTaskId);
        }

        return TwoCaptchaApiResponse.Error(ReadError(root));
    }

    private static TwoCaptchaApiResponse ParseResultResponse(string responseText)
    {
        var normalized = responseText.Trim();
        if (normalized.Equals("CAPCHA_NOT_READY", StringComparison.OrdinalIgnoreCase))
        {
            return TwoCaptchaApiResponse.Pending("CAPCHA_NOT_READY");
        }

        if (normalized.StartsWith("OK|", StringComparison.Ordinal))
        {
            normalized = normalized[3..].Trim();
        }

        using var document = JsonDocument.Parse(normalized);
        var root = document.RootElement;

        if (IsUsableToken(root))
        {
            return TwoCaptchaApiResponse.Ready(root.Clone());
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("request", out var request))
        {
            if (request.ValueKind == JsonValueKind.String)
            {
                var value = request.GetString() ?? string.Empty;
                if (value.Equals("CAPCHA_NOT_READY", StringComparison.OrdinalIgnoreCase))
                {
                    return TwoCaptchaApiResponse.Pending(value);
                }

                if (TryParseToken(value, out var stringToken))
                {
                    return TwoCaptchaApiResponse.Ready(stringToken);
                }
            }
            else if (IsUsableToken(request))
            {
                return TwoCaptchaApiResponse.Ready(request.Clone());
            }
        }

        return ReadStatus(root) == 0
            ? TwoCaptchaApiResponse.Error(ReadError(root))
            : TwoCaptchaApiResponse.Error("2Captcha returned an unusable GeeTest v4 token.");
    }

    private static bool TryParseToken(string value, out JsonElement token)
    {
        token = default;
        if (string.IsNullOrWhiteSpace(value) || value[0] != '{')
        {
            return false;
        }

        using var document = JsonDocument.Parse(value);
        if (!IsUsableToken(document.RootElement))
        {
            return false;
        }

        token = document.RootElement.Clone();
        return true;
    }

    private static bool IsUsableToken(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[]
                 {
                     "captcha_id",
                     "lot_number",
                     "captcha_output",
                     "pass_token",
                     "gen_time"
                 })
        {
            if (!value.TryGetProperty(name, out var field)
                || field.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(field.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadStatus(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status))
        {
            return -1;
        }

        if (status.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        return status.ValueKind == JsonValueKind.String
               && int.TryParse(status.GetString(), out numeric)
            ? numeric
            : -1;
    }

    private static bool TryReadRequestString(JsonElement root, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty("request", out var request)
            || request.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
        {
            return false;
        }

        value = request.ValueKind == JsonValueKind.String
            ? request.GetString() ?? string.Empty
            : request.GetRawText();
        return true;
    }

    private static string ReadError(JsonElement root)
    {
        if (TryReadRequestString(root, out var request)
            && !string.IsNullOrWhiteSpace(request))
        {
            return $"2Captcha error: {request}";
        }

        return "2Captcha returned an unknown error.";
    }

    private static string FormatProxy(ProxyConfiguration proxy)
    {
        if (!proxy.RequiresAuthentication)
        {
            return proxy.Server;
        }

        return $"{proxy.Username}:{proxy.Password}@{proxy.Server}";
    }

    private enum TwoCaptchaResponseStatus
    {
        Pending,
        Ready,
        Error
    }

    private sealed record TwoCaptchaApiResponse(
        TwoCaptchaResponseStatus Status,
        string Message,
        JsonElement? Token = null)
    {
        public static TwoCaptchaApiResponse Pending(string message) =>
            new(TwoCaptchaResponseStatus.Pending, message);

        public static TwoCaptchaApiResponse Ready(JsonElement token) =>
            new(TwoCaptchaResponseStatus.Ready, string.Empty, token);

        public static TwoCaptchaApiResponse Error(string message) =>
            new(TwoCaptchaResponseStatus.Error, message);
    }
}

internal sealed record TwoCaptchaGeetestResult(
    bool IsSuccess,
    JsonElement? Token,
    string Message,
    string? TaskId,
    TimeSpan Elapsed)
{
    public static TwoCaptchaGeetestResult Failure(
        string message,
        TimeSpan elapsed = default) =>
        new(false, null, message, null, elapsed);
}
