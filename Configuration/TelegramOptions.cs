namespace BAOCODETOOL.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public long AdminChatId { get; set; }

    // When supplied, this list is the complete administrator allow-list.
    // AdminChatId remains as a backward-compatible fallback for old settings.
    public List<long> AdminChatIds { get; set; } = new();

    public decimal RegistrationPricePerSite { get; set; } = 200m;
}
