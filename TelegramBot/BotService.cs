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
using BAOCODETOOL.Models;
using BAOCODETOOL.ViewModels;
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
        private readonly TelegramBotClient _botClient;
        private readonly MainViewModel _mainViewModel;
        private CancellationTokenSource? _cts;

        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private readonly HashSet<long> _allowedUsers;
        // Đếm số job đang chờ/chạy của từng user – chặn nhập thêm khi > 0
        private readonly ConcurrentDictionary<long, int> _userPendingJobs = new();

        private const long ADMIN_CHAT_ID = 1853328773;
        private static readonly SemaphoreSlim _queue = new SemaphoreSlim(3, 3);
        private static readonly string AllowedUsersFilePath = Path.Combine(AppContext.BaseDirectory, "allowed_users.json");
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private const string EMOJI_LOADING_ID = "";
        private const string EMOJI_SUCCESS_ID = "";
        private const string EMOJI_FAIL_ID = "";
        private const string EMOJI_GAME_ID = "";
        private const string EMOJI_GIFT_ID = "";

        private static string E(string id, string fb) =>
            string.IsNullOrWhiteSpace(id) || id.StartsWith("PASTE_")
                ? fb
                : $"<tg-emoji emoji-id=\"{id}\">{fb}</tg-emoji>";

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

        // ─────────────────────────────────────────────────────────────────────
        public BotService(string botToken, MainViewModel mainViewModel)
        {
            _botClient = new TelegramBotClient(botToken);
            _mainViewModel = mainViewModel;
            _allowedUsers = LoadAllowedUsers();
        }

        private HashSet<long> LoadAllowedUsers()
        {
            var set = new HashSet<long> { ADMIN_CHAT_ID };
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

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _botClient.StartReceiving(
                HandleUpdateAsync, HandlePollingErrorAsync,
                new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
                _cts.Token);
            Console.WriteLine("Telegram bot started.");
        }

        public void Stop() => _cts?.Cancel();

        // ══════════════════════════════════════════════════════════════════════
        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            // ── Callback queries ─────────────────────────────────────────────
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } cb)
            {
                var cbChat = cb.Message?.Chat.Id ?? 0;
                if (cbChat == 0) return;

                // Admin approve / reject
                if (cb.Data?.StartsWith("approve_") == true && cbChat == ADMIN_CHAT_ID)
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

                if (cb.Data?.StartsWith("reject_") == true && cbChat == ADMIN_CHAT_ID)
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
                            await bot.EditMessageText(cbChat, cb.Message!.MessageId,
                                $"📋 <b>NHẬP HÀNG LOẠT ({gameStr})</b>\n" +
                                "━━━━━━━━━━━━━━━━━━━━\n" +
                                "Mỗi dòng 1 tài khoản theo định dạng:\n" +
                                "<code>HOTEN|TENNGANHANG|SDT|SOTK|PROXY</code>\n\n" +
                                "📌 <b>Ví dụ:</b>\n" +
                                "<code>Nguyen Van A|Vietcombank|0987654321|1234567890|1.2.3.4:8080:u:p</code>\n" +
                                "<code>Tran Thi B|Techcombank|0912345678|9876543210|5.6.7.8:3128:u:p</code>\n\n" +
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
                    if (_userPendingJobs.GetValueOrDefault(cbChat) > 0)
                    {
                        await bot.AnswerCallbackQuery(cb.Id, "⏳ Hệ thống đang chạy! Vui lòng đợi hoàn thành trước.", showAlert: true, cancellationToken: ct);
                        return;
                    }
                    await StartAddFlow(bot, cbChat, ct);
                    break;

                    case "status":
                        int running = _queue.CurrentCount < 3 ? 3 - _queue.CurrentCount : 0;
                        await bot.SendMessage(cbChat,
                            $"📊 <b>Trạng thái hệ thống</b>\n━━━━━━━━━━━━━━━━━━━━\n" +
                            $"🔄 Đang chạy: <b>{running}/3</b> slot\n" +
                            $"✅ Slot còn trống: <b>{_queue.CurrentCount}/3</b>",
                            ParseMode.Html, replyMarkup: MainMenuKeyboard(), cancellationToken: ct);
                        break;

                    case "confirm_submit":
                        if (_sessions.TryGetValue(cbChat, out var sess) && sess.Step == "confirming")
                            await SubmitAccount(bot, cbChat, sess, ct);
                        break;

                case "start_bulk":
                    if (_userPendingJobs.GetValueOrDefault(cbChat) > 0)
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
                    await bot.SendMessage(ADMIN_CHAT_ID,
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
            if (text.StartsWith("/emojiid") && chatId == ADMIN_CHAT_ID)
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
            if (_userPendingJobs.GetValueOrDefault(chatId) > 0)
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
                    sess.Step = "s_proxy";
                    await bot.SendMessage(chatId,
                        "Bước <b>6/6</b> — Nhập <b>Proxy</b>:\n\n" +
                        "📌 <b>Định dạng bắt buộc:</b>\n" +
                        "<code>ip:port</code>  hoặc  <code>ip:port:user:pass</code>\n\n" +
                        "<i>VD: 123.45.67.89:8080:username:password</i>",
                        ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
                    break;

                case "s_proxy":
                    var proxy = text.Replace("http://", "").Replace("https://", "").Replace(" ", "");
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

                    // Hiển thị tóm tắt để xác nhận
                    await bot.SendMessage(chatId,
                        "📋 <b>XÁC NHẬN THÔNG TIN</b>\n" +
                        "━━━━━━━━━━━━━━━━━━━━\n" +
                        $"🎮 Nhà cái:  <b>{WebUtility.HtmlEncode(sess.Game)}</b>\n" +
                        $"👤 Họ tên:    <b>{WebUtility.HtmlEncode(sess.FullName)}</b>\n" +
                        $"📞 SĐT:       <b>{WebUtility.HtmlEncode(sess.Phone)}</b>\n" +
                        $"🏦 Ngân hàng: <b>{WebUtility.HtmlEncode(sess.BankName)}</b>\n" +
                        $"💳 STK:       <b>{WebUtility.HtmlEncode(sess.SoTk)}</b>\n" +
                        $"🌐 Proxy:     <code>{WebUtility.HtmlEncode(sess.Proxy)}</code>\n" +
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
                        }), cancellationToken: ct);
                    break;
            }
        }

        private static async Task SendError(ITelegramBotClient bot, long chatId, string msg, CancellationToken ct)
        {
            await bot.SendMessage(chatId,
                $"⚠️ <b>Thông tin chưa đúng</b>\n\n{msg}\n\n🔄 Vui lòng nhập lại:",
                ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Tạo tài khoản và chạy
        private async Task SubmitAccount(ITelegramBotClient bot, long chatId, UserSession sess, CancellationToken ct)
        {
            _sessions.TryRemove(chatId, out _);

            var rng = new Random();
            // Username có nghĩa: tên người dùng viết thường không dấu + số ngẫu nhiên
            var usernameBase = RemoveDiacritics(sess.FullName.Split(' ').Last().ToLower());
            if (usernameBase.Length < 3) usernameBase = RemoveDiacritics(sess.FullName.Replace(" ", "").ToLower());

            string randomLetters = new string(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz", 3).Select(s => s[rng.Next(s.Length)]).ToArray());
            var account = new AccountModel
            {
                Username = $"{usernameBase}{rng.Next(1000, 9999)}{randomLetters}",
                Password = $"{usernameBase}{rng.Next(1000, 9999)}1",
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

            // Tin nhắn tiến trình
            var statusMsg = await bot.SendMessage(chatId,
                $"{E(EMOJI_LOADING_ID, "⚙️")} <b>Đang khởi động...</b>\n" +
                "⬜⬜⬜⬜⬜⬜⬜⬜⬜⬜  0%",
                ParseMode.Html, cancellationToken: ct);

            var msgId = statusMsg.MessageId;
            var lastEdit = DateTime.MinValue;
            var _refreshPending = false;
            var _refreshLock = new SemaphoreSlim(1, 1);

            async Task RefreshProgress()
            {
                if (_refreshPending) return;

                await _refreshLock.WaitAsync();
                try
                {
                    if ((DateTime.UtcNow - lastEdit).TotalSeconds < 1.5)
                    {
                        _refreshPending = true;
                        _refreshLock.Release();
                        await Task.Delay(TimeSpan.FromSeconds(1.5));
                        await _refreshLock.WaitAsync();
                        _refreshPending = false;
                    }
                    var selectedRuns = account.SiteRuns.Where(x => x.IsSelected).ToList();
                    int total = selectedRuns.Count * 2;
                    int done = 0;
                    bool allDone = true;

                    var siteLines = new System.Text.StringBuilder();
                    foreach (var sr in selectedRuns)
                    {
                        if (sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Failed) allDone = false;
                        if (sr.Status == AccountStatus.Phase1_RegDone) done++;
                        else if (sr.Status == AccountStatus.Success || sr.Status == AccountStatus.Failed) done += 2;

                        string icon = sr.Status switch
                        {
                            AccountStatus.Success         => "✅",
                            AccountStatus.Failed          => "❌",
                            AccountStatus.Phase1_RegDone  => "🔄",
                            AccountStatus.Running         => "⏳",
                            _ => "▪️"
                        };
                        string step = sr.Status switch
                        {
                            AccountStatus.Success         => "Xong",
                            AccountStatus.Failed          => "Thất bại " + (sr.LogMessage.Contains("submission: ") ? "(" + sr.LogMessage.Substring(sr.LogMessage.IndexOf("submission: ") + 12).Replace("\n", " ") + ")" : ""),
                            AccountStatus.Phase1_RegDone  => "ĐK xong, đang set bank...",
                            AccountStatus.Running         => "Đang chạy...",
                            _ => "Chờ..."
                        };
                        siteLines.AppendLine($"  {icon} <b>{sr.Site}</b> — {step}");
                    }

                    int pct = total == 0 ? 0 : done * 100 / total;
                    int blk = pct / 10;
                    string bar = string.Concat(Enumerable.Repeat("🟩", blk)) + string.Concat(Enumerable.Repeat("⬜", 10 - blk));

                    string txt;
                    if (!allDone)
                    {
                        txt = $"⚙️ <b>Đang đăng ký tài khoản...</b>\n"
                            + $"{bar}  {pct}%\n\n"
                            + $"<b>Tiến độ từng site:</b>\n{siteLines}";
                    }
                    else
                    {
                        bool anySuccess = selectedRuns.Any(x => x.Status == AccountStatus.Success);
                        string warning = "";
                        if (account.SpecificUsernames.Any())
                        {
                            warning = "\n\n⚠️ <b>LƯU Ý: Một số site bị trùng tên và đã đổi thành:</b>\n";
                            foreach (var kvp in account.SpecificUsernames)
                            {
                                warning += $"- <b>{kvp.Key}</b>: <code>{kvp.Value}</code> (MK: <code>{kvp.Value}1</code>)\n";
                            }
                        }
                        
                        txt = anySuccess
                            ? $"✅ <b>HOÀN TẤT!</b>\n{bar}  100%\n\n"
                              + "━━━━━━━━━━━━━━━━━━━━\n"
                              + $"👤 {WebUtility.HtmlEncode(account.FullName)}\n"
                              + $"📞 {WebUtility.HtmlEncode(account.PhoneNumber)}\n"
                              + $"🏦 {WebUtility.HtmlEncode(account.BankName)} – {WebUtility.HtmlEncode(account.AccountNumber)}\n"
                              + "━━━━━━━━━━━━━━━━━━━━\n"
                              + $"🔑 TK chung:  <code>{WebUtility.HtmlEncode(account.Username)}</code>\n"
                              + $"🔒 MK chung:  <code>{WebUtility.HtmlEncode(account.Password)}</code>\n"
                              + $"🔐 MK cấp 2: <code>{WebUtility.HtmlEncode(account.Level2Password)}</code>\n\n"
                              + $"<b>Kết quả từng site:</b>\n{siteLines}{warning}"
                            : $"❌ <b>THẤT BẠI</b> – Tất cả các trang đều lỗi.\n\nVui lòng kiểm tra proxy và thử lại.";
                    }

                    await bot.EditMessageText(chatId, msgId, txt, ParseMode.Html);
                    lastEdit = DateTime.UtcNow;
                }
                catch { }
                finally { if (_refreshLock.CurrentCount == 0) _refreshLock.Release(); }
            }

            // Hook siteRun events – cập nhật thanh tiến độ + gửi tin ngay mỗi site
            var selectedSiteRuns = account.SiteRuns.Where(sr => sr.IsSelected).ToList();
            var completedSites = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool success, string log)>();

            foreach (var sr in selectedSiteRuns)
            {
                var capturedSr = sr;
                sr.PropertyChanged += async (_, e) =>
                {
                    if (e.PropertyName != nameof(SiteRunModel.Status)) return;
                    await RefreshProgress();
                };
            }

            // Thêm vào ViewModel
            if (System.Windows.Application.Current != null)
                System.Windows.Application.Current.Dispatcher.Invoke(() => _mainViewModel.Profiles.Add(account));
            else
                _mainViewModel.Profiles.Add(account);

            // Chạy trong queue
            _userPendingJobs.AddOrUpdate(chatId, 1, (_, v) => v + 1);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_queue.CurrentCount == 0)
                        await bot.SendMessage(chatId,
                            "⏳ <b>Hàng đợi đang đầy.</b>\nYêu cầu của bạn đã được xếp hàng, sẽ chạy khi có slot trống...",
                            ParseMode.Html);

                    await _queue.WaitAsync();
                    try
                    {
                        await RefreshProgress();
                        var mgr = new BAOCODETOOL.Core.BotManager();
                        await mgr.RunMassRegistrationAsync(new[] { account }, 10);
                        await RefreshProgress();
                    }
                    finally { _queue.Release(); }
                }
                catch (Exception ex) { Console.WriteLine($"[BotService] Error: {ex}"); }
                finally
                {
                    // Giảm counter, nếu về 0 thì cho phép nhập tiếp
                    var remaining = _userPendingJobs.AddOrUpdate(chatId, 0, (_, v) => Math.Max(0, v - 1));
                    if (remaining == 0)
                    {
                        try
                        {
                            await bot.SendMessage(chatId,
                                "✅ <b>Tất cả yêu cầu đã hoàn thành!</b>\nBấm nút bên dưới để thêm tiếp hoặc kiểm tra trạng thái:",
                                ParseMode.Html, replyMarkup: MainMenuKeyboard());
                        }
                        catch { }
                    }
                }
            });
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            Console.WriteLine(ex is ApiRequestException api
                ? $"Telegram API Error [{api.ErrorCode}]: {api.Message}"
                : ex.ToString());
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
                await bot.SendMessage(chatId,
                    "⚠️ Không tìm thấy dòng nào đúng định dạng.\n\n" +
                    "Mỗi dòng phải theo mẫu:\n<code>HOTEN|TENNGANHANG|SDT|SOTK|PROXY</code>",
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
                if (p.Length != 5) { errors.AppendLine($"• Dòng {lineNum}: Sai số cột (cần 5, có {p.Length})"); continue; }

                var name  = p[0];
                var bank  = p[1];
                var phone = p[2].Replace(" ", "");
                var sotk  = p[3].Replace(" ", "");
                var proxy = p[4].Replace("http://", "").Replace("https://", "").Replace(" ", "");

                if (name.Length < 3 || name.Any(char.IsDigit))       { errors.AppendLine($"• Dòng {lineNum}: Họ tên không hợp lệ ({name})"); continue; }
                if (!Regex.IsMatch(phone, @"^0\d{9}$"))               { errors.AppendLine($"• Dòng {lineNum}: SĐT không hợp lệ ({phone})"); continue; }
                if (bank.Length < 2)                                   { errors.AppendLine($"• Dòng {lineNum}: Tên ngân hàng quá ngắn"); continue; }
                if (!Regex.IsMatch(sotk, @"^\d{6,20}$"))              { errors.AppendLine($"• Dòng {lineNum}: STK không hợp lệ ({sotk})"); continue; }
                if (!proxy.Contains(':'))                              { errors.AppendLine($"• Dòng {lineNum}: Proxy sai định dạng ({proxy})"); continue; }

                var rng = new Random();
                var usernameBase = RemoveDiacritics(name.Split(' ').Last().ToLower());
                if (usernameBase.Length < 3) usernameBase = RemoveDiacritics(name.Replace(" ", "").ToLower());

                string randomLetters = new string(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz", 3).Select(s => s[rng.Next(s.Length)]).ToArray());
                var account = new AccountModel
                {
                    Username      = $"{usernameBase}{rng.Next(1000, 9999)}{randomLetters}",
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
                "Các tài khoản sẽ được chạy tuần tự (tối đa 3 cùng lúc).",
                ParseMode.Html, cancellationToken: ct);

            // Thêm vào ViewModel và chạy từng tài khoản qua queue
            foreach (var acc in validAccounts)
            {
                var account = acc; // capture for closure
                
                // Chuẩn bị các selected site
                foreach (var sr in account.SiteRuns)
                {
                    if (selectedGames.Count == 0)
                        sr.IsSelected = (sr.Site == TargetSite.RR88 || sr.Site == TargetSite.MM88 || sr.Site == TargetSite.XX88);
                    else
                        sr.IsSelected = selectedGames.Contains(sr.Site.ToString());
                }

                // Gửi tin nhắn tiến trình ban đầu
                var selectedRuns = account.SiteRuns.Where(x => x.IsSelected).ToList();
                var progressLines = selectedRuns.Count > 0
                    ? string.Join("\n", selectedRuns.Select(sr => $"  ▪️ {sr.Site}  — Chờ..."))
                    : "";
                var statusMsg = await bot.SendMessage(chatId,
                    $"⚙️ <b>[{WebUtility.HtmlEncode(account.FullName)}] Đang chờ tới lượt...</b>\n" +
                    $"⬜⬜⬜⬜⬜⬜⬜⬜⬜⬜  0%\n\n" +
                    $"<b>Tiến độ từng site:</b>\n{progressLines}",
                    ParseMode.Html, cancellationToken: ct);

                var msgId = statusMsg.MessageId;
                var lastEdit = DateTime.MinValue;
                var _refreshPending = false;
                var _refreshLock = new SemaphoreSlim(1, 1);
                var completedSites = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool success, string log)>();

                async Task RefreshProgress()
                {
                    await _refreshLock.WaitAsync();
                    try
                    {
                        if ((DateTime.UtcNow - lastEdit).TotalSeconds < 1.5)
                        {
                            if (_refreshPending) return;
                            _refreshPending = true;
                            var delay = TimeSpan.FromSeconds(1.5) - (DateTime.UtcNow - lastEdit);
                            if (delay > TimeSpan.Zero) await Task.Delay(delay);
                            _refreshPending = false;
                        }
                        int total = selectedRuns.Count * 2;
                        int done = 0;
                        bool allDone = true;

                        var siteLines = new System.Text.StringBuilder();
                        foreach (var sr in selectedRuns)
                        {
                            if (sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Failed) allDone = false;
                            if (sr.Status == AccountStatus.Phase1_RegDone) done++;
                            else if (sr.Status == AccountStatus.Success || sr.Status == AccountStatus.Failed) done += 2;

                            string icon = sr.Status switch
                            {
                                AccountStatus.Success         => "✅",
                                AccountStatus.Failed          => "❌",
                                AccountStatus.Phase1_RegDone  => "🔄",
                                AccountStatus.Running         => "⏳",
                                _ => "▪️"
                            };
                            string step = sr.Status switch
                            {
                                AccountStatus.Success         => "Xong",
                                AccountStatus.Failed          => "Thất bại " + (sr.LogMessage.Contains("submission: ") ? "(" + sr.LogMessage.Substring(sr.LogMessage.IndexOf("submission: ") + 12).Replace("\n", " ") + ")" : ""),
                                AccountStatus.Phase1_RegDone  => "ĐK xong, đang set bank...",
                                AccountStatus.Running         => "Đang chạy...",
                                _ => "Chờ..."
                            };
                            siteLines.AppendLine($"  {icon} <b>{sr.Site}</b> — {step}");
                        }

                        int pct = total == 0 ? 0 : done * 100 / total;
                        int blk = pct / 10;
                        string bar = string.Concat(Enumerable.Repeat("🟩", blk)) + string.Concat(Enumerable.Repeat("⬜", 10 - blk));

                        string txt;
                        if (!allDone)
                        {
                            txt = $"⚙️ <b>[{WebUtility.HtmlEncode(account.FullName)}] Đang đăng ký...</b>\n"
                                + $"{bar}  {pct}%\n\n"
                                + $"<b>Tiến độ từng site:</b>\n{siteLines}";
                        }
                        else
                        {
                            bool anySuccess = selectedRuns.Any(x => x.Status == AccountStatus.Success);
                            string warning = "";
                            if (account.SpecificUsernames.Any())
                            {
                                warning = "\n\n⚠️ <b>LƯU Ý: Một số site bị trùng tên và đã đổi thành:</b>\n";
                                foreach (var kvp in account.SpecificUsernames)
                                {
                                    warning += $"- <b>{kvp.Key}</b>: <code>{kvp.Value}</code> (MK: <code>{kvp.Value}1</code>)\n";
                                }
                            }

                            txt = anySuccess
                                ? $"✅ <b>HOÀN TẤT: {WebUtility.HtmlEncode(account.FullName)}</b>\n{bar}  100%\n\n"
                                  + "━━━━━━━━━━━━━━━━━━━━\n"
                                  + $"👤 {WebUtility.HtmlEncode(account.FullName)}\n"
                                  + $"📞 {WebUtility.HtmlEncode(account.PhoneNumber)}\n"
                                  + $"🏦 {WebUtility.HtmlEncode(account.BankName)} – {WebUtility.HtmlEncode(account.AccountNumber)}\n"
                                  + "━━━━━━━━━━━━━━━━━━━━\n"
                                  + $"🔑 TK chung:  <code>{WebUtility.HtmlEncode(account.Username)}</code>\n"
                                  + $"🔒 MK chung:  <code>{WebUtility.HtmlEncode(account.Password)}</code>\n"
                                  + $"🔐 MK cấp 2: <code>{WebUtility.HtmlEncode(account.Level2Password)}</code>\n\n"
                                  + $"<b>Kết quả từng site:</b>\n{siteLines}{warning}"
                                : $"❌ <b>THẤT BẠI: {WebUtility.HtmlEncode(account.FullName)}</b> – Tất cả các trang đều lỗi.\n\n"
                                  + $"<b>Kết quả từng site:</b>\n{siteLines}";
                        }

                        await bot.EditMessageText(chatId, msgId, txt, ParseMode.Html);
                        lastEdit = DateTime.UtcNow;
                    }
                    catch { }
                    finally { if (_refreshLock.CurrentCount == 0) _refreshLock.Release(); }
                }

                // Hook siteRun events
                foreach (var sr in selectedRuns)
                {
                    var capturedSr = sr;
                    sr.PropertyChanged += async (_, e) =>
                    {
                        if (e.PropertyName != nameof(SiteRunModel.Status)) return;
                        await RefreshProgress();
                    };
                }

                if (System.Windows.Application.Current != null)
                    System.Windows.Application.Current.Dispatcher.Invoke(() => _mainViewModel.Profiles.Add(account));
                else
                    _mainViewModel.Profiles.Add(account);

                // Chạy mỗi account trong queue độc lập
                _userPendingJobs.AddOrUpdate(chatId, 1, (_, v) => v + 1);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _queue.WaitAsync();
                        try
                        {
                            await RefreshProgress(); // Force update when starting
                            var mgr = new BAOCODETOOL.Core.BotManager();
                            await mgr.RunMassRegistrationAsync(new[] { account }, 10);
                            await RefreshProgress(); // Force update when done
                        }
                        finally { _queue.Release(); }
                    }
                    catch (Exception ex) { Console.WriteLine($"[Bulk] Error for {account.FullName}: {ex.Message}"); }
                    finally
                    {
                        // Giảm counter, nếu về 0 thì báo xong tổng
                        var remaining = _userPendingJobs.AddOrUpdate(chatId, 0, (_, v) => Math.Max(0, v - 1));
                        if (remaining == 0)
                        {
                            try
                            {
                                await bot.SendMessage(chatId,
                                    "✅ <b>Tất cả tài khoản trong danh sách đã chạy xong!</b>\nBấm nút bên dưới để thêm tiếp hoặc kiểm tra trạng thái:",
                                    ParseMode.Html, replyMarkup: MainMenuKeyboard());
                            }
                            catch { }
                        }
                    }
                });
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
