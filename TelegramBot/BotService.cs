using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace BAOCODETOOL.TelegramBot
{
    // Lưu trạng thái từng bước nhập của user
    class UserSession
    {
        public string Step { get; set; } = "idle"; // idle | s_game | s_name | s_phone | s_bank | s_sotk | s_proxy | confirming | bulk | s_bulk_game
        public HashSet<string> SelectedGames { get; set; } = new();
        public string Game => SelectedGames.Count > 0 ? string.Join(", ", SelectedGames) : "ALL";
        public string FullName { get; set; } = "";
        public string Phone { get; set; } = "";
        public string BankName { get; set; } = "";
        public string SoTk { get; set; } = "";
        public string Proxy { get; set; } = "";
        // Lưu messageId của tin nhắn hướng dẫn để edit lại
        public int GuideMessageId { get; set; } = 0;
    }

    public class BotService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly IJobQueueService _jobQueue;
        private readonly IResultSenderService _resultSender;
        private readonly IProfileRepository _profileRepository;
        private readonly ILogger<BotService> _logger;
        private readonly long _adminChatId;
        private readonly int _maxConcurrentJobs;
        private readonly bool _allowDirectConnectionForTesting;

        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private readonly HashSet<long> _allowedUsers;
        private static readonly string AllowedUsersFilePath = Path.Combine(AppContext.BaseDirectory, "allowed_users.json");
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        // ── Helpers ───────────────────────────────────────────────────────────
        private static InlineKeyboardMarkup CancelKeyboard() =>
            new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel"));

        private static InlineKeyboardMarkup MainMenuKeyboard() =>
            new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("➕ Thêm 1 tài khoản (Từng bước)", "start_add") },
                new[] { InlineKeyboardButton.WithCallbackData("📋 Nhập nhiều tài khoản (Hàng loạt)", "start_bulk") },
                new[] { InlineKeyboardButton.WithCallbackData("📊 Xem trạng thái hệ thống", "status") }
            });

        private static string MainMenuText() =>
            "🤖 <b>BAOCODETOOL – Đăng ký tự động</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━\n" +
            "Chào mừng! Chọn thao tác phù hợp:\n\n" +
            "➕ <b>Từng bước</b> – Nhập 1 tài khoản có hướng dẫn\n" +
            "📋 <b>Hàng loạt</b> – Dán ngay danh sách có sẵn (nhiều acc)\n";

        private static InlineKeyboardMarkup BuildGameSelectionKeyboard(HashSet<string> selectedGames, bool isBulk)
        {
            string pfx = isBulk ? "bgame_" : "game_";
            string textRR88 = selectedGames.Contains("RR88") ? "✅ RR88" : "RR88";
            string textMM88 = selectedGames.Contains("MM88") ? "✅ MM88" : "MM88";
            string textXX88 = selectedGames.Contains("XX88") ? "✅ XX88" : "XX88";

            return new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData(textRR88, $"toggle_{pfx}RR88"), InlineKeyboardButton.WithCallbackData(textMM88, $"toggle_{pfx}MM88"), InlineKeyboardButton.WithCallbackData(textXX88, $"toggle_{pfx}XX88") },
                new[] { InlineKeyboardButton.WithCallbackData("➡️ Tiếp tục", $"done_{pfx}") },
                new[] { InlineKeyboardButton.WithCallbackData("🔙 Hủy", "cancel") }
            });
        }

        private UserSession GetSession(long chatId) =>
            _sessions.GetOrAdd(chatId, _ => new UserSession());

        private static void ApplySelectedSites(AccountModel account, IReadOnlySet<string> selectedGames)
        {
            foreach (var siteRun in account.SiteRuns)
            {
                siteRun.IsSelected = selectedGames.Count == 0
                    ? siteRun.Site is TargetSite.RR88 or TargetSite.MM88 or TargetSite.XX88
                    : selectedGames.Contains(siteRun.Site.ToString());
            }
        }

        private static bool SelectedSitesRequireProxy(IReadOnlySet<string> selectedGames) =>
            selectedGames.Count == 0 || selectedGames.Contains(TargetSite.XX88.ToString());

        // ─────────────────────────────────────────────────────────────────────
        public BotService(
            ITelegramBotClient botClient,
            IJobQueueService jobQueue,
            IResultSenderService resultSender,
            IProfileRepository profileRepository,
            IOptions<TelegramOptions> telegramOptions,
            IOptions<AutomationOptions> automationOptions,
            ILogger<BotService> logger)
        {
            _botClient = botClient;
            _jobQueue = jobQueue;
            _resultSender = resultSender;
            _profileRepository = profileRepository;
            _logger = logger;
            _adminChatId = telegramOptions.Value.AdminChatId;
            _maxConcurrentJobs = automationOptions.Value.MaxConcurrentJobs;
            _allowDirectConnectionForTesting = automationOptions.Value.AllowDirectConnectionForTesting;
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
            User bot;
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

            _botClient.StartReceiving(
                HandleUpdateAsync, HandlePollingErrorAsync,
                new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
                stoppingToken);

            _logger.LogInformation("Telegram connected. Bot={BotUsername}", bot.Username);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
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
                            await bot.EditMessageText(cbChat, cb.Message!.MessageId,
                                $"✅ Đã cấp quyền cho ID: <code>{uid}</code>", ParseMode.Html, cancellationToken: ct);
                            await bot.SendMessage(uid,
                                "🎉 <b>Bạn đã được Admin duyệt!</b>\n\nNhấn nút bên dưới để bắt đầu:",
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
                        if (sess.SelectedGames.Contains(game)) sess.SelectedGames.Remove(game);
                        else sess.SelectedGames.Add(game);

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
                        string gameStr = sess.Game;

                        if (isBulk)
                        {
                            sess.Step = "bulk";
                            var bulkFormat = _allowDirectConnectionForTesting
                                ? "HOTEN|TENNGANHANG|SDT|SOTK[|PROXY]"
                                : "HOTEN|TENNGANHANG|SDT|SOTK|PROXY";
                            var bulkExample = _allowDirectConnectionForTesting
                                ? "<code>Nguyen Van A|Vietcombank|0987654321|1234567890</code>\n" +
                                  "<code>Tran Thi B|Techcombank|0912345678|9876543210|5.6.7.8:3128:u:p</code>"
                                : "<code>Nguyen Van A|Vietcombank|0987654321|1234567890|1.2.3.4:8080:u:p</code>\n" +
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

                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);

                switch (cb.Data)
                {
                    case "cancel":
                        _sessions.TryRemove(cbChat, out _);
                        await bot.SendMessage(cbChat, MainMenuText(), ParseMode.Html,
                            replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
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
                        await bot.SendMessage(cbChat,
                            $"📊 <b>Trạng thái hệ thống</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                            $"🔄 Đang chạy: <b>{queueStatus.Processing}/{_maxConcurrentJobs}</b> slot\n" +
                            $"⏳ Đang chờ: <b>{queueStatus.Pending}</b>\n" +
                            $"✅ Slot còn trống: <b>{Math.Max(0, _maxConcurrentJobs - queueStatus.Processing)}/{_maxConcurrentJobs}</b>",
                            ParseMode.Html, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
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
                    var bulkSess = new UserSession { Step = "s_bulk_game" };
                    _sessions[cbChat] = bulkSess;
                    await bot.SendMessage(cbChat,
                        "📋 <b>NHẬP HÀNG LOẠT</b>\n" +
                        "━━━━━━━━━━━━━━━━━━━━\n" +
                        "Chọn <b>Nhà cái</b> để đăng ký hàng loạt:",
                        ParseMode.Html, 
                        replyMarkup: BuildGameSelectionKeyboard(bulkSess.SelectedGames, true), cancellationToken: ct);
                    break;

                case "edit_restart":
                    _sessions.TryRemove(cbChat, out _);
                    await StartAddFlow(bot, cbChat, ct);
                    break;
                }
                return;
            }

            // ── Text messages ─────────────────────────────────────────────────
            if (update.Message is not { } msg || msg.Text is not { } text) return;
            var chatId = msg.Chat.Id;
            text = text.Trim();

            // Chưa được duyệt
            if (!_allowedUsers.Contains(chatId))
            {
                await bot.SendMessage(chatId,
                    "⏳ <b>Chưa có quyền truy cập.</b>\nYêu cầu của bạn đã được gửi Admin. Vui lòng chờ phê duyệt...",
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
                _sessions.TryRemove(chatId, out _);
                await bot.SendMessage(chatId, MainMenuText(), ParseMode.Html,
                    replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
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
                var sess = new UserSession { Step = "s_bulk_game" };
                _sessions[chatId] = sess;
                await bot.SendMessage(chatId,
                    "📋 <b>NHẬP HÀNG LOẠT</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "Chọn <b>Nhà cái</b> để đăng ký hàng loạt:",
                    ParseMode.Html, 
                    replyMarkup: BuildGameSelectionKeyboard(sess.SelectedGames, true), cancellationToken: ct);
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
                await HandleBulkInput(bot, chatId, text, ct);
                return;
            }

            await HandleStepInput(bot, chatId, text, session, ct);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Bắt đầu flow nhập từng bước
        private async Task StartAddFlow(ITelegramBotClient bot, long chatId, CancellationToken ct)
        {
            var sess = new UserSession { Step = "s_game" };
            _sessions[chatId] = sess;

            var sent = await bot.SendMessage(chatId,
                "📝 <b>THÊM TÀI KHOẢN MỚI</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n\n" +
                "Bước <b>1/6</b> — Chọn <b>Nhà cái</b> để đăng ký:",
                ParseMode.Html, 
                replyMarkup: BuildGameSelectionKeyboard(sess.SelectedGames, false), cancellationToken: ct);

            sess.GuideMessageId = sent.MessageId;
        }

        // ══════════════════════════════════════════════════════════════════════
        private async Task HandleStepInput(ITelegramBotClient bot, long chatId, string text, UserSession sess, CancellationToken ct)
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
                    await bot.SendMessage(chatId, "Vui lòng chọn Nhà cái bằng cách nhấn vào các nút ở trên.", cancellationToken: ct);
                    return;

                case "s_name":
                    if (text.Length < 3 || text.Any(char.IsDigit))
                    {
                        await SendError(bot, chatId, "Họ tên phải có ít nhất 3 ký tự và không chứa số.\n<i>VD: Nguyen Van A</i>", ct);
                        return;
                    }
                    sess.FullName = text;
                    sess.Step = "s_phone";
                    await bot.SendMessage(chatId,
                        "Bước <b>3/6</b> — Nhập <b>Số điện thoại</b>:\n\n" +
                        "<i>Yêu cầu: đúng 10 chữ số, bắt đầu bằng 0\nVD: 0987654321</i>",
                        ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                    break;

                case "s_phone":
                    var phone = text.Replace(" ", "");
                    if (!Regex.IsMatch(phone, @"^0\d{9}$"))
                    {
                        await SendError(bot, chatId, "Số điện thoại không hợp lệ!\n<b>Yêu cầu:</b> Đúng 10 chữ số, bắt đầu bằng 0.\n<i>VD: 0987654321</i>", ct);
                        return;
                    }
                    sess.Phone = phone;
                    sess.Step = "s_bank";
                    await bot.SendMessage(chatId,
                        "Bước <b>4/6</b> — Nhập <b>Tên ngân hàng</b>:\n\n" +
                        "<i>VD: Vietcombank, Techcombank, MBBank, ACB, BIDV...</i>",
                        ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                    break;

                case "s_bank":
                    if (text.Length < 2)
                    {
                        await SendError(bot, chatId, "Tên ngân hàng quá ngắn. Hãy nhập đầy đủ.\n<i>VD: Vietcombank</i>", ct);
                        return;
                    }
                    sess.BankName = text;
                    sess.Step = "s_sotk";
                    await bot.SendMessage(chatId,
                        "Bước <b>5/6</b> — Nhập <b>Số tài khoản ngân hàng</b>:\n\n" +
                        "<i>Yêu cầu: chỉ gồm số, độ dài 6-20 ký tự\nVD: 1234567890</i>",
                        ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                    break;

                case "s_sotk":
                    var sotk = text.Replace(" ", "");
                    if (!Regex.IsMatch(sotk, @"^\d{6,20}$"))
                    {
                        await SendError(bot, chatId, "Số tài khoản không hợp lệ!\n<b>Yêu cầu:</b> Chỉ gồm chữ số, dài 6–20 ký tự.\n<i>VD: 1234567890</i>", ct);
                        return;
                    }
                    sess.SoTk = sotk;

                    if (_allowDirectConnectionForTesting && !SelectedSitesRequireProxy(sess.SelectedGames))
                    {
                        sess.Proxy = string.Empty;
                        sess.Step = "confirming";
                        await SendConfirmationAsync(bot, chatId, sess, ct);
                        break;
                    }

                    sess.Step = "s_proxy";
                    await bot.SendMessage(chatId,
                        "Bước <b>6/6</b> — Nhập <b>Proxy</b>:\n\n" +
                        "📌 <b>Định dạng:</b>\n" +
                        "<code>ip:port</code>  hoặc  <code>ip:port:user:pass</code>\n\n" +
                        "<i>VD: 123.45.67.89:8080:username:password</i>",
                        ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                    break;

                case "s_proxy":
                    var proxy = text.Replace("http://", "").Replace("https://", "").Replace(" ", "");
                    if (string.IsNullOrWhiteSpace(proxy)
                        && _allowDirectConnectionForTesting
                        && !SelectedSitesRequireProxy(sess.SelectedGames))
                    {
                        sess.Proxy = string.Empty;
                        sess.Step = "confirming";
                        await SendConfirmationAsync(bot, chatId, sess, ct);
                        break;
                    }

                    var proxyParts = proxy.Split(':');
                    if (proxyParts.Length < 2 || !int.TryParse(proxyParts[1], out _))
                    {
                        await SendError(bot, chatId,
                            "Proxy sai định dạng!\n\n📌 <b>Định dạng đúng:</b>\n" +
                            "<code>ip:port</code>  hoặc  <code>ip:port:user:pass</code>\n\n" +
                            "<i>VD: 123.45.67.89:8080:user:pass</i>", ct);
                        return;
                    }
                    sess.Proxy = proxy;
                    sess.Step = "confirming";
                    await SendConfirmationAsync(bot, chatId, sess, ct);
                    break;
            }
        }

        private static async Task SendError(ITelegramBotClient bot, long chatId, string msg, CancellationToken ct)
        {
            await bot.SendMessage(chatId,
                $"⚠️ <b>Thông tin chưa đúng</b>\n\n{msg}\n\n🔄 Vui lòng nhập lại:",
                ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
        }

        private static async Task SendConfirmationAsync(
            ITelegramBotClient bot,
            long chatId,
            UserSession session,
            CancellationToken cancellationToken)
        {
            var proxyText = string.IsNullOrWhiteSpace(session.Proxy)
                ? "Không dùng proxy (kết nối trực tiếp)"
                : session.Proxy;

            await bot.SendMessage(
                chatId,
                "📋 <b>XÁC NHẬN THÔNG TIN</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                $"🎮 Nhà cái:  <b>{WebUtility.HtmlEncode(session.Game)}</b>\n" +
                $"👤 Họ tên:    <b>{WebUtility.HtmlEncode(session.FullName)}</b>\n" +
                $"📞 SĐT:       <b>{WebUtility.HtmlEncode(session.Phone)}</b>\n" +
                $"🏦 Ngân hàng: <b>{WebUtility.HtmlEncode(session.BankName)}</b>\n" +
                $"💳 STK:       <b>{WebUtility.HtmlEncode(session.SoTk)}</b>\n" +
                $"🌐 Proxy:     <code>{WebUtility.HtmlEncode(proxyText)}</code>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "Thông tin đúng chưa? Nhấn <b>Xác nhận</b> để chạy.",
                ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Xác nhận & Chạy", "confirm_submit"),
                        InlineKeyboardButton.WithCallbackData("🔄 Nhập lại", "edit_restart")
                    }
                }),
                cancellationToken: cancellationToken);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Tạo tài khoản và chạy
        private async Task SubmitAccount(ITelegramBotClient bot, long chatId, UserSession sess, CancellationToken ct)
        {
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

            _profileRepository.Add(account);

            var queueStatus = _jobQueue.GetSnapshot();
            if (queueStatus.Processing >= _maxConcurrentJobs)
            {
                await bot.SendMessage(
                    chatId,
                    "⏳ <b>Hàng đợi đang đầy.</b>\nYêu cầu của bạn đã được xếp hàng, sẽ chạy khi có slot trống...",
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
        private async Task HandleBulkInput(ITelegramBotClient bot, long chatId, string text, CancellationToken ct)
        {
            _sessions.TryRemove(chatId, out var sess);
            var selectedGames = sess?.SelectedGames ?? new HashSet<string>();

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(l => l.Contains('|'))
                            .ToList();

            if (lines.Count == 0)
            {
                var format = _allowDirectConnectionForTesting
                    ? "HOTEN|TENNGANHANG|SDT|SOTK[|PROXY]"
                    : "HOTEN|TENNGANHANG|SDT|SOTK|PROXY";

                await bot.SendMessage(chatId,
                    "⚠️ Không tìm thấy dòng nào đúng định dạng.\n\n" +
                    $"Mỗi dòng phải theo mẫu:\n<code>{format}</code>",
                    ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                return;
            }

            var errors = new System.Text.StringBuilder();
            var validAccounts = new System.Collections.Generic.List<AccountModel>();
            int lineNum = 0;

            foreach (var line in lines)
            {
                lineNum++;
                var p = line.Split('|', StringSplitOptions.TrimEntries);
                var validColumnCount = _allowDirectConnectionForTesting
                    ? p.Length is 4 or 5
                    : p.Length == 5;

                if (!validColumnCount)
                {
                    var expectedColumns = _allowDirectConnectionForTesting ? "4 hoặc 5" : "5";
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
                if (SelectedSitesRequireProxy(selectedGames) && string.IsNullOrWhiteSpace(proxy))
                {
                    errors.AppendLine($"- Dong {lineNum}: XX88 can proxy HTTP/HTTPS de chay direct.");
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

            // Báo lỗi nếu có dòng không hợp lệ
            if (errors.Length > 0)
            {
                await bot.SendMessage(chatId,
                    $"⚠️ <b>Một số dòng bị lỗi và đã bị bỏ qua:</b>\n{WebUtility.HtmlEncode(errors.ToString())}",
                    ParseMode.Html, cancellationToken: ct);
            }

            if (validAccounts.Count == 0)
            {
                await bot.SendMessage(chatId,
                    "❌ Không có tài khoản hợp lệ nào để chạy. Vui lòng kiểm tra lại định dạng.",
                    ParseMode.Html, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                return;
            }

            // Báo trước số lượng
            await bot.SendMessage(chatId,
                $"🚀 <b>Đã tiếp nhận {validAccounts.Count} tài khoản hợp lệ.</b>\n" +
                $"Các tài khoản sẽ được chạy tuần tự (tối đa {_maxConcurrentJobs} cùng lúc).",
                ParseMode.Html, cancellationToken: ct);

            var batchId = Guid.NewGuid();

            foreach (var preparedAccount in validAccounts)
            {
                ApplySelectedSites(preparedAccount, selectedGames);
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
