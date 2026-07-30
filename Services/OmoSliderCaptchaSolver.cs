using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed class OmoSliderCaptchaSolver : ISliderCaptchaSolver
{
    private const string TaskType = "GeetestSliderWebTask";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OmoCaptchaOptions _options;
    private readonly ILogger<OmoSliderCaptchaSolver> _logger;

    public OmoSliderCaptchaSolver(
        IHttpClientFactory httpClientFactory,
        IOptions<OmoCaptchaOptions> options,
        ILogger<OmoSliderCaptchaSolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<SliderCaptchaSolveResult> SolveAsync(
        string imageBase64,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return SliderCaptchaSolveResult.Failure(
                "OMOCaptcha GeetestSliderWebTask is not configured.");
        }

        imageBase64 = NormalizeImageBase64(imageBase64);
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return SliderCaptchaSolveResult.Failure("Slider captcha image is empty.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var timeoutToken = timeoutSource.Token;
        string? taskId = null;

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var createDocument = await PostJsonAsync(
                    client,
                    BuildEndpoint("/v2/createTask"),
                    new
                    {
                        clientKey = _options.ApiKey,
                        task = new
                        {
                            type = TaskType,
                            imageBase64
                        }
                    },
                    timeoutToken)
                .ConfigureAwait(false);

            if (!TryReadSuccessfulEnvelope(createDocument.RootElement, out var createError))
            {
                return SliderCaptchaSolveResult.Failure(
                    createError,
                    elapsed: stopwatch.Elapsed);
            }

            taskId = ReadString(createDocument.RootElement, "taskId");
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return SliderCaptchaSolveResult.Failure(
                    "OMOCaptcha createTask did not return taskId.",
                    elapsed: stopwatch.Elapsed);
            }

            _logger.LogInformation(
                "OMOCaptcha slider task created. TaskId={TaskId}, Base64Length={Base64Length}",
                taskId,
                imageBase64.Length);

            var pollingDelay = _options.InitialPollingDelayMilliseconds;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(_options.TimeoutSeconds))
            {
                await Task.Delay(pollingDelay, timeoutToken).ConfigureAwait(false);

                using var resultDocument = await PostJsonAsync(
                        client,
                        BuildEndpoint("/v2/getTaskResult"),
                        new
                        {
                            clientKey = _options.ApiKey,
                            taskId
                        },
                        timeoutToken)
                    .ConfigureAwait(false);

                var root = resultDocument.RootElement;
                if (!TryReadSuccessfulEnvelope(root, out var resultError))
                {
                    return SliderCaptchaSolveResult.Failure(
                        resultError,
                        taskId,
                        stopwatch.Elapsed);
                }

                var status = ReadString(root, "status");
                if (status.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadEndPoint(root, out var endX, out var endY))
                    {
                        return SliderCaptchaSolveResult.Failure(
                            "OMOCaptcha returned ready without solution.end coordinates.",
                            taskId,
                            stopwatch.Elapsed);
                    }

                    _logger.LogInformation(
                        "OMOCaptcha slider task completed. TaskId={TaskId}, EndX={EndX}, EndY={EndY}, ElapsedMs={ElapsedMs}",
                        taskId,
                        endX,
                        endY,
                        stopwatch.ElapsedMilliseconds);
                    return new SliderCaptchaSolveResult(
                        true,
                        endX,
                        endY,
                        string.Empty,
                        taskId,
                        stopwatch.Elapsed);
                }

                if (status.Equals("fail", StringComparison.OrdinalIgnoreCase))
                {
                    return SliderCaptchaSolveResult.Failure(
                        "OMOCaptcha could not solve the slider captcha.",
                        taskId,
                        stopwatch.Elapsed);
                }

                if (!string.IsNullOrWhiteSpace(status)
                    && !status.Equals("processing", StringComparison.OrdinalIgnoreCase))
                {
                    return SliderCaptchaSolveResult.Failure(
                        $"OMOCaptcha returned unknown task status '{status}'.",
                        taskId,
                        stopwatch.Elapsed);
                }

                pollingDelay = Math.Min(
                    (int)Math.Ceiling(pollingDelay * 1.5),
                    _options.MaxPollingDelayMilliseconds);
            }

            return SliderCaptchaSolveResult.Failure(
                "OMOCaptcha slider polling timed out.",
                taskId,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SliderCaptchaSolveResult.Failure(
                "OMOCaptcha slider request timed out.",
                taskId,
                stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            return SliderCaptchaSolveResult.Failure(
                $"OMOCaptcha slider HTTP error: {exception.Message}",
                taskId,
                stopwatch.Elapsed);
        }
        catch (JsonException exception)
        {
            return SliderCaptchaSolveResult.Failure(
                $"OMOCaptcha slider returned invalid JSON: {exception.Message}",
                taskId,
                stopwatch.Elapsed);
        }
    }

    private async Task<JsonDocument> PostJsonAsync(
        HttpClient client,
        Uri endpoint,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        using var response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OMOCaptcha returned HTTP {(int)response.StatusCode}.");
        }

        return JsonDocument.Parse(responseText);
    }

    private Uri BuildEndpoint(string path) =>
        new(new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute), path.TrimStart('/'));

    private static string NormalizeImageBase64(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var commaIndex = normalized.IndexOf(',');
        return commaIndex >= 0 && commaIndex < normalized.Length - 1
            ? normalized[(commaIndex + 1)..]
            : string.Empty;
    }

    private static bool TryReadSuccessfulEnvelope(JsonElement root, out string error)
    {
        error = string.Empty;
        if (ReadInt32(root, "errorId") == 0)
        {
            return true;
        }

        var errorCode = ReadString(root, "errorCode");
        var description = ReadString(root, "errorDescription");
        error = string.IsNullOrWhiteSpace(errorCode)
            ? string.IsNullOrWhiteSpace(description)
                ? "OMOCaptcha returned an unknown error."
                : $"OMOCaptcha error: {description}"
            : string.IsNullOrWhiteSpace(description)
                ? $"OMOCaptcha error: {errorCode}"
                : $"OMOCaptcha error: {errorCode} - {description}";
        return false;
    }

    private static bool TryReadEndPoint(JsonElement root, out int endX, out int endY)
    {
        endX = 0;
        endY = 0;
        if (!root.TryGetProperty("solution", out var solution)
            || solution.ValueKind != JsonValueKind.Object
            || !solution.TryGetProperty("end", out var end)
            || end.ValueKind != JsonValueKind.Object
            || !TryReadInt32(end, "x", out endX)
            || endX <= 0)
        {
            return false;
        }

        // Current slider responses may omit Y because only horizontal travel
        // is needed. Keep zero when the optional coordinate is absent.
        TryReadInt32(end, "y", out endY);
        return true;
    }

    private static int ReadInt32(JsonElement root, string propertyName) =>
        TryReadInt32(root, propertyName, out var value) ? value : -1;

    private static bool TryReadInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.TryGetInt32(out value)
               || (property.ValueKind == JsonValueKind.String
                   && int.TryParse(property.GetString(), out value));
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? property.GetRawText()
                : string.Empty;
    }
}
