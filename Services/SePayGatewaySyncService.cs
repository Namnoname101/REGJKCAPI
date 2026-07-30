using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BAOCODETOOL.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed class SePayGatewaySyncService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SePayOptions _options;
    private readonly ILogger<SePayGatewaySyncService> _logger;

    public SePayGatewaySyncService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<SePayOptions> options,
        ILogger<SePayGatewaySyncService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TryGetGatewayUri(out var gatewayUri)
            || string.IsNullOrWhiteSpace(_options.GatewayAccessToken))
        {
            _logger.LogInformation("SePay Fly gateway sync is disabled.");
            return;
        }

        var pollingDelay = TimeSpan.FromSeconds(Math.Clamp(_options.GatewayPollingSeconds, 2, 300));
        _logger.LogInformation(
            "SePay Fly gateway sync enabled. Gateway={Gateway}; PollingSeconds={PollingSeconds}",
            gatewayUri,
            pollingDelay.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedAny = await PollOnceAsync(gatewayUri, stoppingToken);
                if (processedAny)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "SePay Fly gateway polling failed.");
            }

            await Task.Delay(pollingDelay, stoppingToken);
        }
    }

    private async Task<bool> PollOnceAsync(Uri gatewayUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(gatewayUri, "api/events/pending?limit=50"));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.GatewayAccessToken.Trim());

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var events = await response.Content.ReadFromJsonAsync<List<SePayGatewayEvent>>(
            JsonOptions,
            cancellationToken) ?? [];

        foreach (var gatewayEvent in events)
        {
            SePayWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<SePayWebhookPayload>(
                    gatewayEvent.Payload,
                    JsonOptions);
            }
            catch (JsonException exception)
            {
                _logger.LogError(
                    exception,
                    "Fly gateway event {EventId} contains invalid JSON.",
                    gatewayEvent.Id);
                await AcknowledgeAsync(gatewayUri, gatewayEvent.Id, "InvalidPayload", cancellationToken);
                continue;
            }

            if (payload is null)
            {
                await AcknowledgeAsync(gatewayUri, gatewayEvent.Id, "InvalidPayload", cancellationToken);
                continue;
            }

            using var scope = _scopeFactory.CreateScope();
            var sePay = scope.ServiceProvider.GetRequiredService<SePayWebhookService>();
            var notifier = scope.ServiceProvider.GetRequiredService<SePayNotificationService>();
            var result = await sePay.ProcessAsync(payload, cancellationToken);
            await notifier.NotifyAsync(result, cancellationToken);
            await AcknowledgeAsync(gatewayUri, gatewayEvent.Id, result.Status, cancellationToken);
        }

        return events.Count > 0;
    }

    private async Task AcknowledgeAsync(
        Uri gatewayUri,
        long eventId,
        string status,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(gatewayUri, $"api/events/{eventId}/ack"));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.GatewayAccessToken.Trim());
        request.Content = JsonContent.Create(new { status });

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private bool TryGetGatewayUri(out Uri gatewayUri)
    {
        if (Uri.TryCreate(_options.GatewayBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps)
        {
            gatewayUri = parsed;
            return true;
        }

        gatewayUri = null!;
        return false;
    }

    private sealed class SePayGatewayEvent
    {
        public long Id { get; set; }
        public string Payload { get; set; } = string.Empty;
    }
}
