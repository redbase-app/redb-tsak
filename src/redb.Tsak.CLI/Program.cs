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
        root.AddCommand(BuildAuditCommand());
        root.AddCommand(BuildDlqCommand());
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

        var configCmd = new Command("config", "Show effective (merged, redacted) configuration");
        configCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var cfg = await client.GetConfigAsync(ctx.GetCancellationToken());
            if (!cfg.Available)
            {
                r.Error("Configuration is not available.");
                ctx.ExitCode = 1;
                return;
            }
            r.RenderTable(cfg.Values,
                ("Key", kv => kv.Key),
                ("Value", kv => kv.Value ?? "—"));
        });

        cmd.AddCommand(healthCmd);
        cmd.AddCommand(metricsCmd);
        cmd.AddCommand(infoCmd);
        cmd.AddCommand(configCmd);
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

        // tsak module keygen --out <prefix>  → writes <prefix>.key (private) + <prefix>.pub (public)
        var keygenOut = new Option<string>("--out", () => "tsak-module", "Output file prefix");
        var keygenCmd = new Command("keygen", "Generate an ECDSA signing key pair for module packages")
        { keygenOut };
        keygenCmd.SetHandler(ctx =>
        {
            var prefix = ctx.ParseResult.GetValueForOption(keygenOut)!;
            var r = CreateRenderer(ctx);
            using var ecdsa = System.Security.Cryptography.ECDsa.Create(
                System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            File.WriteAllText(prefix + ".key", ecdsa.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(prefix + ".pub", ecdsa.ExportSubjectPublicKeyInfoPem());
            r.Success($"Wrote {prefix}.key (PRIVATE — keep secret, use to sign) and {prefix}.pub " +
                      $"(public — configure on the node as Tsak:Modules:Signature:PublicKeyPath).");
        });

        // tsak module sign <file.tpkg> --key <private.pem>  → writes <file.tpkg.sig> (base64)
        var signFileArg = new Argument<string>("file", "Path to the .tpkg to sign");
        var signKey = new Option<string>("--key", "Path to the PEM private key") { IsRequired = true };
        var signCmd = new Command("sign", "Sign a .tpkg with a private key (produces .tpkg.sig)")
        { signFileArg, signKey };
        signCmd.SetHandler(ctx =>
        {
            var file = ctx.ParseResult.GetValueForArgument(signFileArg);
            var keyPath = ctx.ParseResult.GetValueForOption(signKey)!;
            var r = CreateRenderer(ctx);
            var data = File.ReadAllBytes(file);
            var pem = File.ReadAllText(keyPath);

            byte[] sig;
            try
            {
                using var ecdsa = System.Security.Cryptography.ECDsa.Create();
                ecdsa.ImportFromPem(pem);
                sig = ecdsa.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256);
            }
            catch
            {
                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportFromPem(pem);
                sig = rsa.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            }

            var sigPath = file + ".sig";
            File.WriteAllText(sigPath, Convert.ToBase64String(sig));
            r.Success($"Wrote {sigPath}. Deploy the .tpkg and .tpkg.sig together.");
        });

        // tsak module deploy <file.tpkg> [--sig <file.tpkg.sig>]  → upload to the node
        var deployFileArg = new Argument<string>("file", "Path to the .tpkg to deploy");
        var deploySig = new Option<string?>("--sig", "Path to the .tpkg.sig (defaults to <file>.sig if present)");
        var deployCmd = new Command("deploy", "Upload a .tpkg to the node for hot-deploy")
        { deployFileArg, deploySig };
        deployCmd.SetHandler(async ctx =>
        {
            var file = ctx.ParseResult.GetValueForArgument(deployFileArg);
            var sigPath = ctx.ParseResult.GetValueForOption(deploySig) ?? (File.Exists(file + ".sig") ? file + ".sig" : null);
            var r = CreateRenderer(ctx);

            var bytes = await File.ReadAllBytesAsync(file, ctx.GetCancellationToken());
            string? sigB64 = null;
            if (sigPath is not null)
            {
                var raw = await File.ReadAllTextAsync(sigPath, ctx.GetCancellationToken());
                sigB64 = raw.Trim();
            }

            using var client = CreateClient(ctx);
            var result = await client.UploadModuleAsync(bytes, sigB64, ctx.GetCancellationToken());
            if (result.Success) r.Success($"{result.Message} (module: {result.ModuleName ?? "?"}, v{result.Version ?? "?"})");
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak module validate <file.tpkg> [--sig <file.tpkg.sig>]  → dry-run, installs nothing
        var validateFileArg = new Argument<string>("file", "Path to the .tpkg to validate");
        var validateSig = new Option<string?>("--sig", "Path to the .tpkg.sig (defaults to <file>.sig if present)");
        var validateCmd = new Command("validate", "Validate a .tpkg without installing it") { validateFileArg, validateSig };
        validateCmd.SetHandler(async ctx =>
        {
            var file = ctx.ParseResult.GetValueForArgument(validateFileArg);
            var sigPath = ctx.ParseResult.GetValueForOption(validateSig) ?? (File.Exists(file + ".sig") ? file + ".sig" : null);
            var r = CreateRenderer(ctx);

            var bytes = await File.ReadAllBytesAsync(file, ctx.GetCancellationToken());
            string? sigB64 = sigPath is not null ? (await File.ReadAllTextAsync(sigPath, ctx.GetCancellationToken())).Trim() : null;

            using var client = CreateClient(ctx);
            var result = await client.ValidateModuleAsync(bytes, sigB64, ctx.GetCancellationToken());
            if (result.Success) r.Success($"{result.Message} (module: {result.ModuleName ?? "?"}, v{result.Version ?? "?"})");
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak module rollback <name>
        var rollbackArg = new Argument<string>("name", "Module name to roll back");
        var rollbackCmd = new Command("rollback", "Roll a module back to its previous version") { rollbackArg };
        rollbackCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(rollbackArg);
            ConfirmOrAbort(ctx, $"Roll module '{name}' back to the previous version?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RollbackModuleAsync(name, ctx.GetCancellationToken());
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        cmd.AddCommand(listCmd);
        cmd.AddCommand(getCmd);
        cmd.AddCommand(removeCmd);
        cmd.AddCommand(keygenCmd);
        cmd.AddCommand(signCmd);
        cmd.AddCommand(deployCmd);
        cmd.AddCommand(validateCmd);
        cmd.AddCommand(rollbackCmd);
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

        // tsak scheduler fire <key>
        var fireKeyArg = new Argument<string>("key", "Job key (group.name)");
        var fireCmd = new Command("fire", "Fire a job immediately (out of schedule)") { fireKeyArg };
        fireCmd.SetHandler(async ctx =>
        {
            var key = ctx.ParseResult.GetValueForArgument(fireKeyArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.FireJobAsync(key, ctx.GetCancellationToken());
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        cmd.AddCommand(statusCmd);
        cmd.AddCommand(jobsCmd);
        cmd.AddCommand(runningCmd);
        cmd.AddCommand(startCmd);
        cmd.AddCommand(standbyCmd);
        cmd.AddCommand(pauseCmd);
        cmd.AddCommand(resumeCmd);
        cmd.AddCommand(fireCmd);
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
                ("Cordoned", n => n.Cordoned ? "yes" : "no"),
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

        // tsak cluster cordon <nodeId>
        var cordonArg = new Argument<string>("nodeId", "Node id");
        var cordonCmd = new Command("cordon", "Cordon a node (no new work; drain to peers)") { cordonArg };
        cordonCmd.SetHandler(async ctx =>
        {
            var id = ctx.ParseResult.GetValueForArgument(cordonArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.CordonNodeAsync(id, ctx.GetCancellationToken());
            r.Success($"Node '{result.NodeId}' cordoned — it will drain and take no new work.");
        });

        // tsak cluster uncordon <nodeId>
        var uncordonArg = new Argument<string>("nodeId", "Node id");
        var uncordonCmd = new Command("uncordon", "Uncordon a node (resume taking work)") { uncordonArg };
        uncordonCmd.SetHandler(async ctx =>
        {
            var id = ctx.ParseResult.GetValueForArgument(uncordonArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.UncordonNodeAsync(id, ctx.GetCancellationToken());
            r.Success($"Node '{result.NodeId}' uncordoned.");
        });

        cmd.AddCommand(statusCmd);
        cmd.AddCommand(nodesCmd);
        cmd.AddCommand(rebalanceCmd);
        cmd.AddCommand(cordonCmd);
        cmd.AddCommand(uncordonCmd);
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
    // DLQ (dead-letter queue)
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildDlqCommand()
    {
        var cmd = new Command("dlq", "Dead-letter queue: browse, replay, discard failed exchanges");

        // tsak dlq list [--context] [--route] [--status] [--limit] [--offset]
        var ctxOpt = new Option<string?>("--context", "Filter by context");
        var routeOpt = new Option<string?>("--route", "Filter by route id");
        var statusOpt = new Option<string?>("--status", "Filter by status (pending/replayed/discarded)");
        var limitOpt = new Option<int?>("--limit", () => 50, "Page size (1..1000)");
        var offsetOpt = new Option<int?>("--offset", () => 0, "Rows to skip");
        var listCmd = new Command("list", "List dead-lettered exchanges")
        { ctxOpt, routeOpt, statusOpt, limitOpt, offsetOpt };
        listCmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.GetFailedExchangesAsync(
                context: ctx.ParseResult.GetValueForOption(ctxOpt),
                route: ctx.ParseResult.GetValueForOption(routeOpt),
                status: ctx.ParseResult.GetValueForOption(statusOpt),
                limit: ctx.ParseResult.GetValueForOption(limitOpt),
                offset: ctx.ParseResult.GetValueForOption(offsetOpt),
                ct: ctx.GetCancellationToken());
            if (!result.Available)
            {
                r.Error(result.Error ?? "DLQ not available (no database on this node).");
                ctx.ExitCode = 1;
                return;
            }
            r.RenderTable(result.Entries,
                ("Id", e => e.EntryId.Length > 8 ? e.EntryId[..8] : e.EntryId),
                ("Time", e => e.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                ("Route", e => e.RouteId),
                ("Marker", e => e.MarkerName),
                ("Status", e => e.Status),
                ("Error", e => e.ExceptionType ?? "—"),
                ("Replayable", e => e.Replayable ? "yes" : "no"));
        });

        // tsak dlq replay <id>
        var replayArg = new Argument<string>("id", "Entry id (full)");
        var replayCmd = new Command("replay", "Replay a dead-lettered exchange") { replayArg };
        replayCmd.SetHandler(async ctx =>
        {
            var id = ctx.ParseResult.GetValueForArgument(replayArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ReplayExchangeAsync(id, ctx.GetCancellationToken());
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak dlq discard <id>
        var discardArg = new Argument<string>("id", "Entry id (full)");
        var discardCmd = new Command("discard", "Discard a dead-lettered exchange") { discardArg };
        discardCmd.SetHandler(async ctx =>
        {
            var id = ctx.ParseResult.GetValueForArgument(discardArg);
            ConfirmOrAbort(ctx, $"Discard DLQ entry '{id}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.DiscardExchangeAsync(id, ctx.GetCancellationToken());
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        cmd.AddCommand(listCmd);
        cmd.AddCommand(replayCmd);
        cmd.AddCommand(discardCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // AUDIT
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildAuditCommand()
    {
        var cmd = new Command("audit", "Query the persistent admin-action audit trail");

        var actorOption = new Option<string?>("--actor", "Filter by actor (API key id or principal)");
        var actionOption = new Option<string?>("--action", "Filter by action name (e.g. RemoveContext)");
        var targetOption = new Option<string?>("--target", "Filter by target resource");
        var sinceOption = new Option<DateTime?>("--since", "Only entries at or after this UTC time");
        var untilOption = new Option<DateTime?>("--until", "Only entries at or before this UTC time");
        var limitOption = new Option<int?>("--limit", () => 50, "Page size (1..1000)");
        var offsetOption = new Option<int?>("--offset", () => 0, "Rows to skip (pagination)");

        // tsak audit [--actor] [--action] [--target] [--since] [--until] [--limit] [--offset]
        cmd.AddOption(actorOption);
        cmd.AddOption(actionOption);
        cmd.AddOption(targetOption);
        cmd.AddOption(sinceOption);
        cmd.AddOption(untilOption);
        cmd.AddOption(limitOption);
        cmd.AddOption(offsetOption);

        cmd.SetHandler(async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);

            var since = ctx.ParseResult.GetValueForOption(sinceOption);
            var until = ctx.ParseResult.GetValueForOption(untilOption);
            var result = await client.GetAuditAsync(
                actor: ctx.ParseResult.GetValueForOption(actorOption),
                action: ctx.ParseResult.GetValueForOption(actionOption),
                target: ctx.ParseResult.GetValueForOption(targetOption),
                since: since is { } s ? new DateTimeOffset(s.ToUniversalTime(), TimeSpan.Zero) : null,
                until: until is { } u ? new DateTimeOffset(u.ToUniversalTime(), TimeSpan.Zero) : null,
                limit: ctx.ParseResult.GetValueForOption(limitOption),
                offset: ctx.ParseResult.GetValueForOption(offsetOption),
                ct: ctx.GetCancellationToken());

            if (!result.Available)
            {
                r.Error(result.Error ?? "Audit trail is not available (no database configured on this node).");
                ctx.ExitCode = 1;
                return;
            }

            r.RenderTable(result.Entries,
                ("Time", e => e.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                ("Actor", e => e.ActorPrincipal ?? e.ActorKeyId ?? "—"),
                ("Action", e => e.Action),
                ("Target", e => e.TargetResource ?? "—"),
                ("Status", e => e.StatusCode.ToString()),
                ("IP", e => e.RemoteIp ?? "—"));

            var offset = ctx.ParseResult.GetValueForOption(offsetOption) ?? 0;
            var shown = result.Count ?? result.Entries.Length;
            if (shown == (result.Limit ?? 0))
                r.Success($"Showing {shown} entries from offset {offset}. Use --offset {offset + shown} for the next page.");
        });

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
