namespace BAOCODETOOL.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public long AdminChatId { get; set; }
}
