namespace BAOCODETOOL.Configuration;

public sealed class AutomationOptions
{
    public const string SectionName = "Automation";

    public int MaxConcurrentJobs { get; set; } = 2;

    public int MaxBrowserThreadsPerJob { get; set; } = 10;

    /// <summary>
    /// Maximum number of selected sites for one account that may run at once.
    /// Set to 1 to retain the old sequential behavior.
    /// </summary>
    public int MaxConcurrentSitesPerAccount { get; set; } = 1;

    /// <summary>
    /// Limits how many Chrome sessions may be created at the same time.
    /// Existing sessions continue to run while another session is starting.
    /// Keeping this lower than the worker count prevents Chrome startup spikes
    /// from starving the rest of the service.
    /// </summary>
    public int MaxConcurrentBrowserStarts { get; set; } = 1;

    /// <summary>
    /// Maximum number of jobs waiting in memory. A bounded queue applies
    /// backpressure to Telegram producers instead of allowing unbounded growth.
    /// </summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>
    /// Pre-launches reusable direct-connection Chrome sessions for browser
    /// fallback workflows. Keep disabled for API-first operation.
    /// </summary>
    public bool EnablePrewarmedBrowserPool { get; set; }

    /// <summary>
    /// Total number of reusable direct-connection Chrome sessions.
    /// This also caps the number of direct registrations executing in browsers
    /// from the pool at one time.
    /// </summary>
    public int PrewarmedBrowserPoolSize { get; set; }

    /// <summary>
    /// Recreates a pooled Chrome after this many leases to contain memory growth
    /// and recover from state that cannot be cleared reliably.
    /// </summary>
    public int BrowserPoolMaxUsesPerSession { get; set; } = 20;

    /// <summary>
    /// Maximum number of browser workflows active at once across the process.
    /// </summary>
    public int MaxActiveBrowserSessions { get; set; } = 2;

    /// <summary>
    /// Prevents starting another browser workflow while total CPU is at or
    /// above this percentage. The first active workflow is always allowed.
    /// </summary>
    public double MaxCpuPercentBeforeThrottling { get; set; } = 75;

    /// <summary>
    /// Enables CPU-aware admission control for browser workflows.
    /// </summary>
    public bool EnableAdaptiveCpuThrottle { get; set; } = true;

    /// <summary>
    /// Minimum time after admitting one browser workflow before another
    /// workflow may be considered. This gives the first renderer enough time
    /// to expose its real CPU cost instead of admitting a burst during a short
    /// idle sample.
    /// </summary>
    public int BrowserAdmissionStabilizationMs { get; set; } = 2000;

    /// <summary>
    /// Consecutive CPU samples below the configured threshold required before
    /// another browser workflow is admitted.
    /// </summary>
    public int CpuLowSamplesRequired { get; set; } = 3;

    /// <summary>
    /// Runs Chrome in headless mode. Keep disabled when a site requires a
    /// visible window or behaves differently when headless.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>
    /// Legacy switch kept for standalone diagnostics. Telegram registration
    /// flows ignore it and always require a proxy.
    /// </summary>
    public bool AllowDirectConnectionForTesting { get; set; }

    /// <summary>
    /// Uses the site's encrypted HTTP registration API directly for supported
    /// sites. Browser fallback is separately opt-in and disabled by default.
    /// </summary>
    public bool EnableDirectHttpRegistration { get; set; } = true;

    /// <summary>
    /// Allows the legacy Selenium path after a direct API attempt cannot
    /// complete. Keep disabled for high-throughput API-only operation.
    /// </summary>
    public bool EnableBrowserFallback { get; set; }
}
