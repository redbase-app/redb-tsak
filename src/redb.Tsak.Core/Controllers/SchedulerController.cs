using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.Matchers;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Quartz scheduler management endpoints.
/// Mirrors lt.tsak RouteContainerController scheduler/ endpoints.
/// </summary>
[Route("/api/scheduler")]
public class SchedulerController : RedbController
{
    private IScheduler? Scheduler => Context.GetService<IScheduler>();
    private ILogger? Log => Context.GetService<ILogger>();

    [HttpGet("/status")]
    public async Task<object> GetStatus()
    {
        var scheduler = Scheduler;
        if (scheduler is null || scheduler.IsShutdown)
        {
            return new SchedulerStatusResponse
            {
                IsStarted = false,
                InStandbyMode = false,
                IsShutdown = scheduler?.IsShutdown ?? true,
                SchedulerName = scheduler?.SchedulerName ?? "N/A",
                SchedulerInstanceId = scheduler?.SchedulerInstanceId ?? "N/A",
                TotalJobs = 0,
                RunningJobs = 0,
                Status = "Shutdown",
                Timestamp = DateTime.UtcNow
            };
        }

        var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        var running = await scheduler.GetCurrentlyExecutingJobs();

        return new SchedulerStatusResponse
        {
            IsStarted = scheduler.IsStarted,
            InStandbyMode = scheduler.InStandbyMode,
            IsShutdown = scheduler.IsShutdown,
            SchedulerName = scheduler.SchedulerName,
            SchedulerInstanceId = scheduler.SchedulerInstanceId,
            TotalJobs = jobKeys.Count,
            RunningJobs = running.Count,
            Status = scheduler.InStandbyMode ? "Standby" : scheduler.IsStarted ? "Running" : "Stopped",
            Timestamp = DateTime.UtcNow
        };
    }

    [HttpGet("/scheduled")]
    public async Task<object> GetScheduledJobs()
    {
        var scheduler = Scheduler;
        if (scheduler is null || scheduler.IsShutdown)
            return new ScheduledJobsResponse { TotalScheduledJobs = 0, Jobs = [] };

        var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        var jobs = new List<ScheduledJobInfo>();

        foreach (var jobKey in jobKeys)
        {
            var detail = await scheduler.GetJobDetail(jobKey);
            if (detail is null) continue;

            var triggers = await scheduler.GetTriggersOfJob(jobKey);
            foreach (var trigger in triggers)
            {
                var state = await scheduler.GetTriggerState(trigger.Key);
                jobs.Add(new ScheduledJobInfo
                {
                    JobKey = jobKey.ToString(),
                    JobName = jobKey.Name,
                    JobGroup = jobKey.Group,
                    JobType = detail.JobType.Name,
                    TriggerKey = trigger.Key.ToString(),
                    TriggerState = state.ToString(),
                    CronExpression = trigger is ICronTrigger cron ? cron.CronExpressionString : null,
                    NextFireTime = trigger.GetNextFireTimeUtc()?.UtcDateTime,
                    PreviousFireTime = trigger.GetPreviousFireTimeUtc()?.UtcDateTime,
                    Priority = trigger.Priority
                });
            }
        }

        return new ScheduledJobsResponse { TotalScheduledJobs = jobs.Count, Jobs = jobs.ToArray() };
    }

    [HttpGet("/jobs")]
    public async Task<object> GetRunningJobs()
    {
        var scheduler = Scheduler;
        if (scheduler is null || scheduler.IsShutdown)
            return new RunningJobsResponse { TotalRunningJobs = 0, Jobs = [] };

        var running = await scheduler.GetCurrentlyExecutingJobs();
        var jobs = running.Select(j => new RunningJobInfo
        {
            JobKey = j.JobDetail.Key.ToString(),
            JobName = j.JobDetail.Key.Name,
            JobGroup = j.JobDetail.Key.Group,
            JobType = j.JobDetail.JobType.Name,
            TriggerKey = j.Trigger.Key.ToString(),
            FireTime = j.FireTimeUtc.UtcDateTime,
            RunTimeMs = (long)j.JobRunTime.TotalMilliseconds,
            RefireCount = j.RefireCount
        }).ToArray();

        return new RunningJobsResponse { TotalRunningJobs = jobs.Length, Jobs = jobs };
    }

