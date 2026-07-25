using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace BAOCODETOOL.Services;

public sealed class BrowserAutomationService : IBrowserAutomationService, IDisposable
{
    private readonly AutomationOptions _options;
    private readonly IProfileRepository _profileRepository;
    private readonly ILogger<BrowserAutomationService> _logger;
    private readonly SemaphoreSlim _browserStartupGate;
    private readonly IBrowserSessionPool _browserSessionPool;
    private readonly IBrowserConcurrencyGovernor _browserConcurrencyGovernor;

    public BrowserAutomationService(
        IOptions<AutomationOptions> options,
        IProfileRepository profileRepository,
        IBrowserSessionPool browserSessionPool,
        IBrowserConcurrencyGovernor browserConcurrencyGovernor,
        ILogger<BrowserAutomationService> logger)
    {
        _options = options.Value;
        _profileRepository = profileRepository;
        _browserSessionPool = browserSessionPool;
        _browserConcurrencyGovernor = browserConcurrencyGovernor;
        _logger = logger;
        _browserStartupGate = new SemaphoreSlim(_options.MaxConcurrentBrowserStarts);
    }

    public async Task RegisterAsync(AccountModel account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Starting registration. Account={Account}, Sites={Sites}, SiteConcurrency={SiteConcurrency}, BrowserStartLimit={BrowserStartLimit}, Headless={Headless}",
            account.Username,
            account.SelectedSitesText,
            _options.MaxConcurrentSitesPerAccount,
            _options.MaxConcurrentBrowserStarts,
            _options.Headless);

        try
        {
            var manager = new BotManager();
            await manager.RunMassRegistrationAsync(
                new[] { account },
                _options.MaxBrowserThreadsPerJob,
                maxConcurrentSitesPerAccount: _options.MaxConcurrentSitesPerAccount,
                onSiteCompleted: _profileRepository.Save,
                cancellationToken: cancellationToken,
                browserStartupGate: _browserStartupGate,
                headless: _options.Headless,
                enableDirectHttpRegistration: _options.EnableDirectHttpRegistration,
                browserSessionPool: _browserSessionPool,
                browserConcurrencyGovernor: _browserConcurrencyGovernor);
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation(
                "Registration finished. Account={Account}, ElapsedMs={ElapsedMs}, Status={Status}",
                account.Username,
                stopwatch.ElapsedMilliseconds,
                account.Status);
        }
    }

    public void Dispose()
    {
        _browserStartupGate.Dispose();
    }
}
