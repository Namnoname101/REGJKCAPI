using BAOCODETOOL.Models;

namespace BAOCODETOOL.Services;

public interface IBrowserAutomationService
{
    Task RegisterAsync(AccountModel account, CancellationToken cancellationToken);
}
