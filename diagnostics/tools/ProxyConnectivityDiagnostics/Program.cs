using BAOCODETOOL.Core;

var proxies = args.Length > 0
    ? args
    : (Environment.GetEnvironmentVariable("VIBECODE_TEST_PROXIES") ?? string.Empty)
        .Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (proxies.Length == 0)
{
    Console.Error.WriteLine("Usage: ProxyConnectivityDiagnostics <proxy> [proxy...]");
    return 2;
}

var failures = 0;
foreach (var proxy in proxies)
{
    if (!ProxyConfigurationParser.TryParse(proxy, out var configuration, out var parseError)
        || configuration is null)
    {
        Console.WriteLine($"FAIL parse proxy=<invalid> reason={parseError ?? "Proxy is required."}");
        failures++;
        continue;
    }

    var result = await ProxyConnectivityVerifier.CheckAsync(configuration);
    Console.WriteLine(
        $"{(result.IsSuccess ? "PASS" : "FAIL")} proxy={configuration.Masked} observedIp={result.ObservedIp ?? "-"} reason={result.Message}");

    if (!result.IsSuccess)
    {
        failures++;
    }
}

return failures == 0 ? 0 : 1;
