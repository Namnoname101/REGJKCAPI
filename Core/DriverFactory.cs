using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System;
using System.IO;
using VibecodeReg.Models;

namespace VibecodeReg.Core;

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

    public static IWebDriver CreateDriver(AccountModel account, TargetSite? site)
    {
        ArgumentNullException.ThrowIfNull(account);

        var profileFolderPath = site.HasValue
            ? account.GetProfileFolderPath(site.Value)
            : account.ProfileFolderPath;

        Directory.CreateDirectory(profileFolderPath);

        if (!ProxyConfigurationParser.TryParse(account.Proxy, out var proxyConfiguration, out var proxyError))
        {
            throw new FormatException(proxyError ?? "Proxy configuration is invalid.");
        }

        var options = CreateChromeOptions(profileFolderPath, account.UserAgent, proxyConfiguration, out var extensionDir);
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
        out string? extensionDir)
    {
        extensionDir = null;

        var options = new ChromeOptions
        {
            PageLoadStrategy = PageLoadStrategy.Eager
        };
        options.AddArgument($"--user-data-dir={profileFolderPath}");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--disable-popup-blocking");
        options.AddUserProfilePreference("profile.default_content_setting_values.images", 1);
        options.AddUserProfilePreference("profile.managed_default_content_settings.images", 1);

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
