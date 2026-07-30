namespace BAOCODETOOL.Configuration;

public sealed class OmoCaptchaOptions
{
    public const string SectionName = "OmoCaptcha";
    private const string DefaultApiKey =
        "OMO_MLUHQF6ISVZZRSVUHUEWHCWB6SPKKT7KO53E6HIRVZTREAUQCACGZBEX1OTWQB1785351575";

    public bool Enabled { get; set; } = true;

    public string ApiKey { get; set; } = DefaultApiKey;

    public string ApiBaseUrl { get; set; } = "https://api.omocaptcha.com";

    public string Module { get; set; } = "module_1";

    public int TimeoutSeconds { get; set; } = 90;

    public int InitialPollingDelayMilliseconds { get; set; } = 2000;

    public int MaxPollingDelayMilliseconds { get; set; } = 10000;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ApiKey);
}
