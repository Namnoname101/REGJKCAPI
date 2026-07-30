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
    private readonly CaptchaSolverOptions _captchaSolverOptions;
    private readonly IGeetestBrowserTokenService _browserGeetestTokenService;
    private readonly IGgGeeGuardTokenService _ggGeeGuardTokenService;
    private readonly IImageCaptchaSolver? _imageCaptchaSolver;
    private readonly ISliderCaptchaSolver? _sliderCaptchaSolver;

    public BrowserAutomationService(
        IOptions<AutomationOptions> options,
        IOptions<CaptchaSolverOptions> captchaSolverOptions,
        IProfileRepository profileRepository,
        IBrowserSessionPool browserSessionPool,
        IBrowserConcurrencyGovernor browserConcurrencyGovernor,
        IGeetestBrowserTokenService browserGeetestTokenService,
        IGgGeeGuardTokenService ggGeeGuardTokenService,
        ILogger<BrowserAutomationService> logger,
        IImageCaptchaSolver? imageCaptchaSolver = null,
        ISliderCaptchaSolver? sliderCaptchaSolver = null)
    {
        _options = options.Value;
        _captchaSolverOptions = captchaSolverOptions.Value;
        _profileRepository = profileRepository;
        _browserSessionPool = browserSessionPool;
        _browserConcurrencyGovernor = browserConcurrencyGovernor;
        _browserGeetestTokenService = browserGeetestTokenService;
        _ggGeeGuardTokenService = ggGeeGuardTokenService;
        _imageCaptchaSolver = imageCaptchaSolver;
        _sliderCaptchaSolver = sliderCaptchaSolver;
        _logger = logger;
        _browserStartupGate = new SemaphoreSlim(_options.MaxConcurrentBrowserStarts);
    }

    public async Task RegisterAsync(AccountModel account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Starting registration. Account={Account}, Sites={Sites}, SiteConcurrency={SiteConcurrency}, BrowserStartLimit={BrowserStartLimit}, Headless={Headless}, DirectApi={DirectApi}, BrowserFallback={BrowserFallback}",
            account.Username,
            account.SelectedSitesText,
            _options.MaxConcurrentSitesPerAccount,
            _options.MaxConcurrentBrowserStarts,
            _options.Headless,
            _options.EnableDirectHttpRegistration,
            _options.EnableBrowserFallback);

        try
        {
            var manager = new BotManager();
            var browserGeetestTokenService = _options.EnableBrowserFallback
                ? _browserGeetestTokenService
                : null;
            await manager.RunMassRegistrationAsync(
                new[] { account },
                _options.MaxBrowserThreadsPerJob,
                maxConcurrentSitesPerAccount: _options.MaxConcurrentSitesPerAccount,
                onSiteCompleted: _profileRepository.Save,
                cancellationToken: cancellationToken,
                browserStartupGate: _browserStartupGate,
                headless: _options.Headless,
                enableDirectHttpRegistration: _options.EnableDirectHttpRegistration,
                enableBrowserFallback: _options.EnableBrowserFallback,
                browserSessionPool: _browserSessionPool,
                browserConcurrencyGovernor: _browserConcurrencyGovernor,
                captchaSolverOptions: _captchaSolverOptions,
                browserGeetestTokenService: browserGeetestTokenService,
                imageCaptchaSolver: _imageCaptchaSolver,
                ggGeeGuardTokenService: _ggGeeGuardTokenService,
                sliderCaptchaSolver: _sliderCaptchaSolver);
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
