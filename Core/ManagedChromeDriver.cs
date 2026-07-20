using OpenQA.Selenium.Chrome;
using System.Threading;

namespace VibecodeReg.Core;

internal sealed class ManagedChromeDriver : ChromeDriver
{
    private IDisposable? _cleanupResource;

    public ManagedChromeDriver(
        ChromeDriverService service,
        ChromeOptions options,
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
