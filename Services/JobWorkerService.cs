using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BAOCODETOOL.Services;

public sealed class JobWorkerService : BackgroundService
{
    private readonly IJobQueueService _jobQueue;
    private readonly IRegistrationOrchestrator _orchestrator;
    private readonly IResultSenderService _resultSender;
    private readonly IProfileRepository _profileRepository;
    private readonly AutomationOptions _options;
    private readonly ILogger<JobWorkerService> _logger;

    public JobWorkerService(
        IJobQueueService jobQueue,
        IRegistrationOrchestrator orchestrator,
        IResultSenderService resultSender,
        IProfileRepository profileRepository,
        IOptions<AutomationOptions> options,
        ILogger<JobWorkerService> logger)
    {
        _jobQueue = jobQueue;
        _orchestrator = orchestrator;
        _resultSender = resultSender;
        _profileRepository = profileRepository;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Waiting for jobs. WorkerCount={WorkerCount}",
            _options.MaxConcurrentJobs);

        var workers = Enumerable
            .Range(1, _options.MaxConcurrentJobs)
            .Select(workerId => ConsumeAsync(workerId, stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            var pendingJobs = _jobQueue.FailPending(
                "Application shutdown interrupted this queued job.");

            if (pendingJobs.Count > 0)
            {
                try
                {
                    _profileRepository.Save();
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Could not persist shutdown-failed queued jobs. Count={Count}",
                        pendingJobs.Count);
                }

                foreach (var pendingJob in pendingJobs)
                {
                    try
                    {
                        using var notificationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _resultSender.JobCompletedAsync(
                            pendingJob,
                            remainingJobsForUser: 0,
                            notificationTimeout.Token);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(
                            exception,
                            "Could not send shutdown result for queued job. JobId={JobId}",
                            pendingJob.Id);
                    }
                }

                _logger.LogWarning(
                    "Marked {Count} queued jobs as failed during shutdown.",
                    pendingJobs.Count);
            }
        }
    }

    private async Task ConsumeAsync(int workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RegistrationJob job;

            try
            {
                job = await _jobQueue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogInformation(
                "Job started. Worker={WorkerId}, JobId={JobId}, Account={Account}",
                workerId,
                job.Id,
                job.Account.Username);

            try
            {
                await _resultSender.JobStartedAsync(job, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not send the Telegram job-start update. JobId={JobId}, Account={Account}",
                    job.Id,
                    job.Account.Username);
            }

            RegistrationJobStatus finalStatus;
            string? errorMessage = null;

            try
            {
                await _orchestrator.ProcessAsync(job, stoppingToken);

                var selectedRuns = job.Account.SiteRuns.Where(siteRun => siteRun.IsSelected).ToList();
                var allSucceeded = selectedRuns.Count > 0 &&
                                   selectedRuns.All(siteRun => siteRun.Status == AccountStatus.Success);

                finalStatus = allSucceeded
                    ? RegistrationJobStatus.Success
                    : RegistrationJobStatus.Failed;

                if (allSucceeded)
                {
                    _logger.LogInformation(
                        "Job succeeded. Worker={WorkerId}, JobId={JobId}, Account={Account}",
                        workerId,
                        job.Id,
                        job.Account.Username);
                }
                else
                {
                    errorMessage = "One or more selected sites failed.";
                    _logger.LogWarning(
                        "Job failed. Worker={WorkerId}, JobId={JobId}, Account={Account}, Reason={Reason}",
                        workerId,
                        job.Id,
                        job.Account.Username,
                        errorMessage);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                finalStatus = RegistrationJobStatus.Failed;
                errorMessage = "Application shutdown interrupted this job.";
                MarkUnfinishedSitesFailed(job, errorMessage);
            }
            catch (Exception exception)
            {
                finalStatus = RegistrationJobStatus.Failed;
                errorMessage = exception.Message;
                MarkUnfinishedSitesFailed(job, $"Unhandled error: {exception.Message}");

                _logger.LogError(
                    exception,
                    "Job failed with an unhandled error. Worker={WorkerId}, JobId={JobId}, Account={Account}",
                    workerId,
                    job.Id,
                    job.Account.Username);
            }

            var remaining = 0;
            try
            {
                remaining = _jobQueue.Complete(job, finalStatus, errorMessage);
            }
            catch (Exception exception)
            {
                // Finalization must not terminate the consumer loop. The
                // queue has its own idempotency guard, but a malformed job or
                // unexpected implementation failure should still be isolated
                // to this request.
                _logger.LogError(
                    exception,
                    "Could not finalize job state. JobId={JobId}, Account={Account}",
                    job.Id,
                    job.Account.Username);
            }

            try
            {
                _profileRepository.Save();
            }
            catch (Exception exception)
            {
                // A transient profile-store IO failure must not convert a
                // completed browser workflow into a dead worker. The next
                // site/job completion will retry the persisted snapshot.
                _logger.LogError(
                    exception,
                    "Could not persist profiles after job completion. JobId={JobId}, Account={Account}",
                    job.Id,
                    job.Account.Username);
            }

            try
            {
                using var notificationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _resultSender.JobCompletedAsync(job, remaining, notificationTimeout.Token);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not send the Telegram result. JobId={JobId}, Account={Account}",
                    job.Id,
                    job.Account.Username);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static void MarkUnfinishedSitesFailed(RegistrationJob job, string message)
    {
        foreach (var siteRun in job.Account.SiteRuns.Where(siteRun =>
                     siteRun.IsSelected &&
                     siteRun.Status is not AccountStatus.Success and not AccountStatus.Failed))
        {
            AutomationUi.SetSiteStatus(job.Account, siteRun, AccountStatus.Failed, message);
        }
    }
}
