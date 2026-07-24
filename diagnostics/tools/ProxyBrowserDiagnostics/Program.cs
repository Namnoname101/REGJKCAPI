using OpenQA.Selenium;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;

var rawProxy = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("VIBECODE_TEST_PROXY");
var testUrl = args.Skip(1).FirstOrDefault() ?? Environment.GetEnvironmentVariable("VIBECODE_PROXY_TEST_URL") ?? "https://ipinfo.io/ip";
if (string.IsNullOrWhiteSpace(rawProxy))
{
    Console.Error.WriteLine("Usage: ProxyBrowserDiagnostics <proxy> or set VIBECODE_TEST_PROXY.");
    return 2;
}

if (!ProxyConfigurationParser.TryParse(rawProxy, out var proxyConfiguration, out var parseError) || proxyConfiguration is null)
{
    Console.Error.WriteLine(parseError ?? "Proxy configuration is invalid.");
    return 3;
}

var account = new AccountModel
{
    Username = "proxy.smoke",
    Password = "Test@12345",
    PhoneNumber = "0987654321",
    FullName = "Proxy Smoke",
    Level2Password = "123456",
    AccountNumber = "1234567890",
    BankName = "Vietcombank",
    Branch = "Main Branch",
    Proxy = rawProxy.Trim(),
    UserAgent = string.Empty
};

IWebDriver? driver = null;

try
{
    Console.WriteLine($"Proxy: {proxyConfiguration.Masked}");
    driver = DriverFactory.CreateDriver(account);
    Console.WriteLine("Driver created.");

    driver.Navigate().GoToUrl(testUrl);
    var body = driver.FindElement(By.TagName("body")).Text.Trim();

    Console.WriteLine("Page body:");
    Console.WriteLine(body);
    Console.WriteLine("Proxy smoke test passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Proxy smoke test failed.");
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    try
    {
        driver?.Quit();
    }
    catch
    {
    }

    driver?.Dispose();
}

