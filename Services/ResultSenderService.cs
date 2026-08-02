using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BAOCODETOOL.Core;
using BAOCODETOOL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace BAOCODETOOL.Services;

public sealed class ResultSenderService : IResultSenderService
{
    private const string BrandName = "KJC BOT";
    private static readonly TimeSpan MinimumEditInterval = TimeSpan.FromSeconds(1.5);
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<ResultSenderService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<Guid, TrackingState> _tracking = new();

    public ResultSenderService(
        ITelegramBotClient botClient,
        ILogger<ResultSenderService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _botClient = botClient;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<RegistrationJob> CreateJobAsync(
        long chatId,
        AccountModel account,
        bool isBulk,
        Guid batchId,
        decimal registrationPricePerSite,
        string chargeReferenceId,
        CancellationToken cancellationToken)
    {
        var selectedRuns = account.SiteRuns.Where(siteRun => siteRun.IsSelected).ToList();
        var initialText = isBulk
            ? BuildBulkWaitingText(account, selectedRuns)
            : "⚙️ <b>Đang khởi động...</b>\n⬜⬜⬜⬜⬜⬜⬜⬜⬜⬜  0%";

        var statusMessage = await _botClient.SendMessage(
            chatId,
            initialText,
            ParseMode.Html,
            cancellationToken: cancellationToken);

        var job = new RegistrationJob
        {
            BatchId = batchId,
            ChatId = chatId,
            Account = account,
            StatusMessageId = statusMessage.MessageId,
            IsBulk = isBulk,
            RegistrationPricePerSite = registrationPricePerSite,
            ChargeReferenceId = chargeReferenceId
        };

        var state = new TrackingState(initialText);
        foreach (var siteRun in selectedRuns)
        {
            PropertyChangedEventHandler handler = (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(SiteRunModel.Status))
                {
                    _ = RefreshSafelyAsync(job, force: false, CancellationToken.None);
                }
            };

            siteRun.PropertyChanged += handler;
            state.Handlers.Add((siteRun, handler));
        }

        _tracking[job.Id] = state;
        return job;
    }

    public Task JobStartedAsync(RegistrationJob job, CancellationToken cancellationToken) =>
        NotifyJobStartedAsync(job, cancellationToken);

    private async Task NotifyJobStartedAsync(RegistrationJob job, CancellationToken cancellationToken)
    {
        await RefreshSafelyAsync(job, force: true, cancellationToken);
        var userLabel = await GetActivityUserLabelAsync(job.ChatId, cancellationToken);
        await SendActivityLogAsync(
            "🚀 <b>BẮT ĐẦU ĐĂNG KÝ</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━\n" +
            $"👤 Người dùng: {userLabel}\n" +
            $"🌐 Game: <b>{Html(job.Account.SelectedSitesText)}</b>\n" +
            $"🆔 Job: <code>{job.Id.ToString()[..8]}</code>",
            cancellationToken);
    }

    public async Task JobCompletedAsync(
        RegistrationJob job,
        int remainingJobsForUser,
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshSafelyAsync(job, force: true, cancellationToken);
        }
        finally
        {
            StopTracking(job);
        }

        if (job.RefundedAmount > 0m)
        {
            try
            {
                await _botClient.SendMessage(
                    job.ChatId,
                    "💸 <b>ĐÃ HOÀN PHÍ SITE LỖI</b>\n" +
                    "━━━━━━━━━━━━━━━━━━━━\n" +
                    $"❌ Site lỗi: <b>{Html(job.RefundedSites)}</b>\n" +
                    $"🔢 Số site hoàn: <b>{job.RefundedSiteCount:N0}</b>\n" +
                    $"💰 Đã hoàn: <b>{job.RefundedAmount:N0} KJC Xu</b>\n\n" +
                    "Các site đăng ký thành công vẫn được tính phí.",
                    ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not send registration refund notification. JobId={JobId}",
                    job.Id);
            }
        }

