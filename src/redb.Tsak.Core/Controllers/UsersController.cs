using redb.Core;
using redb.Core.Models.Users;
using redb.Core.Providers;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.RedbCore.Extensions;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

[Route("/api/users")]
public class UsersController : RedbController
{
    // Per-request scoped IRedbService (own connection) — NOT the captive singleton, which would
    // share one non-thread-safe connection across concurrent requests.
    private IUserProvider GetProvider() => this.Redb().UserProvider;

    [HttpGet("")]
    public async Task<object> ListUsers()
    {
        var provider = GetProvider();
        var users = await provider.GetUsersAsync();
        return users
            .Where(u => u.Id > 0)
            .Select(u => new TsakUserInfo
            {
                Id = u.Id,
                Login = u.Login,
                Name = u.Name,
                Role = u.CodeString ?? "viewer",
                Enabled = u.Enabled,
                DateRegister = u.DateRegister
            })
            .ToList();
    }

    [HttpGet("/{login}")]
    public async Task<object?> GetUser([FromRoute("login")] string login)
    {
        var provider = GetProvider();
        var user = await provider.GetUserByLoginAsync(login);
        if (user is null)
        {
            ApiResponse.NotFound(Exchange, $"User '{login}' not found.");
            Exchange.Stop();
            return null;
        }

        return new TsakUserInfo
        {
            Id = user.Id,
            Login = user.Login,
            Name = user.Name,
            Role = user.CodeString ?? "viewer",
            Enabled = user.Enabled,
            DateRegister = user.DateRegister
        };
    }

    [HttpPost("")]
    [AuditAdminAction(ActionName = "CreateUser")]
    public async Task<object?> CreateUser([FromBody] TsakCreateUserRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrWhiteSpace(request.Password))
        {
            ApiResponse.BadRequest(Exchange, "Login and password are required.");
            Exchange.Stop();
            return null;
        }

        var provider = GetProvider();

        if (!await provider.IsLoginAvailableAsync(request.Login))
        {
            ApiResponse.BadRequest(Exchange, $"Login '{request.Login}' is already taken.");
            Exchange.Stop();
            return null;
        }

        var coreRequest = new CreateUserRequest
        {
            Login = request.Login,
            Password = request.Password,
            Name = request.Name,
            Enabled = true,
            CodeString = request.Role ?? "viewer"
        };

        var user = await provider.CreateUserAsync(coreRequest);
        return new TsakUserInfo
        {
            Id = user.Id,
            Login = user.Login,
            Name = user.Name,
            Role = user.CodeString ?? "viewer",
            Enabled = user.Enabled,
            DateRegister = user.DateRegister
        };
    }

    [HttpPut("/{login}")]
    [AuditAdminAction(ActionName = "UpdateUser", TargetParam = "login")]
    public async Task<object?> UpdateUser([FromRoute("login")] string login, [FromBody] TsakUpdateUserRequest? request)
    {
        if (request is null)
        {
            ApiResponse.BadRequest(Exchange, "Request body is required.");
            Exchange.Stop();
            return null;
        }

        var provider = GetProvider();
        var user = await provider.GetUserByLoginAsync(login);
        if (user is null)
        {
            ApiResponse.NotFound(Exchange, $"User '{login}' not found.");
            Exchange.Stop();
            return null;
        }

        if (user.Id <= 0)
        {
            ApiResponse.BadRequest(Exchange, "System users cannot be modified.");
            Exchange.Stop();
            return null;
        }

        // Admin (id=1): only password change allowed
        if (user.Id == 1)
        {
            if (!string.IsNullOrEmpty(request.Password))
                await provider.SetPasswordAsync(user, request.Password);

            return new TsakUserInfo
            {
                Id = user.Id,
                Login = user.Login,
                Name = user.Name,
                Role = user.CodeString ?? "viewer",
                Enabled = user.Enabled,
                DateRegister = user.DateRegister
            };
        }

        var coreRequest = new UpdateUserRequest
        {
            Name = request.Name,
            CodeString = request.Role,
            Enabled = request.Enabled
        };

        var updated = await provider.UpdateUserAsync(user, coreRequest);

        if (!string.IsNullOrEmpty(request.Password))
            await provider.SetPasswordAsync(updated, request.Password);

        return new TsakUserInfo
        {
            Id = updated.Id,
            Login = updated.Login,
            Name = updated.Name,
            Role = updated.CodeString ?? "viewer",
            Enabled = updated.Enabled,
            DateRegister = updated.DateRegister
        };
    }

    [HttpDelete("/{login}")]
    [AuditAdminAction(ActionName = "DeleteUser", TargetParam = "login")]
    public async Task<object?> DeleteUser([FromRoute("login")] string login)
    {
        var provider = GetProvider();
        var user = await provider.GetUserByLoginAsync(login);
        if (user is null)
        {
            ApiResponse.NotFound(Exchange, $"User '{login}' not found.");
            Exchange.Stop();
            return null;
        }

        if (user.Id <= 1)
        {
            ApiResponse.BadRequest(Exchange, "System users cannot be deleted.");
            Exchange.Stop();
            return null;
        }

        await provider.DeleteUserAsync(user);

        return new TsakUserActionResponse
        {
            Login = login,
            Action = "deleted"
        };
    }
}
