namespace BAOCODETOOL.Core;

public interface IBrowserConcurrencyGovernor
{
    IBrowserConcurrencyLease Acquire(CancellationToken cancellationToken);
}

public interface IBrowserConcurrencyLease : IDisposable
{
}
