using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Modules;
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
    [RequiresRole(TsakRoles.Admin)]
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

    /// <summary>
    /// Uploads a <c>.tpkg</c> for deployment. The package bytes are the request body; the detached
    /// signature (base64) travels in the <c>X-Tsak-Signature</c> header. Disabled by default
    /// (<c>Tsak:Modules:Upload:Enabled</c>), admin-only, audited. All validation — size, valid ZIP,
    /// manifest, name sanitization, signature — happens in <see cref="ModuleUploadService"/>.
    /// </summary>
    [HttpPost("/upload")]
    [RequiresRole(TsakRoles.Admin)]
    [AuditAdminAction(ActionName = "UploadModule")]
    public async Task<object> Upload(
        [FromBody] byte[] package,
        [FromHeader("X-Tsak-Signature")] string? signature)
    {
        var svc = Context.GetService<ModuleUploadService>();
        if (svc is null || !svc.UploadEnabled)
        {
            ApiResponse.Forbidden(Exchange, "Module upload is disabled (Tsak:Modules:Upload:Enabled=false).");
            Exchange.Stop();
            return null!;
        }

        byte[]? sigBytes = null;
        if (!string.IsNullOrWhiteSpace(signature))
            sigBytes = System.Text.Encoding.ASCII.GetBytes(signature);

        var result = await svc.InstallAsync(package, sigBytes, CancellationToken.None);
        if (!result.Success)
        {
            ApiResponse.BadRequest(Exchange, result.Message);
            Exchange.Stop();
            return null!;
        }

        return new ModuleDeployResponse
        {
            Success = true,
            Message = result.Message,
            ModuleName = result.ModuleName,
            Version = result.Version
        };
    }

    /// <summary>
    /// Dry-run validation of a `.tpkg` — same checks as upload (size, ZIP, manifest, name,
    /// signature) but installs nothing. For CI to verify a package before deploying.
    /// </summary>
    [HttpPost("/validate")]
    [RequiresRole(TsakRoles.Admin)]
    public object Validate(
        [FromBody] byte[] package,
        [FromHeader("X-Tsak-Signature")] string? signature)
    {
        var svc = Context.GetService<ModuleUploadService>();
        if (svc is null || !svc.UploadEnabled)
        {
            ApiResponse.Forbidden(Exchange, "Module upload/validate is disabled.");
            Exchange.Stop();
            return null!;
        }

        byte[]? sigBytes = null;
        if (!string.IsNullOrWhiteSpace(signature))
            sigBytes = System.Text.Encoding.ASCII.GetBytes(signature);

        var result = svc.Validate(package, sigBytes);
        return new ModuleDeployResponse
        {
            Success = result.Success,
            Message = result.Message,
            ModuleName = result.ModuleName,
            Version = result.Version
        };
    }

    /// <summary>Rolls a module back to its previous on-disk version. Admin-only, audited.</summary>
    [HttpPost("/{name}/rollback")]
    [RequiresRole(TsakRoles.Admin)]
    [AuditAdminAction(ActionName = "RollbackModule", TargetParam = "name")]
    public object Rollback([FromRoute("name")] string name)
    {
        var svc = Context.GetService<ModuleUploadService>();
        if (svc is null || !svc.UploadEnabled)
        {
            ApiResponse.Forbidden(Exchange, "Module upload/rollback is disabled.");
            Exchange.Stop();
            return null!;
        }

        var result = svc.Rollback(name);
        if (!result.Success)
        {
            ApiResponse.BadRequest(Exchange, result.Message);
            Exchange.Stop();
            return null!;
        }

        return new ModuleDeployResponse { Success = true, Message = result.Message, ModuleName = result.ModuleName };
    }
}
