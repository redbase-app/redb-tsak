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
}

public sealed record SchedulerActionResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
}
