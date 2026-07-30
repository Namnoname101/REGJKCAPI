namespace BAOCODETOOL.Configuration;

public sealed class CaptchaSolverOptions
{
    public const string SectionName = "CaptchaSolver";
    public const string TwoCaptchaProvider = "TwoCaptcha";
    public const string BrowserFirstMode = "BrowserFirst";
    public const string NodeFirstMode = "NodeFirst";

    public bool Enabled { get; set; }

    public string Provider { get; set; } = TwoCaptchaProvider;

    public string ApiKey { get; set; } = string.Empty;

    public bool UseRegistrationProxy { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 90;

    public int InitialPollingDelayMilliseconds { get; set; } = 2000;

    public int PollingIntervalMilliseconds { get; set; } = 3000;

    /// <summary>
    /// Chooses which zero-cost local GeeTest strategy is tried first on
    /// sites that reject the lightweight Node helper. BrowserFirst keeps a
    /// headless Chromium session for the registration proxy; NodeFirst keeps
    /// the previous fastest-first behavior.
    /// </summary>
    public string LocalGeeTestMode { get; set; } = NodeFirstMode;

    /// <summary>
    /// Maximum number of proxy-bound headless Chromium sessions kept by the
    /// local GeeTest backend. No browser is created until a token is needed.
    /// </summary>
    public int LocalBrowserMaxSessions { get; set; } = 4;

    /// <summary>
    /// Recycles a local browser session after this many token requests to
    /// bound memory and avoid long-lived browser state.
    /// </summary>
    public int LocalBrowserMaxUsesPerSession { get; set; } = 20;

    /// <summary>
    /// Removes an unused local browser session after this idle period.
    /// </summary>
    public int LocalBrowserIdleSeconds { get; set; } = 120;

    public bool IsTwoCaptchaEnabled =>
        Enabled
        && string.Equals(
            Provider,
            TwoCaptchaProvider,
            StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey);

    public bool UsesLocalBrowserFirst =>
        string.Equals(
            LocalGeeTestMode,
            BrowserFirstMode,
            StringComparison.OrdinalIgnoreCase);
}
