namespace BAOCODETOOL.Models;

public sealed class RegistrationJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid BatchId { get; init; } = Guid.NewGuid();

    public required long ChatId { get; init; }

    public required AccountModel Account { get; init; }

    public required int StatusMessageId { get; init; }

    public bool IsBulk { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public RegistrationJobStatus Status { get; internal set; } = RegistrationJobStatus.Pending;

    public string? ErrorMessage { get; internal set; }

    // Completion is owned by the queue. Keeping the claim on the job itself
    // makes Complete idempotent if a shutdown/error path ever retries its
    // finalization, preventing negative processing counters and duplicate
    // success/failure metrics.
    internal int CompletionClaimed;
}
