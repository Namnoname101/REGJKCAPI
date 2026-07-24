using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BAOCODETOOL.Core;

internal static class ProxyVerifier
{
    private static readonly Uri[] DefaultVerificationEndpoints =
    [
        new("http://api.ipify.org/"),
        new("http://ifconfig.me/ip"),
        new("http://ipinfo.io/ip")
    ];

    public static void VerifyIfNeeded(IWebDriver driver, ProxyConfiguration? proxyConfiguration)
    {
        ArgumentNullException.ThrowIfNull(driver);

        if (proxyConfiguration is null)
        {
            return;
        }

        var endpoints = GetVerificationEndpoints().ToArray();
        var originalPageLoadTimeout = driver.Manage().Timeouts().PageLoad;

        try
        {
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(8);

            ProxyVerificationFailureKind? lastFailureKind = null;
            foreach (var endpoint in endpoints)
            {
                try
                {
                    driver.Navigate().GoToUrl(endpoint);
                    var bodyText = driver.FindElement(By.TagName("body")).Text.Trim();

                    if (!string.IsNullOrWhiteSpace(bodyText)
                        && !bodyText.Contains("ERR_", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    lastFailureKind = ProxyVerificationFailureKind.ConnectionFailed;
                }
                catch (WebDriverTimeoutException)
                {
                    lastFailureKind = ProxyVerificationFailureKind.ConnectionTimedOut;
                }
                catch (WebDriverException ex)
                {
                    lastFailureKind = ClassifyWebDriverException(ex);
                }
            }

            throw CreateVerificationException(proxyConfiguration, lastFailureKind ?? ProxyVerificationFailureKind.ConnectionFailed);
        }
        finally
        {
            driver.Manage().Timeouts().PageLoad = originalPageLoadTimeout;
        }
    }

    private static IEnumerable<Uri> GetVerificationEndpoints()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("VIBECODE_PROXY_TEST_URL");
        if (Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var configuredUri)
            && configuredUri.Scheme is "http" or "https")
        {
            yield return configuredUri;
        }

        foreach (var endpoint in DefaultVerificationEndpoints)
        {
            yield return endpoint;
        }
    }

    private static ProxyVerificationException CreateVerificationException(
        ProxyConfiguration proxyConfiguration,
        ProxyVerificationFailureKind failureKind)
    {
        var reason = failureKind switch
        {
            ProxyVerificationFailureKind.AuthenticationFailed => "Proxy authentication failed",
            ProxyVerificationFailureKind.ConnectionTimedOut => "Proxy connection timed out",
            _ => "Proxy connection failed"
        };

        return new ProxyVerificationException(
            $"{reason} for {proxyConfiguration.Masked}. Automation was not started to avoid direct-IP fallback.");
    }

    private static ProxyVerificationFailureKind ClassifyWebDriverException(WebDriverException exception)
    {
        var message = exception.Message;

        if (message.Contains("ERR_INVALID_AUTH_CREDENTIALS", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ERR_PROXY_AUTH_UNSUPPORTED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("407", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyVerificationFailureKind.AuthenticationFailed;
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyVerificationFailureKind.ConnectionTimedOut;
        }

        return ProxyVerificationFailureKind.ConnectionFailed;
    }

    private enum ProxyVerificationFailureKind
    {
        ConnectionFailed,
        ConnectionTimedOut,
        AuthenticationFailed
    }
}

public sealed class ProxyVerificationException : Exception
{
    public ProxyVerificationException(string message)
        : base(message)
    {
    }
}