        if (remainingJobsForUser == 0)
        {
            var completionText = job.IsBulk
                ? $"✅ <b>{BrandName} đã xử lý xong toàn bộ danh sách.</b>\nBạn có thể tiếp tục đăng ký hoặc theo dõi yêu cầu khác:"
                : $"✅ <b>{BrandName} đã hoàn tất yêu cầu.</b>\nBạn có thể tiếp tục đăng ký hoặc sử dụng dịch vụ khác:";

            try
            {
                await _botClient.SendMessage(
                    job.ChatId,
                    completionText,
                    ParseMode.Html,
                    replyMarkup: MainMenuKeyboard(),
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not send the final Telegram completion message. JobId={JobId}",
                    job.Id);
            }
        }

        var selectedRuns = job.Account.SiteRuns.Where(siteRun => siteRun.IsSelected).ToList();
        var successCount = selectedRuns.Count(siteRun => siteRun.Status == AccountStatus.Success);
        var statusIcon = successCount == selectedRuns.Count && selectedRuns.Count > 0 ? "✅" : "❌";
        var userLabel = await GetActivityUserLabelAsync(job.ChatId, cancellationToken);
        var details = selectedRuns.Count == 0
            ? "• Không có game được chọn"
            : string.Join("\n", selectedRuns.Select(siteRun =>
                siteRun.Status == AccountStatus.Success
                    ? $"• ✅ <b>{Html(siteRun.Site.ToString())}</b> — Thành công"
                    : $"• ❌ <b>{Html(siteRun.Site.ToString())}</b> — {Html(BuildFailureStep(siteRun))}"));

