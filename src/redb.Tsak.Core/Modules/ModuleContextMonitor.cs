using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Tells how many module load contexts are in memory and how many of them should be gone. A context should be gone
/// once Tsak let it go (<see cref="ModuleAssemblyLoadContext.TryUnload"/>): a version replaced by a hot swap or a
/// package reload and dropped from the rollback history, a removed module, the throwaway context of a package check.
/// <para>
/// With <see cref="HotReloadOptions.Collectible"/> false such a context never leaves memory, so it counts as orphaned
/// from the moment it is let go. A collectible one is checked after the operation that let it go, off the hot path:
/// a few full collections with growing pauses between them. One still alive after the last of them is logged as a
/// warning and counted. A context that leaves memory later stops being counted, because the count is taken from the
/// module contexts still in memory (<see cref="ModuleAssemblyLoadContext.InMemory"/>).
/// </para>
/// </summary>
public sealed class ModuleContextMonitor
{
    /// <summary>Full collections a released collectible context gets before it counts as orphaned.</summary>
    internal const int Collections = 10;

    /// <summary>What the node needs once too many contexts stay in memory.</summary>
    public const string RestartAdvice = "restart the node to reclaim memory";

    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(5);

    private readonly HotReloadOptions _options;
    private readonly ILogger _logger;
    private readonly TimeSpan _firstPause;
    private readonly object _gate = new();
    private Task _running = Task.CompletedTask;
    private bool _checking;
    private bool _again;
    private int _reportedOrphaned;

    public ModuleContextMonitor(IOptions<HotReloadOptions> options, ILogger<ModuleContextMonitor> logger)
        : this(options.Value, logger, TimeSpan.FromMilliseconds(100))
    {
    }

    /// <param name="firstPause">The pause after the first collection; each next one doubles, up to five seconds.</param>
    internal ModuleContextMonitor(HotReloadOptions options, ILogger? logger, TimeSpan firstPause)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _firstPause = firstPause;
    }

    /// <summary>
    /// Module load contexts in memory: the live modules, the rollback history, the orphaned ones and the ones still
    /// unloading.
    /// </summary>
    public int ModuleContextsAlive => ModuleAssemblyLoadContext.InMemory().Count;

    /// <summary>
    /// Module load contexts in memory that Tsak let go: not collectible, or collectible and still alive after the
    /// collections of their check.
    /// </summary>
    public int OrphanedModuleContexts => ModuleAssemblyLoadContext.InMemory()
        .Count(c => c.ReleasedAtUtc is not null && (!c.IsCollectible || c.OutlivedUnload));

    /// <summary>The limit above which the node should be restarted (<see cref="HotReloadOptions.MaxOrphanedContexts"/>).</summary>
    public int MaxOrphanedContexts => _options.EffectiveMaxOrphanedContexts;

    /// <summary>What to do about the orphaned contexts: null within the limit, the restart advice above it.</summary>
    public string? Notice
    {
        get
        {
            var orphaned = OrphanedModuleContexts;
            return orphaned > MaxOrphanedContexts ? Describe(orphaned) : null;
        }
    }

    /// <summary>
    /// Checks the contexts let go since the last check, in the background. Calls while a check runs fold into one more
    /// round after it, so a series of swaps costs one check at a time.
    /// </summary>
    public void ScheduleCheck()
    {
        lock (_gate)
        {
            if (_checking)
            {
                _again = true;
                return;
            }
            _checking = true;
            _running = Task.Run(RunAsync);
        }
    }

    /// <summary>For tests: schedules a check and returns the task that ends when no check is left.</summary>
    internal Task CheckAsync()
    {
        ScheduleCheck();
        lock (_gate)
            return _running;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await CheckOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HotReload: the unload check of module contexts failed");
            }

            lock (_gate)
            {
                if (!_again)
                {
                    _checking = false;
                    return;
                }
                _again = false;
            }
        }
    }

    private async Task CheckOnceAsync()
    {
        var released = TakeReleased();
        if (released.Count > 0)
        {
            var pause = _firstPause;
            for (var round = 1; round <= Collections && released.Exists(r => r.Context.IsAlive); round++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (round < Collections && released.Exists(r => r.Context.IsAlive))
                {
                    await Task.Delay(pause).ConfigureAwait(false);
                    pause = pause * 2 < LongestPause ? pause * 2 : LongestPause;
                }
            }
            MarkOutlived(released);
        }
        ReportLimit();
    }

    /// <summary>
    /// Out of line, so no local of the async check roots a context: only weak references leave this method.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string Name, WeakReference Context)> TakeReleased()
    {
        var released = new List<(string, WeakReference)>();
        foreach (var context in ModuleAssemblyLoadContext.InMemory())
        {
            if (!context.IsCollectible || context.ReleasedAtUtc is null || context.UnloadChecked)
                continue;
            context.UnloadChecked = true;
            released.Add((context.Name ?? "", new WeakReference(context)));
        }
        return released;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MarkOutlived(List<(string Name, WeakReference Context)> released)
    {
        foreach (var (name, reference) in released)
        {
            if (reference.Target is not ModuleAssemblyLoadContext context)
                continue;
            context.OutlivedUnload = true;
            _logger.LogWarning(
                "HotReload: module context {Name} still alive after {Collections} collections: something outside the " +
                "module still refers to it, and its memory stays until the node restarts",
                name, Collections);
        }
    }

    /// <summary>Warns once each time the orphaned count grows above the limit.</summary>
    private void ReportLimit()
    {
        var orphaned = OrphanedModuleContexts;
        var reported = Volatile.Read(ref _reportedOrphaned);
        if (orphaned > MaxOrphanedContexts && orphaned > reported)
            _logger.LogWarning("HotReload: {Notice}", Describe(orphaned));
        Volatile.Write(ref _reportedOrphaned, orphaned);
    }

    private string Describe(int orphaned) =>
        $"{orphaned} module context(s) stay in memory after their modules were replaced or removed " +
        $"(limit {MaxOrphanedContexts}): {RestartAdvice}";
}
