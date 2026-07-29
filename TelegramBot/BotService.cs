using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Data;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = Telegram.Bot.Types.Message;
using Telegram.Bot.Types.ReplyMarkups;

namespace BAOCODETOOL.TelegramBot
{
    // Lưu trạng thái từng bước nhập của user
    class UserSession
    {
        public string Step { get; set; } = "idle"; // idle | s_game | s_name | s_phone | s_bank | s_sotk | s_proxy | confirming | bulk | s_bulk_game | proxy_quantity | proxy_confirming
        public HashSet<string> SelectedGames { get; set; } = new();
        public string FullName { get; set; } = "";
        public string Phone { get; set; } = "";
        public string BankName { get; set; } = "";
        public string SoTk { get; set; } = "";
        public string Proxy { get; set; } = "";
        public int ProxyPackageId { get; set; }
        public int ProxyQuantity { get; set; }
        public decimal ProxyUnitPrice { get; set; }
        // Tin hướng dẫn hiện tại được thay thế ở mỗi bước để cuộc trò chuyện gọn hơn.
        public int GuideMessageId { get; set; } = 0;
    }

    public class BotService
    {
        private const string BrandName = "KJC BOT";
        private const string ProxyPurchaseErrorCode = "PX-001";

        private static readonly string[] RegistrationGames =
        {
            "RR88",
            "MM88",
            "XX88",
            "LLWIN",
            "GG88"
        };

        private readonly ITelegramBotClient _botClient;
        private readonly IJobQueueService _jobQueue;
        private readonly IResultSenderService _resultSender;
        private readonly IProfileRepository _profileRepository;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<BotService> _logger;
        private readonly long _adminChatId;
        private readonly int _maxConcurrentJobs;
        private readonly int _maxProxyPurchaseQuantity;
        private readonly SePayOptions _sePayOptions;

        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private readonly ConcurrentDictionary<long, byte> _activeProxyPurchases = new();
        private readonly HashSet<long> _allowedUsers;
        private static readonly string AllowedUsersFilePath = RuntimePaths.AllowedUsersPath;
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        // ── Helpers ───────────────────────────────────────────────────────────
        private static InlineKeyboardMarkup CancelKeyboard() =>
            new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel"));

