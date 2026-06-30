namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Provider of compile-time (static) modules registered via ProjectReference.
/// Equivalent of lt.tsak IStaticModuleProvider.
/// </summary>
public interface IStaticModuleProvider
{
    /// <summary>Provider name for identification.</summary>
    string ProviderName { get; }

    /// <summary>Loading priority (higher = loaded first).</summary>
    int Priority { get; }

    /// <summary>Returns modules provided by this provider.</summary>
    IEnumerable<ITsakModule> GetStaticModules();

    /// <summary>Whether this provider can be initialized.</summary>
    bool CanInitialize();
}
