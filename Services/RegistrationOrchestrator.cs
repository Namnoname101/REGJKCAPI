using BAOCODETOOL.Models;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

public sealed class RegistrationOrchestrator : IRegistrationOrchestrator
{
    private readonly IBrowserAutomationService _browserAutomation;
    private readonly ILogger<RegistrationOrchestrator> _logger;

    public RegistrationOrchestrator(
        IBrowserAutomationService browserAutomation,
        ILogger<RegistrationOrchestrator> logger)
    {
        _browserAutomation = browserAutomation;
        _logger = logger;
    }

    public async Task ProcessAsync(RegistrationJob job, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Registering account. JobId={JobId}, Account={Account}",
            job.Id,
            job.Account.Username);

        await _browserAutomation.RegisterAsync(job.Account, cancellationToken);
    }
}
