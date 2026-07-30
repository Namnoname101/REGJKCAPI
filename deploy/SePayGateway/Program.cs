using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

var webhookSecret = builder.Configuration["SePay:WebhookSecret"]?.Trim() ?? string.Empty;
var accessToken = builder.Configuration["Gateway:AccessToken"]?.Trim() ?? string.Empty;
var databasePath = builder.Configuration["Gateway:DatabasePath"]?.Trim();
if (string.IsNullOrWhiteSpace(databasePath))
    databasePath = "/data/sepay-gateway.db";

if (string.IsNullOrWhiteSpace(webhookSecret))
    throw new InvalidOperationException("SePay:WebhookSecret is required.");
if (string.IsNullOrWhiteSpace(accessToken))
    throw new InvalidOperationException("Gateway:AccessToken is required.");

var store = new GatewayStore(databasePath);
await store.InitializeAsync();
builder.Services.AddSingleton(store);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/webhooks/sepay", async (
    HttpRequest request,
    GatewayStore gatewayStore,
    CancellationToken cancellationToken) =>
{
    const int maximumPayloadBytes = 64 * 1024;
    if (request.ContentLength > maximumPayloadBytes)
        return Results.Json(new { success = false }, statusCode: StatusCodes.Status413PayloadTooLarge);

    byte[] rawBody;
    await using (var buffer = new MemoryStream())
    {
        await request.Body.CopyToAsync(buffer, cancellationToken);
        rawBody = buffer.ToArray();
    }

    if (rawBody.Length > maximumPayloadBytes)
        return Results.Json(new { success = false }, statusCode: StatusCodes.Status413PayloadTooLarge);

    if (!VerifySePaySignature(
            rawBody,
            request.Headers["X-SePay-Signature"].FirstOrDefault(),
            request.Headers["X-SePay-Timestamp"].FirstOrDefault(),
            webhookSecret))
    {
        return Results.Json(new { success = false }, statusCode: StatusCodes.Status401Unauthorized);
    }

    long transactionId;
    try
    {
        using var document = JsonDocument.Parse(rawBody);
        transactionId = document.RootElement.GetProperty("id").GetInt64();
    }
    catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { success = false });
    }

    if (transactionId > 0)
    {
        await gatewayStore.EnqueueAsync(
            transactionId,
            Encoding.UTF8.GetString(rawBody),
            cancellationToken);
    }

    return Results.Ok(new { success = true });
});

app.MapGet("/api/events/pending", async (
    HttpRequest request,
    GatewayStore gatewayStore,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (!VerifyGatewayAuthorization(request, accessToken))
        return Results.Unauthorized();

    var events = await gatewayStore.GetPendingAsync(
        Math.Clamp(limit ?? 50, 1, 100),
        cancellationToken);
    return Results.Ok(events);
});

app.MapPost("/api/events/{id:long}/ack", async (
    HttpRequest request,
    GatewayStore gatewayStore,
    long id,
    GatewayAck? ack,
    CancellationToken cancellationToken) =>
{
    if (!VerifyGatewayAuthorization(request, accessToken))
        return Results.Unauthorized();

    if (id <= 0 || string.IsNullOrWhiteSpace(ack?.Status))
        return Results.BadRequest();

    var updated = await gatewayStore.AcknowledgeAsync(
        id,
        ack.Status.Trim(),
        cancellationToken);
    return updated ? Results.Ok(new { success = true }) : Results.NotFound();
});

await app.RunAsync();

static bool VerifySePaySignature(
    ReadOnlySpan<byte> rawBody,
    string? signature,
    string? timestampHeader,
    string secret)
{
    if (string.IsNullOrWhiteSpace(signature)
        || !long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
    {
        return false;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (timestamp < now - 300 || timestamp > now + 300)
        return false;

    var prefix = Encoding.UTF8.GetBytes($"{timestampHeader}.");
    var signedPayload = new byte[prefix.Length + rawBody.Length];
    Buffer.BlockCopy(prefix, 0, signedPayload, 0, prefix.Length);
    rawBody.CopyTo(signedPayload.AsSpan(prefix.Length));

    var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedPayload);
    var expected = Encoding.ASCII.GetBytes(
        $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}");
    var actual = Encoding.ASCII.GetBytes(signature.Trim());
    return expected.Length == actual.Length
           && CryptographicOperations.FixedTimeEquals(expected, actual);
}

static bool VerifyGatewayAuthorization(HttpRequest request, string accessToken)
{
    const string prefix = "Bearer ";
    var authorization = request.Headers.Authorization.FirstOrDefault();
    if (authorization is null
        || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var expected = Encoding.UTF8.GetBytes(accessToken);
    var actual = Encoding.UTF8.GetBytes(authorization[prefix.Length..].Trim());
    return expected.Length == actual.Length
           && CryptographicOperations.FixedTimeEquals(expected, actual);
}

public sealed record GatewayAck(string Status);
public sealed record GatewayEvent(long Id, string Payload, DateTime ReceivedAt);

public sealed class GatewayStore
{
    private readonly string _connectionString;

    public GatewayStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS GatewayEvents (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                SePayTransactionId INTEGER NOT NULL,
                Payload TEXT NOT NULL,
                ReceivedAt TEXT NOT NULL,
                AcknowledgedAt TEXT NULL,
                ResultStatus TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_GatewayEvents_SePayTransactionId
                ON GatewayEvents (SePayTransactionId);
            CREATE INDEX IF NOT EXISTS IX_GatewayEvents_AcknowledgedAt_Id
                ON GatewayEvents (AcknowledgedAt, Id);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task EnqueueAsync(
        long sePayTransactionId,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO GatewayEvents
                (SePayTransactionId, Payload, ReceivedAt)
            VALUES
                ($transactionId, $payload, $receivedAt);
            """;
        command.Parameters.AddWithValue("$transactionId", sePayTransactionId);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$receivedAt", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<GatewayEvent>> GetPendingAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var events = new List<GatewayEvent>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Payload, ReceivedAt
            FROM GatewayEvents
            WHERE AcknowledgedAt IS NULL
            ORDER BY Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new GatewayEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetDateTime(2)));
        }

        return events;
    }

    public async Task<bool> AcknowledgeAsync(
        long id,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE GatewayEvents
            SET AcknowledgedAt = $acknowledgedAt,
                ResultStatus = $status
            WHERE Id = $id
              AND AcknowledgedAt IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status[..Math.Min(status.Length, 50)]);
        command.Parameters.AddWithValue("$acknowledgedAt", DateTime.UtcNow);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
