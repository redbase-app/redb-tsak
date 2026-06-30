using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using redb.Tsak.Client;
using redb.Tsak.Contracts;
using redb.Tsak.CLI.Config;
using redb.Tsak.CLI.Rendering;

namespace redb.Tsak.CLI;

/// <summary>
/// Entry point for the tsak CLI tool.
/// Noun-verb style: tsak context list, tsak scheduler status, etc.
/// </summary>
public static class Program
{
    // ── Global options ──────────────────────────────────────────────

    private static readonly Option<string?> ProfileOption = new(
        aliases: ["--profile", "-p"],
        description: "Connection profile name");

    private static readonly Option<string?> UrlOption = new(
        aliases: ["--url", "-u"],
        description: "Tsak runtime URL (overrides profile)");

    private static readonly Option<string?> KeyOption = new(
        aliases: ["--key", "-k"],
        description: "API key (overrides profile)");

    private static readonly Option<OutputFormat> OutputOption = new(
        aliases: ["--output", "-o"],
        getDefaultValue: () => OutputFormat.Table,
        description: "Output format: table | json | quiet");

    private static readonly Option<bool> NoColorOption = new(
        "--no-color",
        description: "Disable colored output");

    private static readonly Option<bool> YesOption = new(
        aliases: ["--yes", "-y"],
        description: "Skip confirmation prompts");

    private static readonly Option<bool> WatchOption = new(
        "--watch",
        description: "Poll repeatedly (Ctrl+C to stop)");

    private static readonly Option<int> IntervalOption = new(
        "--interval",
        getDefaultValue: () => 5,
        description: "Polling interval in seconds (used with --watch)");

    private static readonly Option<int?> TimeoutOption = new(
        "--timeout",
        description: "HTTP request timeout in seconds (default: 5). Useful for slow operations like diagnostics dump, force-stop, module install.");

    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("tsak — CLI for redb.Tsak runtime management");

        root.AddGlobalOption(ProfileOption);
        root.AddGlobalOption(UrlOption);
        root.AddGlobalOption(KeyOption);
        root.AddGlobalOption(OutputOption);
        root.AddGlobalOption(NoColorOption);
        root.AddGlobalOption(YesOption);
        root.AddGlobalOption(WatchOption);
        root.AddGlobalOption(IntervalOption);
        root.AddGlobalOption(TimeoutOption);

        // ── Register command groups ─────────────────────────────────
        root.AddCommand(BuildSystemCommand());
        root.AddCommand(BuildContextCommand());
        root.AddCommand(BuildModuleCommand());
        root.AddCommand(BuildSchedulerCommand());
        root.AddCommand(BuildClusterCommand());
        root.AddCommand(BuildLogCommand());
        root.AddCommand(BuildAuthCommand());
        root.AddCommand(BuildProfileCommand());
        root.AddCommand(Commands.RouteCommands.Create());
        root.AddCommand(Commands.WatchdogCommands.Create());
        root.AddCommand(Commands.DiagnosticsCommands.Create());

        var parser = new CommandLineBuilder(root)
            .UseDefaults()
            .UseExceptionHandler(HandleException)
            .Build();

        return await parser.InvokeAsync(args);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves connection settings and creates an API client from the invocation context.
    /// </summary>
    internal static TsakApiClient CreateClient(InvocationContext ctx)
    {
        var profile = new ProfileManager().Resolve(
            ctx.ParseResult.GetValueForOption(ProfileOption),
            ctx.ParseResult.GetValueForOption(UrlOption),
            ctx.ParseResult.GetValueForOption(KeyOption));

        if (profile is null)
        {
            Console.Error.WriteLine("No connection configured. Use --url, set TSAK_URL, or create a profile.");
            ctx.ExitCode = 1;
            throw new OperationCanceledException();
        }

        var timeoutSeconds = ctx.ParseResult.GetValueForOption(TimeoutOption);
        var timeout = timeoutSeconds is > 0 ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;
        return new TsakApiClient(profile.Url, profile.ApiKey, timeout);
    }

