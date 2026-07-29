namespace BAOCODETOOL.Configuration;

public sealed class CaptchaSolverOptions
{
    public const string SectionName = "CaptchaSolver";
    public const string TwoCaptchaProvider = "TwoCaptcha";

    public bool Enabled { get; set; }

    public string Provider { get; set; } = TwoCaptchaProvider;

    public string ApiKey { get; set; } = string.Empty;

    public bool UseRegistrationProxy { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 90;

    public int InitialPollingDelayMilliseconds { get; set; } = 2000;

    public int PollingIntervalMilliseconds { get; set; } = 3000;

    public bool IsTwoCaptchaEnabled =>
        Enabled
        && string.Equals(
            Provider,
            TwoCaptchaProvider,
            StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey);
}
