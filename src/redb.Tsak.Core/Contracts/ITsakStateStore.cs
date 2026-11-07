namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Persistent key-value state store for context and coordinator state.
/// Used to persist autoStart flags, context config, last-known state on restart.
/// InMemory for Standalone, redb-backed for Single/Cluster.
/// <para>
/// CONTRACT: keys are compared ORDINALLY, case-sensitive — <c>Get</c>/<c>Set</c>/<c>Remove</c>
/// and <see cref="GetByPrefixAsync"/> alike, on every implementation. The rule lives here on the
/// interface (owner decision 2026-09-02): before that the redb store was ordinal while the
/// in-memory default folded case, so code developed standalone silently changed behavior when
/// deployed clustered (review 2026-09-02, С7). Very long keys may be stored in a normalized
/// deterministic form; round-tripping through <see cref="GetByPrefixAsync"/> returns the stored
/// form.
/// </para>
/// </summary>
public interface ITsakStateStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
    Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix);
}