    /// <summary>
    /// Creates the appropriate output renderer from the invocation context.
    /// </summary>
    internal static IOutputRenderer CreateRenderer(InvocationContext ctx)
    {
        var format = ctx.ParseResult.GetValueForOption(OutputOption);
        var noColor = ctx.ParseResult.GetValueForOption(NoColorOption);
        return RendererFactory.Create(format, noColor);
    }

    /// <summary>
    /// Asks for confirmation before a destructive action. Aborts if declined.
    /// Skipped when --yes is set or output format is not Table (scripts).
    /// </summary>
    internal static void ConfirmOrAbort(InvocationContext ctx, string prompt)
    {
        var yes = ctx.ParseResult.GetValueForOption(YesOption);
        var format = ctx.ParseResult.GetValueForOption(OutputOption);
        if (yes || format != OutputFormat.Table)
            return;

        Console.Write($"{prompt} [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Aborted.");
            throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Runs an async action once or in a polling loop if --watch is set.
    /// </summary>
    internal static async Task RunOrWatch(InvocationContext ctx, Func<CancellationToken, Task> action)
    {
        var watch = ctx.ParseResult.GetValueForOption(WatchOption);
        var interval = ctx.ParseResult.GetValueForOption(IntervalOption);
        var ct = ctx.GetCancellationToken();

        if (!watch)
        {
            await action(ct);
            return;
        }

        Console.WriteLine($"Every {interval}s — Ctrl+C to stop");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Console.Write($"\x1b[2K[{DateTime.Now:HH:mm:ss}] ");
                await action(ct);
                Console.WriteLine();
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Global exception handler for the CLI.
    /// </summary>
    private static void HandleException(Exception ex, InvocationContext ctx)
    {
        if (ex is OperationCanceledException)
            return;

        var renderer = CreateRenderer(ctx);
        if (ex is ApiException apiEx)
        {
            renderer.Error($"[{apiEx.StatusCode}] {apiEx.Message}");
            ctx.ExitCode = 1;
        }
        else
        {
            renderer.Error(ex.Message);
            ctx.ExitCode = 2;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SYSTEM
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildSystemCommand()
    {
        var cmd = new Command("system", "System health, metrics, and info");

        // tsak system health [--watch] [--interval N]
        var healthCmd = new Command("health", "Show system health status");
        healthCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            await RunOrWatch(ctx, async ct =>
            {
                var health = await client.GetHealthAsync(ct);
                r.RenderDetail(
                    ("Status", health.Status.ToString()),
                    ("Description", health.Description),
                    ("Timestamp", health.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")));
                if (health.Checks is { Count: > 0 })
                {
                    r.RenderTable(health.Checks,
                        ("Check", kv => kv.Key),
                        ("Status", kv => kv.Value.ToString()));
                }
            });
        });

        // tsak system metrics [--watch] [--interval N]
        var metricsCmd = new Command("metrics", "Show system metrics");
        metricsCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            await RunOrWatch(ctx, async ct =>
            {
                var m = await client.GetMetricsAsync(ct);
                if (!m.Available || m.Latest is null)
                {
                    r.Error("Metrics not available.");
                    return;
                }
                r.RenderDetail(
                    ("CPU Process", $"{m.Latest.Cpu.ProcessUsage:F1}%"),
                    ("CPU System", $"{m.Latest.Cpu.SystemUsage:F1}%"),
                    ("Memory (MB)", $"{m.Latest.Memory.WorkingSetMB:F1}"),
                    ("Active Threads", m.Latest.Threading.ActiveThreads.ToString()),
                    ("GC Gen0/1/2", $"{m.Latest.GarbageCollector.Gen0Collections}/{m.Latest.GarbageCollector.Gen1Collections}/{m.Latest.GarbageCollector.Gen2Collections}"),
                    ("GC Total (MB)", $"{m.Latest.GarbageCollector.TotalMemoryMB:F1}"),
                    ("Stored Points", m.StoredPoints?.ToString() ?? ""),
                    ("Timestamp", m.Latest.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")));
            });
        });

        // tsak system info
        var infoCmd = new Command("info", "Show system information");
        infoCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var info = await client.GetInfoAsync(ctx.GetCancellationToken());
            r.RenderDetail(
                ("Version", info.Version),
                ("Started", info.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                ("Uptime", info.Uptime),
                ("Contexts", info.ContextCount.ToString()),
                ("Modules", info.ModuleCount.ToString()),
                ("Machine", info.MachineName),
                ("CPUs", info.ProcessorCount.ToString()),
                ("Memory (MB)", $"{info.WorkingSetMb:F1}"));
        });

        cmd.AddCommand(healthCmd);
        cmd.AddCommand(metricsCmd);
        cmd.AddCommand(infoCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // CONTEXT
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildContextCommand()
    {
        var cmd = new Command("context", "Manage route contexts");

        // tsak context list
        var listCmd = new Command("list", "List all contexts");
        listCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var contexts = await client.ListContextsAsync(ctx.GetCancellationToken());
            r.RenderTable(contexts,
                ("Name", c => c.Name),
                ("Status", c => c.Status),
                ("Endpoints", c => c.EndpointCount.ToString()));
        });

        // tsak context get <name>
        var nameArg = new Argument<string>("name", "Context name");
        var getCmd = new Command("get", "Get context details") { nameArg };
        getCmd.SetHandler(async (ctx) =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var detail = await client.GetContextAsync(name, ctx.GetCancellationToken());
            r.RenderDetail(
                ("Name", detail.Name),
                ("Status", detail.Status),
                ("Endpoints", detail.EndpointCount.ToString()),
                ("AutoStart", detail.AutoStart ? "Yes" : "No"));
        });

        // tsak context start <name>
        var startArg = new Argument<string>("name", "Context name");
        var startCmd = new Command("start", "Start a context") { startArg };
        startCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(startArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StartContextAsync(name, ctx.GetCancellationToken());
            r.Success($"Context '{result.Name}' → {result.Status}");
        });

        // tsak context stop <name>
        var stopArg = new Argument<string>("name", "Context name");
        var stopCmd = new Command("stop", "Stop a context") { stopArg };
        stopCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(stopArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StopContextAsync(name, ctx.GetCancellationToken());
            r.Success($"Context '{result.Name}' → {result.Status}");
        });

        // tsak context restart <name>
        var restartArg = new Argument<string>("name", "Context name");
        var restartCmd = new Command("restart", "Restart a context") { restartArg };
        restartCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(restartArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RestartContextAsync(name, ctx.GetCancellationToken());
            r.Success($"Context '{result.Name}' → {result.Status}");
        });

