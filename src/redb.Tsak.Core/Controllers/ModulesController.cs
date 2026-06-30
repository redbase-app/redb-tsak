using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Module management: list, register, unregister modules.
/// GET    /api/modules           — list all modules
/// GET    /api/modules/{name}    — get single module details
/// DELETE /api/modules/{name}    — unregister a module
/// </summary>
[Route("/api/modules")]
public class ModulesController : RedbController
{
    private ITsakModuleRegistry GetRegistry() => Context.GetService<ITsakModuleRegistry>()
        ?? throw new InvalidOperationException("ITsakModuleRegistry not registered in context");

    [HttpGet("")]
    public object ListModules()
    {
        var registry = GetRegistry();
        return registry.GetAllModules().Select(m => new ModuleInfo
        {
            ModuleName = m.ModuleName,
            Version = m.Version,
            Description = m.Description,
            Status = m.Status.ToString(),
            CanInitialize = m.CanInitialize,
            Dependencies = m.Dependencies.ToArray()
        }).ToList();
    }

    [HttpGet("/{name}")]
    public object? GetModule([FromRoute("name")] string name)
    {
        var registry = GetRegistry();
        var module = registry.GetModule(name);
        if (module is null)
        {
            ApiResponse.NotFound(Exchange, $"Module '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        return new ModuleInfo
        {
            ModuleName = module.ModuleName,
            Version = module.Version,
            Description = module.Description,
            Status = module.Status.ToString(),
            CanInitialize = module.CanInitialize,
            Dependencies = module.Dependencies.ToArray()
        };
    }

    [HttpDelete("/{name}")]
    [AuditAdminAction(ActionName = "UnregisterModule", TargetParam = "name")]
    public object? UnregisterModule([FromRoute("name")] string name)
    {
        var registry = GetRegistry();
        var removed = registry.UnregisterModule(name);
        if (!removed)
        {
            ApiResponse.NotFound(Exchange, $"Module '{name}' not found.");
            Exchange.Stop();
            return null;
        }
        return new ModuleRemovedResponse { ModuleName = name, Removed = true };
    }
}
