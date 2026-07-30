namespace BAOCODETOOL.Configuration;

public sealed class SePayOptions
{
    public const string SectionName = "SePay";

    public bool Enabled { get; set; }
    public string WebhookSecret { get; set; } = string.Empty;
    public int AllowedClockSkewSeconds { get; set; } = 300;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolder { get; set; } = string.Empty;
    public string TransferPrefix { get; set; } = "NAP";
    public string QrBankCode { get; set; } = string.Empty;
    public string QrTemplate { get; set; } = "compact";
    public int QrExpirationMinutes { get; set; } = 30;
    public long MinimumTopUpAmount { get; set; } = 10_000;
    public long MaximumTopUpAmount { get; set; } = 50_000_000;
    public string GatewayBaseUrl { get; set; } = string.Empty;
    public string GatewayAccessToken { get; set; } = string.Empty;
    public int GatewayPollingSeconds { get; set; } = 5;
}