        // tsak context remove <name> [--yes]
        var removeArg = new Argument<string>("name", "Context name");
        var removeCmd = new Command("remove", "Remove a context") { removeArg };
        removeCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(removeArg);
            ConfirmOrAbort(ctx, $"Remove context '{name}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RemoveContextAsync(name, ctx.GetCancellationToken());
            r.Success($"Context '{result.Name}' removed.");
        });

        cmd.AddCommand(listCmd);
        cmd.AddCommand(getCmd);
        cmd.AddCommand(startCmd);
        cmd.AddCommand(stopCmd);
        cmd.AddCommand(restartCmd);
        cmd.AddCommand(removeCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // MODULE
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildModuleCommand()
    {
        var cmd = new Command("module", "Manage runtime modules");

        // tsak module list
        var listCmd = new Command("list", "List all modules");
        listCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var modules = await client.ListModulesAsync(ctx.GetCancellationToken());
            r.RenderTable(modules,
                ("Name", m => m.ModuleName),
                ("Version", m => m.Version ?? "—"),
                ("Status", m => m.Status),
                ("Dependencies", m => m.Dependencies.Length > 0 ? string.Join(", ", m.Dependencies) : "—"));
        });

        // tsak module get <name>
        var nameArg = new Argument<string>("name", "Module name");
        var getCmd = new Command("get", "Get module details") { nameArg };
        getCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var mod = await client.GetModuleAsync(name, ctx.GetCancellationToken());
            r.RenderDetail(
                ("Name", mod.ModuleName),
                ("Version", mod.Version ?? "—"),
                ("Description", mod.Description ?? "—"),
                ("Status", mod.Status),
                ("Can Initialize", mod.CanInitialize ? "Yes" : "No"),
                ("Dependencies", mod.Dependencies.Length > 0 ? string.Join(", ", mod.Dependencies) : "—"));
        });

        // tsak module remove <name> [--yes]
        var removeArg = new Argument<string>("name", "Module name");
        var removeCmd = new Command("remove", "Remove a module") { removeArg };
        removeCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(removeArg);
            ConfirmOrAbort(ctx, $"Remove module '{name}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RemoveModuleAsync(name, ctx.GetCancellationToken());
            r.Success($"Module '{result.ModuleName}' removed.");
        });

        cmd.AddCommand(listCmd);
        cmd.AddCommand(getCmd);
        cmd.AddCommand(removeCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // SCHEDULER
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildSchedulerCommand()
    {
        var cmd = new Command("scheduler", "Manage Quartz scheduler");

        // tsak scheduler status [--watch] [--interval N]
        var statusCmd = new Command("status", "Show scheduler status");
        statusCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            await RunOrWatch(ctx, async ct =>
            {
                var s = await client.GetSchedulerStatusAsync(ct);
                r.RenderDetail(
                    ("Status", s.Status),
                    ("Name", s.SchedulerName),
                    ("Instance", s.SchedulerInstanceId),
                    ("Started", s.IsStarted ? "Yes" : "No"),
                    ("Standby", s.InStandbyMode ? "Yes" : "No"),
                    ("Shutdown", s.IsShutdown ? "Yes" : "No"),
                    ("Total Jobs", s.TotalJobs.ToString()),
                    ("Running Jobs", s.RunningJobs.ToString()));
            });
        });

        // tsak scheduler jobs
        var jobsCmd = new Command("jobs", "List scheduled jobs");
        jobsCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ListScheduledJobsAsync(ctx.GetCancellationToken());
            r.RenderTable(result.Jobs,
                ("Job", j => j.JobName),
                ("Group", j => j.JobGroup),
                ("Trigger", j => j.TriggerState),
                ("Cron", j => j.CronExpression ?? "—"),
                ("Next Fire", j => j.NextFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"));
        });

        // tsak scheduler running [--watch] [--interval N]
        var runningCmd = new Command("running", "List currently running jobs");
        runningCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            await RunOrWatch(ctx, async ct =>
            {
                var result = await client.ListRunningJobsAsync(ct);
                r.RenderTable(result.Jobs,
                    ("Job", j => j.JobName),
                    ("Group", j => j.JobGroup),
                    ("Fire Time", j => j.FireTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("Runtime (ms)", j => j.RunTimeMs.ToString()));
            });
        });

        // tsak scheduler start
        var startCmd = new Command("start", "Start the scheduler");
        startCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StartSchedulerAsync(ctx.GetCancellationToken());
            r.Success(result.Message);
        });

        // tsak scheduler standby
        var standbyCmd = new Command("standby", "Put scheduler in standby mode");
        standbyCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StandbySchedulerAsync(ctx.GetCancellationToken());
            r.Success(result.Message);
        });

        // tsak scheduler pause <group> <name>
        var pauseKeyArg = new Argument<string>("key", "Job key (group.name)");
        var pauseCmd = new Command("pause", "Pause a specific job") { pauseKeyArg };
        pauseCmd.SetHandler(async ctx =>
        {
            var key = ctx.ParseResult.GetValueForArgument(pauseKeyArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.PauseJobAsync(key, ctx.GetCancellationToken());
            r.Success(result.Message);
        });

        // tsak scheduler resume <key>
        var resumeKeyArg = new Argument<string>("key", "Job key (group.name)");
        var resumeCmd = new Command("resume", "Resume a specific job") { resumeKeyArg };
        resumeCmd.SetHandler(async ctx =>
        {
            var key = ctx.ParseResult.GetValueForArgument(resumeKeyArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ResumeJobAsync(key, ctx.GetCancellationToken());
            r.Success(result.Message);
        });

        cmd.AddCommand(statusCmd);
        cmd.AddCommand(jobsCmd);
        cmd.AddCommand(runningCmd);
        cmd.AddCommand(startCmd);
        cmd.AddCommand(standbyCmd);
        cmd.AddCommand(pauseCmd);
        cmd.AddCommand(resumeCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // CLUSTER
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildClusterCommand()
    {
        var cmd = new Command("cluster", "Manage cluster topology");

        // tsak cluster status
        var statusCmd = new Command("status", "Show cluster status");
        statusCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var s = await client.GetClusterStatusAsync(ctx.GetCancellationToken());
            r.RenderDetail(
                ("Enabled", s.Enabled ? "Yes" : "No"),
                ("Node ID", s.NodeId ?? "—"),
                ("Leader", s.IsLeader.HasValue ? (s.IsLeader.Value ? "Yes" : "No") : "—"),
                ("Epoch", s.CurrentEpoch?.ToString() ?? "—"));
        });

        // tsak cluster nodes
        var nodesCmd = new Command("nodes", "List cluster nodes");
        nodesCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ListClusterNodesAsync(ctx.GetCancellationToken());
            if (!result.Enabled)
            {
                r.Error("Clustering is not enabled.");
                ctx.ExitCode = 1;
                return;
            }
            r.RenderTable(result.Nodes,
                ("Node ID", n => n.NodeId),
                ("Hostname", n => n.Hostname),
                ("Status", n => n.Status.ToString()),
                ("Started", n => n.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                ("Heartbeat", n => n.LastHeartbeat.ToString("yyyy-MM-dd HH:mm:ss")));
        });

        // tsak cluster rebalance [--yes]
        var rebalanceCmd = new Command("rebalance", "Trigger cluster rebalance");
        rebalanceCmd.SetHandler(async ctx =>
        {
            ConfirmOrAbort(ctx, "Trigger cluster rebalance?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RebalanceClusterAsync(ctx.GetCancellationToken());
            r.Success($"Rebalanced. Epoch: {result.CurrentEpoch}");
        });

        cmd.AddCommand(statusCmd);
        cmd.AddCommand(nodesCmd);
        cmd.AddCommand(rebalanceCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // LOG
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildLogCommand()
    {
        var cmd = new Command("log", "View runtime logs");

        var limitOption = new Option<int?>("--limit", "Maximum number of entries");
        var levelOption = new Option<string?>("--level", "Minimum log level (Trace, Debug, Information, Warning, Error, Critical)");

        // tsak log get [--limit N] [--level LEVEL]
        var getCmd = new Command("get", "Get buffered log entries") { limitOption, levelOption };
        getCmd.SetHandler(async ctx =>
        {
            var limit = ctx.ParseResult.GetValueForOption(limitOption);
            var level = ctx.ParseResult.GetValueForOption(levelOption);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var logs = await client.GetLogsAsync(limit: limit, level: level, ct: ctx.GetCancellationToken());
            if (!logs.Available)
            {
                r.Error("Log buffering is not available.");
                ctx.ExitCode = 1;
                return;
            }
            r.RenderTable(logs.Entries ?? [],
                ("Time", e => e.Timestamp.ToString("HH:mm:ss.fff")),
                ("Level", e => e.Level),
                ("Source", e => e.Source ?? "—"),
                ("Message", e => e.Message));
        });

        cmd.AddCommand(getCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildAuthCommand()
    {
        var cmd = new Command("auth", "Manage API keys");

        // tsak auth list
        var listCmd = new Command("list", "List all API keys");
        listCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var keys = await client.ListApiKeysAsync(ctx.GetCancellationToken());
            r.RenderTable(keys,
                ("ID", k => k.Id.ToString()),
                ("Name", k => k.Name),
                ("Roles", k => string.Join(", ", k.Roles)),
                ("Valid", k => k.IsValid ? "Yes" : "No"),
                ("Created", k => k.CreatedAt.ToString("yyyy-MM-dd")));
        });

        // tsak auth create --name <name> [--roles r1,r2] [--user-id id] [--expires-at dt]
        var createNameOption = new Option<string>("--name", "Key name") { IsRequired = true };
        var createRolesOption = new Option<string[]?>("--roles", "Comma-separated roles");
        var createUserIdOption = new Option<string?>("--user-id", "Associated user ID");
        var createExpiresOption = new Option<DateTime?>("--expires-at", "Expiration date/time");
        var createCmd = new Command("create", "Create a new API key")
        {
            createNameOption, createRolesOption, createUserIdOption, createExpiresOption
        };
        createCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForOption(createNameOption)!;
            var roles = ctx.ParseResult.GetValueForOption(createRolesOption);
            var userId = ctx.ParseResult.GetValueForOption(createUserIdOption);
            var expiresAt = ctx.ParseResult.GetValueForOption(createExpiresOption);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.CreateApiKeyAsync(name, roles, userId, expiresAt, ctx.GetCancellationToken());
            r.RenderDetail(
                ("ID", result.Id.ToString()),
                ("Key", result.Key),
                ("Name", result.Name),
                ("Roles", string.Join(", ", result.Roles)),
                ("Created", result.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            r.Success(result.Message ?? "API key created. Save the key — it won't be shown again.");
        });

        // tsak auth revoke <id> [--yes]
        var revokeArg = new Argument<string>("id", "API key ID");
        var revokeCmd = new Command("revoke", "Revoke an API key") { revokeArg };
        revokeCmd.SetHandler(async ctx =>
        {
            var id = ctx.ParseResult.GetValueForArgument(revokeArg);
            ConfirmOrAbort(ctx, $"Revoke API key '{id}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RevokeApiKeyAsync(id, ctx.GetCancellationToken());
            r.Success($"Key {result.Id} revoked.");
        });

        cmd.AddCommand(listCmd);
        cmd.AddCommand(createCmd);
        cmd.AddCommand(revokeCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // PROFILE (local — no API calls)
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildProfileCommand()
    {
        var pm = new ProfileManager();
        var cmd = new Command("profile", "Manage connection profiles");

        // tsak profile list
        var listCmd = new Command("list", "List saved profiles");
        listCmd.SetHandler(ctx =>
        {
            var r = CreateRenderer(ctx);
            var profiles = pm.List();
            var activeName = pm.GetActiveName();
            r.RenderTable(profiles,
                ("Name", p => p.Name + (p.Name == activeName ? " *" : "")),
                ("URL", p => p.Url),
                ("Key", p => p.ApiKey is not null ? "••••" : "—"));
        });

        // tsak profile add --name <name> --url <url> [--key <key>]
        var addNameOpt = new Option<string>("--name", "Profile name") { IsRequired = true };
        var addUrlOpt = new Option<string>("--url", "Runtime URL") { IsRequired = true };
        var addKeyOpt = new Option<string?>("--key", "API key");
        var addCmd = new Command("add", "Add a new profile") { addNameOpt, addUrlOpt, addKeyOpt };
        addCmd.SetHandler(ctx =>
        {
            var r = CreateRenderer(ctx);
            var name = ctx.ParseResult.GetValueForOption(addNameOpt)!;
            var url = ctx.ParseResult.GetValueForOption(addUrlOpt)!;
            var key = ctx.ParseResult.GetValueForOption(addKeyOpt);
            pm.Save(new ConnectionProfile { Name = name, Url = url, ApiKey = key });
            r.Success($"Profile '{name}' saved.");
        });

        // tsak profile remove <name>
        var removeArg = new Argument<string>("name", "Profile name");
        var removeCmd = new Command("remove", "Remove a profile") { removeArg };
        removeCmd.SetHandler(ctx =>
        {
            var r = CreateRenderer(ctx);
            var name = ctx.ParseResult.GetValueForArgument(removeArg);
            if (pm.Delete(name))
                r.Success($"Profile '{name}' removed.");
            else
                r.Error($"Profile '{name}' not found.");
        });

        // tsak profile use <name>
        var useArg = new Argument<string>("name", "Profile name to activate");
        var useCmd = new Command("use", "Set the active profile") { useArg };
        useCmd.SetHandler(ctx =>
        {
            var r = CreateRenderer(ctx);
            var name = ctx.ParseResult.GetValueForArgument(useArg);
            try
            {
                pm.SetActive(name);
                r.Success($"Active profile: {name}");
            }
            catch (FileNotFoundException)
            {
                r.Error($"Profile '{name}' not found.");
                ctx.ExitCode = 1;
            }
        });

        cmd.AddCommand(listCmd);
        cmd.AddCommand(addCmd);
        cmd.AddCommand(removeCmd);
        cmd.AddCommand(useCmd);
        return cmd;
    }
}
