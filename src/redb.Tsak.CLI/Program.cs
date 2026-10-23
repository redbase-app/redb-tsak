using System.CommandLine;
using System.Globalization;
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

    // Recursive: every subcommand takes them, as global options did before System.CommandLine 2.0.

    private static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Connection profile name",
        Recursive = true,
    };

    private static readonly Option<string?> UrlOption = new("--url", "-u")
    {
        Description = "Tsak runtime URL (overrides profile)",
        Recursive = true,
    };

    private static readonly Option<string?> KeyOption = new("--key", "-k")
    {
        Description = "API key (overrides profile)",
        Recursive = true,
    };

    private static readonly Option<OutputFormat> OutputOption = new("--output", "-o")
    {
        DefaultValueFactory = _ => OutputFormat.Table,
        Description = "Output format: table | json | quiet",
        Recursive = true,
    };

    private static readonly Option<bool> NoColorOption = new("--no-color")
    {
        Description = "Disable colored output",
        Recursive = true,
    };

    private static readonly Option<bool> YesOption = new("--yes", "-y")
    {
        Description = "Skip confirmation prompts",
        Recursive = true,
    };

    private static readonly Option<bool> WatchOption = new("--watch")
    {
        Description = "Poll repeatedly (Ctrl+C to stop)",
        Recursive = true,
    };

    private static readonly Option<int> IntervalOption = new("--interval")
    {
        DefaultValueFactory = _ => 5,
        Description = "Polling interval in seconds (used with --watch)",
        Recursive = true,
    };

    private static readonly Option<int?> TimeoutOption = new("--timeout")
    {
        Description = "HTTP request timeout in seconds (default: 5). Useful for slow operations like diagnostics dump, force-stop, module install.",
        Recursive = true,
    };

    public static async Task<int> Main(string[] args)
    {
        // System.CommandLine ships localized resources and picks them by the OS UI language; the CLI
        // speaks English everywhere, so help, usage and parse errors match the rest of its output.
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        var root = new RootCommand("tsak — CLI for redb.Tsak runtime management");

        root.Add(ProfileOption);
        root.Add(UrlOption);
        root.Add(KeyOption);
        root.Add(OutputOption);
        root.Add(NoColorOption);
        root.Add(YesOption);
        root.Add(WatchOption);
        root.Add(IntervalOption);
        root.Add(TimeoutOption);

        // ── Register command groups ─────────────────────────────────
        root.Add(BuildSystemCommand());
        root.Add(BuildContextCommand());
        root.Add(BuildModuleCommand());
        root.Add(BuildSchedulerCommand());
        root.Add(BuildClusterCommand());
        root.Add(BuildLogCommand());
        root.Add(BuildAuditCommand());
        root.Add(BuildDlqCommand());
        root.Add(BuildAuthCommand());
        root.Add(BuildProfileCommand());
        root.Add(Commands.RouteCommands.Create());
        root.Add(Commands.WatchdogCommands.Create());
        root.Add(Commands.DiagnosticsCommands.Create());

        // Help, version, parse errors and Ctrl+C are the library's defaults; a command's exception is handled by
        // Handle, which sets the exit code the way the CLI always has. Required arguments left out are reported by
        // name (MissingArguments): the library's text names only the command.
        var parseResult = root.Parse(args);
        if (MissingArguments.Of(parseResult) is { Count: > 0 } missing)
            return MissingArguments.Report(parseResult, missing);
        return await parseResult.InvokeAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Sets the action of <paramref name="command"/>: runs <paramref name="action"/> with the parse result and the
    /// Ctrl+C token, turns an exception into a message and an exit code (<see cref="HandleException"/>), and returns
    /// the exit code the action set.
    /// </summary>
    internal static void Handle(Command command, Func<CliInvocation, Task> action)
    {
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var invocation = new CliInvocation(parseResult, cancellationToken);
            try
            {
                await action(invocation);
            }
            catch (Exception ex)
            {
                HandleException(ex, invocation);
            }
            return invocation.ExitCode;
        });
    }

    /// <summary>A synchronous action, otherwise as <see cref="Handle(Command, Func{CliInvocation, Task})"/>.</summary>
    internal static void Handle(Command command, Action<CliInvocation> action)
        => Handle(command, invocation =>
        {
            action(invocation);
            return Task.CompletedTask;
        });

    /// <summary>
    /// Resolves connection settings and creates an API client from the invocation context.
    /// </summary>
    internal static TsakApiClient CreateClient(CliInvocation ctx)
    {
        var profile = new ProfileManager().Resolve(
            ctx.ParseResult.GetValue(ProfileOption),
            ctx.ParseResult.GetValue(UrlOption),
            ctx.ParseResult.GetValue(KeyOption));

        if (profile is null)
        {
            Console.Error.WriteLine("No connection configured. Use --url, set TSAK_URL, or create a profile.");
            ctx.ExitCode = 1;
            throw new OperationCanceledException();
        }

        var timeoutSeconds = ctx.ParseResult.GetValue(TimeoutOption);
        var timeout = timeoutSeconds is > 0 ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;
        return new TsakApiClient(profile.Url, profile.ApiKey, timeout);
    }

    /// <summary>
    /// Creates the appropriate output renderer from the invocation context.
    /// </summary>
    internal static IOutputRenderer CreateRenderer(CliInvocation ctx)
    {
        var format = ctx.ParseResult.GetValue(OutputOption);
        var noColor = ctx.ParseResult.GetValue(NoColorOption);
        return RendererFactory.Create(format, noColor);
    }

    /// <summary>
    /// Asks for confirmation before a destructive action. Aborts if declined.
    /// Skipped when --yes is set or output format is not Table (scripts).
    /// </summary>
    internal static void ConfirmOrAbort(CliInvocation ctx, string prompt)
    {
        var yes = ctx.ParseResult.GetValue(YesOption);
        var format = ctx.ParseResult.GetValue(OutputOption);
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
    internal static async Task RunOrWatch(CliInvocation ctx, Func<CancellationToken, Task> action)
    {
        var watch = ctx.ParseResult.GetValue(WatchOption);
        var interval = ctx.ParseResult.GetValue(IntervalOption);
        var ct = ctx.CancellationToken;

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
    /// <summary>
    /// Writes a PEM key. <see cref="FileMode.CreateNew"/> fails rather than replace a file that appeared after the
    /// check for existing keys; <see cref="FileMode.Create"/> replaces it (<c>--force</c>).
    /// </summary>
    private static void WriteKey(string path, string pem, FileMode mode)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(pem);
    }

    private static void HandleException(Exception ex, CliInvocation ctx)
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
        Handle(healthCmd, async ctx =>
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
                        ("Status", kv => kv.Value.ToString()),
                        ("Detail", kv => health.Details.GetValueOrDefault(kv.Key) ?? ""));
                }
            });
        });

        // tsak system metrics [--watch] [--interval N]
        var metricsCmd = new Command("metrics", "Show system metrics");
        Handle(metricsCmd, async ctx =>
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
        Handle(infoCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var info = await client.GetInfoAsync(ctx.CancellationToken);
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
        Handle(configCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var cfg = await client.GetConfigAsync(ctx.CancellationToken);
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

        cmd.Add(healthCmd);
        cmd.Add(metricsCmd);
        cmd.Add(infoCmd);
        cmd.Add(configCmd);
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
        Handle(listCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var contexts = await client.ListContextsAsync(ctx.CancellationToken);
            r.RenderTable(contexts,
                ("Name", c => c.Name),
                ("Status", c => c.Status),
                ("Endpoints", c => c.EndpointCount.ToString()));
        });

        // tsak context get <name>
        var nameArg = new Argument<string>("name") { Description = "Context name" };
        var getCmd = new Command("get", "Get context details") { nameArg };
        Handle(getCmd, async (ctx) =>
        {
            var name = ctx.ParseResult.GetValue(nameArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var detail = await client.GetContextAsync(name, ctx.CancellationToken);
            r.RenderDetail(
                ("Name", detail.Name),
                ("Status", detail.Status),
                ("Endpoints", detail.EndpointCount.ToString()),
                ("AutoStart", detail.AutoStart ? "Yes" : "No"));
        });

        // tsak context start <name>
        var startArg = new Argument<string>("name") { Description = "Context name" };
        var startCmd = new Command("start", "Start a context") { startArg };
        Handle(startCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(startArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StartContextAsync(name, ctx.CancellationToken);
            r.Success($"Context '{result.Name}' → {result.Status}");
        });

        // tsak context stop <name>
        var stopArg = new Argument<string>("name") { Description = "Context name" };
        var stopCmd = new Command("stop", "Stop a context") { stopArg };
        Handle(stopCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(stopArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StopContextAsync(name, ctx.CancellationToken);
            r.Success($"Context '{result.Name}' → {result.Status}");
        });

        // tsak context restart <name>
        var restartArg = new Argument<string>("name") { Description = "Context name" };
        var restartCmd = new Command("restart", "Restart a context") { restartArg };
        Handle(restartCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(restartArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RestartContextAsync(name, ctx.CancellationToken);
            r.Success($"Context '{result.Name}' → {result.Status}");
        });

        // tsak context remove <name> [--yes]
        var removeArg = new Argument<string>("name") { Description = "Context name" };
        var removeCmd = new Command("remove", "Remove a context") { removeArg };
        Handle(removeCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(removeArg);
            ConfirmOrAbort(ctx, $"Remove context '{name}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RemoveContextAsync(name, ctx.CancellationToken);
            r.Success($"Context '{result.Name}' removed.");
        });

        cmd.Add(listCmd);
        cmd.Add(getCmd);
        cmd.Add(startCmd);
        cmd.Add(stopCmd);
        cmd.Add(restartCmd);
        cmd.Add(removeCmd);
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
        Handle(listCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var modules = await client.ListModulesAsync(ctx.CancellationToken);
            r.RenderTable(modules,
                ("Name", m => m.ModuleName),
                ("Version", m => m.Version ?? "—"),
                ("Status", m => m.Status),
                ("Dependencies", m => m.Dependencies.Length > 0 ? string.Join(", ", m.Dependencies) : "—"));
        });

        // tsak module get <name>
        var nameArg = new Argument<string>("name") { Description = "Module name" };
        var getCmd = new Command("get", "Get module details") { nameArg };
        Handle(getCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(nameArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var mod = await client.GetModuleAsync(name, ctx.CancellationToken);
            r.RenderDetail(
                ("Name", mod.ModuleName),
                ("Version", mod.Version ?? "—"),
                ("Description", mod.Description ?? "—"),
                ("Status", mod.Status),
                ("Can Initialize", mod.CanInitialize ? "Yes" : "No"),
                ("Dependencies", mod.Dependencies.Length > 0 ? string.Join(", ", mod.Dependencies) : "—"));
        });

        // tsak module remove <name> [--yes]
        var removeArg = new Argument<string>("name") { Description = "Module name" };
        var removeCmd = new Command("remove", "Remove a module") { removeArg };
        Handle(removeCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(removeArg);
            ConfirmOrAbort(ctx, $"Remove module '{name}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RemoveModuleAsync(name, ctx.CancellationToken);
            r.Success($"Module '{result.ModuleName}' removed.");
        });

        // tsak module keygen --out <prefix> [--force]  → writes <prefix>.key (private) + <prefix>.pub (public)
        // The command creates a secret, so where it goes is always the caller's choice (no default prefix), and an
        // existing key is never replaced unless asked to: losing a signing key strands every package signed with it.
        var keygenOut = new Option<string>("--out")
        {
            Description = "Output file prefix: writes <prefix>.key (private) and <prefix>.pub (public)",
            Required = true,
        };
        var keygenForce = new Option<bool>("--force") { Description = "Overwrite an existing key pair with that prefix" };
        var keygenCmd = new Command("keygen", "Generate an ECDSA signing key pair for module packages")
        { keygenOut, keygenForce };
        Handle(keygenCmd, ctx =>
        {
            var prefix = ctx.ParseResult.GetValue(keygenOut)!;
            var force = ctx.ParseResult.GetValue(keygenForce);
            var r = CreateRenderer(ctx);
            var keyPath = Path.GetFullPath(prefix + ".key");
            var pubPath = Path.GetFullPath(prefix + ".pub");

            var existing = new[] { keyPath, pubPath }.Where(File.Exists).ToList();
            if (existing.Count > 0 && !force)
            {
                r.Error($"Not overwriting {string.Join(" and ", existing)}. Choose another --out, or pass --force to " +
                        "replace the key pair (packages signed with the old key then no longer verify).");
                ctx.ExitCode = 1;
                return;
            }

            using var ecdsa = System.Security.Cryptography.ECDsa.Create(
                System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var mode = force ? FileMode.Create : FileMode.CreateNew;
            WriteKey(keyPath, ecdsa.ExportPkcs8PrivateKeyPem(), mode);
            WriteKey(pubPath, ecdsa.ExportSubjectPublicKeyInfoPem(), mode);
            r.Success($"Wrote {keyPath} (PRIVATE: keep secret, use to sign) and {pubPath} " +
                      $"(public: configure on the node as Tsak:Modules:Signature:PublicKeyPath).");
        });

        // tsak module sign <file.tpkg> --key <private.pem>  → writes <file.tpkg.sig> (base64)
        var signFileArg = new Argument<string>("file") { Description = "Path to the .tpkg to sign" };
        var signKey = new Option<string>("--key") { Description = "Path to the PEM private key", Required = true };
        var signCmd = new Command("sign", "Sign a .tpkg with a private key (produces .tpkg.sig)")
        { signFileArg, signKey };
        Handle(signCmd, ctx =>
        {
            var file = ctx.ParseResult.GetValue(signFileArg);
            var keyPath = ctx.ParseResult.GetValue(signKey)!;
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
        var deployFileArg = new Argument<string>("file") { Description = "Path to the .tpkg to deploy" };
        var deploySig = new Option<string?>("--sig") { Description = "Path to the .tpkg.sig (defaults to <file>.sig if present)" };
        var deployCmd = new Command("deploy", "Upload a .tpkg to the node for hot-deploy")
        { deployFileArg, deploySig };
        Handle(deployCmd, async ctx =>
        {
            var file = ctx.ParseResult.GetValue(deployFileArg);
            var sigPath = ctx.ParseResult.GetValue(deploySig) ?? (File.Exists(file + ".sig") ? file + ".sig" : null);
            var r = CreateRenderer(ctx);

            var bytes = await File.ReadAllBytesAsync(file, ctx.CancellationToken);
            string? sigB64 = null;
            if (sigPath is not null)
            {
                var raw = await File.ReadAllTextAsync(sigPath, ctx.CancellationToken);
                sigB64 = raw.Trim();
            }

            using var client = CreateClient(ctx);
            var result = await client.UploadModuleAsync(bytes, sigB64, ctx.CancellationToken);
            if (result.Success) r.Success($"{result.Message} (module: {result.ModuleName ?? "?"}, v{result.Version ?? "?"})");
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak module validate <file.tpkg> [--sig <file.tpkg.sig>]  → dry-run, installs nothing
        var validateFileArg = new Argument<string>("file") { Description = "Path to the .tpkg to validate" };
        var validateSig = new Option<string?>("--sig") { Description = "Path to the .tpkg.sig (defaults to <file>.sig if present)" };
        var validateCmd = new Command("validate", "Validate a .tpkg without installing it") { validateFileArg, validateSig };
        Handle(validateCmd, async ctx =>
        {
            var file = ctx.ParseResult.GetValue(validateFileArg);
            var sigPath = ctx.ParseResult.GetValue(validateSig) ?? (File.Exists(file + ".sig") ? file + ".sig" : null);
            var r = CreateRenderer(ctx);

            var bytes = await File.ReadAllBytesAsync(file, ctx.CancellationToken);
            string? sigB64 = sigPath is not null ? (await File.ReadAllTextAsync(sigPath, ctx.CancellationToken)).Trim() : null;

            using var client = CreateClient(ctx);
            var result = await client.ValidateModuleAsync(bytes, sigB64, ctx.CancellationToken);
            if (result.Success) r.Success($"{result.Message} (module: {result.ModuleName ?? "?"}, v{result.Version ?? "?"})");
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak module rollback <name>
        var rollbackArg = new Argument<string>("name") { Description = "Module name to roll back" };
        var rollbackCmd = new Command("rollback", "Roll a module back to its previous version") { rollbackArg };
        Handle(rollbackCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(rollbackArg);
            ConfirmOrAbort(ctx, $"Roll module '{name}' back to the previous version?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RollbackModuleAsync(name, ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        cmd.Add(listCmd);
        cmd.Add(getCmd);
        cmd.Add(removeCmd);
        cmd.Add(keygenCmd);
        cmd.Add(signCmd);
        cmd.Add(deployCmd);
        cmd.Add(validateCmd);
        cmd.Add(rollbackCmd);
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
        Handle(statusCmd, async ctx =>
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
                    ("Running Jobs", s.RunningJobs.ToString()),
                    ("Contexts", s.Schedulers.Length == 0
                        ? "—"
                        : string.Join(", ", s.Schedulers.Select(c => $"{c.Context} ({c.Status}, {c.TotalJobs} jobs)"))));
            });
        });

        // tsak scheduler jobs
        var jobsCmd = new Command("jobs", "List scheduled jobs");
        Handle(jobsCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ListScheduledJobsAsync(ctx.CancellationToken);
            r.RenderTable(result.Jobs,
                ("Context", j => j.Context ?? "—"),
                ("Job", j => j.JobName),
                ("Group", j => j.JobGroup),
                ("Trigger", j => j.TriggerState),
                ("Cron", j => j.CronExpression ?? "—"),
                ("Next Fire", j => j.NextFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"));
        });

        // tsak scheduler running [--watch] [--interval N]
        var runningCmd = new Command("running", "List currently running jobs");
        Handle(runningCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            await RunOrWatch(ctx, async ct =>
            {
                var result = await client.ListRunningJobsAsync(ct);
                r.RenderTable(result.Jobs,
                    ("Context", j => j.Context ?? "—"),
                    ("Job", j => j.JobName),
                    ("Group", j => j.JobGroup),
                    ("Fire Time", j => j.FireTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("Runtime (ms)", j => j.RunTimeMs.ToString()));
            });
        });

        // Every route context owns its Quartz scheduler. Without --context a command addresses all of them;
        // a job key that two contexts share has to be qualified.
        const string jobContextHelp = "Route context whose scheduler holds the job (required when the key is in several)";

        // tsak scheduler start [--context NAME]
        var startContextOpt = new Option<string?>("--context") { Description = "Start only the scheduler of this route context" };
        var startCmd = new Command("start", "Start the scheduler") { startContextOpt };
        Handle(startCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StartSchedulerAsync(
                ctx.ParseResult.GetValue(startContextOpt), ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak scheduler standby [--context NAME]
        var standbyContextOpt = new Option<string?>("--context") { Description = "Put only the scheduler of this route context in standby" };
        var standbyCmd = new Command("standby", "Put scheduler in standby mode") { standbyContextOpt };
        Handle(standbyCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.StandbySchedulerAsync(
                ctx.ParseResult.GetValue(standbyContextOpt), ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak scheduler pause <key> [--context NAME]
        var pauseKeyArg = new Argument<string>("key") { Description = "Job key (group.name)" };
        var pauseContextOpt = new Option<string?>("--context") { Description = jobContextHelp };
        var pauseCmd = new Command("pause", "Pause a specific job") { pauseKeyArg, pauseContextOpt };
        Handle(pauseCmd, async ctx =>
        {
            var key = ctx.ParseResult.GetValue(pauseKeyArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.PauseJobAsync(
                key, ctx.ParseResult.GetValue(pauseContextOpt), ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak scheduler resume <key> [--context NAME]
        var resumeKeyArg = new Argument<string>("key") { Description = "Job key (group.name)" };
        var resumeContextOpt = new Option<string?>("--context") { Description = jobContextHelp };
        var resumeCmd = new Command("resume", "Resume a specific job") { resumeKeyArg, resumeContextOpt };
        Handle(resumeCmd, async ctx =>
        {
            var key = ctx.ParseResult.GetValue(resumeKeyArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ResumeJobAsync(
                key, ctx.ParseResult.GetValue(resumeContextOpt), ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak scheduler fire <key> [--context NAME]
        var fireKeyArg = new Argument<string>("key") { Description = "Job key (group.name)" };
        var fireContextOpt = new Option<string?>("--context") { Description = jobContextHelp };
        var fireCmd = new Command("fire", "Fire a job immediately (out of schedule)") { fireKeyArg, fireContextOpt };
        Handle(fireCmd, async ctx =>
        {
            var key = ctx.ParseResult.GetValue(fireKeyArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.FireJobAsync(
                key, ctx.ParseResult.GetValue(fireContextOpt), ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        cmd.Add(statusCmd);
        cmd.Add(jobsCmd);
        cmd.Add(runningCmd);
        cmd.Add(startCmd);
        cmd.Add(standbyCmd);
        cmd.Add(pauseCmd);
        cmd.Add(resumeCmd);
        cmd.Add(fireCmd);
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
        Handle(statusCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var s = await client.GetClusterStatusAsync(ctx.CancellationToken);
            r.RenderDetail(
                ("Enabled", s.Enabled ? "Yes" : "No"),
                ("Node ID", s.NodeId ?? "—"),
                ("Leader", s.IsLeader.HasValue ? (s.IsLeader.Value ? "Yes" : "No") : "—"),
                ("Epoch", s.CurrentEpoch?.ToString() ?? "—"));
        });

        // tsak cluster nodes
        var nodesCmd = new Command("nodes", "List cluster nodes");
        Handle(nodesCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ListClusterNodesAsync(ctx.CancellationToken);
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
        Handle(rebalanceCmd, async ctx =>
        {
            ConfirmOrAbort(ctx, "Trigger cluster rebalance?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RebalanceClusterAsync(ctx.CancellationToken);
            r.Success($"Rebalanced. Epoch: {result.CurrentEpoch}");
        });

        // tsak cluster cordon <nodeId>
        var cordonArg = new Argument<string>("nodeId") { Description = "Node id" };
        var cordonCmd = new Command("cordon", "Cordon a node (no new work; drain to peers)") { cordonArg };
        Handle(cordonCmd, async ctx =>
        {
            var id = ctx.ParseResult.GetValue(cordonArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.CordonNodeAsync(id, ctx.CancellationToken);
            r.Success($"Node '{result.NodeId}' cordoned — it will drain and take no new work.");
        });

        // tsak cluster uncordon <nodeId>
        var uncordonArg = new Argument<string>("nodeId") { Description = "Node id" };
        var uncordonCmd = new Command("uncordon", "Uncordon a node (resume taking work)") { uncordonArg };
        Handle(uncordonCmd, async ctx =>
        {
            var id = ctx.ParseResult.GetValue(uncordonArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.UncordonNodeAsync(id, ctx.CancellationToken);
            r.Success($"Node '{result.NodeId}' uncordoned.");
        });

        cmd.Add(statusCmd);
        cmd.Add(nodesCmd);
        cmd.Add(rebalanceCmd);
        cmd.Add(cordonCmd);
        cmd.Add(uncordonCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // LOG
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildLogCommand()
    {
        var cmd = new Command("log", "View runtime logs");

        var limitOption = new Option<int?>("--limit") { Description = "Maximum number of entries" };
        var levelOption = new Option<string?>("--level") { Description = "Minimum log level (Trace, Debug, Information, Warning, Error, Critical)" };

        // tsak log get [--limit N] [--level LEVEL]
        var getCmd = new Command("get", "Get buffered log entries") { limitOption, levelOption };
        Handle(getCmd, async ctx =>
        {
            var limit = ctx.ParseResult.GetValue(limitOption);
            var level = ctx.ParseResult.GetValue(levelOption);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var logs = await client.GetLogsAsync(limit: limit, level: level, ct: ctx.CancellationToken);
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

        cmd.Add(getCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // DLQ (dead-letter queue)
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildDlqCommand()
    {
        var cmd = new Command("dlq", "Dead-letter queue: browse, replay, discard failed exchanges");

        // tsak dlq list [--context] [--route] [--status] [--limit] [--offset]
        var ctxOpt = new Option<string?>("--context") { Description = "Filter by context" };
        var routeOpt = new Option<string?>("--route") { Description = "Filter by route id" };
        var statusOpt = new Option<string?>("--status") { Description = "Filter by status (pending/replayed/discarded)" };
        var limitOpt = new Option<int?>("--limit") { DefaultValueFactory = _ => 50, Description = "Page size (1..1000)" };
        var offsetOpt = new Option<int?>("--offset") { DefaultValueFactory = _ => 0, Description = "Rows to skip" };
        var listCmd = new Command("list", "List dead-lettered exchanges")
        { ctxOpt, routeOpt, statusOpt, limitOpt, offsetOpt };
        Handle(listCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.GetFailedExchangesAsync(
                context: ctx.ParseResult.GetValue(ctxOpt),
                route: ctx.ParseResult.GetValue(routeOpt),
                status: ctx.ParseResult.GetValue(statusOpt),
                limit: ctx.ParseResult.GetValue(limitOpt),
                offset: ctx.ParseResult.GetValue(offsetOpt),
                ct: ctx.CancellationToken);
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
        var replayArg = new Argument<string>("id") { Description = "Entry id (full)" };
        var replayCmd = new Command("replay", "Replay a dead-lettered exchange") { replayArg };
        Handle(replayCmd, async ctx =>
        {
            var id = ctx.ParseResult.GetValue(replayArg);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.ReplayExchangeAsync(id, ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        // tsak dlq discard <id>
        var discardArg = new Argument<string>("id") { Description = "Entry id (full)" };
        var discardCmd = new Command("discard", "Discard a dead-lettered exchange") { discardArg };
        Handle(discardCmd, async ctx =>
        {
            var id = ctx.ParseResult.GetValue(discardArg);
            ConfirmOrAbort(ctx, $"Discard DLQ entry '{id}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.DiscardExchangeAsync(id, ctx.CancellationToken);
            if (result.Success) r.Success(result.Message);
            else { r.Error(result.Message); ctx.ExitCode = 1; }
        });

        cmd.Add(listCmd);
        cmd.Add(replayCmd);
        cmd.Add(discardCmd);
        return cmd;
    }

    // ═══════════════════════════════════════════════════════════════
    // AUDIT
    // ═══════════════════════════════════════════════════════════════

    private static Command BuildAuditCommand()
    {
        var cmd = new Command("audit", "Query the persistent admin-action audit trail");

        var actorOption = new Option<string?>("--actor") { Description = "Filter by actor (API key id or principal)" };
        var actionOption = new Option<string?>("--action") { Description = "Filter by action name (e.g. RemoveContext)" };
        var targetOption = new Option<string?>("--target") { Description = "Filter by target resource" };
        var sinceOption = new Option<DateTime?>("--since") { Description = "Only entries at or after this UTC time" };
        var untilOption = new Option<DateTime?>("--until") { Description = "Only entries at or before this UTC time" };
        var limitOption = new Option<int?>("--limit") { DefaultValueFactory = _ => 50, Description = "Page size (1..1000)" };
        var offsetOption = new Option<int?>("--offset") { DefaultValueFactory = _ => 0, Description = "Rows to skip (pagination)" };

        // tsak audit [--actor] [--action] [--target] [--since] [--until] [--limit] [--offset]
        cmd.Add(actorOption);
        cmd.Add(actionOption);
        cmd.Add(targetOption);
        cmd.Add(sinceOption);
        cmd.Add(untilOption);
        cmd.Add(limitOption);
        cmd.Add(offsetOption);

        Handle(cmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);

            var since = ctx.ParseResult.GetValue(sinceOption);
            var until = ctx.ParseResult.GetValue(untilOption);
            var result = await client.GetAuditAsync(
                actor: ctx.ParseResult.GetValue(actorOption),
                action: ctx.ParseResult.GetValue(actionOption),
                target: ctx.ParseResult.GetValue(targetOption),
                since: since is { } s ? new DateTimeOffset(s.ToUniversalTime(), TimeSpan.Zero) : null,
                until: until is { } u ? new DateTimeOffset(u.ToUniversalTime(), TimeSpan.Zero) : null,
                limit: ctx.ParseResult.GetValue(limitOption),
                offset: ctx.ParseResult.GetValue(offsetOption),
                ct: ctx.CancellationToken);

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

            var offset = ctx.ParseResult.GetValue(offsetOption) ?? 0;
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
        Handle(listCmd, async ctx =>
        {
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var keys = await client.ListApiKeysAsync(ctx.CancellationToken);
            r.RenderTable(keys,
                ("ID", k => k.Id.ToString()),
                ("Name", k => k.Name),
                ("Roles", k => string.Join(", ", k.Roles)),
                ("Valid", k => k.IsValid ? "Yes" : "No"),
                ("Created", k => k.CreatedAt.ToString("yyyy-MM-dd")));
        });

        // tsak auth create --name <name> [--roles r1,r2] [--user-id id] [--expires-at dt]
        var createNameOption = new Option<string>("--name") { Description = "Key name", Required = true };
        var createRolesOption = new Option<string[]?>("--roles") { Description = "Comma-separated roles" };
        var createUserIdOption = new Option<string?>("--user-id") { Description = "Associated user ID" };
        var createExpiresOption = new Option<DateTime?>("--expires-at") { Description = "Expiration date/time" };
        var createCmd = new Command("create", "Create a new API key")
        {
            createNameOption, createRolesOption, createUserIdOption, createExpiresOption
        };
        Handle(createCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(createNameOption)!;
            var roles = ctx.ParseResult.GetValue(createRolesOption);
            var userId = ctx.ParseResult.GetValue(createUserIdOption);
            var expiresAt = ctx.ParseResult.GetValue(createExpiresOption);
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.CreateApiKeyAsync(name, roles, userId, expiresAt, ctx.CancellationToken);
            r.RenderDetail(
                ("ID", result.Id.ToString()),
                ("Key", result.Key),
                ("Name", result.Name),
                ("Roles", string.Join(", ", result.Roles)),
                ("Created", result.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
            r.Success(result.Message ?? "API key created. Save the key — it won't be shown again.");
        });

        // tsak auth revoke <id> [--yes]
        var revokeArg = new Argument<string>("id") { Description = "API key ID" };
        var revokeCmd = new Command("revoke", "Revoke an API key") { revokeArg };
        Handle(revokeCmd, async ctx =>
        {
            var id = ctx.ParseResult.GetValue(revokeArg);
            ConfirmOrAbort(ctx, $"Revoke API key '{id}'?");
            using var client = CreateClient(ctx);
            var r = CreateRenderer(ctx);
            var result = await client.RevokeApiKeyAsync(id, ctx.CancellationToken);
            r.Success($"Key {result.Id} revoked.");
        });

        cmd.Add(listCmd);
        cmd.Add(createCmd);
        cmd.Add(revokeCmd);
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
        Handle(listCmd, ctx =>
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
        var addNameOpt = new Option<string>("--name") { Description = "Profile name", Required = true };
        var addUrlOpt = new Option<string>("--url") { Description = "Runtime URL", Required = true };
        var addKeyOpt = new Option<string?>("--key") { Description = "API key" };
        var addCmd = new Command("add", "Add a new profile") { addNameOpt, addUrlOpt, addKeyOpt };
        Handle(addCmd, ctx =>
        {
            var r = CreateRenderer(ctx);
            var name = ctx.ParseResult.GetValue(addNameOpt)!;
            var url = ctx.ParseResult.GetValue(addUrlOpt)!;
            var key = ctx.ParseResult.GetValue(addKeyOpt);
            pm.Save(new ConnectionProfile { Name = name, Url = url, ApiKey = key });
            r.Success($"Profile '{name}' saved.");
        });

        // tsak profile remove <name>
        var removeArg = new Argument<string>("name") { Description = "Profile name" };
        var removeCmd = new Command("remove", "Remove a profile") { removeArg };
        Handle(removeCmd, ctx =>
        {
            var r = CreateRenderer(ctx);
            var name = ctx.ParseResult.GetValue(removeArg);
            if (pm.Delete(name))
                r.Success($"Profile '{name}' removed.");
            else
                r.Error($"Profile '{name}' not found.");
        });

        // tsak profile use <name>
        var useArg = new Argument<string>("name") { Description = "Profile name to activate" };
        var useCmd = new Command("use", "Set the active profile") { useArg };
        Handle(useCmd, ctx =>
        {
            var r = CreateRenderer(ctx);
            var name = ctx.ParseResult.GetValue(useArg);
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

        cmd.Add(listCmd);
        cmd.Add(addCmd);
        cmd.Add(removeCmd);
        cmd.Add(useCmd);
        return cmd;
    }
}
