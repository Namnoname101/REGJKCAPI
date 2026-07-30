using System.Net;
using BAOCODETOOL.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace BAOCODETOOL.Services;

public sealed class SePayNotificationService
{
    private readonly ITelegramBotClient _bot;
    private readonly TelegramOptions _telegramOptions;
    private readonly ILogger<SePayNotificationService> _logger;

    public SePayNotificationService(
        ITelegramBotClient bot,
        IOptions<TelegramOptions> telegramOptions,
        ILogger<SePayNotificationService> logger)
    {
        _bot = bot;
        _telegramOptions = telegramOptions.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(
        SePayProcessingResult result,
        CancellationToken cancellationToken)
    {
        if (result.Status == "Credited" && result.ChatId.HasValue)
        {
            try
            {
                await _bot.SendMessage(
                    result.ChatId.Value,
                    $"✅ <b>NẠP TIỀN THÀNH CÔNG</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                    $"💵 Đã cộng: <b>{result.Amount:N0} Xu</b>\n" +
                    $"💰 Số dư mới: <b>{result.NewBalance:N0} Xu</b>\n" +
                    $"🔖 Mã giao dịch: <code>SEPAY:{result.TransactionId}</code>",
                    ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not notify ChatId {ChatId} about SePay deposit.",
                    result.ChatId);
            }

            return;
        }

        if (result.Status != "Ignored")
            return;

        try
        {
            await _bot.SendMessage(
                _telegramOptions.AdminChatId,
                $"⚠️ <b>SEPAY CHƯA CỘNG TIỀN</b>\n" +
                $"Mã giao dịch: <code>{result.TransactionId}</code>\n" +
                $"Lý do: {WebUtility.HtmlEncode(result.Message)}",
                ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not notify admin about unmatched SePay transaction {TransactionId}.",
                result.TransactionId);
        }
    }
}
