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
}
