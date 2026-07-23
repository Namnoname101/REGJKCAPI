using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
        public string Step { get; set; } = "idle"; // idle | s_name | s_phone | s_bank | s_sotk | s_proxy | confirming
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
        private readonly HashSet<long> _allowedUsers = new() { ADMIN_CHAT_ID };
        // Đếm số job đang chờ/chạy của từng user – chặn nhập thêm khi > 0
        private readonly ConcurrentDictionary<long, int> _userPendingJobs = new();

        private const long ADMIN_CHAT_ID = 1853328773;
        private static readonly SemaphoreSlim _queue = new SemaphoreSlim(3, 3);

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

        private UserSession GetSession(long chatId) =>
            _sessions.GetOrAdd(chatId, _ => new UserSession());

        // ─────────────────────────────────────────────────────────────────────
        public BotService(string botToken, MainViewModel mainViewModel)
        {
            _botClient = new TelegramBotClient(botToken);
            _mainViewModel = mainViewModel;
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
                    _sessions[cbChat] = new UserSession { Step = "bulk" };
                    await bot.SendMessage(cbChat,
                        "📋 <b>NHẬP HÀNG LOẠT</b>\n" +
                        "━━━━━━━━━━━━━━━━━━━━\n" +
                        "Mỗi dòng 1 tài khoản theo định dạng:\n" +
                        "<code>HOTEN|TENNGANHANG|SDT|SOTK|PROXY</code>\n\n" +
                        "📌 <b>Ví dụ:</b>\n" +
                        "<code>Nguyen Van A|Vietcombank|0987654321|1234567890|1.2.3.4:8080:u:p</code>\n" +
                        "<code>Tran Thi B|Techcombank|0912345678|9876543210|5.6.7.8:3128:u:p</code>\n\n" +
                        "⚠️ <b>Lưu ý:</b> Mỗi dòng là 1 tài khoản, dán bao nhiêu dòng cũng được.",
                        ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
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
                _sessions[chatId] = new UserSession { Step = "bulk" };
                await bot.SendMessage(chatId,
                    "📋 <b>CHẾ ĐỘ NHẬP HÀNG LOẠT</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    "Mỗi dòng 1 tài khoản: <code>HOTEN|TENNGANHANG|SDT|SOTK|PROXY</code>",
                    ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);
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
            var sess = new UserSession { Step = "s_name" };
            _sessions[chatId] = sess;

            var sent = await bot.SendMessage(chatId,
                "📝 <b>THÊM TÀI KHOẢN MỚI</b>\n" +
                "━━━━━━━━━━━━━━━━━━━━\n\n" +
                "Bước <b>1/5</b> — Nhập <b>Họ và Tên</b> đầy đủ:\n\n" +
                "<i>VD: Nguyen Van A</i>",
                ParseMode.Html, replyMarkup: CancelKeyboard(), cancellationToken: ct);

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
                case "s_name":
                    if (text.Length < 3 || text.Any(char.IsDigit))
                    {
                        await SendError(bot, chatId, "Họ tên phải có ít nhất 3 ký tự và không chứa số.\n<i>VD: Nguyen Van A</i>", ct);
                        return;
                    }
                    sess.FullName = text;
                    sess.Step = "s_phone";
                    await bot.SendMessage(chatId,
                        "Bước <b>2/5</b> — Nhập <b>Số điện thoại</b>:\n\n" +
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
                        "Bước <b>3/5</b> — Nhập <b>Tên ngân hàng</b>:\n\n" +
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
                        "Bước <b>4/5</b> — Nhập <b>Số tài khoản ngân hàng</b>:\n\n" +
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
                        "Bước <b>5/5</b> — Nhập <b>Proxy</b>:\n\n" +
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

            var chars = "abcdefghijklmnopqrstuvwxyz";
            var rng = new Random();
            var rand5 = new string(Enumerable.Repeat(chars, 5).Select(s => s[rng.Next(s.Length)]).ToArray());

            var account = new AccountModel
            {
                Username = $"{rand5}{rng.Next(1000, 9999)}",
                Password = $"{rand5.ToUpper()}@{rng.Next(100000, 999999)}",
                PhoneNumber = sess.Phone,
                FullName = sess.FullName,
                Level2Password = rng.Next(100000, 999999).ToString(),
                AccountNumber = sess.SoTk,
                BankName = sess.BankName,
                Branch = "Hanoi",
                Proxy = sess.Proxy,
                UserAgent = string.Empty,
                Status = AccountStatus.Ready,
                LogMessage = "Profile added via Telegram."
            };

            // Tin nhắn tiến trình
            var statusMsg = await bot.SendMessage(chatId,
                $"{E(EMOJI_LOADING_ID, "⚙️")} <b>Đang khởi động...</b>\n" +
                "⬜⬜⬜⬜⬜⬜⬜⬜⬜⬜  0%",
                ParseMode.Html, cancellationToken: ct);

            var msgId = statusMsg.MessageId;
            var lastEdit = DateTime.MinValue;
            var editing = false;

            async Task RefreshProgress()
            {
                if (editing || (DateTime.UtcNow - lastEdit).TotalSeconds < 1.5) return;
                editing = true;
                try
                {
                    int total = account.SiteRuns.Count(x => x.IsSelected) * 2;
                    int done = 0;
                    bool allDone = true;
                    foreach (var sr in account.SiteRuns.Where(x => x.IsSelected))
                    {
                        if (sr.Status != AccountStatus.Success && sr.Status != AccountStatus.Failed) allDone = false;
                        if (sr.Status == AccountStatus.Phase1_RegDone) done++;
                        else if (sr.Status == AccountStatus.Success || sr.Status == AccountStatus.Failed) done += 2;
                    }
                    int pct = total == 0 ? 0 : done * 100 / total;
                    int blk = pct / 10;
                    string bar = string.Concat(Enumerable.Repeat("🟩", blk)) + string.Concat(Enumerable.Repeat("⬜", 10 - blk));

                    string txt = $"{E(EMOJI_LOADING_ID, "⚙️")} <b>Đang đăng ký tài khoản...</b>\n{bar}  {pct}%";

                    if (allDone)
                    {
                        bool anySuccess = account.SiteRuns.Any(x => x.IsSelected && x.Status == AccountStatus.Success);
                        txt = anySuccess
                            ? $"{E(EMOJI_SUCCESS_ID, "✅")} <b>HOÀN TẤT!</b>\n{bar}  100%\n\n" +
                              "━━━━━━━━━━━━━━━━━━━━\n" +
                              $"👤 {WebUtility.HtmlEncode(account.FullName)}\n" +
                              $"📞 {WebUtility.HtmlEncode(account.PhoneNumber)}\n" +
                              $"🏦 {WebUtility.HtmlEncode(account.BankName)} – {WebUtility.HtmlEncode(account.AccountNumber)}\n" +
                              "━━━━━━━━━━━━━━━━━━━━\n" +
                              $"🔑 TK: <code>{WebUtility.HtmlEncode(account.Username)}</code>\n" +
                              $"🔒 MK: <code>{WebUtility.HtmlEncode(account.Password)}</code>\n" +
                              $"🔐 MK2: <code>{WebUtility.HtmlEncode(account.Level2Password)}</code>"
                            : $"{E(EMOJI_FAIL_ID, "❌")} <b>THẤT BẠI</b> – Tất cả các trang đều lỗi.\n\nVui lòng kiểm tra proxy và thử lại.";
                    }

                    await bot.EditMessageText(chatId, msgId, txt, ParseMode.Html);
                    lastEdit = DateTime.UtcNow;
                }
                catch { }
                finally { editing = false; }
            }

            // Hook siteRun events – gửi tin riêng cho từng site
            foreach (var sr in account.SiteRuns)
            {
                if (sr.Site != TargetSite.RR88 && sr.Site != TargetSite.MM88 && sr.Site != TargetSite.XX88) continue;
                sr.IsSelected = true;
                sr.PropertyChanged += async (_, e) =>
                {
                    if (e.PropertyName != nameof(SiteRunModel.Status)) return;
                    await RefreshProgress();

                    if (sr.Status == AccountStatus.Success)
                    {
                        try
                        {
                            await bot.SendMessage(chatId,
                                $"{E(EMOJI_SUCCESS_ID, "✅")} <b>Thành công tại {sr.Site}</b>\n" +
                                $"🔑 TK: <code>{WebUtility.HtmlEncode(account.Username)}</code>\n" +
                                $"🔒 MK: <code>{WebUtility.HtmlEncode(account.Password)}</code>",
                                ParseMode.Html);
                        }
                        catch { }
                    }
                    else if (sr.Status == AccountStatus.Failed)
                    {
                        try
                        {
                            await bot.SendMessage(chatId,
                                $"{E(EMOJI_FAIL_ID, "❌")} <b>Lỗi tại {sr.Site}</b>\n" +
                                $"📝 {WebUtility.HtmlEncode(sr.LogMessage)}",
                                ParseMode.Html);
                        }
                        catch { }
                    }
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
                        await mgr.RunMassRegistrationAsync(new[] { account }, 3);
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
            _sessions.TryRemove(chatId, out _);

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
                var chars = "abcdefghijklmnopqrstuvwxyz";
                var rand5 = new string(Enumerable.Repeat(chars, 5).Select(s => s[rng.Next(s.Length)]).ToArray());

                validAccounts.Add(new AccountModel
                {
                    Username      = $"{rand5}{rng.Next(1000, 9999)}",
                    Password      = $"{rand5.ToUpper()}@{rng.Next(100000, 999999)}",
                    PhoneNumber   = phone,
                    FullName      = name,
                    Level2Password= rng.Next(100000, 999999).ToString(),
                    AccountNumber = sotk,
                    BankName      = bank,
                    Branch        = "Hanoi",
                    Proxy         = proxy,
                    UserAgent     = string.Empty,
                    Status        = AccountStatus.Ready,
                    LogMessage    = "Bulk import via Telegram."
                });
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

            // Xác nhận và bắt đầu chạy
            await bot.SendMessage(chatId,
                $"✅ <b>Nhận được {validAccounts.Count} tài khoản hợp lệ.</b>\n" +
                $"Các tài khoản sẽ được xếp vào hàng đợi và chạy tuần tự (tối đa 3 cùng lúc).\n\n" +
                $"⏳ Đang bắt đầu...",
                ParseMode.Html, cancellationToken: ct);

            // Thêm vào ViewModel và chạy từng tài khoản qua queue
            foreach (var acc in validAccounts)
            {
                var account = acc; // capture for closure
                foreach (var sr in account.SiteRuns)
                {
                    if (sr.Site != TargetSite.RR88 && sr.Site != TargetSite.MM88 && sr.Site != TargetSite.XX88) continue;
                    sr.IsSelected = true;
                    sr.PropertyChanged += async (_, e) =>
                    {
                        if (e.PropertyName != nameof(SiteRunModel.Status)) return;
                        if (sr.Status == AccountStatus.Success)
                        {
                            try { await bot.SendMessage(chatId,
                                $"✅ <b>[{WebUtility.HtmlEncode(account.FullName)}]</b> Thành công tại <b>{sr.Site}</b>\n" +
                                $"🔑 TK: <code>{WebUtility.HtmlEncode(account.Username)}</code>  🔒 MK: <code>{WebUtility.HtmlEncode(account.Password)}</code>",
                                ParseMode.Html); } catch { }
                        }
                        else if (sr.Status == AccountStatus.Failed)
                        {
                            try { await bot.SendMessage(chatId,
                                $"❌ <b>[{WebUtility.HtmlEncode(account.FullName)}]</b> Lỗi tại <b>{sr.Site}</b>: {WebUtility.HtmlEncode(sr.LogMessage)}",
                                ParseMode.Html); } catch { }
                        }
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
                            var mgr = new BAOCODETOOL.Core.BotManager();
                            await mgr.RunMassRegistrationAsync(new[] { account }, 3);
                        }
                        finally { _queue.Release(); }
                    }
                    catch (Exception ex) { Console.WriteLine($"[Bulk] Error for {account.FullName}: {ex.Message}"); }
                    finally
                    {
                        // Giảm counter, nếu về 0 thì cho phép nhập tiếp
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

            await bot.SendMessage(chatId,
                $"🚀 <b>Đã đẩy {validAccounts.Count} tài khoản vào hàng đợi.</b>\n" +
                "Kết quả từng tài khoản sẽ được báo cáo ngay khi hoàn thành.",
                ParseMode.Html, cancellationToken: ct);
        }
    }
}
