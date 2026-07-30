using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed class OmoImageCaptchaSolver : IImageCaptchaSolver
{
    private const string TaskType = "ImageToTextTask";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OmoCaptchaOptions _options;
    private readonly ILogger<OmoImageCaptchaSolver> _logger;

    public OmoImageCaptchaSolver(
        IHttpClientFactory httpClientFactory,
        IOptions<OmoCaptchaOptions> options,
        ILogger<OmoImageCaptchaSolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<ImageCaptchaSolveResult> SolveAsync(
        string imageBase64,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return ImageCaptchaSolveResult.Failure(
                "OMOCaptcha ImageToTextTask is not configured.");
        }

        imageBase64 = NormalizeImageBase64(imageBase64);
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return ImageCaptchaSolveResult.Failure("Captcha image is empty.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var timeoutToken = timeoutSource.Token;
        string? taskId = null;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var task = new Dictionary<string, object?>
            {
                ["type"] = TaskType,
                ["imageBase64"] = imageBase64
            };

            if (!string.IsNullOrWhiteSpace(_options.Module))
            {
                task["module"] = _options.Module.Trim();
            }

            using var createDocument = await PostJsonAsync(
                    client,
                    BuildEndpoint("/v2/createTask"),
                    new
                    {
                        clientKey = _options.ApiKey,
                        task
                    },
                    timeoutToken)
                .ConfigureAwait(false);

            if (!TryReadSuccessfulEnvelope(createDocument.RootElement, out var createError))
            {
                return ImageCaptchaSolveResult.Failure(
                    createError,
                    elapsed: stopwatch.Elapsed);
            }

            taskId = ReadString(createDocument.RootElement, "taskId");
            if (string.IsNullOrWhiteSpace(taskId))
            {
                return ImageCaptchaSolveResult.Failure(
                    "OMOCaptcha createTask did not return taskId.",
                    elapsed: stopwatch.Elapsed);
            }

            _logger.LogInformation(
                "OMOCaptcha image task created. TaskId={TaskId}, Base64Length={Base64Length}",
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
                    return ImageCaptchaSolveResult.Failure(
                        resultError,
                        taskId,
                        stopwatch.Elapsed);
                }

                var status = ReadString(root, "status");
                if (status.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    var solution = ReadSolutionText(root).Trim();
                    if (string.IsNullOrWhiteSpace(solution))
                    {
                        return ImageCaptchaSolveResult.Failure(
                            "OMOCaptcha returned ready without solution.text.",
                            taskId,
                            stopwatch.Elapsed);
                    }

                    _logger.LogInformation(
                        "OMOCaptcha image task completed. TaskId={TaskId}, ElapsedMs={ElapsedMs}",
                        taskId,
                        stopwatch.ElapsedMilliseconds);
                    return new ImageCaptchaSolveResult(
                        true,
                        solution,
                        string.Empty,
                        taskId,
                        stopwatch.Elapsed);
                }

                if (status.Equals("fail", StringComparison.OrdinalIgnoreCase))
                {
                    return ImageCaptchaSolveResult.Failure(
                        "OMOCaptcha could not solve the image captcha.",
                        taskId,
                        stopwatch.Elapsed);
                }

                if (!string.IsNullOrWhiteSpace(status)
                    && !status.Equals("processing", StringComparison.OrdinalIgnoreCase))
                {
                    return ImageCaptchaSolveResult.Failure(
                        $"OMOCaptcha returned unknown task status '{status}'.",
                        taskId,
                        stopwatch.Elapsed);
                }

                pollingDelay = Math.Min(
                    (int)Math.Ceiling(pollingDelay * 1.5),
                    _options.MaxPollingDelayMilliseconds);
            }

            return ImageCaptchaSolveResult.Failure(
                "OMOCaptcha polling timed out.",
                taskId,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ImageCaptchaSolveResult.Failure(
                "OMOCaptcha request timed out.",
                taskId,
                stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            return ImageCaptchaSolveResult.Failure(
                $"OMOCaptcha HTTP error: {exception.Message}",
                taskId,
                stopwatch.Elapsed);
        }
        catch (JsonException exception)
        {
            return ImageCaptchaSolveResult.Failure(
                $"OMOCaptcha returned invalid JSON: {exception.Message}",
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

    private static bool TryReadSuccessfulEnvelope(
        JsonElement root,
        out string error)
    {
        error = string.Empty;
        var errorId = ReadInt32(root, "errorId");
        if (errorId == 0)
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

    private static int ReadInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return -1;
        }

        if (property.TryGetInt32(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String
               && int.TryParse(property.GetString(), out value)
            ? value
            : -1;
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

    private static string ReadSolutionText(JsonElement root)
    {
        if (!root.TryGetProperty("solution", out var solution)
            || solution.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadString(solution, "text");
    }
}
