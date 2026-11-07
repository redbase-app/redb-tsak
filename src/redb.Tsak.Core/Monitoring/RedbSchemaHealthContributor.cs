using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Models;
using redb.Route.RedbCore.Extensions;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Reports the redb databases of the worker as <see cref="HealthStatus.Degraded"/> while one of them has schema
/// problems that need a person (<see cref="IRedbService.SchemaProblems"/>): the host's own storage and every named
/// instance the contexts declare. The service keeps working, but redb logs the problem as an error at every start until
/// it is resolved; this check lets monitoring see it too.
/// <para>
/// It reads the list the last start or scheme synchronization left, which costs no database round trip; it never runs
/// <see cref="IRedbService.CheckSchemaAsync"/>, so a health poll does not query the schema. The list is held per
/// database, so any instance of it sees what the start found.
/// </para>
/// </summary>
internal sealed class RedbSchemaHealthContributor : IHealthContributor
{
    /// <summary>The key of this check in <see cref="HealthEvaluation.Checks"/>.</summary>
    internal const string CheckName = "redb";

    /// <summary>Where the fix of a schema problem is described, in the public redb repository.</summary>
    internal const string UpgradingLink = "https://github.com/redbase-app/redb/blob/main/UPGRADING.md#duplicate-root-structures";

    // A health response stays readable: the first problems are spelled out, the rest are counted.
    private const int ShownProblems = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly ITsakContextManager _contexts;
    private readonly ILogger<RedbSchemaHealthContributor>? _logger;

    public RedbSchemaHealthContributor(IServiceScopeFactory scopes, ITsakContextManager contexts,
        ILogger<RedbSchemaHealthContributor>? logger = null)
    {
        _scopes = scopes;
        _contexts = contexts;
        _logger = logger;
    }

    public void Contribute(HealthEvaluation result)
    {
        var databases = 0;
        var problems = new List<(string Database, RedbSchemaProblem Problem)>();

        try
        {
            using var scope = _scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IRedbService>() is { } host)
            {
                databases++;
                problems.AddRange(host.SchemaProblems.Select(p => ("host", p)));
            }
        }
        catch (Exception ex)
        {
            // The health endpoint answers whatever the host's redb wiring does; a service that cannot even be built
            // fails its own start loudly, this check does not repeat that.
            _logger?.LogDebug(ex, "Health: the host's redb service could not be resolved");
        }

        foreach (var instance in _contexts.GetNamedRedbInstances())
        {
            if (instance.ContextName is not { } contextName || _contexts.GetContext(contextName) is not { } context)
                continue;
            try
            {
                // No exchange: the instance the context registered. Reading its list opens no connection.
                var named = context.GetRedbService(instance.Name);
                databases++;
                problems.AddRange(named.SchemaProblems.Select(p => ($"{contextName}/{instance.Name}", p)));
            }
            catch (InvalidOperationException ex)
            {
                // Declared but not registered (its container failed to build): the context manager logged that already.
                _logger?.LogDebug(ex, "Health: named redb '{Name}' of context {Context} is not registered", instance.Name, contextName);
            }
        }

        if (databases == 0)
            return; // a worker without redb storage has nothing to report here

        if (problems.Count == 0)
        {
            result.Checks[CheckName] = HealthStatus.Healthy;
            return;
        }

        result.Checks[CheckName] = HealthStatus.Degraded;
        var shown = problems.Take(ShownProblems).Select(p => $"[{p.Database}] {p.Problem.Message}");
        var more = problems.Count > ShownProblems ? $" And {problems.Count - ShownProblems} more." : "";
        result.Details[CheckName] =
            $"{problems.Count} schema problem(s) need a person. {string.Join(" ", shown)}{more} See {UpgradingLink}";
    }
}
