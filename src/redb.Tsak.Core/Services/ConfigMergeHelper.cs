namespace redb.Tsak.Core.Services;

/// <summary>
/// Deep-merges configuration dictionaries. Source wins on leaf conflicts.
/// Nested dictionaries are merged recursively.
/// </summary>
internal static class ConfigMergeHelper
{
    /// <summary>
    /// Merges <paramref name="source"/> into <paramref name="target"/> in-place.
    /// For conflicting leaf keys, source value wins.
    /// For nested dictionaries, values merge recursively.
    /// </summary>
    public static void DeepMerge(IDictionary<string, object?> target, IDictionary<string, object?> source)
    {
        foreach (var (key, sourceValue) in source)
        {
            if (sourceValue is IDictionary<string, object?> sourceDict
                && target.TryGetValue(key, out var existing)
                && existing is IDictionary<string, object?> targetDict)
            {
                // Both sides are nested dicts — recurse
                DeepMerge(targetDict, sourceDict);
            }
            else
            {
                // Leaf or type mismatch — source wins
                target[key] = sourceValue;
            }
        }
    }
}
