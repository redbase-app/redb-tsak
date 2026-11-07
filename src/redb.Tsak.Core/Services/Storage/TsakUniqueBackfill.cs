using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Data;
using redb.Core.Exceptions;
using redb.Core.Models.Entities;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// One-time V4 migration pass: stamps <c>ValueUnique</c> onto rows written by pre-key
/// versions of Tsak (docs/V4/UNIQUE_STORAGE_REFACTORING_PLAN.md, Ф7). A NULL key sits
/// outside the partial unique index, so without this pass legacy rows would coexist with
/// keyed ones and escape the "one record per key" guarantee. Idempotent: rows that
/// already carry a key are never touched, a second run is a single server-side probe.
/// Duplicates (the trace of the old check-then-insert races) resolve deterministically:
/// the lowest id wins — the same winner the old self-healing picked.
/// </summary>
public static class TsakUniqueBackfill
{
    public enum DuplicatePolicy
    {
        /// <summary>Losers are deleted — operational records (locks, nodes, assignments, KV entries).</summary>
        DeleteLosers,

        /// <summary>
        /// Losers stay, unkeyed, and are logged as critical — for rows that may anchor a
        /// subtree (cluster roots, groups), where silent deletion could orphan children.
        /// </summary>
        KeepLosersUnkeyed,
    }

    /// <summary>
    /// <paramref name="computeKey"/> returns the <c>ValueUnique</c> for a legacy row, or null
    /// when it cannot be computed (the row is skipped with a warning; an
    /// <see cref="ArgumentException"/> from it counts as null). <paramref name="amend"/> runs
    /// on the winner right before it is saved — e.g. to fill a new Props field on the way.
    /// </summary>
    public static async Task RunAsync<TProps>(
        IRedbService redb,
        Func<RedbObject<TProps>, string?> computeKey,
        DuplicatePolicy duplicates,
        ILogger logger,
        Action<RedbObject<TProps>>? amend = null) where TProps : class, new()
    {
        // Post-migration fast path (review 2026-09-02, Q4): one probe of the partial index
        // instead of hydrating the whole scheme on every process start — this pass sits on
        // startup-critical paths (leader election's first lock acquire included).
        var hasLegacy = await redb.Query<TProps>()
            .WhereRedb(o => o.ValueUnique == null)
            .AnyAsync()
            .ConfigureAwait(false);
        if (!hasLegacy) return;

        // Transient deadlocks in the stamp/delete sweep must not abort node startup
        // (review 2026-09-02, С4; plan Ф7.1 required the retry from the start).
        await DeadlockRetryHelper.ExecuteWithRetryAsync(
            () => RunCoreAsync(redb, computeKey, duplicates, logger, amend)).ConfigureAwait(false);
    }

