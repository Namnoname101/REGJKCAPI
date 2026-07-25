using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public sealed class AutomationStatusChangedEventArgs : EventArgs
{
    public required AccountModel Account { get; init; }

    public SiteRunModel? SiteRun { get; init; }

    public AccountStatus? Status { get; init; }

    public string? Message { get; init; }
}
