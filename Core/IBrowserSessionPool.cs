using BAOCODETOOL.Models;
using OpenQA.Selenium;

namespace BAOCODETOOL.Core;

public interface IBrowserSessionPool
{
    bool CanServe(AccountModel account, bool headless);

    IBrowserSessionLease Rent(TargetSite site, CancellationToken cancellationToken);
}

public interface IBrowserSessionLease : IDisposable
{
    IWebDriver Driver { get; }
}
