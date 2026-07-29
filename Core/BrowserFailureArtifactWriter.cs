using System.Text.Json;
using BAOCODETOOL.Models;
using OpenQA.Selenium;

namespace BAOCODETOOL.Core;

internal static class BrowserFailureArtifactWriter
{
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void TryCapture(
        IWebDriver driver,
        AccountModel account,
        SiteRunModel siteRun)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(siteRun);

        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var artifactDirectory = Path.Combine(
                RuntimePaths.DiagnosticsDirectory,
                "browser-failures");
            var stem = $"{timestamp:yyyyMMdd-HHmmssfff}-{account.ProfileId:N}-{siteRun.Site}";

            lock (WriteGate)
            {
                Directory.CreateDirectory(artifactDirectory);

                if (driver is ITakesScreenshot screenshotDriver)
                {
                    screenshotDriver
                        .GetScreenshot()
                        .SaveAsFile(Path.Combine(artifactDirectory, stem + ".png"));
                }

                File.WriteAllText(
                    Path.Combine(artifactDirectory, stem + ".html"),
                    driver.PageSource ?? string.Empty);

                var metadata = new
                {
                    CapturedAtUtc = timestamp,
                    account.ProfileId,
                    Site = siteRun.Site.ToString(),
                    Status = siteRun.Status.ToString(),
                    Message = siteRun.LogMessage,
                    Url = GetSafeUrl(driver),
                    Title = GetSafeTitle(driver)
                };

                File.WriteAllText(
                    Path.Combine(artifactDirectory, stem + ".json"),
                    JsonSerializer.Serialize(metadata, JsonOptions));
            }
        }
        catch
        {
            // Failure diagnostics must never change the automation outcome.
        }
    }

    private static string GetSafeUrl(IWebDriver driver)
    {
        try
        {
            if (!Uri.TryCreate(driver.Url, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            return uri.GetLeftPart(UriPartial.Path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetSafeTitle(IWebDriver driver)
    {
        try
        {
            return driver.Title ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
