using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Data;
using BAOCODETOOL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed partial class SePayWebhookService
{
    private readonly AppDbContext _dbContext;
    private readonly SePayOptions _options;
    private readonly ILogger<SePayWebhookService> _logger;

    public SePayWebhookService(
        AppDbContext dbContext,
        IOptions<SePayOptions> options,
        ILogger<SePayWebhookService> logger)
    {
        _dbContext = dbContext;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.Enabled && !string.IsNullOrWhiteSpace(_options.WebhookSecret);

    public bool VerifySignature(
        ReadOnlySpan<byte> rawBody,
        string? signature,
        string? timestampHeader,
        out string error)
    {
        error = string.Empty;
        if (!IsConfigured)
        {
            error = "SePay webhook is not configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            error = "Missing SePay signature headers.";
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (timestamp < now - _options.AllowedClockSkewSeconds
            || timestamp > now + _options.AllowedClockSkewSeconds)
        {
            error = "SePay webhook timestamp is outside the allowed window.";
            return false;
        }

        var timestampPrefix = Encoding.UTF8.GetBytes($"{timestampHeader}.");
        var signedPayload = new byte[timestampPrefix.Length + rawBody.Length];
        Buffer.BlockCopy(timestampPrefix, 0, signedPayload, 0, timestampPrefix.Length);
        rawBody.CopyTo(signedPayload.AsSpan(timestampPrefix.Length));
        var secret = Encoding.UTF8.GetBytes(_options.WebhookSecret);
        var hash = HMACSHA256.HashData(secret, signedPayload);
        var expected = Encoding.ASCII.GetBytes($"sha256={Convert.ToHexString(hash).ToLowerInvariant()}");
        var actual = Encoding.ASCII.GetBytes(signature.Trim());

        if (expected.Length != actual.Length
            || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            error = "Invalid SePay webhook signature.";
            return false;
        }

        return true;
    }

    public async Task<SePayProcessingResult> ProcessAsync(
        SePayWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Id <= 0)
            return SePayProcessingResult.Test();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var inserted = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT OR IGNORE INTO ""SePayWebhookTransactions""
                (""SePayTransactionId"", ""Gateway"", ""AccountNumber"", ""TransferType"",
                 ""TransferAmount"", ""Content"", ""ReferenceCode"", ""ChatId"", ""Status"",
                 ""StatusMessage"", ""TransactionDate"", ""ReceivedAt"")
            VALUES
                ({payload.Id}, {TrimTo(payload.Gateway, 50)}, {TrimTo(payload.AccountNumber, 50)},
                 {TrimTo(payload.TransferType, 10)}, {payload.TransferAmount}, {TrimTo(payload.Content, 500)},
                 {TrimTo(payload.ReferenceCode, 100)}, NULL, {"Received"}, NULL,
                 {ParseTransactionDate(payload.TransactionDate)}, {DateTime.UtcNow})", cancellationToken);

        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return SePayProcessingResult.Duplicate(payload.Id);
        }

        var receipt = await _dbContext.SePayWebhookTransactions
            .SingleAsync(x => x.SePayTransactionId == payload.Id, cancellationToken);

        if (!string.Equals(payload.TransferType, "in", StringComparison.OrdinalIgnoreCase))
            return await IgnoreAsync(receipt, "Ignored", "Webhook is not an incoming transfer.", transaction, cancellationToken);

        if (payload.TransferAmount <= 0)
            return await IgnoreAsync(receipt, "Ignored", "Transfer amount must be positive.", transaction, cancellationToken);

        if (!string.Equals(payload.AccountNumber?.Trim(), _options.AccountNumber.Trim(), StringComparison.Ordinal))
            return await IgnoreAsync(receipt, "Ignored", "Bank account does not match the configured account.", transaction, cancellationToken);

        var paymentRequest = await FindPaymentRequestAsync(payload, cancellationToken);
        if (paymentRequest is not null)
        {
            return await ProcessPaymentRequestAsync(
                receipt,
                payload,
                paymentRequest,
                transaction,
                cancellationToken);
        }

        if (!TryExtractChatId(payload, out var chatId))
            return await IgnoreAsync(receipt, "Unmatched", "Transfer content does not contain a valid top-up code.", transaction, cancellationToken);

        return await CreditAsync(receipt, payload, chatId, transaction, cancellationToken);
    }

    private async Task<SePayProcessingResult> ProcessPaymentRequestAsync(
        SePayWebhookTransaction receipt,
        SePayWebhookPayload payload,
        SePayPaymentRequest paymentRequest,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(paymentRequest.Status, "Pending", StringComparison.Ordinal))
        {
            return await IgnoreAsync(
                receipt,
                "Ignored",
                $"Payment request {paymentRequest.TransferCode} is {paymentRequest.Status}.",
                transaction,
                cancellationToken,
                paymentRequest.ChatId);
        }

        if (paymentRequest.ExpiresAt <= DateTime.UtcNow)
        {
            paymentRequest.Status = "Expired";
            return await IgnoreAsync(
                receipt,
                "Ignored",
                $"Payment request {paymentRequest.TransferCode} has expired.",
                transaction,
                cancellationToken,
                paymentRequest.ChatId);
        }

        if (paymentRequest.ExpectedAmount != payload.TransferAmount)
        {
            paymentRequest.Status = "AmountMismatch";
            return await IgnoreAsync(
                receipt,
                "Ignored",
                $"Payment request {paymentRequest.TransferCode} expects {paymentRequest.ExpectedAmount:N0} VND but received {payload.TransferAmount:N0} VND.",
                transaction,
                cancellationToken,
                paymentRequest.ChatId);
        }

        return await CreditAsync(receipt, payload, paymentRequest.ChatId, transaction, cancellationToken, paymentRequest);
    }

    private async Task<SePayProcessingResult> CreditAsync(
        SePayWebhookTransaction receipt,
        SePayWebhookPayload payload,
        long chatId,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken,
        SePayPaymentRequest? paymentRequest = null)
    {
        var user = await _dbContext.Users.SingleOrDefaultAsync(x => x.ChatId == chatId, cancellationToken);
        if (user is null)
            return await IgnoreAsync(receipt, "Unmatched", $"No existing user for ChatId {chatId}.", transaction, cancellationToken, chatId);

        user.Balance += payload.TransferAmount;
        user.UpdatedAt = DateTime.UtcNow;
        receipt.ChatId = chatId;
        receipt.Status = "Credited";
        receipt.StatusMessage = null;

        if (paymentRequest is not null)
        {
            paymentRequest.Status = "Paid";
            paymentRequest.PaidAt = DateTime.UtcNow;
            paymentRequest.SePayTransactionId = payload.Id;
        }

        _dbContext.Transactions.Add(new Transaction
        {
            UserId = user.Id,
            Amount = payload.TransferAmount,
            Type = "Deposit",
            Description = $"Nạp tiền tự động qua SePay ({payload.Gateway})",
            ReferenceId = $"SEPAY:{payload.Id}",
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "SePay transaction {TransactionId} credited {Amount} to ChatId {ChatId}. New balance: {Balance}",
            payload.Id,
            payload.TransferAmount,
            chatId,
            user.Balance);

        return SePayProcessingResult.Credited(payload.Id, chatId, payload.TransferAmount, user.Balance);
    }

    private async Task<SePayPaymentRequest?> FindPaymentRequestAsync(
        SePayWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { payload.Code, payload.Content })
        {
            if (!SePayPaymentRequestService.TryExtractPaymentRequestCode(
                    candidate,
                    _options.TransferPrefix,
                    out var transferCode))
            {
                continue;
            }

            var paymentRequest = await _dbContext.SePayPaymentRequests
                .SingleOrDefaultAsync(request => request.TransferCode == transferCode, cancellationToken);
            if (paymentRequest is not null)
                return paymentRequest;
        }

        return null;
    }

    private async Task<SePayProcessingResult> IgnoreAsync(
        SePayWebhookTransaction receipt,
        string status,
        string message,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken,
        long? chatId = null)
    {
        receipt.Status = status;
        receipt.StatusMessage = message;
        receipt.ChatId = chatId;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning(
            "SePay transaction {TransactionId} was not credited: {Reason}",
            receipt.SePayTransactionId,
            message);

        return SePayProcessingResult.Ignored(receipt.SePayTransactionId, message, chatId);
    }

    private bool TryExtractChatId(SePayWebhookPayload payload, out long chatId)
    {
        var candidates = new[] { payload.Code, payload.Content };
        var transferPrefix = SePayPaymentRequestService.NormalizeTransferPrefix(_options.TransferPrefix);
        if (string.IsNullOrEmpty(transferPrefix))
        {
            chatId = 0;
            return false;
        }

        var escapedPrefix = Regex.Escape(transferPrefix);
        var pattern = $@"(?<![A-Z0-9]){escapedPrefix}\s*(?<chatId>[0-9]{{5,20}})(?![0-9])";

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var match = Regex.Match(candidate, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success
                && long.TryParse(match.Groups["chatId"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out chatId)
                && chatId > 0)
            {
                return true;
            }
        }

        chatId = 0;
        return false;
    }

    private static DateTime ParseTransactionDate(string? value) =>
        DateTime.TryParseExact(
            value,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private static string TrimTo(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

public sealed class SePayWebhookPayload
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("gateway")]
    public string Gateway { get; set; } = string.Empty;

    [JsonPropertyName("transactionDate")]
    public string TransactionDate { get; set; } = string.Empty;

    [JsonPropertyName("accountNumber")]
    public string AccountNumber { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("transferType")]
    public string TransferType { get; set; } = string.Empty;

    [JsonPropertyName("transferAmount")]
    public decimal TransferAmount { get; set; }

    [JsonPropertyName("referenceCode")]
    public string ReferenceCode { get; set; } = string.Empty;
}

public sealed record SePayProcessingResult(
    string Status,
    long TransactionId,
    long? ChatId,
    decimal Amount,
    decimal? NewBalance,
    string? Message)
{
    public static SePayProcessingResult Test() => new("Test", 0, null, 0, null, null);
    public static SePayProcessingResult Duplicate(long id) => new("Duplicate", id, null, 0, null, null);
    public static SePayProcessingResult Ignored(long id, string message, long? chatId) => new("Ignored", id, chatId, 0, null, message);
    public static SePayProcessingResult Credited(long id, long chatId, decimal amount, decimal newBalance) => new("Credited", id, chatId, amount, newBalance, null);
}
