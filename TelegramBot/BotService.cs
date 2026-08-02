using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
        public string BankCode { get; set; } = "";
        public string SoTk { get; set; } = "";
        public string Proxy { get; set; } = "";
        // Validated accounts are held only until the user confirms the bulk run.
        // They are never shown back in Telegram messages.
        public List<AccountModel> PendingBulkAccounts { get; } = new();
        public int ProxyPackageId { get; set; }
        public int ProxyQuantity { get; set; }
        public decimal ProxyUnitPrice { get; set; }
        public int AdminTargetUserId { get; set; }
        public int AdminTargetProxyPackageId { get; set; }
        public string AdminBalanceMode { get; set; } = string.Empty;
        public decimal AdminPendingAmount { get; set; }
        public string AdminPendingReason { get; set; } = string.Empty;
        public string AdminPendingBroadcast { get; set; } = string.Empty;
        // Tin hướng dẫn hiện tại được thay thế ở mỗi bước để cuộc trò chuyện gọn hơn.
        public int GuideMessageId { get; set; } = 0;
    }

    public class BotService
    {
        private const string BrandName = "KJC BOT";
        private const string ProxyPurchaseErrorCode = "PX-001";
        private static readonly long[] TopUpPresetAmounts = { 20_000, 50_000, 100_000, 200_000 };

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
        private readonly HashSet<long> _adminChatIds;
        private readonly int _maxConcurrentJobs;
        private readonly int _maxProxyPurchaseQuantity;
        private readonly decimal _registrationPricePerSite;
        private readonly SePayOptions _sePayOptions;

        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private readonly ConcurrentDictionary<long, byte> _activeProxyPurchases = new();
        private readonly HashSet<long> _allowedUsers;
        private static readonly string AllowedUsersFilePath = RuntimePaths.AllowedUsersPath;
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private bool IsAdmin(long chatId) => _adminChatIds.Contains(chatId);

        private static string TruncateButtonText(string value, int maxLength = 48) =>
            value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

        // ── Helpers ───────────────────────────────────────────────────────────
        private static InlineKeyboardMarkup CancelKeyboard() =>
            new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel"));

        private static InlineKeyboardMarkup RegistrationPaymentRetryKeyboard(bool isBulk) =>
            new(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "🔄 Kiểm tra lại số dư",
                        isBulk ? "confirm_bulk_submit" : "confirm_submit")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("❌ Hủy", "cancel")
                }
            });

        private static InlineKeyboardMarkup BankInputKeyboard() =>
            new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🏦 Mã ngân hàng", "bank_codes")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel")
                }
            });

        private static string BuildBankInputPrompt() =>
            "Bước <b>4/6</b> — Nhập <b>Mã ngân hàng</b>:\n\n" +
            "<i>VD: VIETCOMB, MBBANK, TECHCOMB, ACB, BIDV...</i>";

        private static string BuildBulkInputPrompt(UserSession session)
        {
            const string bulkFormat = "HOTEN|MABANK|SDT|SOTK|PROXY";
            const string bulkExample =
                "<code>Nguyen Van A|VIETCOMB|0987654321|1234567890|1.2.3.4:8080:u:p</code>\n" +
                "<code>Tran Thi B|TECHCOMB|0912345678|9876543210|5.6.7.8:3128:u:p</code>";

            return $"📋 <b>NHẬP HÀNG LOẠT ({FormatSelectedGames(session.SelectedGames)})</b>\n" +
                   "━━━━━━━━━━━━━━━━━━━━\n" +
                   "Mỗi dòng 1 tài khoản theo định dạng:\n" +
                   $"<code>{bulkFormat}</code>\n\n" +
                   "📌 <b>Ví dụ:</b>\n" +
                   bulkExample + "\n\n" +
                   "⚠️ <b>Lưu ý:</b> Mỗi dòng là 1 tài khoản, dán bao nhiêu dòng cũng được.";
        }

        private static string BuildBankCatalogText()
        {
            var rows = VietnamBankCatalog.All.Select(bank =>
                $"<code>{WebUtility.HtmlEncode(bank.Code)}</code> — {WebUtility.HtmlEncode(bank.DisplayName)}");

            return "🏦 <b>MÃ NGÂN HÀNG HỖ TRỢ</b>\n" +
                   "Bấm vào mã để sao chép nhanh:\n\n" +
                   string.Join("\n", rows);
        }

        private static InlineKeyboardMarkup BuildBankCatalogKeyboard() =>
            new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ Quay lại nhập mã", "bank_codes_back") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel") }
            });

        private static string FormatBankDisplay(string bankName, string bankCode) =>
            string.IsNullOrWhiteSpace(bankCode)
                ? bankName
                : $"{bankName} ({bankCode})";

        private async Task ShowBankCatalogAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            UserSession session,
            CancellationToken ct)
        {
            await bot.EditMessageText(
                chatId,
                messageId,
                BuildBankCatalogText(),
                ParseMode.Html,
                replyMarkup: BuildBankCatalogKeyboard(),
                cancellationToken: ct);
            session.GuideMessageId = messageId;
        }

        private async Task RestoreBankInputAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            UserSession session,
            CancellationToken ct)
        {
            var prompt = session.Step == "bulk"
                ? BuildBulkInputPrompt(session)
                : BuildBankInputPrompt();
            await bot.EditMessageText(
                chatId,
                messageId,
                prompt,
                ParseMode.Html,
                replyMarkup: BankInputKeyboard(),
                cancellationToken: ct);
            session.GuideMessageId = messageId;
        }

        // Keep the main navigation visible above Telegram's input field. Inline
        // keyboards remain reserved for choices that belong to one message.
        private static ReplyKeyboardMarkup MainMenuKeyboard() =>
            new(new[]
            {
                new[]
                {
                    new KeyboardButton("➕ Đăng ký 1 tài khoản"),
                    new KeyboardButton("📋 Đăng ký hàng loạt")
                },
                new[]
                {
                    new KeyboardButton("📊 Theo dõi yêu cầu"),
                    new KeyboardButton("📚 Lịch sử")
                },
                new[]
                {
                    new KeyboardButton("👤 Tài khoản của tôi"),
                    new KeyboardButton("🌐 Thuê proxy")
                },
                new[]
                {
                    new KeyboardButton("💰 Nạp số dư")
                }
            })
            {
                ResizeKeyboard = true,
                IsPersistent = true,
                InputFieldPlaceholder = "Chọn một dịch vụ..."
            };

        private static ReplyKeyboardMarkup AdminReplyKeyboard() =>
            new(new[]
            {
                new[]
                {
                    new KeyboardButton("📊 Tổng quan"),
                    new KeyboardButton("🧾 Job mới")
                },
                new[]
                {
                    new KeyboardButton("👥 Người dùng"),
                    new KeyboardButton("📦 Gói proxy")
                },
                new[]
                {
                    new KeyboardButton("📣 Broadcast"),
                    new KeyboardButton("🛠 Trang admin")
                }
            })
            {
                ResizeKeyboard = true,
                IsPersistent = true,
                InputFieldPlaceholder = "Chọn chức năng quản trị..."
            };

        private static string NormalizeAdminSelection(string text)
        {
            if (text.Contains("Tổng quan", StringComparison.OrdinalIgnoreCase)) return "/admin_stats";
            if (text.Contains("Job mới", StringComparison.OrdinalIgnoreCase)) return "/admin_jobs";
            if (text.Contains("Người dùng", StringComparison.OrdinalIgnoreCase)) return "/admin_users";
            if (text.Contains("Gói proxy", StringComparison.OrdinalIgnoreCase)) return "/admin_packages";
            if (text.Contains("Broadcast", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Gửi thông báo", StringComparison.OrdinalIgnoreCase)) return "/admin_broadcast";
            if (text.Contains("Trang admin", StringComparison.OrdinalIgnoreCase)) return "/admin";
            return text;
        }

        private static string MainMenuText() =>
            $"🤖 <b>{BrandName}</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━\n" +
            "Đăng ký tài khoản nhanh, theo dõi tiến trình trực tiếp trên Telegram.";

        private static InlineKeyboardMarkup HistoryMenuKeyboard() =>
            new(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🛒 Lịch sử mua hàng", "history_purchases")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("👤 Tài khoản đã tạo", "history_accounts")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔙 Về menu", "history_back")
                }
            });

        private static InlineKeyboardMarkup HistoryDetailKeyboard() =>
            new(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Lịch sử", "history")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔙 Về menu", "history_back")
                }
            });

        private static string HistoryMenuText() =>
            $"📚 <b>LỊCH SỬ CỦA BẠN</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━\n" +
            "Chọn loại lịch sử muốn xem:";

        private async Task ShowHistoryMenuAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            await SendNavigationScreenAsync(
                bot,
                chatId,
                HistoryMenuText(),
                ParseMode.Html,
                HistoryMenuKeyboard(),
                ct,
                messageId);
        }

        private async Task ShowPurchaseHistoryAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.ChatId == chatId, ct);
            var purchases = user is null
                ? []
                : await db.Transactions
                    .AsNoTracking()
                    .Where(transaction => transaction.UserId == user.Id
                        && ((transaction.Type == "Withdraw"
                                && ((transaction.ReferenceId != null && transaction.ReferenceId.StartsWith("REG-"))
                                    || (transaction.Description != null
                                        && EF.Functions.Like(transaction.Description, "%proxy%"))))
                            || (transaction.Type == "Deposit"
                                && transaction.ReferenceId != null
                                && transaction.ReferenceId.StartsWith("REG-REFUND-"))))
                    .OrderByDescending(transaction => transaction.CreatedAt)
                    .Take(20)
                    .ToListAsync(ct);

            var builder = new StringBuilder("🛒 <b>LỊCH SỬ MUA HÀNG</b>\n━━━━━━━━━━━━━━━━━━━━\n");
            if (purchases.Count == 0)
            {
                builder.Append("Bạn chưa có giao dịch mua hàng nào.");
            }
            else
            {
                builder.Append($"20 giao dịch gần nhất:\n\n");
                foreach (var purchase in purchases)
                {
                    var isRefund = purchase.Type == "Deposit";
                    var description = string.IsNullOrWhiteSpace(purchase.Description)
                        ? "Giao dịch mua hàng"
                        : purchase.Description;
                    builder.Append(isRefund ? "↩️ <b>" : "• <b>")
                        .Append(WebUtility.HtmlEncode(description))
                        .Append("</b>\n  💰 ")
                        .Append(isRefund ? "+" : "-")
                        .Append(Math.Abs(purchase.Amount).ToString("N0", CultureInfo.InvariantCulture))
                        .Append(" KJC Xu | ")
                        .Append(purchase.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"))
                        .Append("\n");
                }
            }

            await SendNavigationScreenAsync(
                bot,
                chatId,
                builder.ToString(),
                ParseMode.Html,
                HistoryDetailKeyboard(),
                ct,
                messageId);
        }

        private async Task ShowCreatedAccountHistoryAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.ChatId == chatId, ct);
            var accounts = user is null
                ? []
                : await db.CreatedAccountHistories
                    .AsNoTracking()
                    .Where(history => history.UserId == user.Id)
                    .OrderByDescending(history => history.CreatedAt)
                    .Take(8)
                    .ToListAsync(ct);

            var builder = new StringBuilder("👤 <b>TÀI KHOẢN ĐÃ TẠO</b>\n━━━━━━━━━━━━━━━━━━━━\n");
            if (accounts.Count == 0)
            {
                builder.Append("Chưa có tài khoản tạo thành công nào được lưu.");
            }
            else
            {
                builder.Append("8 tài khoản gần nhất:\n\n");
                foreach (var account in accounts)
                {
                    builder.Append("✅ <b>")
                        .Append(WebUtility.HtmlEncode(account.Sites))
                        .Append("</b> — ")
                        .Append(account.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"))
                        .Append("\n👤 Đăng nhập: <code>")
                        .Append(WebUtility.HtmlEncode(account.Username))
                        .Append("</code>\n🔑 Mật khẩu: <code>")
                        .Append(WebUtility.HtmlEncode(account.Password))
                        .Append("</code>\n");
                }
            }

            await SendNavigationScreenAsync(
                bot,
                chatId,
                builder.ToString(),
                ParseMode.Html,
                HistoryDetailKeyboard(),
                ct,
                messageId);
        }

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

        private async Task ShowDepositEntryAsync(
            ITelegramBotClient bot,
            long chatId,
            int guideMessageId,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var paymentRequests = scope.ServiceProvider.GetRequiredService<SePayPaymentRequestService>();

            if (!paymentRequests.IsQrConfigured)
            {
                await SendNavigationScreenAsync(
                    bot,
                    chatId,
                    BuildDepositInstructions(chatId),
                    ParseMode.Html,
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            await StartCustomDepositAmountAsync(bot, chatId, guideMessageId, ct);
        }

        private static string BuildDepositAmountPrompt(SePayPaymentRequestService paymentRequests) =>
            $"💰 <b>NẠP SỐ DƯ {BrandName}</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━\n\n" +
            "Chọn số tiền muốn nạp hoặc nhấn <b>Nhập số tiền khác</b>.\n" +
            "Bot sẽ tạo QR có sẵn số tiền và nội dung chuyển khoản.\n\n" +
            $"💵 Hạn mức: <b>{paymentRequests.MinimumAmount:N0} – {paymentRequests.MaximumAmount:N0} VNĐ</b>";

        private static InlineKeyboardMarkup BuildDepositAmountKeyboard(SePayPaymentRequestService paymentRequests)
        {
            var rows = new List<InlineKeyboardButton[]>();
            var currentRow = new List<InlineKeyboardButton>();
            foreach (var amount in TopUpPresetAmounts.Where(amount =>
                         amount >= paymentRequests.MinimumAmount && amount <= paymentRequests.MaximumAmount))
            {
                currentRow.Add(InlineKeyboardButton.WithCallbackData(
                    $"{amount:N0} VNĐ",
                    $"deposit_amount_{amount}"));
                if (currentRow.Count == 2)
                {
                    rows.Add(currentRow.ToArray());
                    currentRow.Clear();
                }
            }

            if (currentRow.Count > 0)
                rows.Add(currentRow.ToArray());

            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✏️ Nhập số tiền khác", "deposit_custom") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel") });
            return new InlineKeyboardMarkup(rows);
        }

        private async Task StartCustomDepositAmountAsync(
            ITelegramBotClient bot,
            long chatId,
            int guideMessageId,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var paymentRequests = scope.ServiceProvider.GetRequiredService<SePayPaymentRequestService>();
            if (!paymentRequests.IsQrConfigured)
            {
                await SendNavigationScreenAsync(
                    bot,
                    chatId,
                    BuildDepositInstructions(chatId),
                    ParseMode.Html,
                    MainMenuKeyboard(),
                    ct);
                return;
            }

            var session = new UserSession
            {
                Step = "deposit_amount",
                GuideMessageId = guideMessageId
            };
            _sessions[chatId] = session;

            await ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                $"💰 <b>NHẬP SỐ TIỀN NẠP</b>\n\n" +
                $"Vui lòng nạp Min <b>{paymentRequests.MinimumAmount:N0}</b> VNĐ.\n" +
                "Nạp sai Min Ad không hỗ trợ nhé.",
                ParseMode.Html,
                CancelKeyboard(),
                ct);
        }

        private async Task HandleDepositAmountInputAsync(
            ITelegramBotClient bot,
            long chatId,
            int userMessageId,
            string text,
            UserSession session,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var paymentRequests = scope.ServiceProvider.GetRequiredService<SePayPaymentRequestService>();
            if (!paymentRequests.IsQrConfigured)
            {
                _sessions.TryRemove(chatId, out _);
                await SendReplacingMessageAsync(
                    bot,
                    chatId,
                    session.GuideMessageId,
                    BuildDepositInstructions(chatId),
                    ParseMode.Html,
                    MainMenuKeyboard(),
                    ct,
                    userMessageId);
                return;
            }

            if (!paymentRequests.TryParseAmount(text, out var amount))
            {
                await SendError(
                    bot,
                    chatId,
                    session,
                    $"Số tiền chưa hợp lệ. Hãy nhập từ <b>{paymentRequests.MinimumAmount:N0}</b>.",
                    ct);
                return;
            }

            _sessions.TryRemove(chatId, out _);
            await CreateAndSendPaymentQrAsync(bot, chatId, amount, session.GuideMessageId, userMessageId, ct);
        }

        private async Task CreateAndSendPaymentQrAsync(
            ITelegramBotClient bot,
            long chatId,
            long amount,
            int messageIdToRemove,
            int userMessageIdToRemove,
            CancellationToken ct)
        {
            SePayPaymentQr paymentQr;
            using (var scope = _scopeFactory.CreateScope())
            {
                var paymentRequests = scope.ServiceProvider.GetRequiredService<SePayPaymentRequestService>();
                if (!paymentRequests.IsQrConfigured)
                {
                    await SendNavigationScreenAsync(
                        bot,
                        chatId,
                        BuildDepositInstructions(chatId),
                        ParseMode.Html,
                        MainMenuKeyboard(),
                        ct);
                    return;
                }

                try
                {
                    paymentQr = await paymentRequests.CreateAsync(chatId, amount, ct);
                }
                catch (ArgumentOutOfRangeException)
                {
                    await bot.SendMessage(
                        chatId,
                        $"⚠️ Số tiền phải từ <b>{paymentRequests.MinimumAmount:N0}</b> đến <b>{paymentRequests.MaximumAmount:N0} VNĐ</b>.",
                        ParseMode.Html,
                        cancellationToken: ct);
                    return;
                }
                catch (InvalidOperationException exception)
                {
                    _logger.LogWarning(exception, "SePay QR is not ready for ChatId {ChatId}.", chatId);
                    await bot.SendMessage(
                        chatId,
                        "⚠️ QR thanh toán lỗi. Vui lòng thử lại sau hoặc liên hệ hỗ trợ.",
                        cancellationToken: ct);
                    return;
                }
            }

            var paymentRequest = paymentQr.PaymentRequest;
            var expiresAt = paymentRequest.ExpiresAt.ToLocalTime().ToString("HH:mm dd/MM/yyyy");
            var caption =
                $"💰 <b>QUÉT QR ĐỂ NẠP SỐ DƯ</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n\n" +
                $"💵 Số tiền: <b>{paymentRequest.ExpectedAmount:N0} VNĐ</b>\n" +
                $"📝 Nội dung: <code>{paymentRequest.TransferCode}</code>\n" +
                $"⏳ Mã có hiệu lực đến: <b>{expiresAt}</b>\n\n" +
                "App ngân hàng sẽ tự điền sẵn số tiền và nội dung sau khi quét. " +
                "Hệ thống chỉ tự cộng tiền khi giao dịch khớp đúng mã và số tiền này.";
            var replyMarkup = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("➕ Tạo mã nạp khác", "deposit") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Về menu", "cancel") }
            });

            try
            {
                await bot.SendPhoto(
                    chatId,
                    InputFile.FromUri(paymentQr.QrImageUrl),
                    caption: caption,
                    parseMode: ParseMode.Html,
                    replyMarkup: replyMarkup,
                    cancellationToken: ct);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not send SePay QR image to ChatId {ChatId}.", chatId);
                await bot.SendMessage(
                    chatId,
                    $"{caption}\n\n<a href=\"{WebUtility.HtmlEncode(paymentQr.QrImageUrl)}\">Mở mã QR thanh toán</a>",
                    ParseMode.Html,
                    replyMarkup: replyMarkup,
                    cancellationToken: ct);
            }

            await DeleteMessageSafelyAsync(bot, chatId, messageIdToRemove, ct);
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
                    var emptyMsg = "⚠️ Hiện đang hết Proxy. Vui lòng quay lại sau hoặc liên hệ bộ phận hỗ trợ KJCBOT.";
                    if (messageIdToEdit.HasValue)
                    {
                        await bot.EditMessageText(chatId, messageIdToEdit.Value, emptyMsg, cancellationToken: ct);
                        await bot.SendMessage(chatId, "Sử dụng Menu bên dưới để thao tác nhé.", replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                    }
                    else
                        await bot.SendMessage(chatId, emptyMsg, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                    return;
                }

                var text = $"🌐 <b>PROXY TĨNH VIỆT NAM | {BrandName}</b>\n" +
                           "━━━━━━━━━━━━━━━━━━━━\n" +
                           "Vui lòng chọn <b>nhà mạng</b> muốn sử dụng:\n\n" +
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
                var emptyMsg = $"⚠️ Hiện tại nhà mạng <b>{catDisplay}</b> tạm hết hàng. Vui lòng chọn nhà mạng khác.";
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
            ReplyMarkup? replyMarkup,
            CancellationToken ct)
        {
            if (messageId > 0)
            {
                await bot.EditMessageText(
                    chatId,
                    messageId,
                    text,
                    ParseMode.Html,
                    replyMarkup: replyMarkup as InlineKeyboardMarkup,
                    cancellationToken: ct);

                if (replyMarkup is ReplyKeyboardMarkup)
                {
                    await bot.SendMessage(
                        chatId,
                        "Menu dịch vụ đã sẵn sàng bên dưới.",
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }
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
            ReplyMarkup? replyMarkup,
            CancellationToken ct,
            int userMessageId = 0)
        {
            var sent = await bot.SendMessage(
                chatId,
                text,
                parseMode,
                replyMarkup: replyMarkup,
                cancellationToken: ct);

            // This helper is only for transient bot prompts. User messages are
            // records supplied by the sender, so never delete them from chat.
            await DeleteMessageSafelyAsync(bot, chatId, previousMessageId, ct);

            return sent;
        }

        private async Task<Message> ReplaceSessionPromptAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            string text,
            ParseMode parseMode,
            ReplyMarkup? replyMarkup,
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
            ReplyMarkup? replyMarkup,
            CancellationToken ct) =>
            bot.SendMessage(
                chatId,
                text,
                parseMode,
                replyMarkup: replyMarkup,
                cancellationToken: ct);

        // Admin screens replace only the previous bot-owned screen; messages
        // entered by admins and users remain in the conversation.
        private Task<Message> SendNavigationScreenAsync(
            ITelegramBotClient bot,
            long chatId,
            string text,
            ParseMode parseMode,
            ReplyMarkup? replyMarkup,
            CancellationToken ct,
            int previousMessageId) =>
            SendReplacingMessageAsync(
                bot,
                chatId,
                previousMessageId,
                text,
                parseMode,
                replyMarkup,
                ct);

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

        private decimal CalculateRegistrationCharge(int accountCount, int siteCount)
        {
            if (accountCount <= 0 || siteCount <= 0)
            {
                return 0m;
            }

            return checked(accountCount * siteCount * _registrationPricePerSite);
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

        private static List<string> SplitBulkInputLines(string text)
        {
            var rawLines = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var lines = new List<string>(rawLines.Length);

            foreach (var rawLine in rawLines)
            {
                var line = rawLine.Trim();
                if (lines.Count > 0
                    && lines[^1].Count(character => character == '|') == 4
                    && !line.Contains('|')
                    && line.Contains(':'))
                {
                    // Some clipboard sources insert a real line break before or
                    // inside the proxy field. Join only a proxy-like continuation
                    // so a genuinely malformed account line still gets reported.
                    lines[^1] += line;
                    continue;
                }

                lines.Add(line);
            }

            return lines;
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
            var configuredAdminIds = telegramOptions.Value.AdminChatIds
                .Where(chatId => chatId > 0)
                .Distinct()
                .ToArray();
            _adminChatIds = configuredAdminIds.Length > 0
                ? new HashSet<long>(configuredAdminIds)
                : new HashSet<long> { telegramOptions.Value.AdminChatId };
            _adminChatId = _adminChatIds.First();
            _maxConcurrentJobs = automationOptions.Value.MaxConcurrentJobs;
            _registrationPricePerSite = telegramOptions.Value.RegistrationPricePerSite;
            _maxProxyPurchaseQuantity = Math.Max(
                1,
                configuration.GetValue<int?>("ProxyVn:MaxPurchaseQuantity") ?? 100);
            _sePayOptions = sePayOptions.Value;
            _allowedUsers = LoadAllowedUsers();
        }

        private HashSet<long> LoadAllowedUsers()
        {
            var set = new HashSet<long>(_adminChatIds);
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

        // ── Telegram admin dashboard ────────────────────────────────────────
        private static InlineKeyboardMarkup AdminMenuKeyboard() => new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📊 Tổng quan", "admin_stats"),
                InlineKeyboardButton.WithCallbackData("🧾 Job mới", "admin_jobs")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("👥 Người dùng", "admin_users_0"),
                InlineKeyboardButton.WithCallbackData("📦 Gói proxy", "admin_packages_0")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📣 Gửi thông báo", "admin_broadcast"),
                InlineKeyboardButton.WithCallbackData("🔄 Làm mới", "admin_menu")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🚪 Thoát quản trị", "admin_exit")
            }
        });

        private async Task ShowAdminPanelAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct) =>
            await SendNavigationScreenAsync(
                bot,
                chatId,
                "🛠 <b>QUẢN TRỊ KJC BOT</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "Chọn chức năng quản trị bên dưới.",
                ParseMode.Html,
                AdminMenuKeyboard(),
                ct,
                messageId);

        private async Task ShowAdminHomeAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            await bot.SendMessage(
                chatId,
                "🛠 <b>Đã bật giao diện quản trị riêng.</b>\nDùng các nút dưới ô nhập tin nhắn để quản lý bot.",
                ParseMode.Html,
                replyMarkup: AdminReplyKeyboard(),
                cancellationToken: ct);
            await ShowAdminPanelAsync(bot, chatId, messageId, ct);
        }

        private async Task ShowAdminStatsAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var totalUsers = await db.Users.CountAsync(ct);
            var totalBalance = (await db.Users.Select(user => user.Balance).ToListAsync(ct)).Sum();
            var queue = _jobQueue.GetSnapshot();
            var today = DateTime.UtcNow.Date;
            var dbRunning = await db.Jobs.CountAsync(job => job.Status == "Running", ct);
            var succeeded = await db.Jobs.CountAsync(job => job.Status == "Success" && job.StartTime >= today, ct);
            var failed = await db.Jobs.CountAsync(job => job.Status == "Failed" && job.StartTime >= today, ct);

            await SendNavigationScreenAsync(
                bot,
                chatId,
                "📊 <b>TỔNG QUAN HỆ THỐNG</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                $"👥 Người dùng: <b>{totalUsers:N0}</b>\n" +
                $"💰 Tổng số dư: <b>{totalBalance:N0} Xu</b>\n" +
                $"🔄 Đang xử lý: <b>{Math.Max(queue.Processing, dbRunning)}/{_maxConcurrentJobs}</b>\n" +
                $"⏳ Đang chờ: <b>{queue.Pending:N0}</b>\n" +
                $"✅ Thành công hôm nay: <b>{Math.Max((int)queue.Succeeded, succeeded):N0}</b>\n" +
                $"❌ Thất bại hôm nay: <b>{Math.Max((int)queue.Failed, failed):N0}</b>",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🔄 Làm mới", "admin_stats") },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Menu quản trị", "admin_menu") }
                }),
                ct,
                messageId);
        }

        private async Task ShowAdminJobsAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = await db.Jobs
                .OrderByDescending(job => job.StartTime)
                .Take(10)
                .Select(job => new { job.Id, job.Provider, job.Status, job.StartTime })
                .ToListAsync(ct);

            var details = jobs.Count == 0
                ? "Chưa có job nào."
                : string.Join("\n", jobs.Select(job =>
                    $"• <code>#{job.Id}</code> <b>{WebUtility.HtmlEncode(job.Provider)}</b> — {WebUtility.HtmlEncode(job.Status)}\n  <i>{job.StartTime.ToLocalTime():dd/MM HH:mm}</i>"));

            await SendNavigationScreenAsync(
                bot,
                chatId,
                "🧾 <b>10 JOB MỚI NHẤT</b>\n━━━━━━━━━━━━━━━━━━━━\n" + details,
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🔄 Làm mới", "admin_jobs") },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Menu quản trị", "admin_menu") }
                }),
                ct,
                messageId);
        }

        private async Task ShowAdminUsersAsync(
            ITelegramBotClient bot,
            long chatId,
            int page,
            int messageId,
            CancellationToken ct)
        {
            const int pageSize = 6;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var total = await db.Users.CountAsync(ct);
            var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            page = Math.Clamp(page, 0, pageCount - 1);
            var users = await db.Users.OrderByDescending(user => user.CreatedAt)
                .Skip(page * pageSize).Take(pageSize)
                .Select(user => new { user.Id, user.ChatId, user.Username, user.Balance })
                .ToListAsync(ct);

            var rows = users.Select(user => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    TruncateButtonText($"👤 #{user.Id} | {(string.IsNullOrWhiteSpace(user.Username) ? user.ChatId.ToString() : "@" + user.Username)} | {user.Balance:N0} Xu"),
                    $"admin_user_{user.Id}_{page}")
            }).ToList();
            if (page > 0 || page < pageCount - 1)
            {
                var navigation = new List<InlineKeyboardButton>();
                if (page > 0) navigation.Add(InlineKeyboardButton.WithCallbackData("◀️", $"admin_users_{page - 1}"));
                navigation.Add(InlineKeyboardButton.WithCallbackData($"{page + 1}/{pageCount}", "admin_noop"));
                if (page < pageCount - 1) navigation.Add(InlineKeyboardButton.WithCallbackData("▶️", $"admin_users_{page + 1}"));
                rows.Add(navigation.ToArray());
            }
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 Tìm username / Chat ID", "admin_users_search") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Menu quản trị", "admin_menu") });

            await SendNavigationScreenAsync(
                bot,
                chatId,
                $"👥 <b>NGƯỜI DÙNG</b> — {total:N0} tài khoản\nChọn một người dùng để xem và điều chỉnh số dư.",
                ParseMode.Html,
                new InlineKeyboardMarkup(rows),
                ct,
                messageId);
        }

        private async Task StartAdminUserSearchAsync(
            ITelegramBotClient bot,
            long chatId,
            int messageId,
            CancellationToken ct)
        {
            var session = new UserSession { Step = "admin_user_search", GuideMessageId = messageId };
            _sessions[chatId] = session;
            await ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                "🔎 <b>TÌM NGƯỜI DÙNG</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                "Nhập <b>@username</b>, username, hoặc <b>Chat ID</b> cần tìm.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Danh sách người dùng", "admin_users_0") },
                    new[] { InlineKeyboardButton.WithCallbackData("🚪 Thoát quản trị", "admin_exit") }
                }),
                ct);
        }

        private async Task HandleAdminUserSearchAsync(
            ITelegramBotClient bot,
            long chatId,
            int userMessageId,
            string text,
            UserSession session,
            CancellationToken ct)
        {
            var query = text.Trim().TrimStart('@');
            if (string.IsNullOrWhiteSpace(query))
            {
                await SendAdminUserSearchErrorAsync(bot, chatId, session, "Hãy nhập username hoặc Chat ID cần tìm.", ct);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var normalized = query.ToLowerInvariant();
            var hasChatId = long.TryParse(query, out var requestedChatId);
            var users = await db.Users
                .Where(user => (hasChatId && user.ChatId == requestedChatId)
                    || (user.Username != null && user.Username.ToLower().Contains(normalized)))
                .OrderByDescending(user => user.CreatedAt)
                .Take(10)
                .Select(user => new { user.Id, user.ChatId, user.Username, user.Balance })
                .ToListAsync(ct);

            if (users.Count == 0)
            {
                await SendAdminUserSearchErrorAsync(bot, chatId, session, "Không tìm thấy người dùng phù hợp. Thử lại @username hoặc Chat ID khác.", ct);
                return;
            }

            _sessions.TryRemove(chatId, out _);
            var rows = users.Select(user => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    TruncateButtonText($"👤 #{user.Id} | {(string.IsNullOrWhiteSpace(user.Username) ? user.ChatId.ToString() : "@" + user.Username)} | {user.Balance:N0} Xu"),
                    $"admin_user_{user.Id}_0")
            }).ToList();
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 Tìm lại", "admin_users_search") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Danh sách người dùng", "admin_users_0") });

            await SendNavigationScreenAsync(
                bot,
                chatId,
                $"🔎 <b>KẾT QUẢ TÌM KIẾM</b> — {users.Count} kết quả\nChọn người dùng để xem chi tiết.",
                ParseMode.Html,
                new InlineKeyboardMarkup(rows),
                ct,
                session.GuideMessageId == 0 ? userMessageId : session.GuideMessageId);
        }

        private Task SendAdminUserSearchErrorAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            string error,
            CancellationToken ct) =>
            ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                $"⚠️ <b>{WebUtility.HtmlEncode(error)}</b>\n\nNhập lại @username hoặc Chat ID:",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Danh sách người dùng", "admin_users_0") },
                    new[] { InlineKeyboardButton.WithCallbackData("🚪 Thoát quản trị", "admin_exit") }
                }),
                ct);

        private async Task ShowAdminUserAsync(
            ITelegramBotClient bot,
            long chatId,
            int userId,
            int returnPage,
            int messageId,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FindAsync(new object[] { userId }, ct);
            if (user is null)
            {
                await ShowAdminUsersAsync(bot, chatId, returnPage, messageId, ct);
                return;
            }

            var jobsCount = await db.Jobs.CountAsync(job => job.UserId == user.Id, ct);
            var name = string.IsNullOrWhiteSpace(user.Username) ? "Không có username" : "@" + user.Username;
            await SendNavigationScreenAsync(
                bot,
                chatId,
                "👤 <b>CHI TIẾT NGƯỜI DÙNG</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                $"🆔 ID hệ thống: <code>{user.Id}</code>\n" +
                $"💬 Chat ID: <code>{user.ChatId}</code>\n" +
                $"👤 Username: <b>{WebUtility.HtmlEncode(name)}</b>\n" +
                $"💰 Số dư: <b>{user.Balance:N0} Xu</b>\n" +
                $"💸 Đã chi: <b>{user.TotalSpent:N0} Xu</b>\n" +
                $"🧾 Jobs: <b>{jobsCount:N0}</b>",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("➕ Cộng", $"admin_bal_add_{user.Id}"),
                        InlineKeyboardButton.WithCallbackData("➖ Trừ", $"admin_bal_deduct_{user.Id}"),
                        InlineKeyboardButton.WithCallbackData("✏️ Đặt số dư", $"admin_bal_set_{user.Id}")
                    },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Danh sách", $"admin_users_{returnPage}") }
                }),
                ct,
                messageId);
        }

        private async Task StartAdminBalanceInputAsync(
            ITelegramBotClient bot,
            long chatId,
            int userId,
            string mode,
            int messageId,
            CancellationToken ct)
        {
            var session = new UserSession
            {
                Step = "admin_balance_amount",
                AdminTargetUserId = userId,
                AdminBalanceMode = mode,
                GuideMessageId = messageId
            };
            _sessions[chatId] = session;
            var verb = mode == "add" ? "cộng" : mode == "deduct" ? "trừ" : "đặt";
            await ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                $"💰 <b>{verb.ToUpperInvariant()} SỐ DƯ</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "Gửi theo mẫu: <code>SOTIEN|ghi chú</code>\n" +
                "Ví dụ: <code>50000|Thưởng hỗ trợ</code>\n\n" +
                "Sau bước này cần xác nhận lần cuối.",
                ParseMode.Html,
                CancelKeyboard(),
                ct);
        }

        private static bool TryParseAdminAmount(string value, out decimal amount)
        {
            var trimmed = value.Trim();
            // Existing proxy prices may be copied from the edit template as
            // 800.0000. Treat that as a decimal value, while 50.000 / 50,000
            // remains the common Vietnamese thousands notation.
            if (Regex.IsMatch(trimmed, @"^\d+[\.,]\d{4}$")
                && decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            {
                return amount > 0;
            }

            var normalized = trimmed.Replace(".", string.Empty).Replace(",", string.Empty);
            return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) && amount > 0;
        }

        private async Task HandleAdminBalanceAmountAsync(
            ITelegramBotClient bot,
            long chatId,
            int userMessageId,
            string text,
            UserSession session,
            CancellationToken ct)
        {
            var parts = text.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !TryParseAdminAmount(parts[0], out var amount))
            {
                await SendError(bot, chatId, session, "Số tiền chưa hợp lệ. Ví dụ: <code>50000|Thưởng hỗ trợ</code>", ct);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var target = await db.Users.FindAsync(new object[] { session.AdminTargetUserId }, ct);
            if (target is null)
            {
                _sessions.TryRemove(chatId, out _);
                await bot.SendMessage(chatId, "⚠️ Người dùng không còn tồn tại.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                return;
            }

            var expectedBalance = session.AdminBalanceMode switch
            {
                "add" => target.Balance + amount,
                "deduct" => target.Balance - amount,
                _ => amount
            };
            if (expectedBalance < 0)
            {
                await SendError(bot, chatId, session, "Không thể trừ vượt quá số dư hiện tại.", ct);
                return;
            }

            session.AdminPendingAmount = amount;
            session.AdminPendingReason = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "Điều chỉnh bởi admin";
            session.Step = "admin_balance_confirm";
            var action = session.AdminBalanceMode == "add" ? "CỘNG" : session.AdminBalanceMode == "deduct" ? "TRỪ" : "ĐẶT";
            await ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                $"⚠️ <b>XÁC NHẬN {action} SỐ DƯ</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                $"💬 Chat ID: <code>{target.ChatId}</code>\n" +
                $"💰 Số dư hiện tại: <b>{target.Balance:N0} Xu</b>\n" +
                $"🧮 Giá trị nhập: <b>{amount:N0} Xu</b>\n" +
                $"✅ Số dư sau điều chỉnh: <b>{expectedBalance:N0} Xu</b>\n" +
                $"📝 Ghi chú: {WebUtility.HtmlEncode(session.AdminPendingReason)}\n\n" +
                "Người dùng sẽ được thông báo sau khi bạn xác nhận.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("✅ Xác nhận điều chỉnh", "admin_bal_apply") },
                    new[] { InlineKeyboardButton.WithCallbackData("❌ Hủy", "cancel") }
                }),
                ct,
                userMessageId);
        }

        private async Task ApplyAdminBalanceAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var target = await db.Users.FindAsync(new object[] { session.AdminTargetUserId }, ct);
            if (target is null)
            {
                await bot.SendMessage(chatId, "⚠️ Người dùng không còn tồn tại.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                return;
            }

            var oldBalance = target.Balance;
            var success = session.AdminBalanceMode switch
            {
                "add" => await userService.AddBalanceAsync(target.ChatId, session.AdminPendingAmount, session.AdminPendingReason, "ADMIN_TELEGRAM", ct),
                "deduct" => await userService.DeductBalanceAsync(target.ChatId, session.AdminPendingAmount, session.AdminPendingReason, "ADMIN_TELEGRAM", ct),
                _ => await userService.SetBalanceAsync(target.ChatId, session.AdminPendingAmount, session.AdminPendingReason, "ADMIN_TELEGRAM", ct)
            };
            if (!success)
            {
                await bot.SendMessage(chatId, "⚠️ Không thể điều chỉnh số dư. Có thể số dư đã thay đổi.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                return;
            }

            var updated = await userService.GetUserByChatIdAsync(target.ChatId, null, ct);
            var action = session.AdminBalanceMode == "add" ? "được cộng" : session.AdminBalanceMode == "deduct" ? "bị trừ" : "được đặt";
            try
            {
                await bot.SendMessage(target.ChatId,
                    "💰 <b>THÔNG BÁO TỪ ADMIN</b>\n" +
                    $"Số dư của bạn vừa {action} <b>{session.AdminPendingAmount:N0} Xu</b>.\n" +
                    $"Số dư hiện tại: <b>{updated.Balance:N0} Xu</b>\n" +
                    $"Ghi chú: {WebUtility.HtmlEncode(session.AdminPendingReason)}",
                    ParseMode.Html,
                    cancellationToken: ct);
            }
            catch { }

            _sessions.TryRemove(chatId, out _);
            await bot.SendMessage(chatId,
                $"✅ Đã điều chỉnh số dư Chat ID <code>{target.ChatId}</code>: <b>{oldBalance:N0}</b> → <b>{updated.Balance:N0} Xu</b>.",
                ParseMode.Html,
                replyMarkup: AdminMenuKeyboard(),
                cancellationToken: ct);
        }

        private async Task StartAdminBroadcastAsync(ITelegramBotClient bot, long chatId, int messageId, CancellationToken ct)
        {
            var session = new UserSession { Step = "admin_broadcast_text", GuideMessageId = messageId };
            _sessions[chatId] = session;
            await ReplaceSessionPromptAsync(bot, chatId, session,
                "📣 <b>GỬI THÔNG BÁO TOÀN THỂ</b>\n━━━━━━━━━━━━━━━━━━━━\nGửi nội dung thông báo ở tin nhắn tiếp theo. Bot sẽ cho xem trước và yêu cầu xác nhận.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Menu quản trị", "admin_menu") },
                    new[] { InlineKeyboardButton.WithCallbackData("🚪 Thoát quản trị", "admin_exit") }
                }),
                ct);
        }

        private async Task HandleAdminBroadcastTextAsync(ITelegramBotClient bot, long chatId, int userMessageId, string text, UserSession session, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await SendError(bot, chatId, session, "Nội dung thông báo không được để trống.", ct);
                return;
            }

            session.AdminPendingBroadcast = text.Trim();
            session.Step = "admin_broadcast_confirm";
            var preview = session.AdminPendingBroadcast.Length > 1000 ? session.AdminPendingBroadcast[..1000] + "…" : session.AdminPendingBroadcast;
            await ReplaceSessionPromptAsync(bot, chatId, session,
                "⚠️ <b>XÁC NHẬN GỬI THÔNG BÁO</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                $"📝 Nội dung:\n{WebUtility.HtmlEncode(preview)}\n" +
                "Tin nhắn sẽ được gửi đến toàn bộ người dùng đã lưu.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("✅ Gửi toàn thể", "admin_broadcast_send") },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Menu quản trị", "admin_menu") },
                    new[] { InlineKeyboardButton.WithCallbackData("🚪 Thoát quản trị", "admin_exit") }
                }),
                ct,
                userMessageId);
        }

        private async Task SendAdminBroadcastAsync(ITelegramBotClient bot, long chatId, UserSession session, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var recipients = await db.Users.Select(user => user.ChatId).Distinct().ToListAsync(ct);
            var text = "<b>Thông báo từ admin:</b>\n\n" + WebUtility.HtmlEncode(session.AdminPendingBroadcast);
            var success = 0;
            var failed = 0;
            foreach (var recipient in recipients)
            {
                try { await bot.SendMessage(recipient, text, ParseMode.Html, cancellationToken: ct); success++; }
                catch { failed++; }
                await Task.Delay(50, ct);
            }
            _sessions.TryRemove(chatId, out _);
            await bot.SendMessage(chatId, $"✅ Đã gửi thông báo: <b>{success:N0}</b> thành công, <b>{failed:N0}</b> thất bại.", ParseMode.Html, replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
        }

        private async Task ShowAdminPackagesAsync(ITelegramBotClient bot, long chatId, int page, int messageId, CancellationToken ct)
        {
            const int pageSize = 6;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var total = await db.ProxyPackages.CountAsync(ct);
            var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            page = Math.Clamp(page, 0, pageCount - 1);
            var packages = await db.ProxyPackages.OrderBy(package => package.Id)
                .Skip(page * pageSize).Take(pageSize).ToListAsync(ct);
            var rows = packages.Select(package => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    TruncateButtonText($"{(package.IsActive ? "🟢" : "⚪")} #{package.Id} {package.Name} | {package.SellingPrice:N0} Xu"),
                    $"admin_package_{package.Id}_{page}")
            }).ToList();
            if (page > 0 || page < pageCount - 1)
            {
                var navigation = new List<InlineKeyboardButton>();
                if (page > 0) navigation.Add(InlineKeyboardButton.WithCallbackData("◀️", $"admin_packages_{page - 1}"));
                navigation.Add(InlineKeyboardButton.WithCallbackData($"{page + 1}/{pageCount}", "admin_noop"));
                if (page < pageCount - 1) navigation.Add(InlineKeyboardButton.WithCallbackData("▶️", $"admin_packages_{page + 1}"));
                rows.Add(navigation.ToArray());
            }
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("➕ Thêm gói", "admin_package_add") });
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Menu quản trị", "admin_menu") });
            await SendNavigationScreenAsync(bot, chatId,
                $"📦 <b>GÓI PROXY</b> — {total:N0} gói\n🟢 đang bán | ⚪ đã tắt. Chọn gói để chỉnh sửa.",
                ParseMode.Html, new InlineKeyboardMarkup(rows), ct, messageId);
        }

        private async Task ShowAdminPackageAsync(ITelegramBotClient bot, long chatId, int packageId, int returnPage, int messageId, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var package = await db.ProxyPackages.FindAsync(new object[] { packageId }, ct);
            if (package is null)
            {
                await ShowAdminPackagesAsync(bot, chatId, returnPage, messageId, ct);
                return;
            }
            var description = string.IsNullOrWhiteSpace(package.Description) ? "Không có" : package.Description;
            await SendNavigationScreenAsync(bot, chatId,
                "📦 <b>CHI TIẾT GÓI PROXY</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                $"🆔 <code>#{package.Id}</code> — <b>{WebUtility.HtmlEncode(package.Name)}</b>\n" +
                $"🏷 Nhóm: <b>{WebUtility.HtmlEncode(package.Category)}</b>\n" +
                $"🌐 Loại: <b>{(package.ProxyType == 1 ? "SOCKS5" : "HTTP/HTTPS")}</b>\n" +
                $"📅 Hạn: <b>{package.PeriodDays} ngày</b>\n" +
                $"💵 Giá gốc: <b>{package.OriginalPrice:N0} Xu</b>\n" +
                $"💰 Giá bán: <b>{package.SellingPrice:N0} Xu</b>\n" +
                $"📈 Lợi nhuận: <b>{package.SellingPrice - package.OriginalPrice:N0} Xu</b>\n" +
                $"⚙️ Trạng thái: <b>{(package.IsActive ? "Đang bán" : "Đã tắt")}</b>\n" +
                $"📝 {WebUtility.HtmlEncode(description)}",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData(package.IsActive ? "⏸ Tắt bán" : "▶️ Mở bán", $"admin_package_toggle_{package.Id}_{returnPage}"),
                        InlineKeyboardButton.WithCallbackData("💰 Sửa giá bán", $"admin_package_price_{package.Id}_{returnPage}")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✏️ Sửa toàn bộ", $"admin_package_edit_{package.Id}_{returnPage}"),
                        InlineKeyboardButton.WithCallbackData("🗑 Xóa", $"admin_package_delete_{package.Id}_{returnPage}")
                    },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Danh sách", $"admin_packages_{returnPage}") }
                }),
                ct,
                messageId);
        }

        private async Task StartAdminPackageInputAsync(ITelegramBotClient bot, long chatId, int packageId, int messageId, CancellationToken ct)
        {
            var isNew = packageId == 0;
            var session = new UserSession
            {
                Step = isNew ? "admin_package_add" : "admin_package_edit",
                AdminTargetProxyPackageId = packageId,
                GuideMessageId = messageId
            };
            _sessions[chatId] = session;
            var existing = string.Empty;
            if (!isNew)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var package = await db.ProxyPackages.FindAsync(new object[] { packageId }, ct);
                if (package is null)
                {
                    _sessions.TryRemove(chatId, out _);
                    await bot.SendMessage(chatId, "⚠️ Gói proxy không còn tồn tại.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                    return;
                }
                existing = $"\n\nDữ liệu hiện tại để bạn sửa:\n<code>{WebUtility.HtmlEncode($"{package.Name}|{package.Category}|{(package.ProxyType == 1 ? "SOCKS5" : "HTTP")}|{package.PeriodDays}|{package.OriginalPrice}|{package.SellingPrice}|{package.Description}")}</code>";
            }
            await ReplaceSessionPromptAsync(bot, chatId, session,
                $"📦 <b>{(isNew ? "THÊM" : "SỬA")} GÓI PROXY</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                "Gửi một dòng theo mẫu:\n" +
                "<code>TÊN|NHÓM|HTTP hoặc SOCKS5|SỐ NGÀY|GIÁ GỐC|GIÁ BÁN|MÔ TẢ</code>\n" +
                "Ví dụ: <code>Proxy VNPT 1 ngày|VNPT|HTTP|1|800|2000|Proxy tốc độ cao</code>" + existing,
                ParseMode.Html, CancelKeyboard(), ct);
        }

        private async Task StartAdminPackagePriceInputAsync(ITelegramBotClient bot, long chatId, int packageId, int messageId, CancellationToken ct)
        {
            var session = new UserSession { Step = "admin_package_price", AdminTargetProxyPackageId = packageId, GuideMessageId = messageId };
            _sessions[chatId] = session;
            await ReplaceSessionPromptAsync(bot, chatId, session,
                "💰 <b>SỬA GIÁ BÁN PROXY</b>\nGửi giá bán mới bằng số. Ví dụ: <code>2500</code>",
                ParseMode.Html, CancelKeyboard(), ct);
        }

        private static bool TryParseAdminPackage(string text, out ProxyPackage package, out string error)
        {
            package = new ProxyPackage();
            error = string.Empty;
            var parts = text.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 7)
            {
                error = "Cần đủ 7 phần, ngăn cách bằng ký tự |.";
                return false;
            }
            if (!int.TryParse(parts[3], out var days) || days <= 0 || !TryParseAdminAmount(parts[4], out var original) || !TryParseAdminAmount(parts[5], out var selling))
            {
                error = "Số ngày hoặc giá chưa hợp lệ.";
                return false;
            }
            var type = parts[2].Trim().ToUpperInvariant();
            if (type is not "HTTP" and not "HTTPS" and not "SOCKS5")
            {
                error = "Loại proxy chỉ nhận HTTP, HTTPS hoặc SOCKS5.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = "Tên gói và nhóm không được để trống.";
                return false;
            }
            package.Name = parts[0];
            package.Category = parts[1];
            package.ProxyType = type == "SOCKS5" ? 1 : 0;
            package.PeriodDays = days;
            package.OriginalPrice = original;
            package.SellingPrice = selling;
            package.Description = string.IsNullOrWhiteSpace(parts[6]) ? null : parts[6];
            return true;
        }

        private async Task HandleAdminPackageInputAsync(ITelegramBotClient bot, long chatId, int userMessageId, string text, UserSession session, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (session.Step == "admin_package_price")
            {
                if (!TryParseAdminAmount(text, out var sellingPrice))
                {
                    await SendError(bot, chatId, session, "Giá bán chưa hợp lệ. Ví dụ: <code>2500</code>", ct);
                    return;
                }
                var target = await db.ProxyPackages.FindAsync(new object[] { session.AdminTargetProxyPackageId }, ct);
                if (target is null)
                {
                    _sessions.TryRemove(chatId, out _);
                    await bot.SendMessage(chatId, "⚠️ Gói proxy không còn tồn tại.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                    return;
                }
                target.SellingPrice = sellingPrice;
                target.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                _sessions.TryRemove(chatId, out _);
                await bot.SendMessage(chatId, $"✅ Đã đổi giá bán <b>{WebUtility.HtmlEncode(target.Name)}</b> thành <b>{sellingPrice:N0} Xu</b>.", ParseMode.Html, replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                return;
            }

            if (!TryParseAdminPackage(text, out var input, out var error))
            {
                await SendError(bot, chatId, session, WebUtility.HtmlEncode(error), ct);
                return;
            }
            if (session.Step == "admin_package_add")
            {
                input.IsActive = true;
                input.CreatedAt = DateTime.UtcNow;
                input.UpdatedAt = DateTime.UtcNow;
                db.ProxyPackages.Add(input);
                await db.SaveChangesAsync(ct);
                _sessions.TryRemove(chatId, out _);
                await bot.SendMessage(chatId, $"✅ Đã thêm gói proxy <b>{WebUtility.HtmlEncode(input.Name)}</b>.", ParseMode.Html, replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                return;
            }

            var package = await db.ProxyPackages.FindAsync(new object[] { session.AdminTargetProxyPackageId }, ct);
            if (package is null)
            {
                _sessions.TryRemove(chatId, out _);
                await bot.SendMessage(chatId, "⚠️ Gói proxy không còn tồn tại.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                return;
            }
            package.Name = input.Name;
            package.Category = input.Category;
            package.ProxyType = input.ProxyType;
            package.PeriodDays = input.PeriodDays;
            package.OriginalPrice = input.OriginalPrice;
            package.SellingPrice = input.SellingPrice;
            package.Description = input.Description;
            package.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _sessions.TryRemove(chatId, out _);
            await bot.SendMessage(chatId, $"✅ Đã cập nhật gói proxy <b>{WebUtility.HtmlEncode(package.Name)}</b>.", ParseMode.Html, replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
        }

        private async Task HandleAdminCallbackAsync(
            ITelegramBotClient bot,
            CallbackQuery callback,
            long chatId,
            CancellationToken ct)
        {
            var data = callback.Data ?? string.Empty;
            var messageId = callback.Message?.MessageId ?? 0;
            if (data == "admin_noop") return;
            if (data == "admin_menu")
            {
                // Returning from an input flow (for example Broadcast) must
                // discard that pending input so the next message is not sent.
                _sessions.TryRemove(chatId, out _);
                await ShowAdminPanelAsync(bot, chatId, messageId, ct);
                return;
            }
            if (data == "admin_exit")
            {
                _sessions.TryRemove(chatId, out var adminSession);
                await SendReplacingMessageAsync(
                    bot,
                    chatId,
                    messageId == 0 ? adminSession?.GuideMessageId ?? 0 : messageId,
                    MainMenuText(),
                    ParseMode.Html,
                    MainMenuKeyboard(),
                    ct);
                return;
            }
            if (data == "admin_stats")
            {
                await ShowAdminStatsAsync(bot, chatId, messageId, ct);
                return;
            }
            if (data == "admin_jobs")
            {
                await ShowAdminJobsAsync(bot, chatId, messageId, ct);
                return;
            }
            if (data == "admin_users_search")
            {
                await StartAdminUserSearchAsync(bot, chatId, messageId, ct);
                return;
            }
            if (data == "admin_broadcast")
            {
                await StartAdminBroadcastAsync(bot, chatId, messageId, ct);
                return;
            }
            if (data == "admin_broadcast_send")
            {
                if (_sessions.TryGetValue(chatId, out var broadcastSession)
                    && broadcastSession.Step == "admin_broadcast_confirm"
                    && !string.IsNullOrWhiteSpace(broadcastSession.AdminPendingBroadcast))
                {
                    broadcastSession.Step = "admin_broadcast_sending";
                    try { await bot.EditMessageReplyMarkup(chatId, messageId, replyMarkup: null, cancellationToken: ct); } catch { }
                    await SendAdminBroadcastAsync(bot, chatId, broadcastSession, ct);
                }
                else
                {
                    await bot.SendMessage(chatId, "⚠️ Thông báo đã hết hiệu lực hoặc đang gửi.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                }
                return;
            }
            if (data == "admin_bal_apply")
            {
                if (_sessions.TryGetValue(chatId, out var balanceSession)
                    && balanceSession.Step == "admin_balance_confirm")
                {
                    balanceSession.Step = "admin_balance_applying";
                    try { await bot.EditMessageReplyMarkup(chatId, messageId, replyMarkup: null, cancellationToken: ct); } catch { }
                    await ApplyAdminBalanceAsync(bot, chatId, balanceSession, ct);
                }
                else
                {
                    await bot.SendMessage(chatId, "⚠️ Yêu cầu điều chỉnh số dư đã hết hiệu lực hoặc đang xử lý.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
                }
                return;
            }

            var parts = data.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && parts[0] == "admin" && parts[1] == "users" && int.TryParse(parts[2], out var usersPage))
            {
                await ShowAdminUsersAsync(bot, chatId, usersPage, messageId, ct);
                return;
            }
            if (parts.Length == 4 && parts[0] == "admin" && parts[1] == "user"
                && int.TryParse(parts[2], out var userId) && int.TryParse(parts[3], out var returnPage))
            {
                await ShowAdminUserAsync(bot, chatId, userId, returnPage, messageId, ct);
                return;
            }
            if (parts.Length == 4 && parts[0] == "admin" && parts[1] == "bal"
                && parts[2] is "add" or "deduct" or "set" && int.TryParse(parts[3], out var balanceUserId))
            {
                await StartAdminBalanceInputAsync(bot, chatId, balanceUserId, parts[2], messageId, ct);
                return;
            }
            if (parts.Length == 3 && parts[0] == "admin" && parts[1] == "packages" && int.TryParse(parts[2], out var packagesPage))
            {
                await ShowAdminPackagesAsync(bot, chatId, packagesPage, messageId, ct);
                return;
            }
            if (parts.Length == 4 && parts[0] == "admin" && parts[1] == "package"
                && int.TryParse(parts[2], out var packageId) && int.TryParse(parts[3], out var packagePage))
            {
                await ShowAdminPackageAsync(bot, chatId, packageId, packagePage, messageId, ct);
                return;
            }
            if (data == "admin_package_add")
            {
                await StartAdminPackageInputAsync(bot, chatId, 0, messageId, ct);
                return;
            }
            if (parts.Length == 5 && parts[0] == "admin" && parts[1] == "package"
                && int.TryParse(parts[3], out var actionPackageId) && int.TryParse(parts[4], out var actionPage))
            {
                var action = parts[2];
                if (action == "price")
                {
                    await StartAdminPackagePriceInputAsync(bot, chatId, actionPackageId, messageId, ct);
                    return;
                }
                if (action == "edit")
                {
                    await StartAdminPackageInputAsync(bot, chatId, actionPackageId, messageId, ct);
                    return;
                }
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var package = await db.ProxyPackages.FindAsync(new object[] { actionPackageId }, ct);
                if (package is null)
                {
                    await ShowAdminPackagesAsync(bot, chatId, actionPage, messageId, ct);
                    return;
                }
                if (action == "toggle")
                {
                    package.IsActive = !package.IsActive;
                    package.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await ShowAdminPackageAsync(bot, chatId, actionPackageId, actionPage, messageId, ct);
                    return;
                }
                if (action == "delete")
                {
                    await SendNavigationScreenAsync(bot, chatId,
                        $"⚠️ <b>XÁC NHẬN XÓA GÓI</b>\nBạn sắp xóa <b>{WebUtility.HtmlEncode(package.Name)}</b>. Thao tác này không thể hoàn tác.",
                        ParseMode.Html,
                        new InlineKeyboardMarkup(new[]
                        {
                            new[] { InlineKeyboardButton.WithCallbackData("🗑 Xóa vĩnh viễn", $"admin_package_deleteyes_{actionPackageId}_{actionPage}") },
                            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Quay lại", $"admin_package_{actionPackageId}_{actionPage}") }
                        }),
                        ct,
                        messageId);
                    return;
                }
            }
            if (parts.Length == 5 && parts[0] == "admin" && parts[1] == "package" && parts[2] == "deleteyes"
                && int.TryParse(parts[3], out var deletePackageId) && int.TryParse(parts[4], out var deletePage))
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var package = await db.ProxyPackages.FindAsync(new object[] { deletePackageId }, ct);
                if (package is not null)
                {
                    db.ProxyPackages.Remove(package);
                    await db.SaveChangesAsync(ct);
                }
                await ShowAdminPackagesAsync(bot, chatId, deletePage, messageId, ct);
                return;
            }

            await bot.SendMessage(chatId, "⚠️ Chức năng quản trị này không còn hiệu lực. Mở lại /admin.", replyMarkup: AdminMenuKeyboard(), cancellationToken: ct);
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

            try
            {
                var userCommands = new[]
                {
                    new BotCommand { Command = "add", Description = "Đăng ký một tài khoản" },
                    new BotCommand { Command = "bulk", Description = "Đăng ký hàng loạt" },
                    new BotCommand { Command = "status", Description = "Theo dõi yêu cầu" },
                    new BotCommand { Command = "balance", Description = "Tài khoản của tôi" },
                    new BotCommand { Command = "buyproxy", Description = "Thuê proxy" },
                    new BotCommand { Command = "deposit", Description = "Nạp số dư" },
                    new BotCommand { Command = "menu", Description = "Mở lời chào" }
                };
                await _botClient.SetMyCommands(userCommands, cancellationToken: stoppingToken);
                foreach (var adminChatId in _adminChatIds)
                {
                    await _botClient.SetMyCommands(
                        userCommands.Append(new BotCommand { Command = "admin", Description = "Quản trị hệ thống" }),
                        scope: new BotCommandScopeChat { ChatId = adminChatId },
                        cancellationToken: stoppingToken);
                }

                // Some Telegram clients retain the attachment/microphone
                // affordance until the default menu button is explicitly set.
                await _botClient.SetChatMenuButton(
                    chatId: null,
                    menuButton: new MenuButtonCommands(),
                    cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not configure Telegram command menu.");
            }

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

            if (updateChatId != 0)
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

                if (cb.Data?.StartsWith("admin_", StringComparison.Ordinal) == true && !IsAdmin(cb.From?.Id ?? 0))
                {
                    await bot.AnswerCallbackQuery(cb.Id, "⛔ Bạn không có quyền quản trị.", showAlert: true, cancellationToken: ct);
                    return;
                }

                // Admin deposit approval / rejection
                if (cb.Data?.StartsWith("app_") == true || cb.Data?.StartsWith("rej_") == true)
                {
                    if (!IsAdmin(cb.From?.Id ?? 0))
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
                            await bot.EditMessageText(cbChat, cb.Message!.MessageId,
                                BuildBulkInputPrompt(sess),
                                ParseMode.Html, replyMarkup: BankInputKeyboard(), cancellationToken: ct);
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

                if (cb.Data == "bank_codes_back")
                {
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    if (_sessions.TryGetValue(cbChat, out var bankSession)
                        && (bankSession.Step == "s_bank" || bankSession.Step == "bulk")
                        && cb.Message is not null)
                    {
                        await RestoreBankInputAsync(
                            bot,
                            cbChat,
                            cb.Message.MessageId,
                            bankSession,
                            ct);
                    }
                    return;
                }

                if (cb.Data == "bank_codes")
                {
                    await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                    if (cb.Message is null
                        || !_sessions.TryGetValue(cbChat, out var bankSession)
                        || (bankSession.Step != "s_bank" && bankSession.Step != "bulk"))
                    {
                        return;
                    }

                    await ShowBankCatalogAsync(
                        bot,
                        cbChat,
                        cb.Message.MessageId,
                        bankSession,
                        ct);
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

                if (cb.Data?.StartsWith("admin_", StringComparison.Ordinal) == true)
                {
                    await HandleAdminCallbackAsync(bot, cb, cbChat, ct);
                    return;
                }

                if (cb.Data == "deposit_custom")
                {
                    await StartCustomDepositAmountAsync(
                        bot,
                        cbChat,
                        cb.Message?.MessageId ?? 0,
                        ct);
                    return;
                }

                if (cb.Data?.StartsWith("deposit_amount_", StringComparison.Ordinal) == true)
                {
                    if (long.TryParse(cb.Data["deposit_amount_".Length..], out var amount))
                    {
                        await CreateAndSendPaymentQrAsync(
                            bot,
                            cbChat,
                            amount,
                            cb.Message?.MessageId ?? 0,
                            0,
                            ct);
                    }
                    else
                    {
                        await bot.SendMessage(
                            cbChat,
                            "⚠️ Không thể đọc số tiền nạp. Vui lòng chọn lại.",
                            cancellationToken: ct);
                    }
                    return;
                }

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

                    case "history":
                        await ShowHistoryMenuAsync(bot, cbChat, cb.Message?.MessageId ?? 0, ct);
                        break;

                    case "history_purchases":
                        await ShowPurchaseHistoryAsync(bot, cbChat, cb.Message?.MessageId ?? 0, ct);
                        break;

                    case "history_accounts":
                        await ShowCreatedAccountHistoryAsync(bot, cbChat, cb.Message?.MessageId ?? 0, ct);
                        break;

                    case "history_back":
                        _sessions.TryRemove(cbChat, out _);
                        await SendReplacingMessageAsync(
                            bot,
                            cbChat,
                            cb.Message?.MessageId ?? 0,
                            MainMenuText(),
                            ParseMode.Html,
                            MainMenuKeyboard(),
                            ct);
                        break;

                    case "confirm_bulk_submit":
                        if (_sessions.TryGetValue(cbChat, out var bulkConfirmation)
                            && bulkConfirmation.Step == "bulk_confirming"
                            && bulkConfirmation.PendingBulkAccounts.Count > 0)
                        {
                            // Change state before awaiting Telegram so repeated taps cannot
                            // submit this batch more than once.
                            bulkConfirmation.Step = "bulk_submitting";
                            try
                            {
                                await bot.EditMessageReplyMarkup(
                                    cbChat,
                                    cb.Message!.MessageId,
                                    replyMarkup: null,
                                    cancellationToken: ct);
                            }
                            catch { }

                            await SubmitBulkAccountsAsync(bot, cbChat, bulkConfirmation, ct);
                        }
                        else
                        {
                            await bot.SendMessage(
                                cbChat,
                                "⚠️ Danh sách hàng loạt đã hết hiệu lực hoặc đang được xử lý.",
                                replyMarkup: MainMenuKeyboard(),
                                cancellationToken: ct);
                        }
                        break;

                    case "bulk_reenter":
                        if (_sessions.TryGetValue(cbChat, out var bulkReentry)
                            && bulkReentry.Step == "bulk_confirming")
                        {
                            bulkReentry.PendingBulkAccounts.Clear();
                            bulkReentry.Step = "bulk";
                            await ReplaceSessionPromptAsync(
                                bot,
                                cbChat,
                                bulkReentry,
                                "📋 <b>NHẬP LẠI DANH SÁCH HÀNG LOẠT</b>\n" +
                                "━━━━━━━━━━━━━━━━━━━━\n" +
                                "Dán lại toàn bộ danh sách, mỗi dòng theo định dạng:\n" +
                                "<code>HOTEN|TENNGANHANG|SDT|SOTK|PROXY</code>\n\n" +
                                "Danh sách trước chưa được chạy.",
                                ParseMode.Html,
                                CancelKeyboard(),
                                ct);
                        }
                        else
                        {
                            await bot.SendMessage(
                                cbChat,
                                "⚠️ Phiên nhập hàng loạt đã hết hiệu lực. Vui lòng bắt đầu lại.",
                                replyMarkup: MainMenuKeyboard(),
                                cancellationToken: ct);
                        }
                        break;

                    case "confirm_submit":
                        await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                        if (_sessions.TryGetValue(cbChat, out var sess) && sess.Step == "confirming")
                        {
                            // Set this before the first await so a second tap cannot enqueue
                            // the same account a second time.
                            sess.Step = "submitting";
                            try
                            {
                                await bot.EditMessageReplyMarkup(
                                    cbChat,
                                    cb.Message!.MessageId,
                                    replyMarkup: null,
                                    cancellationToken: ct);
                            }
                            catch
                            {
                                // The job is still safe to submit if Telegram cannot remove
                                // the buttons (for example, the message was deleted).
                            }

                            await SubmitAccount(bot, cbChat, sess, ct);
                        }
                        else
                        {
                            await bot.SendMessage(
                                cbChat,
                                "⚠️ Yêu cầu tạo tài khoản đã hết hiệu lực hoặc đang được xử lý.",
                                replyMarkup: MainMenuKeyboard(),
                                cancellationToken: ct);
                        }
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
                    await ShowDepositEntryAsync(
                        bot,
                        cbChat,
                        cb.Message?.MessageId ?? 0,
                        ct);
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

                foreach (var adminChatId in _adminChatIds)
                {
                    try
                    {
                        await bot.SendPhoto(adminChatId, InputFile.FromFileId(largestPhoto.FileId), caption: caption, parseMode: ParseMode.Html, replyMarkup: adminKeyboard, cancellationToken: ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to forward deposit receipt photo to admin {AdminChatId}.", adminChatId);
                    }
                }

                await bot.SendMessage(chatId, "✅ Đã tiếp nhận biên lai. Bộ phận hỗ trợ KJCBOT sẽ phản hồi sau khi kiểm tra.", replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                return;
            }

            if (msg.Text is not { } text) return;
            if (text.Trim().StartsWith("/setlog", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAdmin(msg.From?.Id ?? 0))
                {
                    await bot.SendMessage(chatId, "⛔ Chỉ admin mới được liên kết group log.", cancellationToken: ct);
                    return;
                }

                if (msg.Chat.Type is not ChatType.Group and not ChatType.Supergroup)
                {
                    await bot.SendMessage(chatId, "ℹ️ Hãy dùng lệnh này ngay trong group nhận log.", cancellationToken: ct);
                    return;
                }

                try
                {
                    File.WriteAllText(RuntimePaths.ActivityLogChatPath, chatId.ToString(CultureInfo.InvariantCulture));
                    await bot.SendMessage(
                        chatId,
                        "✅ <b>Đã liên kết group log vận hành.</b>\nTừ bây giờ bot sẽ báo bắt đầu, thành công và lỗi theo từng game tại đây.",
                        ParseMode.Html,
                        cancellationToken: ct);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Could not save activity-log group ChatId.");
                    await bot.SendMessage(chatId, "⚠️ Không thể lưu group log. Vui lòng thử lại.", cancellationToken: ct);
                }
                return;
            }

            text = text.Trim();
            if (IsAdmin(chatId))
            {
                // The admin reply keyboard sends ordinary text messages. Map
                // its labels before customer-menu labels, while all actual
                // customer buttons (e.g. "Tài khoản của tôi") remain normal.
                var adminText = NormalizeAdminSelection(text);
                text = adminText != text ? adminText : NormalizeMainMenuSelection(text);
            }
            else
            {
                text = NormalizeMainMenuSelection(text);
            }

            if (text.Equals("/admin", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAdmin(chatId))
                {
                    await bot.SendMessage(chatId, "⛔ Bạn không có quyền quản trị.", cancellationToken: ct);
                    return;
                }

                _sessions.TryRemove(chatId, out var previousAdminSession);
                await ShowAdminHomeAsync(bot, chatId, previousAdminSession?.GuideMessageId ?? 0, ct);
                return;
            }

            if (IsAdmin(chatId) && text == "/admin_stats")
            {
                await ShowAdminStatsAsync(bot, chatId, 0, ct);
                return;
            }

            if (IsAdmin(chatId) && text == "/admin_jobs")
            {
                await ShowAdminJobsAsync(bot, chatId, 0, ct);
                return;
            }

            if (IsAdmin(chatId) && text == "/admin_users")
            {
                await ShowAdminUsersAsync(bot, chatId, 0, 0, ct);
                return;
            }

            if (IsAdmin(chatId) && text == "/admin_packages")
            {
                await ShowAdminPackagesAsync(bot, chatId, 0, 0, ct);
                return;
            }

            if (IsAdmin(chatId) && text == "/admin_broadcast")
            {
                await StartAdminBroadcastAsync(bot, chatId, 0, ct);
                return;
            }


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

            // /status shortcut
            if (text.StartsWith("/status"))
            {
                var queueStatus = _jobQueue.GetSnapshot();
                await bot.SendMessage(chatId,
                    $"📊 <b>TRẠNG THÁI XỬ LÝ | {BrandName}</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                    $"🔄 Đang xử lý: <b>{queueStatus.Processing}/{_maxConcurrentJobs}</b> yêu cầu\n" +
                    $"⏳ Đang chờ: <b>{queueStatus.Pending}</b> yêu cầu\n" +
                    $"✅ Năng lực còn trống: <b>{Math.Max(0, _maxConcurrentJobs - queueStatus.Processing)}/{_maxConcurrentJobs}</b>",
                    ParseMode.Html,
                    replyMarkup: MainMenuKeyboard(),
                    cancellationToken: ct);
                return;
            }

            if (text.StartsWith("/history"))
            {
                _sessions.TryRemove(chatId, out _);
                await ShowHistoryMenuAsync(bot, chatId, 0, ct);
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
                await ShowDepositEntryAsync(bot, chatId, 0, ct);
                return;
            }

            // /emojiid (admin tool)
            if (text.StartsWith("/emojiid") && IsAdmin(chatId))
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

            if (IsAdmin(chatId) && session.Step == "admin_balance_amount")
            {
                await HandleAdminBalanceAmountAsync(bot, chatId, msg.MessageId, text, session, ct);
                return;
            }

            if (IsAdmin(chatId) && session.Step == "admin_user_search")
            {
                await HandleAdminUserSearchAsync(bot, chatId, msg.MessageId, text, session, ct);
                return;
            }

            if (IsAdmin(chatId) && session.Step == "admin_broadcast_text")
            {
                await HandleAdminBroadcastTextAsync(bot, chatId, msg.MessageId, text, session, ct);
                return;
            }

            if (IsAdmin(chatId) && session.Step is "admin_package_add" or "admin_package_edit" or "admin_package_price")
            {
                await HandleAdminPackageInputAsync(bot, chatId, msg.MessageId, text, session, ct);
                return;
            }

            if (session.Step == "deposit_amount")
            {
                await HandleDepositAmountInputAsync(
                    bot,
                    chatId,
                    msg.MessageId,
                    text,
                    session,
                    ct);
                return;
            }

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

        private static string NormalizeMainMenuSelection(string text)
        {
            // Reply-keyboard text can contain emoji variation selectors or
            // invisible spacing from some Telegram clients. Each button has a
            // unique stable emoji, with a short text fallback for accessibility.
            if (text.Contains("➕", StringComparison.Ordinal)
                || text.Contains("1 tài khoản", StringComparison.OrdinalIgnoreCase)) return "/add";
            if (text.Contains("📋", StringComparison.Ordinal)
                || text.Contains("hàng loạt", StringComparison.OrdinalIgnoreCase)) return "/bulk";
            if (text.Contains("📊", StringComparison.Ordinal)
                || text.Contains("Theo dõi", StringComparison.OrdinalIgnoreCase)) return "/status";
            if (text.Contains("📚", StringComparison.Ordinal)
                || text.Contains("Lịch sử", StringComparison.OrdinalIgnoreCase)) return "/history";
            if (text.Contains("👤", StringComparison.Ordinal)
                || text.Contains("Tài khoản của tôi", StringComparison.OrdinalIgnoreCase)) return "/balance";
            if (text.Contains("🌐", StringComparison.Ordinal)
                || text.Contains("Thuê proxy", StringComparison.OrdinalIgnoreCase)) return "/buyproxy";
            if (text.Contains("💰", StringComparison.Ordinal)
                || text.Contains("Nạp", StringComparison.OrdinalIgnoreCase)) return "/deposit";
            return text;
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

                case "confirming":
                case "submitting":
                    await bot.SendMessage(
                        chatId,
                        "Vui lòng dùng nút trong tin nhắn xác nhận tạo tài khoản.",
                        cancellationToken: ct);
                    return;

                case "bulk_confirming":
                case "bulk_submitting":
                    await bot.SendMessage(
                        chatId,
                        "Vui lòng dùng nút trong tin nhắn xác nhận đăng ký hàng loạt.",
                        cancellationToken: ct);
                    return;

                case "admin_balance_confirm":
                case "admin_balance_applying":
                case "admin_broadcast_confirm":
                case "admin_broadcast_sending":
                    await bot.SendMessage(
                        chatId,
                        "Vui lòng dùng nút trong tin nhắn xác nhận quản trị.",
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
                        BuildBankInputPrompt(),
                        ParseMode.Html, BankInputKeyboard(), ct, userMessageId);
                    break;

                case "s_bank":
                    if (!VietnamBankCatalog.TryNormalize(text, out var bankName, out var bankCode))
                    {
                        await ReplaceSessionPromptAsync(
                            bot,
                            chatId,
                            sess,
                            "⚠️ <b>Mã ngân hàng chưa được hỗ trợ</b>\n\n" +
                            "Hãy nhập đúng mã trong nút <b>Mã ngân hàng</b>.",
                            ParseMode.Html,
                            BankInputKeyboard(),
                            ct,
                            userMessageId);
                        return;
                    }
                    sess.BankName = bankName;
                    sess.BankCode = bankCode;
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

        private static string MaskPhone(string phone) =>
            phone.Length <= 6 ? "••••" : $"{phone[..3]}••••{phone[^3..]}";

        private static string MaskAccountNumber(string accountNumber) =>
            accountNumber.Length <= 4 ? "••••" : $"••••{accountNumber[^4..]}";

        private static string MaskProxy(string proxy)
        {
            if (string.IsNullOrWhiteSpace(proxy))
                return "Không dùng proxy";

            var parts = proxy.Split(':', StringSplitOptions.TrimEntries);
            return parts.Length >= 2
                ? $"{parts[0]}:{parts[1]} (đã ẩn thông tin đăng nhập)"
                : "Đã nhập proxy (đã ẩn)";
        }

        private async Task SendConfirmationAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            CancellationToken cancellationToken,
            int userMessageId = 0)
        {
            using var scope = _scopeFactory.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var user = await userService.GetUserByChatIdAsync(chatId, null, cancellationToken);
            var totalRegistrationPrice = CalculateRegistrationCharge(
                accountCount: 1,
                siteCount: session.SelectedGames.Count);

            await ReplaceSessionPromptAsync(
                bot,
                chatId,
                session,
                "🧾 <b>XÁC NHẬN TẠO TÀI KHOẢN</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                $"🎮 Site:      <b>{WebUtility.HtmlEncode(FormatSelectedGames(session.SelectedGames))}</b>\n" +
                $"📌 Số lượt:   <b>1 tài khoản trên {session.SelectedGames.Count} site</b>\n" +
                 $"👤 Họ tên:    <b>{WebUtility.HtmlEncode(session.FullName)}</b>\n" +
                 $"📞 SĐT:       <b>{WebUtility.HtmlEncode(MaskPhone(session.Phone))}</b>\n" +
                 $"🏦 Ngân hàng: <b>{WebUtility.HtmlEncode(FormatBankDisplay(session.BankName, session.BankCode))}</b>\n" +
                $"💳 STK:       <b>{WebUtility.HtmlEncode(MaskAccountNumber(session.SoTk))}</b>\n" +
                $"🌐 Proxy:     <code>{WebUtility.HtmlEncode(MaskProxy(session.Proxy))}</code>\n" +
                $"💰 Đơn giá:    <b>{_registrationPricePerSite:N0} KJC Xu/site</b>\n" +
                $"💵 Tổng phí:   <b>{totalRegistrationPrice:N0} KJC Xu</b>\n" +
                $"💳 Số dư:      <b>{user.Balance:N0} KJC Xu</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "Phí được trừ một lần khi xác nhận và đưa yêu cầu vào hàng đợi.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Xác nhận tạo", "confirm_submit")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✏️ Sửa thông tin", "edit_restart")
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("❌ Hủy", "cancel")
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
            using var paymentScope = _scopeFactory.CreateScope();
            var userService = paymentScope.ServiceProvider.GetRequiredService<IUserService>();
            var chargeReference = $"REG-{Guid.NewGuid():N}";
            var totalRegistrationPrice = CalculateRegistrationCharge(
                accountCount: 1,
                siteCount: sess.SelectedGames.Count);
            var deducted = await userService.DeductBalanceAsync(
                chatId,
                totalRegistrationPrice,
                $"Đăng ký 1 tài khoản trên {sess.SelectedGames.Count:N0} site - {FormatSelectedGames(sess.SelectedGames)}",
                chargeReference,
                ct);
            if (!deducted)
            {
                var user = await userService.GetUserByChatIdAsync(chatId, null, ct);
                sess.Step = "confirming";
                await ReplaceSessionPromptAsync(
                    bot,
                    chatId,
                    sess,
                    "⚠️ <b>SỐ DƯ KHÔNG ĐỦ</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    $"🎮 Số site: <b>{sess.SelectedGames.Count:N0}</b>\n" +
                    $"💰 Đơn giá: <b>{_registrationPricePerSite:N0} KJC Xu/site</b>\n" +
                    $"💵 Cần thanh toán: <b>{totalRegistrationPrice:N0} KJC Xu</b>\n" +
                    $"💳 Số dư hiện tại: <b>{user.Balance:N0} KJC Xu</b>\n\n" +
                    "Thông tin đã nhập vẫn được giữ. Nạp thêm số dư rồi nhấn kiểm tra lại.",
                    ParseMode.Html,
                    RegistrationPaymentRetryKeyboard(isBulk: false),
                    ct);
                return;
            }

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
                 BankCode = sess.BankCode,
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
                registrationPricePerSite: _registrationPricePerSite,
                chargeReferenceId: chargeReference,
                cancellationToken: ct);

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

            var lines = SplitBulkInputLines(text);

            if (lines.Count == 0)
            {
                const string format = "HOTEN|MABANK|SDT|SOTK|PROXY";

                await ReplaceSessionPromptAsync(bot, chatId, sess,
                    "⚠️ Không tìm thấy dòng nào đúng định dạng.\n\n" +
                    $"Mỗi dòng phải theo mẫu:\n<code>{format}</code>\n\n" +
                    "🔄 Yêu cầu vẫn được giữ. Vui lòng nhập lại:",
                    ParseMode.Html, BankInputKeyboard(), ct);
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
                if (!VietnamBankCatalog.TryNormalize(bank, out var normalizedBank, out var bankCode))
                {
                    errors.AppendLine($"• Dòng {lineNum}: Mã ngân hàng không được hỗ trợ ({bank})");
                    continue;
                }
                bank = normalizedBank;
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
                    BankCode      = bankCode,
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
                    ParseMode.Html, BankInputKeyboard(), ct);
                return;
            }

            foreach (var preparedAccount in validAccounts)
            {
                ApplySelectedSites(preparedAccount, sess.SelectedGames);
            }

            sess.PendingBulkAccounts.Clear();
            sess.PendingBulkAccounts.AddRange(validAccounts);
            sess.Step = "bulk_confirming";

            using var paymentScope = _scopeFactory.CreateScope();
            var userService = paymentScope.ServiceProvider.GetRequiredService<IUserService>();
            var user = await userService.GetUserByChatIdAsync(chatId, null, ct);
            var totalRegistrationPrice = CalculateRegistrationCharge(
                validAccounts.Count,
                sess.SelectedGames.Count);

            await ReplaceSessionPromptAsync(bot, chatId, sess,
                "🧾 <b>XÁC NHẬN ĐĂNG KÝ HÀNG LOẠT</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                $"👥 Số tài khoản hợp lệ: <b>{validAccounts.Count}</b>\n" +
                $"🎮 Site đã chọn: <b>{WebUtility.HtmlEncode(FormatSelectedGames(sess.SelectedGames))}</b>\n" +
                $"🚀 Tổng lượt đăng ký: <b>{validAccounts.Count * sess.SelectedGames.Count}</b>\n" +
                $"⚙️ Tối đa chạy cùng lúc: <b>{_maxConcurrentJobs} tài khoản</b>\n" +
                $"💰 Đơn giá: <b>{_registrationPricePerSite:N0} KJC Xu/site</b>\n" +
                $"💵 Tổng phí: <b>{totalRegistrationPrice:N0} KJC Xu</b>\n" +
                $"💳 Số dư: <b>{user.Balance:N0} KJC Xu</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "Phí được trừ khi nhấn <b>Xác nhận chạy</b>.",
                ParseMode.Html,
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("✅ Xác nhận chạy", "confirm_bulk_submit") },
                    new[] { InlineKeyboardButton.WithCallbackData("✏️ Nhập lại danh sách", "bulk_reenter") },
                    new[] { InlineKeyboardButton.WithCallbackData("❌ Hủy", "cancel") }
                }),
                ct,
                userMessageId);
        }

        private async Task SubmitBulkAccountsAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            CancellationToken ct)
        {
            var accounts = session.PendingBulkAccounts.ToList();
            if (accounts.Count == 0)
            {
                await bot.SendMessage(chatId,
                    "⚠️ Danh sách đăng ký hàng loạt đã hết hiệu lực. Vui lòng nhập lại.",
                    replyMarkup: MainMenuKeyboard(),
                    cancellationToken: ct);
                return;
            }

            var totalRegistrationPrice = CalculateRegistrationCharge(
                accounts.Count,
                session.SelectedGames.Count);
            var batchId = Guid.NewGuid();
            using var paymentScope = _scopeFactory.CreateScope();
            var userService = paymentScope.ServiceProvider.GetRequiredService<IUserService>();
            var chargeReference = $"REG-{batchId:N}";
            var deducted = await userService.DeductBalanceAsync(
                chatId,
                totalRegistrationPrice,
                $"Đăng ký {accounts.Count:N0} tài khoản × {session.SelectedGames.Count:N0} site - {FormatSelectedGames(session.SelectedGames)}",
                chargeReference,
                ct);
            if (!deducted)
            {
                var user = await userService.GetUserByChatIdAsync(chatId, null, ct);
                session.Step = "bulk_confirming";
                await ReplaceSessionPromptAsync(
                    bot,
                    chatId,
                    session,
                    "⚠️ <b>SỐ DƯ KHÔNG ĐỦ</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    $"👥 Số tài khoản: <b>{accounts.Count:N0}</b>\n" +
                    $"🎮 Số site: <b>{session.SelectedGames.Count:N0}</b>\n" +
                    $"💰 Đơn giá: <b>{_registrationPricePerSite:N0} KJC Xu/site</b>\n" +
                    $"💵 Cần thanh toán: <b>{totalRegistrationPrice:N0} KJC Xu</b>\n" +
                    $"💳 Số dư hiện tại: <b>{user.Balance:N0} KJC Xu</b>\n\n" +
                    "Danh sách vẫn được giữ. Nạp thêm số dư rồi nhấn kiểm tra lại.",
                    ParseMode.Html,
                    RegistrationPaymentRetryKeyboard(isBulk: true),
                    ct);
                return;
            }

            _sessions.TryRemove(chatId, out _);
            await bot.SendMessage(chatId,
                $"🚀 <b>Đã xác nhận {accounts.Count} tài khoản.</b>\n" +
                $"💰 Đã thanh toán: <b>{totalRegistrationPrice:N0} KJC Xu</b>\n" +
                $"Các tài khoản sẽ được chạy tuần tự (tối đa {_maxConcurrentJobs} cùng lúc).",
                ParseMode.Html,
                cancellationToken: ct);

            var jobs = new List<RegistrationJob>(accounts.Count);
            foreach (var account in accounts)
            {
                jobs.Add(await _resultSender.CreateJobAsync(
                    chatId,
                    account,
                    isBulk: true,
                    batchId: batchId,
                    registrationPricePerSite: _registrationPricePerSite,
                    chargeReferenceId: chargeReference,
                    cancellationToken: ct));
            }

            _profileRepository.AddRange(accounts);
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
