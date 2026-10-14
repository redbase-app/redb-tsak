using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.Matchers;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Quartz scheduler management endpoints.
/// Mirrors lt.tsak RouteContainerController scheduler/ endpoints.
/// <para>
/// Every route context owns its Quartz scheduler (redb.Route's scheduler per context, as Apache Camel's per
/// CamelContext): the endpoints read the schedulers of every context of the node — this one included — and report
/// each job with its context. A job key is unique within its context only, so an action takes the context; without
/// it the key must be found in exactly one context, never guessed.
/// </para>
/// </summary>
[Route("/api/scheduler")]
public class SchedulerController : RedbController
{
    private ILogger? Log => Context.GetService<ILogger>();

    [HttpGet("/status")]
    public async Task<object> GetStatus()
    {
        var schedulers = Schedulers();
        var perContext = new List<SchedulerContextStatus>(schedulers.Count);
        foreach (var (context, scheduler) in schedulers)
            perContext.Add(await StatusOf(context, scheduler));

        var live = perContext.Where(s => !s.IsShutdown).ToList();
        if (live.Count == 0)
        {
            return new SchedulerStatusResponse
            {
                IsStarted = false,
                InStandbyMode = false,
                IsShutdown = true,
                SchedulerName = perContext.Count == 1 ? perContext[0].SchedulerName : "N/A",
                SchedulerInstanceId = perContext.Count == 1 ? perContext[0].SchedulerInstanceId : "N/A",
                TotalJobs = 0,
                RunningJobs = 0,
                Status = "Shutdown",
                Timestamp = DateTime.UtcNow,
                Schedulers = [.. perContext]
            };
        }

        // The fields of a single scheduler summarize the node's: sums, all started, any in standby.
        var isStarted = live.All(s => s.IsStarted);
        var inStandby = live.Any(s => s.InStandbyMode);
        return new SchedulerStatusResponse
        {
            IsStarted = isStarted,
            InStandbyMode = inStandby,
            IsShutdown = false,
            SchedulerName = live.Count == 1 ? live[0].SchedulerName : CommonPrefix(live.Select(s => s.SchedulerName)),
            SchedulerInstanceId = live.Count == 1 ? live[0].SchedulerInstanceId : $"{live.Count} schedulers",
            TotalJobs = live.Sum(s => s.TotalJobs),
            RunningJobs = live.Sum(s => s.RunningJobs),
            Status = inStandby ? "Standby" : isStarted ? "Running" : "Stopped",
            Timestamp = DateTime.UtcNow,
            Schedulers = [.. perContext]
        };
    }