        private static InlineKeyboardMarkup MainMenuKeyboard() =>
            new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("➕ Đăng ký 1 tài khoản", "start_add") },
                new[] { InlineKeyboardButton.WithCallbackData("📋 Đăng ký hàng loạt", "start_bulk") },
                new[] { InlineKeyboardButton.WithCallbackData("📊 Theo dõi yêu cầu", "status") },
                new[] { InlineKeyboardButton.WithCallbackData("👤 Tài khoản của tôi", "account_balance") },
                new[] { InlineKeyboardButton.WithCallbackData("🌐 Thuê proxy", "buy_proxy") },
                new[] { InlineKeyboardButton.WithCallbackData("💰 Nạp số dư", "deposit") }
            });

        private static string MainMenuText() =>
            $"🤖 <b>{BrandName}</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━\n" +
            "KJCBOT sẵn sàng hỗ trợ bạn. Vui lòng chọn dịch vụ bên dưới:\n\n" +
            "➕ <b>Đăng ký 1 tài khoản</b> – Nhập thông tin theo từng bước\n" +
            "📋 <b>Đăng ký hàng loạt</b> – Gửi danh sách nhiều tài khoản\n" +
            "📊 <b>Theo dõi yêu cầu</b> – Xem tình trạng xử lý hiện tại\n" +
            "👤 <b>Tài khoản của tôi</b> – Xem số dư và tổng chi tiêu\n" +
            "🌐 <b>Thuê proxy</b> – Chọn gói theo nhà mạng và thời hạn\n" +
            "💰 <b>Nạp số dư</b> – Chuyển khoản và cộng tiền tự động\n";

        private string BuildDepositInstructions(long chatId)
        {
            var transferCode = $"{_sePayOptions.TransferPrefix.Trim().ToUpperInvariant()}{chatId}";
            return
                $"💰 <b>NẠP SỐ DƯ {BrandName}</b>\n" +
                $"━━━━━━━━━━━━━━━━━━━━\n\n" +
                $"🏦 Ngân hàng: <b>{WebUtility.HtmlEncode(_sePayOptions.BankName)}</b>\n" +
                $"🔢 Số tài khoản: <code>{WebUtility.HtmlEncode(_sePayOptions.AccountNumber)}</code>\n" +
                $"👤 Chủ tài khoản: <b>{WebUtility.HtmlEncode(_sePayOptions.AccountHolder)}</b>\n" +
                $"📝 Nội dung chuyển khoản: <code>{transferCode}</code>\n\n" +
                $"⚠️ Vui lòng nhập chính xác nội dung chuyển khoản. Số tiền nhận được sẽ tự động cộng vào số dư KJC Xu của bạn.";
        }

        private async Task ShowProxyPackagesMenuAsync(ITelegramBotClient bot, long chatId, int? messageIdToEdit, string? category, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (string.IsNullOrEmpty(category))
            {
                var activeCategories = await db.ProxyPackages
                    .Where(p =>
                        p.IsActive
                        && p.ProxyType == 0
                        && (p.Category == "VNPT"
                            || p.Category == "Viettel"
                            || p.Category == "FPT"
                            || p.Category == "ALL"))
                    .Select(p => p.Category)
                    .Distinct()
                    .ToListAsync(ct);

                if (activeCategories.Count == 0)
                {
                    var emptyMsg = "⚠️ Hiện chưa có gói proxy nào đang mở bán. Vui lòng quay lại sau hoặc liên hệ bộ phận hỗ trợ KJCBOT.";
                    if (messageIdToEdit.HasValue)
                        await bot.EditMessageText(chatId, messageIdToEdit.Value, emptyMsg, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                    else
                        await bot.SendMessage(chatId, emptyMsg, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                    return;
                }

                var text = $"🌐 <b>PROXY TĨNH VIỆT NAM | {BrandName}</b>\n" +
                           "━━━━━━━━━━━━━━━━━━━━\n" +
                           "Vui lòng chọn <b>nhà mạng</b> bạn muốn sử dụng:\n\n" +
                           "💡 <i>Proxy HTTP/HTTPS được hệ thống kiểm tra kết nối trước khi bàn giao.</i>";

                var buttons = new List<InlineKeyboardButton[]>();
                var row = new List<InlineKeyboardButton>();
                foreach (var cat in activeCategories.OrderBy(c => c == "ALL" ? 1 : 0).ThenBy(c => c))
                {
                    string label = cat == "ALL" ? "🌐 Đa Mạng (ALL)" : $"📶 {cat}";
                    row.Add(InlineKeyboardButton.WithCallbackData(label, $"pkgs_{cat}"));
                    if (row.Count == 2)
                    {
                        buttons.Add(row.ToArray());
                        row.Clear();
                    }
                }
                if (row.Count > 0)
                    buttons.Add(row.ToArray());

                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Hủy / Về menu", "cancel") });
                var markup = new InlineKeyboardMarkup(buttons);
                if (messageIdToEdit.HasValue)
                    await bot.EditMessageText(chatId, messageIdToEdit.Value, text, ParseMode.Html, replyMarkup: markup, cancellationToken: ct);
                else
                    await bot.SendMessage(chatId, text, ParseMode.Html, replyMarkup: markup, cancellationToken: ct);
                return;
            }

            var packages = await db.ProxyPackages
                .Where(p =>
                    p.IsActive
                    && p.ProxyType == 0
                    && p.Category == category
                    && (p.Category == "VNPT"
                        || p.Category == "Viettel"
                        || p.Category == "FPT"
                        || p.Category == "ALL"))
                .OrderBy(p => p.ProxyType)
                .ThenBy(p => p.PeriodDays)
                .ThenBy(p => p.Id)
                .ToListAsync(ct);

            string catDisplay = category == "ALL" ? "VIP ĐA MẠNG (ALL)" : category.ToUpper();
            if (packages.Count == 0)
            {
                var emptyMsg = $"⚠️ Hiện tại nhà mạng <b>{catDisplay}</b> tạm hết gói mở bán. Vui lòng chọn nhà mạng khác.";
                var emptyButtons = new List<InlineKeyboardButton[]>
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🔙 Chọn nhà mạng khác", "pkgs_menu") },
                    new[] { InlineKeyboardButton.WithCallbackData("❌ Hủy / Về menu", "cancel") }
                };
                if (messageIdToEdit.HasValue)
                    await bot.EditMessageText(chatId, messageIdToEdit.Value, emptyMsg, ParseMode.Html, replyMarkup: new InlineKeyboardMarkup(emptyButtons), cancellationToken: ct);
                else
                    await bot.SendMessage(chatId, emptyMsg, ParseMode.Html, replyMarkup: new InlineKeyboardMarkup(emptyButtons), cancellationToken: ct);
                return;
            }

            var subText = $"🌐 <b>DANH SÁCH GÓI PROXY – {catDisplay}</b>\n" +
                          "━━━━━━━━━━━━━━━━━━━━\n" +
                          $"Vui lòng chọn gói và thời hạn thuê phù hợp cho nhà mạng <b>{catDisplay}</b>:\n\n" +
                          "💡 <i>Giá hiển thị là giá thuê trọn gói và chỉ được trừ khi yêu cầu hoàn tất.</i>";

            var subButtons = new List<InlineKeyboardButton[]>();
            foreach (var p in packages)
            {
                string btnText = $"⚡ {p.Name} ({p.SellingPrice:N0} KJC Xu)";
                subButtons.Add(new[] { InlineKeyboardButton.WithCallbackData(btnText, $"buypkg_{p.Id}") });
            }
            subButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Chọn nhà mạng khác", "pkgs_menu") });
            subButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("❌ Hủy / Về menu chính", "cancel") });

            var subMarkup = new InlineKeyboardMarkup(subButtons);
            if (messageIdToEdit.HasValue)
                await bot.EditMessageText(chatId, messageIdToEdit.Value, subText, ParseMode.Html, replyMarkup: subMarkup, cancellationToken: ct);
            else
                await bot.SendMessage(chatId, subText, ParseMode.Html, replyMarkup: subMarkup, cancellationToken: ct);
        }

        private static bool IsPurchasableProxyPackage(ProxyPackage? package) =>
            package is
            {
                IsActive: true,
                ProxyType: 0,
                SellingPrice: > 0
            }
            && (package.Category == "VNPT"
                || package.Category == "Viettel"
                || package.Category == "FPT"
                || package.Category == "ALL");

        private static bool TryCalculateProxyTotal(
            decimal unitPrice,
            int quantity,
            out decimal total)
        {
            try
            {
                total = checked(unitPrice * quantity);
                return total > 0;
            }
            catch (OverflowException)
            {
                total = 0;
                return false;
            }
        }

        private async Task HandleProxyQuantityInputAsync(
            ITelegramBotClient bot,
            long chatId,
            string? username,
            int userMessageId,
            string text,
            UserSession session,
            CancellationToken ct)
        {
            if (!Regex.IsMatch(text, @"^[1-9]\d*$")
                || !int.TryParse(text, out var quantity)
                || quantity > _maxProxyPurchaseQuantity)
            {
                await SendError(
                    bot,
                    chatId,
                    session,
                    $"Số lượng phải là số nguyên từ <b>1</b> đến <b>{_maxProxyPurchaseQuantity:N0}</b>.",
                    ct);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var package = await db.ProxyPackages.FindAsync(
                new object[] { session.ProxyPackageId },
                ct);

            if (!IsPurchasableProxyPackage(package))
            {
                _sessions.TryRemove(chatId, out _);
                await SendReplacingMessageAsync(
                    bot,
                    chatId,
                    session.GuideMessageId,
                    "❌ Gói proxy này không còn mở bán. Vui lòng chọn lại gói.",
                    ParseMode.Html,
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            if (!TryCalculateProxyTotal(package!.SellingPrice, quantity, out var total))
            {
                await SendError(
                    bot,
                    chatId,
                    session,
                    "Không thể tính tổng tiền cho số lượng này. Vui lòng nhập số lượng nhỏ hơn.",
                    ct);
                return;
            }

            var user = await userService.GetUserByChatIdAsync(chatId, username, ct);
            if (user.Balance < total)
            {
                var affordableQuantity = (int)Math.Min(
                    _maxProxyPurchaseQuantity,
                    decimal.Floor(user.Balance / package.SellingPrice));
                var affordableText = affordableQuantity > 0
                    ? $"Với số dư hiện tại, bạn có thể mua tối đa <b>{affordableQuantity:N0}</b> proxy."
                    : "Vui lòng nạp thêm số dư để tiếp tục.";

                await SendError(
                    bot,
                    chatId,
                    session,
                    $"Số dư không đủ cho <b>{quantity:N0}</b> proxy.\n" +
                    $"💰 Cần thanh toán: <b>{total:N0} KJC Xu</b>\n" +
                    $"💳 Số dư hiện tại: <b>{user.Balance:N0} KJC Xu</b>\n\n" +
                    affordableText,
                    ct);
                return;
            }

            session.ProxyQuantity = quantity;
            session.ProxyUnitPrice = package.SellingPrice;
            session.Step = "proxy_confirming";
            await ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                $"🧾 <b>XÁC NHẬN MUA PROXY</b>\n" +
                $"━━━━━━━━━━━━━━━━━━━━\n" +
                $"📦 Gói: <b>{WebUtility.HtmlEncode(package.Name)}</b>\n" +
                $"🔢 Số lượng: <b>{quantity:N0}</b> proxy\n" +
                $"💰 Đơn giá: <b>{package.SellingPrice:N0} KJC Xu</b>\n" +
                $"💵 Tổng thanh toán: <b>{total:N0} KJC Xu</b>\n" +
                $"💳 Số dư hiện tại: <b>{user.Balance:N0} KJC Xu</b>\n\n" +
                "Nhấn <b>Xác nhận mua</b> để tiếp tục.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Xác nhận mua", "confirm_proxy_purchase")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✏️ Nhập lại số lượng", $"buypkg_{package.Id}")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("❌ Hủy / Về menu", "cancel")
                    }
                }),
                ct,
                userMessageId);
        }

        private async Task ExecuteProxyPurchaseAsync(
            ITelegramBotClient bot,
            long chatId,
            string? username,
            int messageId,
            int packageId,
            int quantity,
            decimal confirmedUnitPrice,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var proxyVnService = scope.ServiceProvider.GetRequiredService<ProxyVnService>();
            var package = await db.ProxyPackages.FindAsync(new object[] { packageId }, ct);

            if (!IsPurchasableProxyPackage(package))
            {
                await ReplaceProxyPurchaseMessageAsync(
                    bot,
                    chatId,
                    messageId,
                    "❌ Gói proxy này không còn mở bán. Vui lòng chọn lại gói.",
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            if (package!.SellingPrice != confirmedUnitPrice)
            {
                await ReplaceProxyPurchaseMessageAsync(
                    bot,
                    chatId,
                    messageId,
                    "⚠️ Giá gói proxy vừa được thay đổi. Vui lòng chọn lại gói để xác nhận mức giá mới.",
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            if (!TryCalculateProxyTotal(package.SellingPrice, quantity, out var requestedTotal))
            {
                await ReplaceProxyPurchaseMessageAsync(
                    bot,
                    chatId,
                    messageId,
                    "⚠️ Không thể tính tổng thanh toán. Vui lòng chọn lại số lượng.",
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            var user = await userService.GetUserByChatIdAsync(chatId, username, ct);
            if (user.Balance < requestedTotal)
            {
                await ReplaceProxyPurchaseMessageAsync(
                    bot,
                    chatId,
                    messageId,
                    $"⚠️ <b>SỐ DƯ KHÔNG ĐỦ!</b>\n" +
                    $"━━━━━━━━━━━━━━━━━━━━\n" +
                    $"🔢 Số lượng: <b>{quantity:N0}</b> proxy\n" +
                    $"💰 Cần thanh toán: <b>{requestedTotal:N0} KJC Xu</b>\n" +
                    $"💳 Số dư hiện tại: <b>{user.Balance:N0} KJC Xu</b>",
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            messageId = await ReplaceProxyPurchaseMessageAsync(
                bot,
                chatId,
                messageId,
                $"⏳ <b>{BrandName} đang chuẩn bị {quantity:N0} proxy cho bạn...</b>\n" +
                $"Gói đã chọn: {WebUtility.HtmlEncode(package.Name)}",
                null,
                ct);

            IReadOnlyList<string> proxies;
            try
            {
                var apiKey = _configuration["ProxyVn:ApiKey"] ?? string.Empty;
                proxies = await proxyVnService.BuyProxyPackagesAsync(
                    package,
                    apiKey,
                    quantity,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Proxy purchase failed. ErrorCode={ErrorCode}, ChatId={ChatId}, PackageId={PackageId}, Quantity={Quantity}",
                    ProxyPurchaseErrorCode,
                    chatId,
                    package.Id,
                    quantity);
                await ReplaceProxyPurchaseMessageAsync(
                    bot,
                    chatId,
                    messageId,
                    $"⚠️ <b>Chưa thể hoàn tất yêu cầu</b>\n\n" +
                    $"Số dư của bạn chưa bị trừ. Vui lòng thử lại sau hoặc liên hệ bộ phận hỗ trợ KJCBOT.\n\n" +
                    $"Mã lỗi: <code>{ProxyPurchaseErrorCode}</code>",
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            if (!TryCalculateProxyTotal(package.SellingPrice, proxies.Count, out var chargedTotal))
            {
                _logger.LogError(
                    "Could not calculate proxy charge. ChatId={ChatId}, PackageId={PackageId}, FulfilledQuantity={Quantity}",
                    chatId,
                    package.Id,
                    proxies.Count);
                await ReplaceProxyPurchaseMessageAsync(
                    bot,
                    chatId,
                    messageId,
                    $"⚠️ <b>Chưa thể hoàn tất thanh toán</b>\n\n" +
                    $"Vui lòng liên hệ bộ phận hỗ trợ KJCBOT.\n\n" +
                    $"Mã lỗi: <code>{ProxyPurchaseErrorCode}</code>",
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            var transactionReference =
                $"PX-{package.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            var deducted = await userService.DeductBalanceAsync(
                chatId,
                chargedTotal,
                $"Thuê {proxies.Count:N0} proxy - {package.Name}",
                transactionReference,
                ct);
            if (!deducted)
            {
                _logger.LogError(
                    "Proxy purchase completed but balance deduction failed. ErrorCode={ErrorCode}, ChatId={ChatId}, PackageId={PackageId}, Quantity={Quantity}, Amount={Amount}",
                    ProxyPurchaseErrorCode,
                    chatId,
                    package.Id,
                    proxies.Count,
                    chargedTotal);
                await ReplaceProxyPurchaseMessageAsync(
                    bot,
                    chatId,
                    messageId,
                    $"⚠️ <b>Chưa thể hoàn tất thanh toán</b>\n\n" +
                    $"Vui lòng liên hệ bộ phận hỗ trợ KJCBOT.\n\n" +
                    $"Mã lỗi: <code>{ProxyPurchaseErrorCode}</code>",
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            var updatedUser = await userService.GetUserByChatIdAsync(chatId, null, ct);
            var isPartial = proxies.Count < quantity;
            var resultTitle = isPartial
                ? "⚠️ <b>ĐÃ MUA MỘT PHẦN SỐ LƯỢNG!</b>"
                : "✅ <b>THUÊ PROXY THÀNH CÔNG!</b>";
            var partialText = isPartial
                ? $"\n📌 Yêu cầu: <b>{quantity:N0}</b> — Đã nhận: <b>{proxies.Count:N0}</b>\n" +
                  "Hệ thống chỉ tính tiền số proxy đã kiểm tra và bàn giao được."
                : $"\n🔢 Số lượng: <b>{proxies.Count:N0}</b> proxy";

            await ReplaceProxyPurchaseMessageAsync(
                bot,
                chatId,
                messageId,
                $"{resultTitle}\n" +
                $"━━━━━━━━━━━━━━━━━━━━\n" +
                $"📦 Gói proxy: <b>{WebUtility.HtmlEncode(package.Name)}</b> ({package.PeriodDays} ngày)" +
                $"{partialText}\n" +
                $"💰 Đã thanh toán: <b>{chargedTotal:N0} KJC Xu</b>\n" +
                $"💳 Số dư còn lại: <b>{updatedUser.Balance:N0} KJC Xu</b>\n\n" +
                "Danh sách proxy được gửi ngay bên dưới.",
                null,
                ct);

            var proxyMessages = BuildProxyListMessages(proxies);
            foreach (var proxyMessage in proxyMessages)
            {
                await bot.SendMessage(
                    chatId,
                    proxyMessage,
                    ParseMode.Html,
                    cancellationToken: ct);
            }

            // Keep the delivered proxies immutable. The menu lives in a separate
            // message so navigating elsewhere can never replace the credentials.
            await bot.SendMessage(
                chatId,
                "📌 <i>Danh sách proxy ở trên được giữ lại để bạn sao chép khi cần.</i>",
                ParseMode.Html,
                replyMarkup: MainMenuKeyboard(),
                cancellationToken: ct);
        }

        private async Task<int> ReplaceProxyPurchaseMessageAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            string text,
            InlineKeyboardMarkup? replyMarkup,
            CancellationToken ct)
        {
            if (messageId > 0)
            {
                await bot.EditMessageText(
                    chatId,
                    messageId,
                    text,
                    ParseMode.Html,
                    replyMarkup: replyMarkup,
                    cancellationToken: ct);
                return messageId;
            }

            var sent = await bot.SendMessage(
                chatId,
                text,
                ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: ct);
            return sent.MessageId;
        }

        private static IReadOnlyList<string> BuildProxyListMessages(
            IReadOnlyList<string> proxies)
        {
            const int maxBodyLength = 3200;
            var messages = new List<string>();
            var body = new StringBuilder();
            var firstIndex = 1;

            for (var index = 0; index < proxies.Count; index++)
            {
                var line = $"{index + 1}. {WebUtility.HtmlEncode(proxies[index])}";
                if (body.Length > 0
                    && body.Length + Environment.NewLine.Length + line.Length > maxBodyLength)
                {
                    messages.Add(
                        $"🌐 <b>PROXY {firstIndex:N0}–{index:N0}/{proxies.Count:N0}</b>\n" +
                        $"<code>{body}</code>");
                    body.Clear();
                    firstIndex = index + 1;
                }

                if (body.Length > 0)
                {
                    body.AppendLine();
                }
                body.Append(line);
            }

            if (body.Length > 0)
            {
                messages.Add(
                    $"🌐 <b>PROXY {firstIndex:N0}–{proxies.Count:N0}/{proxies.Count:N0}</b>\n" +
                    $"<code>{body}</code>");
            }

            return messages;
        }

        private static InlineKeyboardMarkup BuildGameSelectionKeyboard(HashSet<string> selectedGames, bool isBulk)
        {
            string pfx = isBulk ? "bgame_" : "game_";
            bool allSelected = RegistrationGames.All(selectedGames.Contains);
            string textAll = allSelected ? "✅ Tất cả" : "☑️ Chọn tất cả";
            string textRR88 = selectedGames.Contains("RR88") ? "✅ RR88" : "RR88";
            string textMM88 = selectedGames.Contains("MM88") ? "✅ MM88" : "MM88";
            string textXX88 = selectedGames.Contains("XX88") ? "✅ XX88" : "XX88";

            string textLLWIN = selectedGames.Contains("LLWIN") ? "✅ LLWIN" : "LLWIN";
            string textGG88 = selectedGames.Contains("GG88") ? "✅ GG88" : "GG88";

            return new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData(textAll, $"toggle_{pfx}ALL") },
                new[] { InlineKeyboardButton.WithCallbackData(textRR88, $"toggle_{pfx}RR88"), InlineKeyboardButton.WithCallbackData(textMM88, $"toggle_{pfx}MM88"), InlineKeyboardButton.WithCallbackData(textXX88, $"toggle_{pfx}XX88") },
                new[] { InlineKeyboardButton.WithCallbackData(textLLWIN, $"toggle_{pfx}LLWIN"), InlineKeyboardButton.WithCallbackData(textGG88, $"toggle_{pfx}GG88") },
                new[] { InlineKeyboardButton.WithCallbackData("➡️ Tiếp tục", $"done_{pfx}") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel") }
            });
        }

        private static string FormatSelectedGames(IReadOnlySet<string> selectedGames) =>
            RegistrationGames.All(selectedGames.Contains)
                ? "Tất cả"
                : string.Join(", ", RegistrationGames.Where(selectedGames.Contains));

        private UserSession GetSession(long chatId) =>
            _sessions.GetOrAdd(chatId, _ => new UserSession());

        private async Task DeleteMessageSafelyAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            if (messageId <= 0)
                return;

            try
            {
                await bot.DeleteMessage(chatId, messageId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiRequestException exception)
            {
                // Message cleanup is best-effort (Telegram may reject old/already deleted messages).
                _logger.LogDebug(
                    exception,
                    "Could not clean up Telegram message. ChatId={ChatId}, MessageId={MessageId}",
                    chatId,
                    messageId);
            }
        }

        private async Task<Message> SendReplacingMessageAsync(
            ITelegramBotClient bot,
            long chatId,
            int previousMessageId,
            string text,
            ParseMode parseMode,
            InlineKeyboardMarkup? replyMarkup,
            CancellationToken ct,
            int userMessageId = 0)
        {
            var sent = await bot.SendMessage(
                chatId,
                text,
                parseMode,
                replyMarkup: replyMarkup,
                cancellationToken: ct);

            // This helper is only for transient bot prompts. Never pass a receipt,
            // registration result, proxy delivery, or other user-facing record here.
            await DeleteMessageSafelyAsync(bot, chatId, previousMessageId, ct);
            if (userMessageId != previousMessageId)
                await DeleteMessageSafelyAsync(bot, chatId, userMessageId, ct);

            return sent;
        }

        private async Task<Message> ReplaceSessionPromptAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            string text,
            ParseMode parseMode,
            InlineKeyboardMarkup? replyMarkup,
            CancellationToken ct,
            int userMessageId = 0)
        {
            var sent = await SendReplacingMessageAsync(
                bot,
                chatId,
                session.GuideMessageId,
                text,
                parseMode,
                replyMarkup,
                ct,
                userMessageId);

            session.GuideMessageId = sent.MessageId;
            return sent;
        }

        // Navigation deliberately opens a fresh screen. A callback can originate
        // from a completion/receipt message, which must remain available to copy.
        private Task<Message> SendNavigationScreenAsync(
            ITelegramBotClient bot,
            long chatId,
            string text,
            ParseMode parseMode,
            InlineKeyboardMarkup replyMarkup,
            CancellationToken ct) =>
            bot.SendMessage(
                chatId,
                text,
                parseMode,
                replyMarkup: replyMarkup,
                cancellationToken: ct);

        private static void ApplySelectedSites(AccountModel account, IReadOnlySet<string> selectedGames)
        {
            foreach (var siteRun in account.SiteRuns)
            {
                siteRun.IsSelected = selectedGames.Count == 0
                    ? siteRun.Site is TargetSite.RR88
                        or TargetSite.MM88
                        or TargetSite.XX88
                        or TargetSite.LLWIN
                        or TargetSite.GG88
                    : selectedGames.Contains(siteRun.Site.ToString());
            }
        }

        private static bool TryNormalizeRequiredProxyInput(
            string? rawProxy,
            out string normalizedProxy,
            out string error)
        {
            normalizedProxy = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(rawProxy))
            {
                error = "Vui lòng nhập proxy để tiếp tục.";
                return false;
            }

            var value = rawProxy.Trim();
            if (!ProxyConfigurationParser.TryParse(value, out var proxyConfiguration, out _)
                || proxyConfiguration is null)
            {
                error = "Proxy chưa đúng định dạng hoặc chứa thông tin không hợp lệ.";
                return false;
            }

            if (proxyConfiguration.IsSocks)
            {
                error = "Hiện tại KJCBOT chỉ hỗ trợ proxy HTTP/HTTPS cho dịch vụ đăng ký.";
                return false;
            }

            normalizedProxy = proxyConfiguration.RequiresAuthentication
                ? $"{proxyConfiguration.Host}:{proxyConfiguration.Port}:{proxyConfiguration.Username}:{proxyConfiguration.Password}"
                : proxyConfiguration.Server;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        public BotService(
            ITelegramBotClient botClient,
            IJobQueueService jobQueue,
            IResultSenderService resultSender,
            IProfileRepository profileRepository,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            IOptions<TelegramOptions> telegramOptions,
            IOptions<AutomationOptions> automationOptions,
            IOptions<SePayOptions> sePayOptions,
            ILogger<BotService> logger)
        {
            _botClient = botClient;
            _jobQueue = jobQueue;
            _resultSender = resultSender;
            _profileRepository = profileRepository;
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
            _adminChatId = telegramOptions.Value.AdminChatId;
            _maxConcurrentJobs = automationOptions.Value.MaxConcurrentJobs;
            _maxProxyPurchaseQuantity = Math.Max(
                1,
                configuration.GetValue<int?>("ProxyVn:MaxPurchaseQuantity") ?? 100);
            _sePayOptions = sePayOptions.Value;
            _allowedUsers = LoadAllowedUsers();
        }

        private HashSet<long> LoadAllowedUsers()
        {
            var set = new HashSet<long> { _adminChatId };
            try
            {
                if (File.Exists(AllowedUsersFilePath))
                {
                    var json = File.ReadAllText(AllowedUsersFilePath);
                    var ids = JsonSerializer.Deserialize<List<long>>(json, JsonOpts);
                    if (ids != null)
                        foreach (var id in ids)
                            set.Add(id);
                }
            }
            catch { }
            return set;
        }

        private void SaveAllowedUsers()
        {
            try
            {
                var json = JsonSerializer.Serialize(_allowedUsers.ToList(), JsonOpts);
                File.WriteAllText(AllowedUsersFilePath, json);
            }
            catch { }
        }

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            Telegram.Bot.Types.User bot;
            var retryDelay = TimeSpan.FromSeconds(2);
            while (true)
            {
                try
                {
                    bot = await _botClient.GetMe(stoppingToken);
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Telegram connection failed; retrying in {RetrySeconds:0.#} seconds.",
                        retryDelay.TotalSeconds);

                    await Task.Delay(retryDelay, stoppingToken);
                    retryDelay = TimeSpan.FromSeconds(
                        Math.Min(30, Math.Max(2, retryDelay.TotalSeconds * 2)));
                }
            }

            try
            {
                // This host uses long polling. A webhook left over from an
                // older deployment prevents getUpdates from receiving any
                // messages, so clear it without discarding the backlog.
                await _botClient.DeleteWebhook(
                    dropPendingUpdates: false,
                    cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not clear the Telegram webhook before polling.");
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                foreach (var uid in _allowedUsers)
                {
                    await userService.GetUserByChatIdAsync(uid, null, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync allowed users to database.");
            }

            _logger.LogInformation("Telegram connected. Bot={BotUsername}", bot.Username);

            var pollingRetryDelay = TimeSpan.FromSeconds(2);
            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Await the receiver instead of fire-and-forget. If the
                    // polling loop unexpectedly ends, this service remains in
                    // control and reconnects rather than silently leaving the
                    // host alive with an offline bot.
                    await _botClient.ReceiveAsync(
                        HandleUpdateAsync,
                        HandlePollingErrorAsync,
                        receiverOptions,
                        stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Telegram polling stopped unexpectedly; reconnecting.");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Telegram polling ended; reconnecting.");
                }

                try
                {
                    await Task.Delay(pollingRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                pollingRetryDelay = TimeSpan.FromSeconds(
                    Math.Min(30, Math.Max(2, pollingRetryDelay.TotalSeconds * 2)));
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            try
            {
                await HandleUpdateCoreAsync(bot, update, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                // Telegram invokes update handlers concurrently. Isolate a
                // malformed update or transient API/queue exception so one
                // user cannot terminate the long-polling callback pipeline.
                _logger.LogError(
                    exception,
                    "Unhandled Telegram update. UpdateId={UpdateId}, Type={UpdateType}",
                    update.Id,
                    update.Type);
            }
        }

        private async Task HandleUpdateCoreAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            long updateChatId = 0;
            string? updateUsername = null;
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                updateChatId = update.CallbackQuery.Message?.Chat.Id ?? 0;
                updateUsername = update.CallbackQuery.From?.Username;
            }
            else if (update.Type == UpdateType.Message && update.Message != null)
            {
                updateChatId = update.Message.Chat.Id;
                updateUsername = update.Message.From?.Username;
            }

            if (updateChatId != 0 && (_allowedUsers.Contains(updateChatId) || updateChatId == _adminChatId))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                    await userService.GetUserByChatIdAsync(updateChatId, updateUsername, ct);
                }
                catch { }
            }

            // ── Callback queries ─────────────────────────────────────────────
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } cb)
            {
                var cbChat = cb.Message?.Chat.Id ?? 0;
                if (cbChat == 0) return;

                // Admin approve / reject
                if (cb.Data?.StartsWith("approve_") == true && cbChat == _adminChatId)
                {
                    if (long.TryParse(cb.Data["approve_".Length..], out var uid))
                    {
                        _allowedUsers.Add(uid);
                        SaveAllowedUsers();
                        try
                        {
                            using (var scope = _scopeFactory.CreateScope())
                            {
                                var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                                await userService.GetUserByChatIdAsync(uid, null, ct);
                            }
                        }
                        catch { }
                        try
                        {
                            await bot.EditMessageText(cbChat, cb.Message!.MessageId,
                                $"✅ Đã cấp quyền cho ID: <code>{uid}</code>", ParseMode.Html, cancellationToken: ct);
                            await bot.SendMessage(uid,
                                $"🎉 <b>Yêu cầu truy cập {BrandName} đã được duyệt.</b>\n\nChọn dịch vụ bên dưới để bắt đầu:",
                                ParseMode.Html, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                        }
                        catch { }
                    }
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    return;
                }

                if (cb.Data?.StartsWith("reject_") == true && cbChat == _adminChatId)
                {
                    if (long.TryParse(cb.Data["reject_".Length..], out var uid))
                    {
                        _allowedUsers.Remove(uid);
                        SaveAllowedUsers();
                        try
                        {
                            await bot.EditMessageText(cbChat, cb.Message!.MessageId,
                                $"❌ Đã từ chối ID: <code>{uid}</code>", ParseMode.Html, cancellationToken: ct);
                            await bot.SendMessage(uid, "⛔ Yêu cầu của bạn đã bị từ chối.", cancellationToken: ct);
                        }
                        catch { }
                    }
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    return;
                }

                // Admin deposit approval / rejection
                if (cb.Data?.StartsWith("app_") == true || cb.Data?.StartsWith("rej_") == true)
                {
                    if (cb.From?.Id.ToString() != _adminChatId.ToString())
                    {
                        await bot.AnswerCallbackQuery(cb.Id, "⛔ Bạn không có quyền Admin!", showAlert: true, cancellationToken: ct);
                        return;
                    }

                    if (cb.Data.StartsWith("app_"))
                    {
                        var parts = cb.Data.Split('_');
                        if (parts.Length >= 3 && decimal.TryParse(parts[1], out var amount) && long.TryParse(parts[2], out var guestChatId))
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                            await userService.AddBalanceAsync(guestChatId, amount, "Nạp tiền qua Telegram", null, ct);

                            try
                            {
                                await bot.SendMessage(guestChatId, $"✅ <b>Nạp số dư thành công</b>\nTài khoản của bạn đã được cộng <b>{amount:N0} KJC Xu</b>.", ParseMode.Html, cancellationToken: ct);
                            }
                            catch { }

                            try
                            {
                                await bot.EditMessageCaption(cbChat, cb.Message!.MessageId, $"Đã duyệt {amount:N0} cho {guestChatId}", replyMarkup: null, cancellationToken: ct);
                            }
                            catch
                            {
                                try
                                {
                                    await bot.EditMessageReplyMarkup(cbChat, cb.Message!.MessageId, replyMarkup: null, cancellationToken: ct);
                                    await bot.SendMessage(cbChat, $"Đã duyệt {amount:N0} cho {guestChatId}", cancellationToken: ct);
                                }
                                catch { }
                            }
                        }
                    }
                    else if (cb.Data.StartsWith("rej_"))
                    {
                        var parts = cb.Data.Split('_');
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var guestChatId))
                        {
                            try
                            {
                                await bot.SendMessage(guestChatId, "❌ Biên lai của bạn bị từ chối.", cancellationToken: ct);
                            }
                            catch { }

                            try
                            {
                                await bot.EditMessageCaption(cbChat, cb.Message!.MessageId, $"Đã từ chối {guestChatId}", replyMarkup: null, cancellationToken: ct);
                            }
                            catch
                            {
                                try
                                {
                                    await bot.EditMessageReplyMarkup(cbChat, cb.Message!.MessageId, replyMarkup: null, cancellationToken: ct);
                                    await bot.SendMessage(cbChat, $"Đã từ chối {guestChatId}", cancellationToken: ct);
                                }
                                catch { }
                            }
                        }
                    }

                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    return;
                }

                // Chưa được duyệt
                if (!_allowedUsers.Contains(cbChat))
                {
                    await bot.AnswerCallbackQuery(cb.Id, "⛔ Bạn chưa có quyền!", showAlert: true, cancellationToken: ct);
                    return;
                }

                if (cb.Data?.StartsWith("toggle_game_") == true || cb.Data?.StartsWith("toggle_bgame_") == true)
                {
                    if (_sessions.TryGetValue(cbChat, out var sess))
                    {
                        bool isBulk = cb.Data.StartsWith("toggle_bgame_");
                        string game = cb.Data.Substring(cb.Data.LastIndexOf('_') + 1);
                        if (game == "ALL")
                        {
                            bool allSelected = RegistrationGames.All(sess.SelectedGames.Contains);
                            if (allSelected)
                                sess.SelectedGames.Clear();
                            else
                                sess.SelectedGames.UnionWith(RegistrationGames);
                        }
                        else if (RegistrationGames.Contains(game))
                        {
                            if (sess.SelectedGames.Contains(game)) sess.SelectedGames.Remove(game);
                            else sess.SelectedGames.Add(game);
                        }

                        await bot.EditMessageReplyMarkup(cbChat, cb.Message!.MessageId, replyMarkup: BuildGameSelectionKeyboard(sess.SelectedGames, isBulk), cancellationToken: ct);
                    }
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    return;
                }

                if (cb.Data == "done_game_" || cb.Data == "done_bgame_")
                {
                    if (_sessions.TryGetValue(cbChat, out var sess))
                    {
                        if (sess.SelectedGames.Count == 0)
                        {
                            await bot.AnswerCallbackQuery(cb.Id, "⚠️ Vui lòng chọn ít nhất 1 nhà cái!", showAlert: true, cancellationToken: ct);
                            return;
                        }

                        bool isBulk = cb.Data == "done_bgame_";
                        string gameStr = FormatSelectedGames(sess.SelectedGames);

                        if (isBulk)
                        {
                            sess.Step = "bulk";
                            const string bulkFormat = "HOTEN|TENNGANHANG|SDT|SOTK|PROXY";
                            const string bulkExample =
                                "<code>Nguyen Van A|Vietcombank|0987654321|1234567890|1.2.3.4:8080:u:p</code>\n" +
                                "<code>Tran Thi B|Techcombank|0912345678|9876543210|5.6.7.8:3128:u:p</code>";

                            await bot.EditMessageText(cbChat, cb.Message!.MessageId,
                                $"📋 <b>NHẬP HÀNG LOẠT ({gameStr})</b>\n" +
                                "━━━━━━━━━━━━━━━━━━━━\n" +
                                "Mỗi dòng 1 tài khoản theo định dạng:\n" +
                                $"<code>{bulkFormat}</code>\n\n" +
                                "📌 <b>Ví dụ:</b>\n" +
                                bulkExample + "\n\n" +
                                "⚠️ <b>Lưu ý:</b> Mỗi dòng là 1 tài khoản, dán bao nhiêu dòng cũng được.",
                                ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                        }
                        else
                        {
                            sess.Step = "s_name";
                            await bot.EditMessageText(cbChat, cb.Message!.MessageId,
                                $"📝 <b>THÊM TÀI KHOẢN MỚI ({gameStr})</b>\n" +
                                "━━━━━━━━━━━━━━━━━━━━\n\n" +
                                "Bước <b>2/6</b> — Nhập <b>Họ và Tên</b> đầy đủ:\n\n" +
                                "<i>VD: Nguyen Van A</i>",
                                ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                        }
                    }
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    return;
                }

                if (cb.Data?.StartsWith("pkgs_") == true)
                {
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    string cat = cb.Data["pkgs_".Length..];
                    await ShowProxyPackagesMenuAsync(bot, cbChat, cb.Message!.MessageId, cat == "menu" ? null : cat, ct);
                    return;
                }

                if (cb.Data?.StartsWith("buypkg_") == true)
                {
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    if (int.TryParse(cb.Data["buypkg_".Length..], out var pkgId))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

                        var pkg = await db.ProxyPackages.FindAsync(new object[] { pkgId }, ct);
                        if (!IsPurchasableProxyPackage(pkg))
                        {
                            await bot.SendMessage(cbChat, "❌ Gói proxy này không tồn tại hoặc đã ngừng bán!", cancellationToken: ct);
                            return;
                        }

                        var user = await userService.GetUserByChatIdAsync(cbChat, cb.From?.Username, ct);
                        var purchaseSession = new UserSession
                        {
                            Step = "proxy_quantity",
                            ProxyPackageId = pkg!.Id,
                            ProxyUnitPrice = pkg.SellingPrice,
                            GuideMessageId = cb.Message?.MessageId ?? 0
                        };
                        _sessions[cbChat] = purchaseSession;

                        await ReplaceSessionPromptAsync(
                            bot,
                            cbChat,
                            purchaseSession,
                            $"🔢 <b>NHẬP SỐ LƯỢNG PROXY</b>\n" +
                            $"━━━━━━━━━━━━━━━━━━━━\n" +
                            $"📦 Gói: <b>{WebUtility.HtmlEncode(pkg.Name)}</b>\n" +
                            $"💰 Đơn giá: <b>{pkg.SellingPrice:N0} KJC Xu/proxy</b>\n" +
                            $"💳 Số dư: <b>{user.Balance:N0} KJC Xu</b>\n\n" +
                            $"Vui lòng nhập số lượng muốn mua (từ <b>1</b> đến <b>{_maxProxyPurchaseQuantity:N0}</b>):",
                            ParseMode.Html,
                            CancelKeyboard(),
                            ct);
                    }
                    return;
                }

                if (cb.Data == "confirm_proxy_purchase")
                {
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    if (!_sessions.TryGetValue(cbChat, out var purchaseSession)
                        || purchaseSession.Step != "proxy_confirming"
                        || purchaseSession.ProxyPackageId <= 0
                        || purchaseSession.ProxyQuantity <= 0
                        || cb.Message?.MessageId != purchaseSession.GuideMessageId)
                    {
                        await bot.SendMessage(
                            cbChat,
                            "⚠️ Yêu cầu mua proxy đã hết hiệu lực. Vui lòng chọn lại gói.",
                            replyMarkup: MainMenuKeyboard(),
                            cancellationToken: ct);
                        return;
                    }

                    if (!_activeProxyPurchases.TryAdd(cbChat, 0))
                    {
                        await bot.SendMessage(
                            cbChat,
                            "⏳ Yêu cầu mua proxy của bạn đang được xử lý.",
                            cancellationToken: ct);
                        return;
                    }

                    _sessions.TryRemove(cbChat, out _);
                    try
                    {
                        await ExecuteProxyPurchaseAsync(
                            bot,
                            cbChat,
                            cb.From?.Username,
                            cb.Message?.MessageId ?? purchaseSession.GuideMessageId,
                            purchaseSession.ProxyPackageId,
                            purchaseSession.ProxyQuantity,
                            purchaseSession.ProxyUnitPrice,
                            ct);
                    }
                    finally
                    {
                        _activeProxyPurchases.TryRemove(cbChat, out _);
                    }
                    return;
                }

                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);

                switch (cb.Data)
                {
                    case "hide_result":
                        await DeleteMessageSafelyAsync(bot, cbChat, cb.Message?.MessageId ?? 0, ct);
                        break;

                    case "cancel":
                        _sessions.TryRemove(cbChat, out var cancelledSession);
                        var messageToDelete = cb.Message?.MessageId
                            ?? cancelledSession?.GuideMessageId
                            ?? 0;
                        await SendReplacingMessageAsync(
                            bot,
                            cbChat,
                            messageToDelete,
                            MainMenuText(),
                            ParseMode.Html,
                            MainMenuKeyboard(),
                            ct);
                        break;

                    case "start_add":
                    if (_jobQueue.HasActiveJobs(cbChat))
                    {
                        await bot.AnswerCallbackQuery(cb.Id, "⏳ Hệ thống đang chạy! Vui lòng đợi hoàn thành trước.", showAlert: true, cancellationToken: ct);
                        return;
                    }
                    await StartAddFlow(bot, cbChat, ct);
                    break;

                    case "status":
                        var queueStatus = _jobQueue.GetSnapshot();
                        await SendNavigationScreenAsync(bot, cbChat,
                            $"📊 <b>TRẠNG THÁI XỬ LÝ | {BrandName}</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                            $"🔄 Đang xử lý: <b>{queueStatus.Processing}/{_maxConcurrentJobs}</b> yêu cầu\n" +
                            $"⏳ Đang chờ: <b>{queueStatus.Pending}</b> yêu cầu\n" +
                            $"✅ Năng lực còn trống: <b>{Math.Max(0, _maxConcurrentJobs - queueStatus.Processing)}/{_maxConcurrentJobs}</b>",
                            ParseMode.Html, MainMenuKeyboard(), ct);
                        break;

                    case "confirm_submit":
                        if (_sessions.TryGetValue(cbChat, out var sess) && sess.Step == "confirming")
                            await SubmitAccount(bot, cbChat, sess, ct);
                        break;

                case "start_bulk":
                    if (_jobQueue.HasActiveJobs(cbChat))
                    {
                        await bot.AnswerCallbackQuery(cb.Id, "⏳ Hệ thống đang chạy! Vui lòng đợi hoàn thành trước.", showAlert: true, cancellationToken: ct);
                        return;
                    }
                    var bulkSess = new UserSession
                    {
                        Step = "s_bulk_game"
                    };
                    _sessions[cbChat] = bulkSess;
                    await ReplaceSessionPromptAsync(bot, cbChat, bulkSess,
                        "📋 <b>NHẬP HÀNG LOẠT</b>\n" +
                        "━━━━━━━━━━━━━━━━━━━━\n" +
                        "Chọn <b>Nhà cái</b> để đăng ký hàng loạt:",
                        ParseMode.Html,
                        BuildGameSelectionKeyboard(bulkSess.SelectedGames, true), ct);
                    break;

                case "account_balance":
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                        var user = await userService.GetUserByChatIdAsync(cbChat, cb.From?.Username, ct);
                        await SendNavigationScreenAsync(bot, cbChat,
                            $"👤 <b>TÀI KHOẢN KJC BOT</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                            $"🆔 Chat ID: <code>{cbChat}</code>\n" +
                            $"👤 Username: {(string.IsNullOrEmpty(user.Username) ? "Không có" : "@" + user.Username)}\n" +
                            $"💰 Số dư hiện tại: <b>{user.Balance:N0} KJC Xu</b>\n" +
                            $"💸 Tổng đã chi: <b>{user.TotalSpent:N0} KJC Xu</b>",
                            ParseMode.Html, MainMenuKeyboard(), ct);
                    }
                    break;

                case "buy_proxy":
                    await ShowProxyPackagesMenuAsync(bot, cbChat, null, null, ct);
                    break;

                case "deposit":
                    await SendNavigationScreenAsync(bot, cbChat,
                        BuildDepositInstructions(cbChat),
                        ParseMode.Html, MainMenuKeyboard(), ct);
                    break;

                case "edit_restart":
                    _sessions.TryRemove(cbChat, out _);
                    await StartAddFlow(bot, cbChat, ct, cb.Message?.MessageId ?? 0);
                    break;
                }
                return;
            }

            // ── Messages & Photos ─────────────────────────────────────────────
            if (update.Message is not { } msg) return;
            var chatId = msg.Chat.Id;

            if (msg.Type == MessageType.Photo && msg.Photo is { Length: > 0 })
            {
                if (!_allowedUsers.Contains(chatId))
                {
                    await bot.SendMessage(chatId,
                        $"⏳ <b>Yêu cầu truy cập {BrandName} đang được xét duyệt.</b>\nVui lòng chờ thông báo xác nhận từ bộ phận hỗ trợ.",
                        ParseMode.Html, cancellationToken: ct);
                    return;
                }

                var largestPhoto = msg.Photo[^1];
                string uname = msg.From?.Username != null ? $"@{msg.From.Username}" : (msg.From?.FirstName ?? "Khách");
                var caption = $"💳 <b>YÊU CẦU NẠP TIỀN MỚI</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                              $"👤 Người gửi: {WebUtility.HtmlEncode(uname)}\n" +
                              $"🆔 ID: <code>{chatId}</code>\n" +
                              $"💬 Nội dung: {WebUtility.HtmlEncode(msg.Caption ?? "Không có")}\n\n" +
                              $"Chọn số tiền duyệt cho khách:";

                var adminKeyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Duyệt 50k", $"app_50000_{chatId}"),
                        InlineKeyboardButton.WithCallbackData("✅ Duyệt 100k", $"app_100000_{chatId}")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("❌ Từ chối", $"rej_{chatId}")
                    }
                });

                try
                {
                    await bot.SendPhoto(_adminChatId, InputFile.FromFileId(largestPhoto.FileId), caption: caption, parseMode: ParseMode.Html, replyMarkup: adminKeyboard, cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to forward deposit receipt photo to admin.");
                }

                await bot.SendMessage(chatId, "✅ Đã tiếp nhận biên lai. Bộ phận hỗ trợ KJCBOT sẽ phản hồi sau khi kiểm tra.", replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                return;
            }

            if (msg.Text is not { } text) return;
            text = text.Trim();

            // Chưa được duyệt
            if (!_allowedUsers.Contains(chatId))
            {
                await bot.SendMessage(chatId,
                    $"⏳ <b>Yêu cầu truy cập {BrandName} đang được xét duyệt.</b>\nVui lòng chờ thông báo xác nhận từ bộ phận hỗ trợ.",
                    ParseMode.Html, cancellationToken: ct);

                string uname = msg.From?.Username != null ? $"@{msg.From.Username}" : (msg.From?.FirstName ?? "Khách lạ");
                try
                {
                    await bot.SendMessage(_adminChatId,
                        $"🔔 <b>YÊU CẦU TRUY CẬP MỚI</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                        $"👤 Người dùng: {WebUtility.HtmlEncode(uname)}\n🆔 ID: <code>{chatId}</code>\n\n" +
                        $"Cấp quyền không?",
                        ParseMode.Html,
                        replyMarkup: new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✅ Duyệt", $"approve_{chatId}"),
                                InlineKeyboardButton.WithCallbackData("❌ Từ chối", $"reject_{chatId}")
                            }
                        }), cancellationToken: ct);
                }
                catch { }
                return;
            }

            // /start hoặc /menu
            if (text.StartsWith("/start") || text == "/menu")
            {
                _sessions.TryRemove(chatId, out var previousSession);
                await SendReplacingMessageAsync(
                    bot,
                    chatId,
                    previousSession?.GuideMessageId ?? 0,
                    MainMenuText(),
                    ParseMode.Html,
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            // /add shortcut
            if (text.StartsWith("/add"))
            {
                await StartAddFlow(bot, chatId, ct);
                return;
            }

            // /bulk shortcut
            if (text.StartsWith("/bulk"))
            {
                var sess = new UserSession
                {
                    Step = "s_bulk_game"
                };
                _sessions[chatId] = sess;
                await ReplaceSessionPromptAsync(bot, chatId, sess,
                    "📋 <b>NHẬP HÀNG LOẠT</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "Chọn <b>Nhà cái</b> để đăng ký hàng loạt:",
                    ParseMode.Html,
                    BuildGameSelectionKeyboard(sess.SelectedGames, true), ct);
                return;
            }

            // /balance shortcut
            if (text.StartsWith("/balance"))
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
                    var user = await userService.GetUserByChatIdAsync(chatId, msg.From?.Username, ct);
                    await bot.SendMessage(chatId,
                        $"👤 <b>TÀI KHOẢN KJC BOT</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                        $"🆔 Chat ID: <code>{chatId}</code>\n" +
                        $"👤 Username: {(string.IsNullOrEmpty(user.Username) ? "Không có" : "@" + user.Username)}\n" +
                        $"💰 Số dư hiện tại: <b>{user.Balance:N0} KJC Xu</b>\n" +
                        $"💸 Tổng đã chi: <b>{user.TotalSpent:N0} KJC Xu</b>",
                        ParseMode.Html, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                }
                return;
            }

            // /buyproxy shortcut
            if (text.StartsWith("/buyproxy"))
            {
                await ShowProxyPackagesMenuAsync(bot, chatId, null, null, ct);
                return;
            }

            // /deposit shortcut
            if (text.StartsWith("/deposit"))
            {
                await bot.SendMessage(chatId,
                    BuildDepositInstructions(chatId),
                    ParseMode.Html, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                return;
            }

            // /emojiid (admin tool)
            if (text.StartsWith("/emojiid") && chatId == _adminChatId)
            {
                var tgt = msg.ReplyToMessage ?? msg;
                var ents = tgt.Entities ?? tgt.CaptionEntities;
                var emoji = ents?.Where(e => e.Type == MessageEntityType.CustomEmoji).ToList();
                if (emoji?.Any() == true)
                    await bot.SendMessage(chatId, $"Emoji IDs:\n<code>{string.Join("\n", emoji.Select(e => e.CustomEmojiId))}</code>", ParseMode.Html, cancellationToken: ct);
                else
                    await bot.SendMessage(chatId, "Không tìm thấy custom emoji.", cancellationToken: ct);
                return;
            }

            // Xử lý input theo bước
            var session = GetSession(chatId);

            if (session.Step == "proxy_quantity")
            {
                await HandleProxyQuantityInputAsync(
                    bot,
                    chatId,
                    msg.From?.Username,
                    msg.MessageId,
                    text,
                    session,
                    ct);
                return;
            }

            // Chặn nhập khi đang chạy
            if (_jobQueue.HasActiveJobs(chatId))
            {
                await bot.SendMessage(chatId,
                    "⏳ <b>Hệ thống đang xử lý yêu cầu của bạn.</b>\n" +
                    "Vui lòng đợi hoàn thành trước khi thêm tài khoản mới.",
                    ParseMode.Html, cancellationToken: ct);
                return;
            }

            // Bulk mode: xử lý nhiều dòng
            if (session.Step == "bulk")
            {
                await HandleBulkInput(bot, chatId, msg.MessageId, text, ct);
                return;
            }

            await HandleStepInput(bot, chatId, msg.MessageId, text, session, ct);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Bắt đầu flow nhập từng bước
        private async Task StartAddFlow(
            ITelegramBotClient bot,
            long chatId,
            CancellationToken ct,
            int messageIdToReplace = 0)
        {
            var sess = new UserSession
            {
                Step = "s_game",
                GuideMessageId = messageIdToReplace
            };
            _sessions[chatId] = sess;

            await ReplaceSessionPromptAsync(bot, chatId, sess,
                "📝 <b>THÊM TÀI KHOẢN MỚI</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n\n" +
                "Bước <b>1/6</b> — Chọn <b>Nhà cái</b> để đăng ký:",
                ParseMode.Html,
                BuildGameSelectionKeyboard(sess.SelectedGames, false), ct);
        }

        // ══════════════════════════════════════════════════════════════════════
        private async Task HandleStepInput(
            ITelegramBotClient bot,
            long chatId,
            int userMessageId,
            string text,
            UserSession sess,
            CancellationToken ct)
        {
            if (sess.Step == "idle")
            {
                await bot.SendMessage(chatId, MainMenuText(), ParseMode.Html,
                    replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                return;
            }

            switch (sess.Step)
            {
                case "s_game":
                case "s_bulk_game":
                    await ReplaceSessionPromptAsync(bot, chatId, sess,
                        "Vui lòng chọn Nhà cái bằng cách nhấn vào các nút bên dưới.",
                        ParseMode.None,
                        BuildGameSelectionKeyboard(sess.SelectedGames, sess.Step == "s_bulk_game"),
                        ct);
                    return;

                case "proxy_confirming":
                    await bot.SendMessage(
                        chatId,
                        "Vui lòng dùng các nút trong tin nhắn xác nhận mua proxy.",
                        cancellationToken: ct);
                    return;

                case "s_name":
                    if (text.Length < 3 || text.Any(char.IsDigit))
                    {
                        await SendError(bot, chatId, sess, "Họ tên phải có ít nhất 3 ký tự và không chứa số.\n<i>VD: Nguyen Van A</i>", ct);
                        return;
                    }
                    sess.FullName = text;
                    sess.Step = "s_phone";
                    await ReplaceSessionPromptAsync(bot, chatId, sess,
                        "Bước <b>3/6</b> — Nhập <b>Số điện thoại</b>:\n\n" +
                        "<i>Yêu cầu: đúng 10 chữ số, bắt đầu bằng 0\nVD: 0987654321</i>",
                        ParseMode.Html, CancelKeyboard(), ct, userMessageId);
                    break;

                case "s_phone":
                    var phone = text.Replace(" ", "");
                    if (!Regex.IsMatch(phone, @"^0\d{9}$"))
                    {
                        await SendError(bot, chatId, sess, "Số điện thoại không hợp lệ!\n<b>Yêu cầu:</b> Đúng 10 chữ số, bắt đầu bằng 0.\n<i>VD: 0987654321</i>", ct);
                        return;
                    }
                    sess.Phone = phone;
                    sess.Step = "s_bank";
                    await ReplaceSessionPromptAsync(bot, chatId, sess,
                        "Bước <b>4/6</b> — Nhập <b>Tên ngân hàng</b>:\n\n" +
                        "<i>VD: Vietcombank, Techcombank, MBBank, ACB, BIDV...</i>",
                        ParseMode.Html, CancelKeyboard(), ct, userMessageId);
                    break;

                case "s_bank":
                    if (text.Length < 2)
                    {
                        await SendError(bot, chatId, sess, "Tên ngân hàng quá ngắn. Hãy nhập đầy đủ.\n<i>VD: Vietcombank</i>", ct);
                        return;
                    }
                    sess.BankName = text;
                    sess.Step = "s_sotk";
                    await ReplaceSessionPromptAsync(bot, chatId, sess,
                        "Bước <b>5/6</b> — Nhập <b>Số tài khoản ngân hàng</b>:\n\n" +
                        "<i>Yêu cầu: chỉ gồm số, độ dài 6-20 ký tự\nVD: 1234567890</i>",
                        ParseMode.Html, CancelKeyboard(), ct, userMessageId);
                    break;

                case "s_sotk":
                    var sotk = text.Replace(" ", "");
                    if (!Regex.IsMatch(sotk, @"^\d{6,20}$"))
                    {
                        await SendError(bot, chatId, sess, "Số tài khoản không hợp lệ!\n<b>Yêu cầu:</b> Chỉ gồm chữ số, dài 6–20 ký tự.\n<i>VD: 1234567890</i>", ct);
                        return;
                    }
                    sess.SoTk = sotk;

                    sess.Step = "s_proxy";
                    await ReplaceSessionPromptAsync(bot, chatId, sess,
                        "Bước <b>6/6</b> — Nhập <b>Proxy</b>:\n\n" +
                        "📌 <b>Định dạng:</b>\n" +
                        "<code>ip:port</code>  hoặc  <code>ip:port:user:pass</code>\n\n" +
                        "<i>VD: 123.45.67.89:8080:username:password</i>",
                        ParseMode.Html, CancelKeyboard(), ct, userMessageId);
                    break;

                case "s_proxy":
                    if (!TryNormalizeRequiredProxyInput(text, out var proxy, out var proxyError))
                    {
                        await SendError(bot, chatId, sess,
                            $"{WebUtility.HtmlEncode(proxyError)}\n\n" +
                            "<code>ip:port</code> or <code>ip:port:user:pass</code>\n" +
                            "<i>VD: 123.45.67.89:8080:user:pass</i>", ct);
                        return;
                    }

                    sess.Proxy = proxy;
                    sess.Step = "confirming";
                    await SendConfirmationAsync(bot, chatId, sess, ct, userMessageId);
                    break;
            }
        }

        private Task SendError(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            string msg,
            CancellationToken ct)
        {
            return ReplaceSessionPromptAsync(bot, chatId, session,
                $"⚠️ <b>Thông tin chưa đúng</b>\n\n{msg}\n\n🔄 Vui lòng nhập lại:",
                ParseMode.Html, CancelKeyboard(), ct);
        }

        private async Task SendConfirmationAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            CancellationToken cancellationToken,
            int userMessageId = 0)
        {
            var proxyText = string.IsNullOrWhiteSpace(session.Proxy)
                ? "Không dùng proxy (kết nối trực tiếp)"
                : session.Proxy;

            await ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                "📋 <b>XÁC NHẬN THÔNG TIN</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                $"🎮 Nhà cái:  <b>{WebUtility.HtmlEncode(FormatSelectedGames(session.SelectedGames))}</b>\n" +
                $"👤 Họ tên:    <b>{WebUtility.HtmlEncode(session.FullName)}</b>\n" +
                $"📞 SĐT:       <b>{WebUtility.HtmlEncode(session.Phone)}</b>\n" +
                $"🏦 Ngân hàng: <b>{WebUtility.HtmlEncode(session.BankName)}</b>\n" +
                $"💳 STK:       <b>{WebUtility.HtmlEncode(session.SoTk)}</b>\n" +
                $"🌐 Proxy:     <code>{WebUtility.HtmlEncode(proxyText)}</code>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "Thông tin đúng chưa? Nhấn <b>Xác nhận</b> để chạy.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Xác nhận & Chạy", "confirm_submit"),
                        InlineKeyboardButton.WithCallbackData("🔄 Nhập lại", "edit_restart")
                    }
                }),
                cancellationToken,
                userMessageId);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Tạo tài khoản và chạy
        private async Task SubmitAccount(ITelegramBotClient bot, long chatId, UserSession sess, CancellationToken ct)
        {
            if (!TryNormalizeRequiredProxyInput(
                    sess.Proxy,
                    out var normalizedProxy,
                    out var proxyError))
            {
                sess.Step = "s_proxy";
                await SendError(
                    bot,
                    chatId,
                    sess,
                    $"{WebUtility.HtmlEncode(proxyError)}\n\n" +
                    "Tài khoản chưa được đưa vào hàng đợi. Hãy nhập proxy HTTP/HTTPS hợp lệ.",
                    ct);
                return;
            }

            sess.Proxy = normalizedProxy;
            _sessions.TryRemove(chatId, out _);

            // Username có nghĩa: tên người dùng viết thường không dấu + số ngẫu nhiên
            var account = new AccountModel
            {
                Username = ShortUsernameGenerator.Next(),
                PhoneNumber = sess.Phone,
                FullName = sess.FullName,
                Level2Password = "112233",
                AccountNumber = sess.SoTk,
                BankName = sess.BankName,
                Branch = "Hanoi",
                Proxy = sess.Proxy,
                UserAgent = string.Empty,
                Status = AccountStatus.Ready,
                LogMessage = "Profile added via Telegram."
            };
            // Password = Username + "1" cho dễ nhớ
            account.Password = account.Username + "1";
            ApplySelectedSites(account, sess.SelectedGames);

            // Tin nhắn tiến trình
            var job = await _resultSender.CreateJobAsync(
                chatId,
                account,
                isBulk: false,
                batchId: Guid.NewGuid(),
                ct);

            // The progress message now carries the request state; the confirmation is no longer needed.
            await DeleteMessageSafelyAsync(bot, chatId, sess.GuideMessageId, ct);
            sess.GuideMessageId = 0;

            _profileRepository.Add(account);

            var queueStatus = _jobQueue.GetSnapshot();
            if (queueStatus.Processing >= _maxConcurrentJobs)
            {
                await bot.SendMessage(
                    chatId,
                    "⏳ <b>Hệ thống đang xử lý nhiều yêu cầu.</b>\nYêu cầu của bạn đã được xếp hàng và sẽ tự động bắt đầu khi đến lượt.",
                    ParseMode.Html,
                    cancellationToken: ct);
            }

            await _jobQueue.EnqueueBatchAsync(new[] { job }, ct);
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            if (ex is ApiRequestException api)
            {
                _logger.LogError(
                    "Telegram API error. Code={ErrorCode}, Message={Message}",
                    api.ErrorCode,
                    api.Message);
            }
            else
            {
                _logger.LogError(ex, "Telegram polling error.");
            }

            return Task.CompletedTask;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Xử lý bulk: nhiều tài khoản cùng lúc
        private async Task HandleBulkInput(
            ITelegramBotClient bot,
            long chatId,
            int userMessageId,
            string text,
            CancellationToken ct)
        {
            if (!_sessions.TryGetValue(chatId, out var sess) || sess.Step != "bulk")
            {
                await bot.SendMessage(chatId,
                    "⚠️ Phiên nhập hàng loạt không còn hiệu lực. Vui lòng bắt đầu lại từ menu.",
                    replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                return;
            }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList();

            if (lines.Count == 0)
            {
                const string format = "HOTEN|TENNGANHANG|SDT|SOTK|PROXY";

                await ReplaceSessionPromptAsync(bot, chatId, sess,
                    "⚠️ Không tìm thấy dòng nào đúng định dạng.\n\n" +
                    $"Mỗi dòng phải theo mẫu:\n<code>{format}</code>\n\n" +
                    "🔄 Yêu cầu vẫn được giữ. Vui lòng nhập lại:",
                    ParseMode.Html, CancelKeyboard(), ct);
                return;
            }

            var errors = new System.Text.StringBuilder();
            var validAccounts = new System.Collections.Generic.List<AccountModel>();
            int lineNum = 0;

            foreach (var line in lines)
            {
                lineNum++;
                var p = line.Split('|', StringSplitOptions.TrimEntries);
                var validColumnCount = p.Length == 5;

                if (!validColumnCount)
                {
                    const string expectedColumns = "5";
                    errors.AppendLine($"• Dòng {lineNum}: Sai số cột (cần {expectedColumns}, có {p.Length})");
                    continue;
                }

                var name  = p[0];
                var bank  = p[1];
                var phone = p[2].Replace(" ", "");
                var sotk  = p[3].Replace(" ", "");
                var proxy = p.Length == 5
                    ? p[4].Replace("http://", "").Replace("https://", "").Replace(" ", "")
                    : string.Empty;
                if (!TryNormalizeRequiredProxyInput(proxy, out proxy, out var proxyError))
                {
                    errors.AppendLine($"- Dong {lineNum}: {proxyError}");
                    continue;
                }
                if (name.Length < 3 || name.Any(char.IsDigit))       { errors.AppendLine($"• Dòng {lineNum}: Họ tên không hợp lệ ({name})"); continue; }
                if (!Regex.IsMatch(phone, @"^0\d{9}$"))               { errors.AppendLine($"• Dòng {lineNum}: SĐT không hợp lệ ({phone})"); continue; }
                if (bank.Length < 2)                                   { errors.AppendLine($"• Dòng {lineNum}: Tên ngân hàng quá ngắn"); continue; }
                if (!Regex.IsMatch(sotk, @"^\d{6,20}$"))              { errors.AppendLine($"• Dòng {lineNum}: STK không hợp lệ ({sotk})"); continue; }
                if (!string.IsNullOrWhiteSpace(proxy) && !proxy.Contains(':'))
                {
                    errors.AppendLine($"• Dòng {lineNum}: Proxy sai định dạng ({proxy})");
                    continue;
                }

                var account = new AccountModel
                {
                    Username      = ShortUsernameGenerator.Next(),
                    PhoneNumber   = phone,
                    FullName      = name,
                    Level2Password= "112233",
                    AccountNumber = sotk,
                    BankName      = bank,
                    Branch        = "Hanoi",
                    Proxy         = proxy,
                    UserAgent     = string.Empty,
                    Status        = AccountStatus.Ready,
                    LogMessage    = "Bulk import via Telegram."
                };
                account.Password = account.Username + "1";
                validAccounts.Add(account);
            }

            // Không nhận một phần danh sách vì người dùng có thể bỏ sót các dòng lỗi.
            // Giữ nguyên phiên và lựa chọn game để họ sửa rồi gửi lại toàn bộ dữ liệu.
            if (errors.Length > 0)
            {
                var errorDetails = errors.Length <= 3000
                    ? errors.ToString()
                    : errors.ToString(0, 3000) + "\n… và thêm lỗi khác.";

                await ReplaceSessionPromptAsync(bot, chatId, sess,
                    $"⚠️ <b>Dữ liệu chưa hợp lệ:</b>\n{WebUtility.HtmlEncode(errorDetails)}\n" +
                    "Chưa có tài khoản nào được đưa vào hàng đợi.\n\n" +
                    "🔄 Yêu cầu và các game đã chọn vẫn được giữ. Vui lòng sửa rồi gửi lại toàn bộ danh sách:",
                    ParseMode.Html, CancelKeyboard(), ct);
                return;
            }

            // Báo trước số lượng
            await ReplaceSessionPromptAsync(bot, chatId, sess,
                $"🚀 <b>Đã tiếp nhận {validAccounts.Count} tài khoản hợp lệ.</b>\n" +
                $"Các tài khoản sẽ được chạy tuần tự (tối đa {_maxConcurrentJobs} cùng lúc).",
                ParseMode.Html, null, ct, userMessageId);

            _sessions.TryRemove(chatId, out _);

            var batchId = Guid.NewGuid();

            foreach (var preparedAccount in validAccounts)
            {
                ApplySelectedSites(preparedAccount, sess.SelectedGames);
            }

            // Chuẩn bị và đưa từng tài khoản vào queue nền.
            var jobs = new List<RegistrationJob>(validAccounts.Count);
            foreach (var account in validAccounts)
            {
                
                // Chuẩn bị các selected site
                var job = await _resultSender.CreateJobAsync(
                    chatId,
                    account,
                    isBulk: true,
                    batchId,
                    ct);

                jobs.Add(job);
            }

            _profileRepository.AddRange(validAccounts);

            foreach (var job in jobs)
            {
                await _jobQueue.EnqueueBatchAsync(new[] { job }, ct);
            }
        }

        // Helper: xóa dấu tiếng Việt để tạo username hợp lệ
        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var t = text
                .Replace("à","a").Replace("á","a").Replace("ả","a").Replace("ã","a").Replace("ạ","a")
                .Replace("ă","a").Replace("ắ","a").Replace("ặ","a").Replace("ằ","a").Replace("ẳ","a").Replace("ẵ","a")
                .Replace("â","a").Replace("ấ","a").Replace("ầ","a").Replace("ẩ","a").Replace("ẫ","a").Replace("ậ","a")
                .Replace("è","e").Replace("é","e").Replace("ẻ","e").Replace("ẽ","e").Replace("ẹ","e")
                .Replace("ê","e").Replace("ế","e").Replace("ề","e").Replace("ể","e").Replace("ễ","e").Replace("ệ","e")
                .Replace("ì","i").Replace("í","i").Replace("ỉ","i").Replace("ĩ","i").Replace("ị","i")
                .Replace("ò","o").Replace("ó","o").Replace("ỏ","o").Replace("õ","o").Replace("ọ","o")
                .Replace("ô","o").Replace("ố","o").Replace("ồ","o").Replace("ổ","o").Replace("ỗ","o").Replace("ộ","o")
                .Replace("ơ","o").Replace("ớ","o").Replace("ờ","o").Replace("ở","o").Replace("ỡ","o").Replace("ợ","o")
                .Replace("ù","u").Replace("ú","u").Replace("ủ","u").Replace("ũ","u").Replace("ụ","u")
                .Replace("ư","u").Replace("ứ","u").Replace("ừ","u").Replace("ử","u").Replace("ữ","u").Replace("ự","u")
                .Replace("ỳ","y").Replace("ý","y").Replace("ỷ","y").Replace("ỹ","y").Replace("ỵ","y")
                .Replace("đ","d");
            return new string(t.Where(c => char.IsLetterOrDigit(c)).ToArray());
        }
    }
}
