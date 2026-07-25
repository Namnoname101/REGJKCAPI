using System.Diagnostics;
using System.Text.Json;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

internal sealed class SitePerformanceMetrics
{
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    public long CapacityWaitMs { get; set; }

    public long BrowserAcquireMs { get; set; }

    public long RegistrationMs { get; set; }

    public long BankSetupMs { get; set; }

    public long BrowserResetMs { get; set; }

    public long TotalMs { get; set; }
}

internal static class SitePerformanceMetricsWriter
{
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Append(
        AccountModel account,
        SiteRunModel siteRun,
        SitePerformanceMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(siteRun);
        ArgumentNullException.ThrowIfNull(metrics);

        try
        {
            var metricsDirectory = Path.Combine(AppContext.BaseDirectory, "metrics");
            var metricsPath = Path.Combine(metricsDirectory, "site-performance.jsonl");
            var payload = new
            {
                metrics.StartedAtUtc,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                account.ProfileId,
                Site = siteRun.Site.ToString(),
                Outcome = siteRun.Status.ToString(),
                FinalMessage = TrimForTelemetry(siteRun.LogMessage),
                metrics.CapacityWaitMs,
                metrics.BrowserAcquireMs,
                metrics.RegistrationMs,
                metrics.BankSetupMs,
                metrics.BrowserResetMs,
                metrics.TotalMs
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);

            lock (WriteGate)
            {
                Directory.CreateDirectory(metricsDirectory);
                File.AppendAllText(metricsPath, json + Environment.NewLine);
            }
        }
        catch (Exception exception)
        {
            // Performance telemetry must never fail a registration.
            Debug.WriteLine($"Could not append site performance metric: {exception}");
        }
    }

    private static string TrimForTelemetry(string? value)
    {
        const int maxLength = 300;
        var normalized = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }
}
