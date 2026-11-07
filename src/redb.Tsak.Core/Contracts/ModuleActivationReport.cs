namespace redb.Tsak.Core.Contracts;

/// <summary>
/// What the coordinator could not bring up while activating modules in their contexts.
/// <para>
/// The coordinator deliberately swallows (and logs) a module's <c>Initialize</c> exception and a
/// context start exception, so that one bad module can never crash the node. Callers that must
/// react to a module that did not come up — a hot swap, a package reload, a cluster assignment —
/// read this report instead of waiting for an exception that never arrives.
/// </para>
/// <para>
/// <c>default</c> means "nothing failed", which is also what an unconfigured test double returns.
/// Endpoint start failures that redb.Route isolates inside an otherwise started context are NOT
/// reported: Tsak does not see them.
/// </para>
/// </summary>
public readonly struct ModuleActivationReport
{
    private readonly IReadOnlyList<ModuleActivationFailure>? _failures;

    public ModuleActivationReport(IReadOnlyList<ModuleActivationFailure> failures) => _failures = failures;

    /// <summary>A report with no failures.</summary>
    public static ModuleActivationReport Success => default;

    public IReadOnlyList<ModuleActivationFailure> Failures => _failures ?? Array.Empty<ModuleActivationFailure>();

    public bool Succeeded => Failures.Count == 0;

    /// <summary>The first failure recorded for <paramref name="moduleName"/>, or null when that module came up.</summary>
    public ModuleActivationFailure? FailureFor(string moduleName)
    {
        foreach (var failure in Failures)
        {
            if (string.Equals(failure.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                return failure;
        }
        return null;
    }
}

/// <summary>A module that did not come up: its <c>Initialize</c> threw, or its context failed to start.</summary>
public sealed record ModuleActivationFailure(string ModuleName, string ContextName, Exception Exception);
