using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.TelegramBot;

public sealed class TelegramBotService : BackgroundService
{
    private readonly BotService _botService;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        BotService botService,
        ILogger<TelegramBotService> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Connecting to Telegram...");
        await _botService.RunAsync(stoppingToken);
    }
}
