using System.Collections.Concurrent;
using System.Threading.Channels;
using BAOCODETOOL.Configuration;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed class JobQueueService : IJobQueueService
{
    private readonly Channel<RegistrationJob> _channel;
    private readonly ConcurrentDictionary<long, int> _activeJobsByChat = new();
    private readonly ILogger<JobQueueService> _logger;
    private int _pending;
    private int _processing;
    private long _succeeded;
    private long _failed;

    public JobQueueService(
        IOptions<AutomationOptions> options,
        ILogger<JobQueueService> logger)
    {
        _logger = logger;
        var queueCapacity = options.Value.QueueCapacity;
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                queueCapacity,
                "Automation:QueueCapacity must be greater than zero.");
        }

        _channel = Channel.CreateBounded<RegistrationJob>(new BoundedChannelOptions(queueCapacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public async ValueTask EnqueueBatchAsync(
        IReadOnlyCollection<RegistrationJob> jobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        cancellationToken.ThrowIfCancellationRequested();

        if (jobs.Count == 0)
        {
            return;
        }

        foreach (var job in jobs)
        {
            job.Status = RegistrationJobStatus.Pending;
            Interlocked.Increment(ref _pending);
            _activeJobsByChat.AddOrUpdate(job.ChatId, 1, static (_, current) => current + 1);

            try
            {
                // BoundedChannelFullMode.Wait intentionally applies backpressure
                // when all workers are busy instead of growing memory without a
                // limit or silently dropping registration requests.
                await _channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Decrement(ref _pending);
                DecrementActiveJobCount(job.ChatId);
                throw;
            }

            _logger.LogInformation(
                "New job received. JobId={JobId}, ChatId={ChatId}, Account={Account}, Status={Status}",
                job.Id,
                job.ChatId,
                job.Account.Username,
                job.Status);
        }
    }

    public async ValueTask<RegistrationJob> DequeueAsync(CancellationToken cancellationToken)
    {
        var job = await _channel.Reader.ReadAsync(cancellationToken);

        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _processing);
        job.Status = RegistrationJobStatus.Processing;
        job.StartedAt = DateTimeOffset.UtcNow;

        return job;
    }

    public int Complete(
        RegistrationJob job,
        RegistrationJobStatus status,
        string? errorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (status is not RegistrationJobStatus.Success and not RegistrationJobStatus.Failed)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A completed job must be Success or Failed.");
        }

        if (Interlocked.Exchange(ref job.CompletionClaimed, 1) != 0)
        {
            _logger.LogWarning(
                "Duplicate job completion ignored. JobId={JobId}, ChatId={ChatId}, Status={Status}",
                job.Id,
                job.ChatId,
                status);

            return _activeJobsByChat.TryGetValue(job.ChatId, out var activeCount)
                ? activeCount
                : 0;
        }

        Interlocked.Decrement(ref _processing);
        job.Status = status;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = DateTimeOffset.UtcNow;

        if (status == RegistrationJobStatus.Success)
        {
            Interlocked.Increment(ref _succeeded);
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }

        var remaining = DecrementActiveJobCount(job.ChatId);

        return remaining;
    }

    public IReadOnlyList<RegistrationJob> FailPending(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = "Application shutdown interrupted this queued job.";
        }

        // Reject producers that are still waiting on a full channel, then
        // settle every item that was already accepted into the queue.
        _channel.Writer.TryComplete();
        var failedJobs = new List<RegistrationJob>();

        while (_channel.Reader.TryRead(out var job))
        {
            Interlocked.Decrement(ref _pending);

            if (Interlocked.Exchange(ref job.CompletionClaimed, 1) != 0)
            {
                continue;
            }

            job.Status = RegistrationJobStatus.Failed;
            job.ErrorMessage = errorMessage;
            job.CompletedAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _failed);
            DecrementActiveJobCount(job.ChatId);
            failedJobs.Add(job);
        }

        return failedJobs;
    }

    public bool HasActiveJobs(long chatId) =>
        _activeJobsByChat.TryGetValue(chatId, out var count) && count > 0;

    public JobQueueSnapshot GetSnapshot() =>
        new(
            Volatile.Read(ref _pending),
            Volatile.Read(ref _processing),
            Interlocked.Read(ref _succeeded),
            Interlocked.Read(ref _failed));

    private int DecrementActiveJobCount(long chatId)
    {
        var remaining = _activeJobsByChat.AddOrUpdate(
            chatId,
            0,
            static (_, current) => Math.Max(0, current - 1));

        if (remaining == 0)
        {
            _activeJobsByChat.TryRemove(chatId, out _);
        }

        return remaining;
    }
}
