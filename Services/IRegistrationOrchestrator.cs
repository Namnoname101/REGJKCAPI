using BAOCODETOOL.Models;

namespace BAOCODETOOL.Services;

public interface IRegistrationOrchestrator
{
    Task ProcessAsync(RegistrationJob job, CancellationToken cancellationToken);
}