    [HttpGet("/scheduled")]
    public async Task<object> GetScheduledJobs()
    {
        var jobs = new List<ScheduledJobInfo>();
        foreach (var (context, scheduler) in Schedulers().Where(s => !s.Scheduler.IsShutdown))
        {
            var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
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
                        Priority = trigger.Priority,
                        Context = context
                    });
                }
            }
        }

        return new ScheduledJobsResponse { TotalScheduledJobs = jobs.Count, Jobs = jobs.ToArray() };
    }

    [HttpGet("/jobs")]
    public async Task<object> GetRunningJobs()
    {
        var jobs = new List<RunningJobInfo>();
        foreach (var (context, scheduler) in Schedulers().Where(s => !s.Scheduler.IsShutdown))
        {
            var running = await scheduler.GetCurrentlyExecutingJobs();
            jobs.AddRange(running.Select(j => new RunningJobInfo
            {
                JobKey = j.JobDetail.Key.ToString(),
                JobName = j.JobDetail.Key.Name,
                JobGroup = j.JobDetail.Key.Group,
                JobType = j.JobDetail.JobType.Name,
                TriggerKey = j.Trigger.Key.ToString(),
                FireTime = j.FireTimeUtc.UtcDateTime,
                RunTimeMs = (long)j.JobRunTime.TotalMilliseconds,
                RefireCount = j.RefireCount,
                Context = context
            }));
        }

        return new RunningJobsResponse { TotalRunningJobs = jobs.Count, Jobs = jobs.ToArray() };
    }

    /// <summary>Starts (or resumes from standby) the scheduler of <paramref name="context"/>, or of every context.</summary>
    [HttpPost("/start")]
    [AuditAdminAction(ActionName = "SchedulerStart")]
    public async Task<object> Start([FromQuery("context")] string? context = null)
    {
        var (targets, error) = Targets(context);
        if (error is not null)
            return error;

        var messages = new List<string>();
        foreach (var (name, scheduler) in targets)
        {
            if (!scheduler.IsStarted)
            {
                await scheduler.Start();
                Log?.LogInformation("Scheduler of context {Context} started", name);
                messages.Add(Say(name, "Scheduler started", targets.Count));
            }
            else if (scheduler.InStandbyMode)
            {
                await scheduler.Start();
                Log?.LogInformation("Scheduler of context {Context} resumed from standby", name);
                messages.Add(Say(name, "Scheduler resumed from standby", targets.Count));
            }
            else
            {
                messages.Add(Say(name, "Scheduler already running", targets.Count));
            }
        }

        return new SchedulerActionResponse { Success = true, Message = string.Join("; ", messages) };
    }

    /// <summary>Moves the scheduler of <paramref name="context"/>, or of every context, to standby.</summary>
    [HttpPost("/standby")]
    [AuditAdminAction(ActionName = "SchedulerStandby")]
    public async Task<object> Standby([FromQuery("context")] string? context = null)
    {
        var (targets, error) = Targets(context);
        if (error is not null)
            return error;

        var success = true;
        var messages = new List<string>();
        foreach (var (name, scheduler) in targets)
        {
            if (scheduler.InStandbyMode)
            {
                messages.Add(Say(name, "Scheduler already in standby", targets.Count));
            }
            else if (!scheduler.IsStarted)
            {
                success = false;
                messages.Add(Say(name, "Scheduler not started", targets.Count));
            }
            else
            {
                await scheduler.Standby();
                Log?.LogInformation("Scheduler of context {Context} moved to standby", name);
                messages.Add(Say(name, "Scheduler moved to standby", targets.Count));
            }
        }

        return new SchedulerActionResponse { Success = success, Message = string.Join("; ", messages) };
    }

    [HttpPost("/pause-job")]
    [AuditAdminAction(ActionName = "PauseJob", TargetParam = "jobKeyStr")]
    public async Task<object> PauseJob([FromQuery("key")] string jobKeyStr, [FromQuery("context")] string? context = null)
    {
        var (scheduler, key, error) = await FindJob(jobKeyStr, context);
        if (error is not null)
            return error;

        await scheduler!.PauseJob(key!);
        Log?.LogInformation("Job '{JobKey}' paused", key);
        return new SchedulerActionResponse { Success = true, Message = $"Job '{key}' paused" };
    }

    [HttpPost("/resume-job")]
    [AuditAdminAction(ActionName = "ResumeJob", TargetParam = "jobKeyStr")]
    public async Task<object> ResumeJob([FromQuery("key")] string jobKeyStr, [FromQuery("context")] string? context = null)
    {
        var (scheduler, key, error) = await FindJob(jobKeyStr, context);
        if (error is not null)
            return error;

        await scheduler!.ResumeJob(key!);
        Log?.LogInformation("Job '{JobKey}' resumed", key);
        return new SchedulerActionResponse { Success = true, Message = $"Job '{key}' resumed" };
    }

    /// <summary>
    /// Fires a job immediately (out of schedule) — the "run it now" operator button. Requires the
    /// <c>operator</c> role and is audited.
    /// </summary>
    [HttpPost("/fire-job")]
    [RequiresRole(TsakRoles.Operator)]
    [AuditAdminAction(ActionName = "FireJob", TargetParam = "jobKeyStr")]
    public async Task<object> FireJob([FromQuery("key")] string jobKeyStr, [FromQuery("context")] string? context = null)
    {
        var (scheduler, key, error) = await FindJob(jobKeyStr, context);
        if (error is not null)
            return error;

        await scheduler!.TriggerJob(key!);
        Log?.LogInformation("Job '{JobKey}' fired manually", key);
        return new SchedulerActionResponse { Success = true, Message = $"Job '{key}' fired" };
    }

    /// <summary>
    /// The scheduler of every route context of the node that has one — this management context first — each once.
    /// A context gets its scheduler with its first Quartz route; one without such a route has none.
    /// </summary>
    private List<(string Context, IScheduler Scheduler)> Schedulers()
    {
        var result = new List<(string, IScheduler)>();
        var seen = new HashSet<IScheduler>(ReferenceEqualityComparer.Instance);
        if (Context.GetService<IScheduler>() is { } own && seen.Add(own))
            result.Add((Context.ContextId, own));
        if (Context.GetService<ITsakContextManager>() is { } manager)
        {
            foreach (var (name, context) in manager.GetAllContexts())
            {
                if (context.GetService<IScheduler>() is { } scheduler && seen.Add(scheduler))
                    result.Add((name, scheduler));
            }
        }
        return result;
    }

    /// <summary>The live schedulers an action targets: the one of <paramref name="context"/>, or all of them.</summary>
    private (List<(string Context, IScheduler Scheduler)> Targets, SchedulerActionResponse? Error) Targets(string? context)
    {
        var live = Schedulers().Where(s => !s.Scheduler.IsShutdown).ToList();
        if (context is not null)
        {
            var match = live.Where(s => s.Context == context).ToList();
            return match.Count == 0
                ? ([], Fail($"Scheduler of context '{context}' not available"))
                : (match, null);
        }
        return live.Count == 0 ? ([], Fail("Scheduler not available")) : (live, null);
    }

    /// <summary>
    /// The scheduler holding <paramref name="jobKeyStr"/>: in <paramref name="context"/>, or — without it — in the one
    /// context that has the key; a key in several contexts is refused with their names, never guessed.
    /// </summary>
    private async Task<(IScheduler? Scheduler, JobKey? Key, SchedulerActionResponse? Error)> FindJob(string jobKeyStr, string? context)
    {
        var (targets, error) = Targets(context);
        if (error is not null)
            return (null, null, error);

        var found = new List<(string Context, IScheduler Scheduler, JobKey Key)>();
        foreach (var (name, scheduler) in targets)
        {
            if (await FindJobKey(scheduler, jobKeyStr) is { } key)
                found.Add((name, scheduler, key));
        }

        return found.Count switch
        {
            0 => (null, null, Fail($"Job '{jobKeyStr}' not found")),
            1 => (found[0].Scheduler, found[0].Key, null),
            _ => (null, null, Fail(
                $"Job '{jobKeyStr}' exists in contexts {string.Join(", ", found.Select(f => $"'{f.Context}'"))}; pass the context."))
        };
    }

    private static async Task<SchedulerContextStatus> StatusOf(string context, IScheduler scheduler)
    {
        if (scheduler.IsShutdown)
        {
            return new SchedulerContextStatus
            {
                Context = context, SchedulerName = scheduler.SchedulerName, SchedulerInstanceId = scheduler.SchedulerInstanceId,
                IsStarted = false, InStandbyMode = false, IsShutdown = true, TotalJobs = 0, RunningJobs = 0, Status = "Shutdown"
            };
        }

        var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        var running = await scheduler.GetCurrentlyExecutingJobs();
        return new SchedulerContextStatus
        {
            Context = context,
            SchedulerName = scheduler.SchedulerName,
            SchedulerInstanceId = scheduler.SchedulerInstanceId,
            IsStarted = scheduler.IsStarted,
            InStandbyMode = scheduler.InStandbyMode,
            IsShutdown = false,
            TotalJobs = jobKeys.Count,
            RunningJobs = running.Count,
            Status = scheduler.InStandbyMode ? "Standby" : scheduler.IsStarted ? "Running" : "Stopped"
        };
    }

    /// <summary>Find a Quartz JobKey by its ToString() representation among all registered jobs.</summary>
    private static async Task<JobKey?> FindJobKey(IScheduler scheduler, string jobKeyStr)
    {
        var allKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        return allKeys.FirstOrDefault(k => k.ToString() == jobKeyStr);
    }

    private static string CommonPrefix(IEnumerable<string> names)
    {
        var list = names.ToList();
        var prefix = list[0];
        foreach (var name in list.Skip(1))
        {
            var i = 0;
            while (i < prefix.Length && i < name.Length && prefix[i] == name[i]) i++;
            prefix = prefix[..i];
        }
        return prefix.TrimEnd('-');
    }

    private static string Say(string context, string message, int targets) => targets == 1 ? message : $"{context}: {message}";

    private static SchedulerActionResponse Fail(string message) => new() { Success = false, Message = message };
}
