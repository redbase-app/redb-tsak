using redb.Tsak.Core.Modules;

namespace redb.Tsak.Worker;

/// <summary>
/// BCL-only entry hook, invoked as the FIRST statement of the process (see Program.cs), BEFORE
/// any redb.* type is touched. Installs the shared-layer resolver and preloads the redb.*
/// framework assemblies from <c>Libs/shared</c> so they can be removed from the application bin
/// (stage B2) and delivered as swappable patch DLLs. See <c>docs/SHARED_RUNTIME_PLAN.md</c>.
/// <para>
/// This class must reference ONLY BCL and <see cref="SharedRuntime"/> (itself BCL-only). It must
/// NOT touch any redb.Core/redb.Route type, or those assemblies would load before the resolver
/// is installed. <see cref="SharedRuntime"/> lives in redb.Tsak.Core (which stays in bin), and
/// merely loading that assembly does not force-load its redb.* dependencies.
/// </para>
/// </summary>
internal static class SharedRuntimeBootstrap
{
    /// <summary>
    /// The redb.* assemblies that redb.Tsak.Core references at compile time and therefore touches
    /// while the host is being built (AddTsak). They MUST be present in the Default ALC before
    /// that happens. Keep in sync with the redb.* ProjectReferences in redb.Tsak.Core.csproj —
    /// if a redb.* reference is added there, add it here too. (Diagnostic mirror of
    /// scripts/shared-manifest.psd1; redb.Tsak.* are NOT listed — they stay in the bin.)
    /// </summary>
    private static readonly string[] FrameworkAssemblies =
    [
        "redb.Core",
        "redb.Core.Pro",
        "redb.Route.Core",
        "redb.Route.Http",
        "redb.Route.Quartz",
        "redb.Route.Sql",
        "redb.Postgres",
        "redb.Postgres.Pro",
        "redb.MSSql",
        "redb.MSSql.Pro",
        "redb.SQLite",
        "redb.SQLite.Pro",
    ];

    /// <summary>
    /// Installs the resolver over <c>Libs/shared</c> and preloads the redb.* framework set. Optional
    /// verbose logging to stderr when <c>TSAK_SHARED_DEBUG</c> is set (the DI logger is not up yet).
    /// <para>
    /// FAIL-FAST: the framework no longer ships in the bin (pruned — see Worker.csproj), so every
    /// name in <see cref="FrameworkAssemblies"/> MUST resolve from the shared layer. If any is
    /// missing/corrupt the process refuses to start with a precise message — NOW, deterministically,
    /// rather than as an obscure MissingMethodException a day later under load. This fail-fast is
    /// scoped to the framework set only; the lazy transitive tail stays soft (loaded on demand).
    /// </para>
    /// </summary>
    public static void InstallEarly()
    {
        var verbose = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSAK_SHARED_DEBUG"));
        Action<string>? log = verbose ? (msg => Console.Error.WriteLine(msg)) : null;

        var sharedPath = Environment.GetEnvironmentVariable("TSAK_SHARED_DIR");
        if (string.IsNullOrWhiteSpace(sharedPath))
            sharedPath = SharedRuntime.DefaultSharedPath;

        var missing = SharedRuntime.InstallEarly(sharedPath, FrameworkAssemblies, log);

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Shared runtime layer is incomplete — the redb.* framework could not be loaded from " +
                $"'{sharedPath}'. Startup aborted (fail-fast). Missing/unloadable:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", missing) + Environment.NewLine +
                "Rebuild the shared layer: scripts/build-shared.ps1 -IncludeFramework " +
                "(or set TSAK_SHARED_DIR to a complete shared directory).");
        }

        AssertFrameworkCompatible(log);
    }

    /// <summary>
    /// COMPAT-GATE: the framework now comes from an external shared layer that could be swapped, so
    /// we verify on startup that it matches THIS Tsak build's minor. Patch differences are allowed
    /// (that is the whole point — leaf/provider patches ship independently); a minor mismatch, or a
    /// mix of minors inside the shared layer, aborts startup with a precise message instead of a
    /// cryptic MissingMethodException deep in a request later. Expected minor is derived from
    /// redb.Tsak.Core's own version (the ecosystem number), so there is nothing to hand-maintain.
    /// </summary>
    private static void AssertFrameworkCompatible(Action<string>? log)
    {
        var expected = typeof(SharedRuntime).Assembly.GetName().Version; // = redb.Tsak.Core version
        if (expected is null) return;

        var problems = new List<string>();

        // Representative contract libs must match Tsak's major.minor exactly.
        foreach (var name in new[] { "redb.Core", "redb.Route.Core" })
        {
            var v = SharedRuntime.GetLoadedVersion(name);
            if (v is null) { problems.Add($"{name}: not loaded"); continue; }
            if (v.Major != expected.Major || v.Minor != expected.Minor)
                problems.Add($"{name} is {v.Major}.{v.Minor}, expected {expected.Major}.{expected.Minor}");
        }

        // The whole redb.* framework in the shared layer must share ONE minor (catch "forgot to
        // update one provider" — a patch swap that pulled in a different minor).
        var minors = FrameworkAssemblies
            .Select(SharedRuntime.GetLoadedVersion)
            .Where(v => v is not null)
            .Select(v => $"{v!.Major}.{v.Minor}")
            .Distinct()
            .ToList();
        if (minors.Count > 1)
            problems.Add($"shared layer mixes redb minors: {string.Join(", ", minors)}");

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"redb ecosystem version mismatch — this is Tsak {expected.Major}.{expected.Minor} but the " +
                $"shared layer is incompatible. Startup aborted (compat-gate):{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", problems) + Environment.NewLine +
                "The shared layer must match Tsak's minor (patches may differ). Rebuild it from the same " +
                "ecosystem release.");
        }

        log?.Invoke($"[shared] compat-gate ok: framework minor {expected.Major}.{expected.Minor}");
    }
}
