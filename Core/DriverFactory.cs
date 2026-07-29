using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public static class DriverFactory
{
    public static IWebDriver CreateDriver(AccountModel account)
    {
        return CreateDriver(account, TargetSite.RR88);
    }

    public static IWebDriver CreateDriver(
        AccountModel account,
        TargetSite? site,
        bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(account);

        var profileFolderPath = Path.Combine(
            Path.GetTempPath(),
            "BAOCODETOOL",
            "BrowserProfiles",
            Guid.NewGuid().ToString("N"));

        return CreateDriver(
            profileFolderPath,
            account.UserAgent,
            account.Proxy,
            headless,
            site,
            deleteProfileOnDispose: true);
    }

    public static IWebDriver CreateDriver(
        string profileFolderPath,
        string? userAgent,
        string? proxy,
        bool headless = false,
        TargetSite? site = null,
        bool verifyProxy = true,
        bool deleteProfileOnDispose = false,
        bool forceMobileEmulation = false)
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
            headless,
            site,
            forceMobileEmulation);
        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;

        var cleanupResources = new List<IDisposable>();
        if (deleteProfileOnDispose)
        {
            cleanupResources.Add(new TemporaryProfileCleanup(profileFolderPath));
        }

        if (proxyConfiguration != null && proxyConfiguration.RequiresAuthentication)
        {
            var relay = new LocalAuthenticatedProxyRelay(proxyConfiguration);
            options.AddArgument($"--proxy-server={relay.LocalProxyServer}");
            cleanupResources.Add(relay);
        }
        else if (proxyConfiguration != null)
        {
            options.AddArgument($"--proxy-server={proxyConfiguration.ProxyServerArgument}");
        }

        var cleanupResource = cleanupResources.Count switch
        {
            0 => null,
            1 => cleanupResources[0],
            _ => new CleanupBundle(cleanupResources)
        };

        ManagedChromeDriver driver;
        try
        {
            driver = new ManagedChromeDriver(service, options, cleanupResource);
        }
        catch
        {
            cleanupResource?.Dispose();
            throw;
        }
        
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
            var blockedUrls = forceMobileEmulation
                              || site is TargetSite.LLWIN or TargetSite.GG88 or TargetSite.XX88
                ? new[]
                {
                    "*.mp4", "*.webm", "*.avi", "*.mov", "*.mp3", "*.wav",
                    "*google-analytics.com/*", "*googletagmanager.com/*",
                    "*doubleclick.net/*", "*facebook.net/tr/*"
                }
                : new[]
                {
                    "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg", "*.ico",
                    "*.woff", "*.woff2", "*.ttf", "*.otf",
                    "*.mp4", "*.webm", "*.avi", "*.mov", "*.mp3", "*.wav",
                    "*google-analytics.com/*", "*googletagmanager.com/*",
                    "*doubleclick.net/*", "*facebook.net/tr/*"
                };
            var blockedDict = new System.Collections.Generic.Dictionary<string, object>
            {
                ["urls"] = blockedUrls
            };

            driver.ExecuteCdpCommand("Network.enable", emptyDict);
            driver.ExecuteCdpCommand("Network.setBlockedURLs", blockedDict);

            if (forceMobileEmulation
                || site is TargetSite.LLWIN or TargetSite.GG88 or TargetSite.XX88)
            {
                driver.ExecuteCdpCommand(
                    "Emulation.setDeviceMetricsOverride",
                    new Dictionary<string, object>
                    {
                        ["width"] = 390,
                        ["height"] = 844,
                        ["deviceScaleFactor"] = 3,
                        ["mobile"] = true,
                        ["screenWidth"] = 390,
                        ["screenHeight"] = 844,
                        ["positionX"] = 0,
                        ["positionY"] = 0,
                        ["screenOrientation"] = new Dictionary<string, object>
                        {
                            ["type"] = "portraitPrimary",
                            ["angle"] = 0
                        }
                    });
                driver.ExecuteCdpCommand(
                    "Emulation.setTouchEmulationEnabled",
                    new Dictionary<string, object>
                    {
                        ["enabled"] = true,
                        ["maxTouchPoints"] = 5
                    });
                driver.ExecuteCdpCommand(
                    "Network.setUserAgentOverride",
                    new Dictionary<string, object>
                    {
                        ["userAgent"] =
                            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) " +
                            "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 " +
                            "Mobile/15E148 Safari/604.1",
                        ["acceptLanguage"] = "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7",
                        ["platform"] = "iPhone"
                    });
            }

            if (verifyProxy)
            {
                ProxyVerifier.VerifyIfNeeded(driver, proxyConfiguration);
            }
            return driver;
        }
        catch
        {
            driver.Dispose();
            throw;
        }
    }

    private static ChromeOptions CreateChromeOptions(
        string profileFolderPath,
        string? userAgent,
        bool headless,
        TargetSite? site,
        bool forceMobileEmulation)
    {
        var useMobileSite = forceMobileEmulation
                            || site is TargetSite.LLWIN or TargetSite.GG88 or TargetSite.XX88;
        var options = new ChromeOptions
        {
            // Registration strategies already wait for their own form/toast
            // selectors. Do not make every subsequent Selenium command wait
            // for unrelated home-page resources to finish loading.
            PageLoadStrategy = PageLoadStrategy.None,
            UnhandledPromptBehavior = UnhandledPromptBehavior.Accept
        };
        
        // Tắt chế độ tàng hình theo yêu cầu của user, hiện cửa sổ nhỏ
        options.AddArgument(useMobileSite
            ? "--window-size=390,844"
            : "--window-size=800,600");

        // LLWIN still needs a visible interactive image challenge. GG88 and
        // XX88 can acquire their short-lived risk token in mobile headless.
        if (headless && site != TargetSite.LLWIN)
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
        if (!useMobileSite)
        {
            options.AddArgument("--blink-settings=imagesEnabled=false");
        }

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
        if (!useMobileSite)
        {
            options.AddUserProfilePreference("profile.default_content_setting_values.images", 2);
            options.AddUserProfilePreference("profile.managed_default_content_settings.images", 2);
        }
        options.AddUserProfilePreference("profile.default_content_setting_values.notifications", 2);
        options.AddUserProfilePreference("profile.default_content_setting_values.geolocation", 2);
        options.AddUserProfilePreference("profile.default_content_setting_values.media_stream_mic", 2);
        options.AddUserProfilePreference("profile.default_content_setting_values.media_stream_camera", 2);

        if (useMobileSite)
        {
            options.AddArgument(
                "--user-agent=Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) " +
                "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 " +
                "Mobile/15E148 Safari/604.1");
        }
        else if (!string.IsNullOrWhiteSpace(userAgent))
        {
            options.AddArgument($"--user-agent={userAgent.Trim()}");
        }

        return options;
    }

    private sealed class CleanupBundle(IReadOnlyList<IDisposable> resources) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            for (var index = resources.Count - 1; index >= 0; index--)
            {
                try
                {
                    resources[index].Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private sealed class TemporaryProfileCleanup(string profilePath) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var consecutiveMissingChecks = 0;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (Directory.Exists(profilePath))
                    {
                        Directory.Delete(profilePath, recursive: true);
                        consecutiveMissingChecks = 0;
                    }
                    else if (++consecutiveMissingChecks >= 3)
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    consecutiveMissingChecks = 0;
                }
                catch (UnauthorizedAccessException)
                {
                    consecutiveMissingChecks = 0;
                }
                catch
                {
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }
}
