using System.Text.Json;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: OmoCaptchaLiveDiagnostics <captcha-image-path>");
    return 2;
}

var settingsPath = Path.Combine(
    Directory.GetCurrentDirectory(),
    "appsettings.json");
if (!File.Exists(settingsPath))
{
    Console.Error.WriteLine($"Settings file was not found: {settingsPath}");
    return 2;
}

using var settingsDocument = JsonDocument.Parse(
    await File.ReadAllTextAsync(settingsPath));
if (!settingsDocument.RootElement.TryGetProperty("OmoCaptcha", out var section))
{
    Console.Error.WriteLine("OmoCaptcha configuration was not found.");
    return 2;
}

var configuredKey = Environment.GetEnvironmentVariable("OMO_API_KEY");
if (string.IsNullOrWhiteSpace(configuredKey))
{
    configuredKey = section.GetProperty("ApiKey").GetString();
}

var options = new OmoCaptchaOptions
{
    Enabled = true,
    ApiKey = configuredKey ?? string.Empty,
    ApiBaseUrl = section.GetProperty("ApiBaseUrl").GetString()
                 ?? "https://api.omocaptcha.com",
    Module = section.GetProperty("Module").GetString() ?? "module_1",
    TimeoutSeconds = section.GetProperty("TimeoutSeconds").GetInt32(),
    InitialPollingDelayMilliseconds = section
        .GetProperty("InitialPollingDelayMilliseconds")
        .GetInt32(),
    MaxPollingDelayMilliseconds = section
        .GetProperty("MaxPollingDelayMilliseconds")
        .GetInt32()
};

var solver = new OmoImageCaptchaSolver(
    new SingleHttpClientFactory(new HttpClient()),
    Options.Create(options),
    NullLogger<OmoImageCaptchaSolver>.Instance);
var imageBase64 = Convert.ToBase64String(
    await File.ReadAllBytesAsync(args[0]));
var result = await solver.SolveAsync(
    imageBase64,
    CancellationToken.None);

if (!result.IsSuccess)
{
    Console.Error.WriteLine($"LIVE FAIL: {result.Message}");
    return 1;
}

Console.WriteLine(
    $"LIVE PASS: solution={result.Text}; taskId={result.TaskId}; elapsedMs={result.Elapsed.TotalMilliseconds:0}");
return 0;

file sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