        await SendActivityLogAsync(
            $"{statusIcon} <b>KẾT QUẢ ĐĂNG KÝ</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━\n" +
            $"👤 Người dùng: {userLabel}\n" +
            $"🌐 Game: <b>{Html(job.Account.SelectedSitesText)}</b>\n" +
            $"📊 Kết quả: <b>{successCount}/{selectedRuns.Count}</b> thành công\n\n" +
            $"<b>Chi tiết:</b>\n{details}",
            cancellationToken);
    }

    private async Task<string> GetActivityUserLabelAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var username = await db.Users
                .Where(user => user.ChatId == chatId)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(username))
            {
                return "<code>@" + Html(username.Trim().TrimStart('@')) + "</code>";
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not resolve Telegram username for activity log. ChatId={ChatId}", chatId);
        }

        return "<code>không có username</code>";
    }

    private async Task SendActivityLogAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(RuntimePaths.ActivityLogChatPath)
                || !long.TryParse(File.ReadAllText(RuntimePaths.ActivityLogChatPath).Trim(), out var groupChatId)
                || groupChatId >= 0)
            {
                return;
            }

            await _botClient.SendMessage(
                groupChatId,
                text,
                ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send activity log to the configured Telegram group.");
        }
    }

    private async Task RefreshSafelyAsync(
        RegistrationJob job,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!_tracking.TryGetValue(job.Id, out var state))
        {
            return;
        }

        try
        {
            await state.Gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (!force)
            {
                var wait = MinimumEditInterval - (DateTimeOffset.UtcNow - state.LastEdit);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }

            var text = BuildProgressText(job);
            if (string.Equals(text, state.LastText, StringComparison.Ordinal))
            {
                return;
            }

            await _botClient.EditMessageText(
                job.ChatId,
                job.StatusMessageId,
                text,
                ParseMode.Html,
                replyMarkup: BuildResultActionsKeyboard(job),
                cancellationToken: cancellationToken);

            state.LastEdit = DateTimeOffset.UtcNow;
            state.LastText = text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not update Telegram progress. JobId={JobId}, MessageId={MessageId}",
                job.Id,
                job.StatusMessageId);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string BuildBulkWaitingText(
        AccountModel account,
        IReadOnlyCollection<SiteRunModel> selectedRuns)
    {
        var progressLines = selectedRuns.Count == 0
            ? string.Empty
            : string.Join("\n", selectedRuns.Select(siteRun => $"  ▪️ {siteRun.Site} — Chờ..."));

        return $"⚙️ <b>[{Html(account.FullName)}] Đang chờ tới lượt...</b>\n" +
               "⬜⬜⬜⬜⬜⬜⬜⬜⬜⬜  0%\n\n" +
               $"<b>Tiến độ từng dịch vụ:</b>\n{progressLines}";
    }

    private static string BuildProgressText(RegistrationJob job)
    {
        var account = job.Account;
        var selectedRuns = account.SiteRuns.Where(siteRun => siteRun.IsSelected).ToList();
        var totalSteps = selectedRuns.Count * 2;
        var completedSteps = 0;
        var allDone = true;
        var siteLines = new StringBuilder();

        foreach (var siteRun in selectedRuns)
        {
            if (siteRun.Status is not AccountStatus.Success and not AccountStatus.Failed)
            {
                allDone = false;
            }

            if (siteRun.Status == AccountStatus.Phase1_RegDone)
            {
                completedSteps++;
            }
            else if (siteRun.Status is AccountStatus.Success or AccountStatus.Failed)
            {
                completedSteps += 2;
            }

            var icon = siteRun.Status switch
            {
                AccountStatus.Success => "✅",
                AccountStatus.Failed => "❌",
                AccountStatus.Phase1_RegDone => "🔄",
                AccountStatus.Running => "⏳",
                _ => "▪️"
            };

            var step = siteRun.Status switch
            {
                AccountStatus.Success => "Xong",
                AccountStatus.Failed => BuildFailureStep(siteRun),
                AccountStatus.Phase1_RegDone => "Đã đăng ký, đang cập nhật ngân hàng...",
                AccountStatus.Running => "Đang xử lý...",
                _ => "Đang chờ..."
            };

            siteLines.AppendLine($"  {icon} <b>{siteRun.Site}</b> — {step}");
        }

        var percentage = totalSteps == 0 ? 0 : completedSteps * 100 / totalSteps;
        var filledBlocks = percentage / 10;
        var progressBar = string.Concat(Enumerable.Repeat("🟩", filledBlocks)) +
                          string.Concat(Enumerable.Repeat("⬜", 10 - filledBlocks));

        if (!allDone)
        {
            var heading = job.IsBulk
                ? $"⚙️ <b>[{Html(account.FullName)}] Đang đăng ký...</b>\n"
                : "⚙️ <b>Đang đăng ký tài khoản...</b>\n";

            return heading +
                   $"{progressBar}  {percentage}%\n\n" +
                   $"<b>Tiến độ từng dịch vụ:</b>\n{siteLines}";
        }

        var anySuccess = selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Success);
        var hasRegisteredAccount = selectedRuns.Any(siteRun => siteRun.RegistrationCompleted);
        if (!anySuccess && !hasRegisteredAccount)
        {
            var name = job.IsBulk ? $": {Html(account.FullName)}" : string.Empty;
            return $"❌ <b>THẤT BẠI{name}</b> — Tất cả các trang đều lỗi.\n\n" +
                   $"<b>Kết quả từng dịch vụ:</b>\n{siteLines}" +
                   ResultRetentionNotice;
        }

        var hasFailures = selectedRuns.Any(siteRun => siteRun.Status == AccountStatus.Failed);
        return BuildSuccessText(job, progressBar, siteLines.ToString(), hasFailures, selectedRuns.Count);
    }

    private static string BuildSuccessText(
        RegistrationJob job,
        string progressBar,
        string siteLines,
        bool hasFailures,
        int selectedRunCount)
    {
        var account = job.Account;
        var successCount = account.SiteRuns.Count(siteRun =>
            siteRun.IsSelected && siteRun.Status == AccountStatus.Success);
        var registeredCount = account.SiteRuns.Count(siteRun =>
            siteRun.IsSelected && siteRun.RegistrationCompleted);
        var heading = hasFailures
            ? successCount == 0 && registeredCount > 0
                ? job.IsBulk
                    ? $"⚠️ <b>TÀI KHOẢN ĐÃ ĐƯỢC ĐĂNG KÝ: {Html(account.FullName)}</b> — cần xử lý ngân hàng thủ công\n"
                    : "⚠️ <b>TÀI KHOẢN ĐÃ ĐƯỢC ĐĂNG KÝ</b> — cần xử lý ngân hàng thủ công\n"
                : job.IsBulk
                    ? $"⚠️ <b>HOÀN TẤT MỘT PHẦN: {Html(account.FullName)}</b> — {successCount}/{selectedRunCount} dịch vụ thành công\n"
                    : $"⚠️ <b>HOÀN TẤT MỘT PHẦN</b> — {successCount}/{selectedRunCount} dịch vụ thành công\n"
            : job.IsBulk
                ? $"✅ <b>HOÀN TẤT: {Html(account.FullName)}</b>\n"
                : "✅ <b>HOÀN TẤT!</b>\n";

        var changedUsernames = string.Empty;
        if (account.SpecificUsernames.Any())
        {
            var builder = new StringBuilder(
                "\n\n⚠️ <b>LƯU Ý: Một số dịch vụ bị trùng tên và đã tự động đổi thành:</b>\n");

            foreach (var pair in account.SpecificUsernames)
            {
                builder.AppendLine(
                    $"- <b>{pair.Key}</b>: <code>{Html(pair.Value)}</code> (MK: <code>{Html(pair.Value + "1")}</code>)");
            }

            changedUsernames = builder.ToString();
        }

        return heading +
               $"{progressBar}  100%\n\n" +
               "━━━━━━━━━━━━━━━━━━━━\n" +
               $"👤 {Html(account.FullName)}\n" +
               $"📞 {Html(account.PhoneNumber)}\n" +
               $"🏦 {Html(account.BankName)} – {Html(account.AccountNumber)}\n" +
               "━━━━━━━━━━━━━━━━━━━━\n" +
               $"🔑 TK chung:  <code>{Html(account.Username)}</code>\n" +
               $"🔒 MK chung:  <code>{Html(account.Password)}</code>\n" +
               $"🔐 MK cấp 2: <code>{Html(account.Level2Password)}</code>\n\n" +
               $"<b>Kết quả từng dịch vụ:</b>\n{siteLines}{changedUsernames}" +
               ResultRetentionNotice;
    }

    private const string ResultRetentionNotice =
        "\n📌 <i>Kết quả này được giữ trong cuộc trò chuyện để bạn xem và sao chép lại. " +
        "Chỉ ẩn khi bạn chủ động bấm nút bên dưới.</i>";

    private static InlineKeyboardMarkup? BuildResultActionsKeyboard(RegistrationJob job)
    {
        var selectedRuns = job.Account.SiteRuns.Where(siteRun => siteRun.IsSelected).ToList();
        var isFinal = selectedRuns.Count > 0 && selectedRuns.All(siteRun =>
            siteRun.Status is AccountStatus.Success or AccountStatus.Failed);

        return isFinal
            ? new InlineKeyboardMarkup(
                InlineKeyboardButton.WithCallbackData("🗑 Ẩn kết quả", "hide_result"))
            : null;
    }

    private static string BuildFailureStep(SiteRunModel siteRun)
    {
        var message = siteRun.LogMessage ?? string.Empty;
        var isDuplicate = ContainsAny(
            message,
            "duplicate",
            "already",
            "exists",
            "registered",
            "đã tồn tại",
            "đã được đăng ký",
            "đã được đăng kí",
            "đã được sử dụng",
            "da ton tai");

        if (siteRun.RequiresManualBankChange)
            return "Ngân hàng đã được sử dụng; TK/MK đã xuất để bạn đăng nhập và đổi ngân hàng";

        if (ContainsAny(message, "proxy die"))
            return "Proxy die";

        if (ContainsAny(
                message,
                "captcha",
                "geetest",
                "mã xác minh",
                "ma xac minh",
                "verification code"))
            return "Bước xác minh chưa hoàn tất";

        if (ContainsAny(message, "phone", "mobile", "số điện thoại", "so dien thoai", "sđt"))
        {
            return isDuplicate
                ? "Số điện thoại đã được đăng ký, vui lòng thử số điện thoại khác"
                : "Số điện thoại không hợp lệ, vui lòng thử số điện thoại khác";
        }

        if (siteRun.RegistrationCompleted
            && ContainsAny(message, "bank", "ngân hàng", "ngan hang", "account number", "card number"))
            return "Tài khoản đã đăng ký nhưng chưa hoàn tất ngân hàng; hãy đăng nhập để kiểm tra";

        if (ContainsAny(message, "account number", "bank account", "card number", "số tài khoản", "so tai khoan", "stk"))
            return isDuplicate
                ? "Số tài khoản đã được sử dụng"
                : "Số tài khoản ngân hàng chưa hợp lệ";

        if (ContainsAny(message, "username", "user name", "tên đăng nhập", "ten dang nhap"))
            return isDuplicate
                ? "Tên đăng nhập đã tồn tại"
                : "Tên đăng nhập chưa hợp lệ";

        if (ContainsAny(message, "bank", "ngân hàng", "ngan hang"))
            return "Thông tin ngân hàng đã được xử dụng vùi lòng xài ngân hàng khác !!!";

        if (ContainsAny(message,
                "legacy browser fallback is disabled",
                "direct http registration did not complete"))
            return "Đăng ký tự động không hoàn tất (chưa có bước thử lại)";

        // "Direct HTTP registration failed" is part of every direct API
        // failure, not evidence of a transport outage. Match only concrete
        // network signals so Telegram does not mislabel server-side errors.
        if (ContainsAny(
                message,
                "proxy",
                "connection",
                "network",
                "timeout",
                "timed out",
                "httprequestexception",
                "direct http exception"))
            return "Kết nối tạm thời gián đoạn";

        return "Chưa thể hoàn tất";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string CompactWebsiteError(string value)
    {
        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        if (normalized.Length == 0)
            return string.Empty;

        // Do not relay raw HTML, credentials, or a full upstream response to
        // the monitoring group. The first concise error is enough to diagnose.
        normalized = Regex.Replace(normalized, "<.*?>", string.Empty).Trim();
        return normalized.Length <= 180 ? normalized : normalized[..177] + "...";
    }

    private void StopTracking(RegistrationJob job)
    {
        if (!_tracking.TryRemove(job.Id, out var state))
        {
            return;
        }

        foreach (var (siteRun, handler) in state.Handlers)
        {
            siteRun.PropertyChanged -= handler;
        }
    }

    private static string Html(string? value) =>
        WebUtility.HtmlEncode(value ?? string.Empty);

    private static InlineKeyboardMarkup MainMenuKeyboard() =>
        new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("➕ Đăng ký 1 tài khoản", "start_add") },
            new[] { InlineKeyboardButton.WithCallbackData("📋 Đăng ký hàng loạt", "start_bulk") },
            new[] { InlineKeyboardButton.WithCallbackData("📊 Theo dõi yêu cầu", "status") },
            new[] { InlineKeyboardButton.WithCallbackData("👤 Tài khoản của tôi", "account_balance") },
            new[] { InlineKeyboardButton.WithCallbackData("🌐 Thuê proxy", "buy_proxy") },
            new[] { InlineKeyboardButton.WithCallbackData("💰 Nạp số dư", "deposit") }
        });

    private sealed class TrackingState
    {
        public TrackingState(string initialText)
        {
            LastText = initialText;
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset LastEdit { get; set; } = DateTimeOffset.MinValue;

        public string LastText { get; set; }

        public List<(SiteRunModel SiteRun, PropertyChangedEventHandler Handler)> Handlers { get; } = new();
    }
}
