using System.Security.Cryptography;
using System.Text;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Data;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var databasePath = Path.Combine(Path.GetTempPath(), $"sepay-diagnostic-{Guid.NewGuid():N}.db");
AppDbContext? db = null;
try
{
    var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={databasePath}")
        .Options;

    db = new AppDbContext(dbOptions);
    var sePayOptions = new SePayOptions
    {
        Enabled = true,
        WebhookSecret = "diagnostic-secret",
        AccountNumber = "0905425286",
        BankName = "MBBank",
        AccountHolder = "LE TAN",
        TransferPrefix = "NAP",
        AllowedClockSkewSeconds = 300
    };

    var service = new SePayWebhookService(
        db,
        Options.Create(sePayOptions),
        NullLogger<SePayWebhookService>.Instance);

    const long chatId = 123456789;
    db.Users.Add(new User { ChatId = chatId });
    await db.SaveChangesAsync();

    VerifySignatures(service, sePayOptions.WebhookSecret);

    var incoming = NewPayload(1001, "in", 50_000, $"NAP{chatId}");
    var credited = await service.ProcessAsync(incoming, CancellationToken.None);
    Equal("Credited", credited.Status, "valid incoming transfer status");
    Equal(50_000m, credited.NewBalance, "balance after valid transfer");

    var duplicate = await service.ProcessAsync(incoming, CancellationToken.None);
    Equal("Duplicate", duplicate.Status, "duplicate transaction status");
    Equal(50_000m, await db.Users.Where(x => x.ChatId == chatId).Select(x => x.Balance).SingleAsync(),
        "duplicate must not change balance");
    Equal(1, await db.Transactions.CountAsync(x => x.ReferenceId == "SEPAY:1001"),
        "duplicate must not add a second ledger transaction");

    Equal("Ignored", (await service.ProcessAsync(NewPayload(1002, "out", 50_000, $"NAP{chatId}"), CancellationToken.None)).Status,
        "outgoing transfer status");
    Equal("Ignored", (await service.ProcessAsync(NewPayload(1003, "in", 50_000, "NO-CODE"), CancellationToken.None)).Status,
        "unmatched transfer status");

    var wrongAccount = NewPayload(1004, "in", 50_000, $"NAP{chatId}");
    wrongAccount.AccountNumber = "0000000000";
    Equal("Ignored", (await service.ProcessAsync(wrongAccount, CancellationToken.None)).Status,
        "wrong bank account status");

    var testPayload = NewPayload(0, "in", 50_000, $"NAP{chatId}");
    Equal("Test", (await service.ProcessAsync(testPayload, CancellationToken.None)).Status,
        "SePay test payload status");
    Equal(4, await db.SePayWebhookTransactions.CountAsync(), "persisted real webhook count");

    Console.WriteLine("SePay webhook diagnostics passed.");
}
finally
{
    if (db is not null)
        await db.DisposeAsync();
    SqliteConnection.ClearAllPools();
    if (File.Exists(databasePath))
        File.Delete(databasePath);
}

static SePayWebhookPayload NewPayload(long id, string transferType, decimal amount, string content) => new()
{
    Id = id,
    Gateway = "MBBank",
    TransactionDate = "2026-07-29 12:00:00",
    AccountNumber = "0905425286",
    Code = content,
    Content = content,
    TransferType = transferType,
    TransferAmount = amount,
    ReferenceCode = $"REF{id}"
};

static void VerifySignatures(SePayWebhookService service, string secret)
{
    const string body = "{\"id\":1001}";
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    var hash = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret),
        Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
    var signature = $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";

    Equal(true, service.VerifySignature(body, signature, timestamp, out _), "valid HMAC signature");
    Equal(false, service.VerifySignature(body + " ", signature, timestamp, out _), "modified payload signature");
    Equal(false, service.VerifySignature(body, signature, "1", out _), "expired signature timestamp");
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
}
