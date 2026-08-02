using BAOCODETOOL.Configuration;
using BAOCODETOOL.Core;
using BAOCODETOOL.Data;
using BAOCODETOOL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory _scopeFactory;

    public JobWorkerService(
        IJobQueueService jobQueue,
        IRegistrationOrchestrator orchestrator,
        IResultSenderService resultSender,
        IProfileRepository profileRepository,
        IOptions<AutomationOptions> options,
        ILogger<JobWorkerService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _jobQueue = jobQueue;
        _orchestrator = orchestrator;
        _resultSender = resultSender;
        _profileRepository = profileRepository;
        _options = options.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
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
                        MarkUnfinishedSitesFailed(
                            pendingJob,
                            "Application shutdown interrupted this queued job.");
                        using var refundTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await RefundFailedSitesAsync(pendingJob, refundTimeout.Token);

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

            var dbJobId = await RecordJobStartAsync(job);

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
                await RecordJobEndAsync(dbJobId, finalStatus, errorMessage, job);
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

            using (var refundTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await RefundFailedSitesAsync(job, refundTimeout.Token);
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

    private async Task RefundFailedSitesAsync(
        RegistrationJob job,
        CancellationToken cancellationToken)
    {
        if (job.RegistrationPricePerSite <= 0m
            || string.IsNullOrWhiteSpace(job.ChargeReferenceId))
        {
            return;
        }

        var failedSites = job.Account.SiteRuns
            .Where(siteRun => siteRun.IsSelected && siteRun.Status != AccountStatus.Success)
            .Select(siteRun => siteRun.Site.ToString())
            .ToArray();
        if (failedSites.Length == 0
            || Interlocked.Exchange(ref job.RefundClaimed, 1) != 0)
        {
            return;
        }

        try
        {
            var refundAmount = failedSites.Length * job.RegistrationPricePerSite;
            using var scope = _scopeFactory.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var refunded = await userService.AddBalanceAsync(
                job.ChatId,
                refundAmount,
                $"Hoàn tiền {failedSites.Length:N0} site đăng ký thất bại - {string.Join(", ", failedSites)}",
                $"REG-REFUND-{job.Id:N}",
                cancellationToken);
            if (!refunded)
            {
                Interlocked.Exchange(ref job.RefundClaimed, 0);
                _logger.LogError(
                    "Could not refund failed registration sites. JobId={JobId}, ChatId={ChatId}, Amount={Amount}",
                    job.Id,
                    job.ChatId,
                    refundAmount);
                return;
            }

            job.RefundedAmount = refundAmount;
            job.RefundedSiteCount = failedSites.Length;
            job.RefundedSites = string.Join(", ", failedSites);
            _logger.LogInformation(
                "Refunded failed registration sites. JobId={JobId}, ChatId={ChatId}, Sites={Sites}, Amount={Amount}",
                job.Id,
                job.ChatId,
                job.RefundedSites,
                refundAmount);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref job.RefundClaimed, 0);
            _logger.LogError(
                exception,
                "Failed to refund registration sites. JobId={JobId}, ChatId={ChatId}",
                job.Id,
                job.ChatId);
        }
    }

    private async Task<int?> RecordJobStartAsync(RegistrationJob job)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstOrDefaultAsync(u => u.ChatId == job.ChatId);
            if (user != null)
            {
                var dbJob = new Job
                {
                    UserId = user.Id,
                    Provider = job.Account.SelectedSitesText,
                    Status = "Running",
                    StartTime = DateTime.UtcNow,
                    Logs = $"Bắt đầu chạy tiến trình cho {job.Account.Username}"
                };
                db.Jobs.Add(dbJob);
                await db.SaveChangesAsync();
                return dbJob.Id;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record job start to database for ChatId={ChatId}", job.ChatId);
        }
        return null;
    }

    private async Task RecordJobEndAsync(int? dbJobId, RegistrationJobStatus status, string? errorMessage, RegistrationJob job)
    {
        if (dbJobId == null) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbJob = await db.Jobs.FindAsync(dbJobId.Value);
            if (dbJob != null)
            {
                dbJob.Status = status == RegistrationJobStatus.Success ? "Success" : "Failed";
                dbJob.EndTime = DateTime.UtcNow;
                dbJob.Logs = errorMessage ?? job.Account.SiteLogText ?? "Hoàn thành thành công";

                var successfulSites = job.Account.SiteRuns
                    .Where(siteRun => siteRun.IsSelected && siteRun.Status == AccountStatus.Success)
                    .Select(siteRun => siteRun.Site.ToString())
                    .ToArray();
                if (successfulSites.Length > 0
                    && !await db.CreatedAccountHistories.AnyAsync(history => history.JobId == dbJob.Id))
                {
                    db.CreatedAccountHistories.Add(new CreatedAccountHistory
                    {
                        UserId = dbJob.UserId,
                        JobId = dbJob.Id,
                        Sites = string.Join(", ", successfulSites),
                        Username = job.Account.Username,
                        Password = job.Account.Password,
                        FullName = job.Account.FullName,
                        PhoneNumber = job.Account.PhoneNumber,
                        BankName = job.Account.BankName,
                        BankCode = job.Account.BankCode,
                        AccountNumber = job.Account.AccountNumber,
                        Level2Password = job.Account.Level2Password,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await db.SaveChangesAsync();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record job end to database for JobId={JobId}", dbJobId);
        }
    }
}
