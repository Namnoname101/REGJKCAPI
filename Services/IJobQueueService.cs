using BAOCODETOOL.Models;

namespace BAOCODETOOL.Services;

public interface IJobQueueService
{
    ValueTask EnqueueBatchAsync(IReadOnlyCollection<RegistrationJob> jobs, CancellationToken cancellationToken);

    ValueTask<RegistrationJob> DequeueAsync(CancellationToken cancellationToken);

    int Complete(RegistrationJob job, RegistrationJobStatus status, string? errorMessage = null);

    IReadOnlyList<RegistrationJob> FailPending(string errorMessage);

    bool HasActiveJobs(long chatId);

    JobQueueSnapshot GetSnapshot();
}
