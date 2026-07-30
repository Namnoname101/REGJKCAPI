using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Data;
using BAOCODETOOL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed class SePayPaymentRequestService
{
    private const string VietQrImageEndpoint = "https://vietqr.app/img";
    private const int TransferCodeRandomBytes = 8;

    private readonly AppDbContext _dbContext;
    private readonly SePayOptions _options;

    public SePayPaymentRequestService(
        AppDbContext dbContext,
        IOptions<SePayOptions> options)
    {
        _dbContext = dbContext;
        _options = options.Value;
    }

    public bool IsQrConfigured =>
        _options.Enabled
        && (!string.IsNullOrWhiteSpace(_options.WebhookSecret)
            || IsGatewayConfigured())
        && !string.IsNullOrWhiteSpace(_options.AccountNumber)
        && !string.IsNullOrWhiteSpace(GetBankCode())
        && !string.IsNullOrWhiteSpace(NormalizeTransferPrefix(_options.TransferPrefix));

    public long MinimumAmount => _options.MinimumTopUpAmount;
    public long MaximumAmount => _options.MaximumTopUpAmount;

    public async Task<SePayPaymentQr> CreateAsync(
        long chatId,
        long amount,
        CancellationToken cancellationToken)
    {
        if (!IsQrConfigured)
            throw new InvalidOperationException("SePay QR is not configured.");

        if (chatId <= 0)
            throw new ArgumentOutOfRangeException(nameof(chatId));

        ValidateAmount(amount);

        var transferCode = await CreateUniqueTransferCodeAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var paymentRequest = new SePayPaymentRequest
        {
            ChatId = chatId,
            TransferCode = transferCode,
            ExpectedAmount = amount,
            Status = "Pending",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_options.QrExpirationMinutes)
        };

        _dbContext.SePayPaymentRequests.Add(paymentRequest);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new SePayPaymentQr(paymentRequest, BuildQrImageUrl(paymentRequest));
    }

    public bool TryParseAmount(string? value, out long amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().ToUpperInvariant()
            .Replace("VND", string.Empty, StringComparison.Ordinal)
            .Replace("VNĐ", string.Empty, StringComparison.Ordinal)
            .Replace("Đ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        var multiplier = 1L;
        if (normalized.EndsWith('K'))
        {
            multiplier = 1_000L;
            normalized = normalized[..^1];
        }

        if (!Regex.IsMatch(normalized, @"^(?:[0-9]+|[0-9]{1,3}(?:[.,][0-9]{3})+)$")
            || !long.TryParse(
                normalized.Replace(".", string.Empty, StringComparison.Ordinal)
                    .Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var baseAmount))
        {
            return false;
        }

        try
        {
            amount = checked(baseAmount * multiplier);
            ValidateAmount(amount);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static string NormalizeTransferPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().ToUpperInvariant();
        return Regex.IsMatch(normalized, "^[A-Z0-9]{1,32}$")
            ? normalized
            : string.Empty;
    }

    public static bool TryExtractPaymentRequestCode(
        string? value,
        string transferPrefix,
        out string transferCode)
    {
        transferCode = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalizedPrefix = NormalizeTransferPrefix(transferPrefix);
        if (string.IsNullOrEmpty(normalizedPrefix))
            return false;

        var pattern = $@"(?<![A-Z0-9])(?<code>{Regex.Escape(normalizedPrefix)}[A-F0-9]{{{TransferCodeRandomBytes * 2}}})(?![A-Z0-9])";
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        transferCode = match.Groups["code"].Value.ToUpperInvariant();
        return true;
    }

    private async Task<string> CreateUniqueTransferCodeAsync(CancellationToken cancellationToken)
    {
        var prefix = NormalizeTransferPrefix(_options.TransferPrefix);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var randomSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(TransferCodeRandomBytes));
            var transferCode = prefix + randomSuffix;
            var exists = await _dbContext.SePayPaymentRequests
                .AnyAsync(request => request.TransferCode == transferCode, cancellationToken);
            if (!exists)
                return transferCode;
        }

        throw new InvalidOperationException("Could not create a unique SePay transfer code.");
    }

    private string BuildQrImageUrl(SePayPaymentRequest paymentRequest)
    {
        var parameters = new Dictionary<string, string>
        {
            ["acc"] = _options.AccountNumber.Trim(),
            ["bank"] = GetBankCode(),
            ["amount"] = paymentRequest.ExpectedAmount.ToString("0", CultureInfo.InvariantCulture),
            ["des"] = paymentRequest.TransferCode,
            ["template"] = string.IsNullOrWhiteSpace(_options.QrTemplate)
                ? "compact"
                : _options.QrTemplate.Trim(),
            ["showinfo"] = "true"
        };

        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"{VietQrImageEndpoint}?{query}";
    }

    private string GetBankCode() => string.IsNullOrWhiteSpace(_options.QrBankCode)
        ? _options.BankName.Trim()
        : _options.QrBankCode.Trim();

    private bool IsGatewayConfigured() =>
        Uri.TryCreate(_options.GatewayBaseUrl, UriKind.Absolute, out var gatewayUri)
        && gatewayUri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(_options.GatewayAccessToken);

    private void ValidateAmount(long amount)
    {
        if (amount < _options.MinimumTopUpAmount || amount > _options.MaximumTopUpAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                $"Top-up amount must be between {_options.MinimumTopUpAmount:N0} and {_options.MaximumTopUpAmount:N0} VND.");
        }
    }
}

public sealed record SePayPaymentQr(SePayPaymentRequest PaymentRequest, string QrImageUrl);
