using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System;
using System.IO;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public static class DriverFactory
{
    public static IWebDriver CreateDriver(AccountModel account)
    {
        TargetSite? siteToOpen = null;

        foreach (TargetSite site in Enum.GetValues<TargetSite>())
        {
            var path = account.GetProfileFolderPath(site);
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length > 0)
            {
                siteToOpen = site;
                break;
            }
        }

        siteToOpen ??= TargetSite.RR88;

        return CreateDriver(account, siteToOpen);
    }

    public static IWebDriver CreateDriver(
        AccountModel account,
        TargetSite? site,
        bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(account);

        var profileFolderPath = site.HasValue
            ? account.GetProfileFolderPath(site.Value)
            : account.ProfileFolderPath;

        return CreateDriver(
            profileFolderPath,
            account.UserAgent,
            account.Proxy,
            headless);
    }

    public static IWebDriver CreateDriver(
        string profileFolderPath,
        string? userAgent,
        string? proxy,
        bool headless = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileFolderPath);
        Directory.CreateDirectory(profileFolderPath);

        if (!ProxyConfigurationParser.TryParse(proxy, out var proxyConfiguration, out var proxyError))
        {
            throw new FormatException(proxyError ?? "Proxy configuration is invalid.");
        }

        var options = CreateChromeOptions(
            profileFolderPath,
            userAgent,
            proxyConfiguration,
            headless,
            out var extensionDir);
        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;

        IDisposable? cleanupResource = null;

        if (!string.IsNullOrWhiteSpace(extensionDir))
        {
            cleanupResource = new ExtensionCleanup(extensionDir);
        }

        if (proxyConfiguration != null && proxyConfiguration.RequiresAuthentication)
        {
            var relay = new LocalAuthenticatedProxyRelay(proxyConfiguration);
            options.AddArgument($"--proxy-server={relay.LocalProxyServer}");

            if (cleanupResource != null)
            {
                var existingCleanup = cleanupResource;
                cleanupResource = new CompositeCleanup(existingCleanup, relay);
            }
            else
            {
                cleanupResource = relay;
            }
        }
        else if (proxyConfiguration != null)
        {
            options.AddArgument($"--proxy-server={proxyConfiguration.ProxyServerArgument}");
        }

        var driver = new ManagedChromeDriver(service, options, cleanupResource: cleanupResource);
        
        try
        {
            // Tối ưu hóa Timeout
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
            // The authenticated bank API normally completes in under two
            // seconds, but a saturated low-end CPU can delay its encrypted
            // promise beyond 15 seconds. A longer script timeout lets the
            // request return a definitive result instead of risking a duplicate
            // visual retry after the server has already accepted it.
            driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
            driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero; // Không dùng implicit wait dài theo lời khuyên của ChatGPT

            // Chặn tải các tài nguyên nặng bằng CDP để cực kỳ nhẹ và chống đơ
            var emptyDict = new System.Collections.Generic.Dictionary<string, object>();
            var blockedDict = new System.Collections.Generic.Dictionary<string, object>
            {
                ["urls"] = new[]
                {
                    "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg", "*.ico",
                    "*.woff", "*.woff2", "*.ttf", "*.otf",
                    "*.mp4", "*.webm", "*.avi", "*.mov", "*.mp3", "*.wav",
                    "*google-analytics.com/*", "*googletagmanager.com/*", "*doubleclick.net/*", "*facebook.net/tr/*"
                }
            };

            driver.ExecuteCdpCommand("Network.enable", emptyDict);
            driver.ExecuteCdpCommand("Network.setBlockedURLs", blockedDict);

            ProxyVerifier.VerifyIfNeeded(driver, proxyConfiguration);
            return driver;
        }
        catch
        {
            driver.Dispose();
            throw;
        }
    }

    private sealed class ExtensionCleanup : IDisposable
    {
        private readonly string? _extensionDir;
        public ExtensionCleanup(string? extensionDir) => _extensionDir = extensionDir;
        public void Dispose() => ProxyAuthExtensionRuntime.Cleanup(_extensionDir);
    }

    private static ChromeOptions CreateChromeOptions(
        string profileFolderPath,
        string? userAgent,
        ProxyConfiguration? proxyConfiguration,
        bool headless,
        out string? extensionDir)
    {
        extensionDir = null;

        var options = new ChromeOptions
        {
            // Registration strategies already wait for their own form/toast
            // selectors. Do not make every subsequent Selenium command wait
            // for unrelated home-page resources to finish loading.
            PageLoadStrategy = PageLoadStrategy.None,
            UnhandledPromptBehavior = UnhandledPromptBehavior.Accept
        };
        
        // Tắt chế độ tàng hình theo yêu cầu của user, hiện cửa sổ nhỏ
        options.AddArgument("--window-size=800,600");

        if (headless)
        {
            options.AddArgument("--headless=new");
        }
        
        // --- CÁC TỐI ƯU HIỆU NĂNG VÀ CHỐNG ĐƠ TỪ CHATGPT ---
        
        // Giảm hoạt động nền
        options.AddArgument("--disable-background-networking");
        options.AddArgument("--disable-background-mode");
        options.AddArgument("--disable-default-apps");
        options.AddArgument("--disable-sync");
        options.AddArgument("--disable-extensions");
        options.AddArgument("--disable-component-update");
        options.AddArgument("--disable-domain-reliability");
        
        // Cho phép Windows bóp băng thông CPU nếu cửa sổ nằm dưới (tiết kiệm 90% CPU)
        options.AddArgument("--disable-features=Translate,MediaRouter,OptimizationHints,site-per-process");
        options.AddArgument("--disable-smooth-scrolling");
        options.AddArgument("--wm-window-animations-disabled");
        options.AddArgument("--disable-ipc-flooding-protection");
        options.AddArgument("--disable-backgrounding-occluded-windows=false");
        options.AddArgument("--blink-settings=imagesEnabled=false");

        // KHÔNG TẮT GPU VÀ WEBGL vì trên Windows việc tắt nó sẽ ép CPU gánh toàn bộ quá trình render web gây CPU 100%
        // options.AddArgument("--disable-gpu"); 
        // options.AddArgument("--disable-software-rasterizer");

        // Giảm tính năng không cần thiết
        options.AddArgument("--disable-notifications");
        options.AddArgument("--disable-popup-blocking");
        options.AddArgument("--disable-print-preview");
        options.AddArgument("--disable-speech-api");
        options.AddArgument("--disable-logging");
        options.AddArgument("--log-level=3");
        options.AddArgument("--silent");
        options.AddArgument("--mute-audio");
        
        // Cho phép Chrome tự động ngủ đông các luồng ngầm để cứu CPU
        // options.AddArgument("--disable-background-timer-throttling");
        // options.AddArgument("--disable-backgrounding-occluded-windows");
        // options.AddArgument("--disable-renderer-backgrounding");
        
        // Ẩn các dấu hiệu Selenium
        options.AddExcludedArgument("enable-automation");
        options.AddArgument("--disable-blink-features=AutomationControlled");

        options.AddArgument($"--user-data-dir={profileFolderPath}");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--ignore-certificate-errors");

        // Không lưu cache lâu dài
        options.AddArgument("--disk-cache-size=1");
        options.AddArgument("--media-cache-size=1");
        
        // Tắt tính năng bằng Preference
        options.AddUserProfilePreference("profile.default_content_setting_values.images", 2);
        options.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);
        options.AddUserProfilePreference("profile.default_content_setting_values.notifications", 2);
        options.AddUserProfilePreference("profile.default_content_setting_values.geolocation", 2);
        options.AddUserProfilePreference("profile.default_content_setting_values.media_stream_mic", 2);
        options.AddUserProfilePreference("profile.default_content_setting_values.media_stream_camera", 2);

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            options.AddArgument($"--user-agent={userAgent.Trim()}");
        }

        if (proxyConfiguration is null)
        {
            return options;
        }

        extensionDir = ProxyAuthExtensionRuntime.CreateOrUpdate(profileFolderPath, proxyConfiguration);
        options.AddArgument($"--load-extension={extensionDir}");

        return options;
    }

    private sealed class CompositeCleanup : IDisposable
    {
        private readonly IDisposable _a;
        private readonly IDisposable _b;

        public CompositeCleanup(IDisposable a, IDisposable b)
        {
            _a = a;
            _b = b;
        }

        public void Dispose()
        {
            _a.Dispose();
            _b.Dispose();
        }
    }
}
