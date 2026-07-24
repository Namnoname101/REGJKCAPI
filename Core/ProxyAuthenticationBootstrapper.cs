using OpenQA.Selenium;
using System;
using System.Diagnostics;
using System.Threading;

namespace BAOCODETOOL.Core;

internal static class ProxyAuthenticationBootstrapper
{
    public static bool TryAttach(IWebDriver driver, ProxyConfiguration? proxyConfiguration)
    {
        ArgumentNullException.ThrowIfNull(driver);

        if (proxyConfiguration is null || !proxyConfiguration.RequiresAuthentication)
        {
            return true;
        }

        try
        {
            var network = driver.Manage().Network;

            network.StartMonitoring().GetAwaiter().GetResult();
            network.AddAuthenticationHandler(new NetworkAuthenticationHandler
            {
                UriMatcher = static _ => true,
                Credentials = new PasswordCredentials(proxyConfiguration.Username!, proxyConfiguration.Password!)
            });

            Thread.Sleep(250);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Proxy auth network fallback unavailable: {ex}");
            return false;
        }
    }
}
