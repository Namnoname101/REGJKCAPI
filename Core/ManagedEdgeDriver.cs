using OpenQA.Selenium.Edge;

namespace BAOCODETOOL.Core;

internal sealed class ManagedEdgeDriver : EdgeDriver
{
    private IDisposable? _cleanupResource;

    public ManagedEdgeDriver(
        EdgeDriverService service,
        EdgeOptions options,
        IDisposable? cleanupResource)
        : base(service, options)
    {
        _cleanupResource = cleanupResource;
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            var cleanupResource = Interlocked.Exchange(ref _cleanupResource, null);
            cleanupResource?.Dispose();
        }
    }
}
