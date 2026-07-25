using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

public sealed class ApplicationLifetimeLoggingService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ApplicationLifetimeLoggingService> _logger;

    public ApplicationLifetimeLoggingService(
        IHostApplicationLifetime lifetime,
        ILogger<ApplicationLifetimeLoggingService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(
            () => _logger.LogInformation("Application started. Press Ctrl+C to stop."));
        _lifetime.ApplicationStopping.Register(
            () => _logger.LogInformation("Shutdown requested."));
        _lifetime.ApplicationStopped.Register(
            () => _logger.LogInformation("Application shut down cleanly."));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
