using BAOCODETOOL.Models;

namespace BAOCODETOOL.Services;

public interface IResultSenderService
{
    Task<RegistrationJob> CreateJobAsync(
        long chatId,
        AccountModel account,
        bool isBulk,
        Guid batchId,
        CancellationToken cancellationToken);

    Task JobStartedAsync(RegistrationJob job, CancellationToken cancellationToken);

    Task JobCompletedAsync(
        RegistrationJob job,
        int remainingJobsForUser,
        CancellationToken cancellationToken);
}
