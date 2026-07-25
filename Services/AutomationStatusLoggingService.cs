using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

public sealed class AutomationStatusLoggingService : IHostedService
{
    private readonly ILogger<AutomationStatusLoggingService> _logger;

    public AutomationStatusLoggingService(ILogger<AutomationStatusLoggingService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        AutomationUi.StatusChanged += OnStatusChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AutomationUi.StatusChanged -= OnStatusChanged;
        return Task.CompletedTask;
    }

    private void OnStatusChanged(object? sender, AutomationStatusChangedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Message))
        {
            return;
        }

        var site = eventArgs.SiteRun?.Site.ToString() ?? "ACCOUNT";

        if (eventArgs.Status == AccountStatus.Failed)
        {
            _logger.LogError(
                "{Account} [{Site}] {Message}",
                eventArgs.Account.Username,
                site,
                eventArgs.Message);
        }
        else if (eventArgs.Status == AccountStatus.Success)
        {
            _logger.LogInformation(
                "SUCCESS {Account} [{Site}] {Message}",
                eventArgs.Account.Username,
                site,
                eventArgs.Message);
        }
        else
        {
            _logger.LogInformation(
                "{Account} [{Site}] {Message}",
                eventArgs.Account.Username,
                site,
                eventArgs.Message);
        }
    }
}
