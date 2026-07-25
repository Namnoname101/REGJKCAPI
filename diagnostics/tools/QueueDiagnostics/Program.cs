using BAOCODETOOL.Configuration;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.SetMinimumLevel(LogLevel.Warning));

var options = Options.Create(new AutomationOptions
{
    QueueCapacity = 2
});

var queue = new JobQueueService(
    options,
    loggerFactory.CreateLogger<JobQueueService>());

var jobs = Enumerable.Range(0, 5)
    .Select(index => CreateJob(7001, $"queue-{index:D2}"))
    .ToArray();

var enqueueTask = queue.EnqueueBatchAsync(jobs, CancellationToken.None).AsTask();
await Task.Delay(150);

var backpressureSnapshot = queue.GetSnapshot();
if (backpressureSnapshot.Pending < options.Value.QueueCapacity
    || enqueueTask.IsCompleted)
{
    throw new InvalidOperationException(
        $"Bounded queue did not apply backpressure. Pending={backpressureSnapshot.Pending}; " +
        $"Capacity={options.Value.QueueCapacity}");
}

foreach (var job in jobs)
{
    var dequeued = await queue.DequeueAsync(CancellationToken.None);
    if (!ReferenceEquals(job, dequeued))
    {
        throw new InvalidOperationException("Queue returned jobs out of FIFO order.");
    }

    queue.Complete(dequeued, RegistrationJobStatus.Success);
}

await enqueueTask;

var completedSnapshot = queue.GetSnapshot();
if (completedSnapshot.Pending != 0
    || completedSnapshot.Processing != 0
    || completedSnapshot.Succeeded != jobs.Length
    || completedSnapshot.Failed != 0
    || queue.HasActiveJobs(7001))
{
    throw new InvalidOperationException(
        $"Queue counters did not settle after draining. {completedSnapshot}");
}

// A duplicate finalization must not drive counters negative or inflate
// success/failure totals.
var duplicateRemaining = queue.Complete(jobs[0], RegistrationJobStatus.Success);
var duplicateSnapshot = queue.GetSnapshot();
if (duplicateRemaining != 0
    || duplicateSnapshot.Processing != 0
    || duplicateSnapshot.Succeeded != jobs.Length)
{
    throw new InvalidOperationException(
        $"Duplicate completion was not ignored. Remaining={duplicateRemaining}; {duplicateSnapshot}");
}

var cancellationQueue = new JobQueueService(
    Options.Create(new AutomationOptions { QueueCapacity = 1 }),
    loggerFactory.CreateLogger<JobQueueService>());
var first = CreateJob(7002, "cancel-00");
var second = CreateJob(7002, "cancel-01");
await cancellationQueue.EnqueueBatchAsync(new[] { first }, CancellationToken.None);

using var enqueueCancellation = new CancellationTokenSource(250);
try
{
    await cancellationQueue.EnqueueBatchAsync(new[] { second }, enqueueCancellation.Token);
    throw new InvalidOperationException("A full queue accepted a canceled write.");
}
catch (OperationCanceledException)
{
}

var canceledSnapshot = cancellationQueue.GetSnapshot();
if (canceledSnapshot.Pending != 1 || cancellationQueue.HasActiveJobs(7002) == false)
{
    throw new InvalidOperationException(
        $"Canceled enqueue corrupted queue accounting. {canceledSnapshot}");
}

var remainingJob = await cancellationQueue.DequeueAsync(CancellationToken.None);
cancellationQueue.Complete(remainingJob, RegistrationJobStatus.Failed, "diagnostic cleanup");
if (cancellationQueue.GetSnapshot().Processing != 0
    || cancellationQueue.HasActiveJobs(7002))
{
    throw new InvalidOperationException("Canceled enqueue cleanup did not settle.");
}

var shutdownQueue = new JobQueueService(
    Options.Create(new AutomationOptions { QueueCapacity = 2 }),
    loggerFactory.CreateLogger<JobQueueService>());
var shutdownJobs = new[]
{
    CreateJob(7003, "shutdown-00"),
    CreateJob(7003, "shutdown-01")
};
await shutdownQueue.EnqueueBatchAsync(shutdownJobs, CancellationToken.None);
var failedOnShutdown = shutdownQueue.FailPending("diagnostic shutdown");
var shutdownSnapshot = shutdownQueue.GetSnapshot();
if (failedOnShutdown.Count != shutdownJobs.Length
    || shutdownSnapshot.Pending != 0
    || shutdownSnapshot.Processing != 0
    || shutdownSnapshot.Failed != shutdownJobs.Length
    || shutdownQueue.HasActiveJobs(7003))
{
    throw new InvalidOperationException(
        $"Pending shutdown drain did not settle. {shutdownSnapshot}");
}

Console.WriteLine(
    $"PASS: bounded queue backpressure, cancellation and idempotent completion. " +
    $"Succeeded={completedSnapshot.Succeeded}; CanceledPending={canceledSnapshot.Pending}; " +
    $"ShutdownFailed={failedOnShutdown.Count}");

static RegistrationJob CreateJob(long chatId, string username) =>
    new()
    {
        ChatId = chatId,
        Account = new AccountModel
        {
            Username = username,
            Password = username + "1",
            FullName = "Queue diagnostic",
            PhoneNumber = "0000000000"
        },
        StatusMessageId = 1
    };
