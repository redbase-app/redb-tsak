namespace redb.Tsak.Contracts;

public sealed record SchedulerStatusResponse
{
    public required bool IsStarted { get; init; }
    public required bool InStandbyMode { get; init; }
    public required bool IsShutdown { get; init; }
    public required string SchedulerName { get; init; }
    public required string SchedulerInstanceId { get; init; }
    public required int TotalJobs { get; init; }
    public required int RunningJobs { get; init; }
    public required string Status { get; init; }
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// One entry per route context that has a Quartz scheduler — each context owns one. The fields above summarize
    /// them for the node: the counts are sums, <see cref="IsStarted"/> means all are started,
    /// <see cref="InStandbyMode"/> that at least one is in standby, <see cref="SchedulerName"/> is the common prefix.
    /// </summary>
    public SchedulerContextStatus[] Schedulers { get; init; } = [];
}

/// <summary>The Quartz scheduler of one route context.</summary>
public sealed record SchedulerContextStatus
{
    public required string Context { get; init; }
    public required string SchedulerName { get; init; }
    public required string SchedulerInstanceId { get; init; }
    public required bool IsStarted { get; init; }
    public required bool InStandbyMode { get; init; }
    public required bool IsShutdown { get; init; }
    public required int TotalJobs { get; init; }
    public required int RunningJobs { get; init; }
    public required string Status { get; init; }
}

public sealed record ScheduledJobsResponse
{
    public required int TotalScheduledJobs { get; init; }
    public required ScheduledJobInfo[] Jobs { get; init; }
}

public sealed record ScheduledJobInfo
{
    public required string JobKey { get; init; }
    public required string JobName { get; init; }
    public required string JobGroup { get; init; }
    public required string JobType { get; init; }
    public required string TriggerKey { get; init; }
    public required string TriggerState { get; init; }
    public string? CronExpression { get; init; }
    public DateTime? NextFireTime { get; init; }
    public DateTime? PreviousFireTime { get; init; }
    public required int Priority { get; init; }

    /// <summary>The route context whose scheduler holds the job; a job key is unique within it only.</summary>
    public string? Context { get; init; }
}

public sealed record RunningJobsResponse
{
    public required int TotalRunningJobs { get; init; }
    public required RunningJobInfo[] Jobs { get; init; }
}

public sealed record RunningJobInfo
{
    public required string JobKey { get; init; }
    public required string JobName { get; init; }
    public required string JobGroup { get; init; }
    public required string JobType { get; init; }
    public required string TriggerKey { get; init; }
    public required DateTime FireTime { get; init; }
    public required long RunTimeMs { get; init; }
    public required int RefireCount { get; init; }

    /// <summary>The route context whose scheduler runs the job.</summary>
    public string? Context { get; init; }
}

public sealed record SchedulerActionResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
}
