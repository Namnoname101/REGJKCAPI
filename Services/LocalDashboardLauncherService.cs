using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

public sealed record LocalDashboardLauncherOptions(string Url, bool OpenBrowser);

/// <summary>
/// Opens the local UI only after Kestrel is listening. The web host and the
/// Telegram worker remain alive when that browser tab is later closed.
/// </summary>
public sealed class LocalDashboardLauncherService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LocalDashboardLauncherOptions _options;
    private readonly ILogger<LocalDashboardLauncherService> _logger;

    public LocalDashboardLauncherService(
        IHostApplicationLifetime lifetime,
        LocalDashboardLauncherOptions options,
        ILogger<LocalDashboardLauncherService> logger)
    {
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.OpenBrowser)
        {
            _lifetime.ApplicationStarted.Register(OpenDashboard);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static void OpenDashboard(string url, ILogger? logger = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            logger?.LogWarning("The configured local dashboard URL is invalid: {Url}", url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Could not open the local dashboard in a browser.");
        }
    }

    private void OpenDashboard() => OpenDashboard(_options.Url, _logger);
}