    [HttpPost("/start")]
    [AuditAdminAction(ActionName = "SchedulerStart")]
    public async Task<object> Start()
    {
        var scheduler = Scheduler;
        if (scheduler is null)
            return new SchedulerActionResponse { Success = false, Message = "Scheduler not available" };

        if (!scheduler.IsStarted)
        {
            await scheduler.Start();
            Log?.LogInformation("Scheduler started");
            return new SchedulerActionResponse { Success = true, Message = "Scheduler started" };
        }

        if (scheduler.InStandbyMode)
        {
            await scheduler.Start();
            Log?.LogInformation("Scheduler resumed from standby");
            return new SchedulerActionResponse { Success = true, Message = "Scheduler resumed from standby" };
        }

        return new SchedulerActionResponse { Success = true, Message = "Scheduler already running" };
    }

    [HttpPost("/standby")]
    [AuditAdminAction(ActionName = "SchedulerStandby")]
    public async Task<object> Standby()
    {
        var scheduler = Scheduler;
        if (scheduler is null)
            return new SchedulerActionResponse { Success = false, Message = "Scheduler not available" };

        if (scheduler.InStandbyMode)
            return new SchedulerActionResponse { Success = true, Message = "Scheduler already in standby" };

        if (!scheduler.IsStarted)
            return new SchedulerActionResponse { Success = false, Message = "Scheduler not started" };

        await scheduler.Standby();
        Log?.LogInformation("Scheduler moved to standby");
        return new SchedulerActionResponse { Success = true, Message = "Scheduler moved to standby" };
    }

    [HttpPost("/pause-job")]
    [AuditAdminAction(ActionName = "PauseJob", TargetParam = "jobKeyStr")]
    public async Task<object> PauseJob([FromQuery("key")] string jobKeyStr)
    {
        var scheduler = Scheduler;
        if (scheduler is null || scheduler.IsShutdown)
            return new SchedulerActionResponse { Success = false, Message = "Scheduler not available" };

        var key = await FindJobKey(scheduler, jobKeyStr);
        if (key is null)
            return new SchedulerActionResponse { Success = false, Message = $"Job '{jobKeyStr}' not found" };

        await scheduler.PauseJob(key);
        Log?.LogInformation("Job '{JobKey}' paused", key);
        return new SchedulerActionResponse { Success = true, Message = $"Job '{key}' paused" };
    }

    [HttpPost("/resume-job")]
    [AuditAdminAction(ActionName = "ResumeJob", TargetParam = "jobKeyStr")]
    public async Task<object> ResumeJob([FromQuery("key")] string jobKeyStr)
    {
        var scheduler = Scheduler;
        if (scheduler is null || scheduler.IsShutdown)
            return new SchedulerActionResponse { Success = false, Message = "Scheduler not available" };

        var key = await FindJobKey(scheduler, jobKeyStr);
        if (key is null)
            return new SchedulerActionResponse { Success = false, Message = $"Job '{jobKeyStr}' not found" };

        await scheduler.ResumeJob(key);
        Log?.LogInformation("Job '{JobKey}' resumed", key);
        return new SchedulerActionResponse { Success = true, Message = $"Job '{key}' resumed" };
    }

    /// <summary>Find a Quartz JobKey by its ToString() representation among all registered jobs.</summary>
    private static async Task<JobKey?> FindJobKey(IScheduler scheduler, string jobKeyStr)
    {
        var allKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        return allKeys.FirstOrDefault(k => k.ToString() == jobKeyStr);
    }
}