    private static async Task RunCoreAsync<TProps>(
        IRedbService redb,
        Func<RedbObject<TProps>, string?> computeKey,
        DuplicatePolicy duplicates,
        ILogger logger,
        Action<RedbObject<TProps>>? amend) where TProps : class, new()
    {
        // The full load runs only while legacy rows exist; every scheme this pass runs on is
        // small by construction (locks, nodes, assignments, state entries, module records, keys).
        var all = await redb.Query<TProps>().ToListAsync().ConfigureAwait(false);
        var legacy = all.Where(o => o.ValueUnique is null).ToList();
        if (legacy.Count == 0) return;

        var claimedKeys = new HashSet<string>(
            all.Where(o => o.ValueUnique is not null).Select(o => o.ValueUnique!),
            StringComparer.Ordinal);

        var withKeys = new List<(RedbObject<TProps> Row, string? Key)>(legacy.Count);
        foreach (var row in legacy)
        {
            string? key;
            try { key = computeKey(row); }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "{Scheme}: cannot compute a unique key for legacy row {Id}",
                    typeof(TProps).Name, row.id);
                key = null;
            }
            withKeys.Add((row, key));
        }

        foreach (var group in withKeys.GroupBy(t => t.Key, StringComparer.Ordinal))
        {
            if (group.Key is null)
            {
                foreach (var (row, _) in group)
                    logger.LogWarning("{Scheme}: legacy row {Id} has no computable unique key — left unkeyed",
                        typeof(TProps).Name, row.id);
                continue;
            }

            var ordered = group.Select(t => t.Row).OrderBy(o => o.id).ToList();
            var losers = ordered.AsEnumerable();

            // A keyed row for this value may already exist (another node re-created the record
            // after its own backfill) — then every legacy row is a loser.
            if (!claimedKeys.Contains(group.Key))
            {
                var winner = ordered[0];
                winner.ValueUnique = group.Key;
                amend?.Invoke(winner);
                try
                {
                    await redb.SaveAsync(winner).ConfigureAwait(false);
                    losers = ordered.Skip(1);
                    if (ordered.Count > 1)
                        logger.LogWarning("{Scheme}: key '{Key}' had {Count} legacy duplicates — kept id {Id}",
                            typeof(TProps).Name, group.Key, ordered.Count, winner.id);
                }
                catch (RedbUniqueViolationException ex) when (!IsObjectKeyViolation(ex))
                {
                    // The violation came from a DIFFERENT unique index (e.g. an amend-stamped
                    // [RedbUnique] Props field). These rows are NOT duplicates of each other's
                    // object key — deleting them would destroy live data (review 2026-09-02, К4:
                    // pre-V4 api-key rotation left two hashes sharing one note; the second row is
                    // the CURRENT credential). Keep everything, report loudly, move on.
                    logger.LogCritical(ex,
                        "{Scheme}: stamping key '{Key}' violated a different unique index ({Where}) — "
                        + "rows of this group are left unkeyed and NOT deleted; resolve the conflicting "
                        + "values (e.g. duplicate ids) and restart to finish the migration",
                        typeof(TProps).Name, group.Key, ex.PropertyName ?? ex.ConstraintName ?? "unresolved index");
                    continue;
                }
                catch (RedbUniqueViolationException)
                {
                    // The object key itself was claimed concurrently by another node's backfill —
                    // our winner is a loser like the rest.
                    logger.LogInformation("{Scheme}: key '{Key}' was claimed concurrently — all legacy rows are losers",
                        typeof(TProps).Name, group.Key);
                }
                catch (RedbUniqueKeyValueException ex)
                {
                    // The computed key breaks a core constraint (e.g. longer than 440 chars).
                    // A data problem in one row must not crash node startup (review 2026-09-02,
                    // В8) — leave the group unkeyed and report it.
                    logger.LogCritical(ex,
                        "{Scheme}: key for legacy row {Id} is not storable ({Message}) — row left unkeyed",
                        typeof(TProps).Name, winner.id, ex.Message);
                    winner.ValueUnique = null;
                    continue;
                }
            }

            foreach (var loser in losers)
            {
                if (duplicates == DuplicatePolicy.KeepLosersUnkeyed)
                {
                    logger.LogCritical(
                        "{Scheme}: legacy row {Id} duplicates key '{Key}' and may anchor a subtree — left unkeyed. "
                        + "Verify which subtree actually holds the live children before touching either row",
                        typeof(TProps).Name, loser.id, group.Key);
                    continue;
                }

                try { await redb.DeleteAsync(loser).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{Scheme}: failed to delete legacy duplicate {Id} (key '{Key}')",
                        typeof(TProps).Name, loser.id, group.Key);
                }
            }
        }
    }

    /// <summary>
    /// True only when the violation is confidently attributable to the OBJECT key index
    /// (<c>_objects._value_unique</c>) — first-class via <see cref="RedbUniqueViolationException.Kind"/>
    /// since BR-8 closed (the old heuristic parsed constraint names and driver text). An UNKNOWN
    /// source answers false: the destructive claimed-concurrently path must never run on a guess
    /// (review 2026-09-02, К4).
    /// </summary>
    internal static bool IsObjectKeyViolation(RedbUniqueViolationException ex) =>
        ex.Kind == RedbUniqueViolationKind.ObjectKey;
}
