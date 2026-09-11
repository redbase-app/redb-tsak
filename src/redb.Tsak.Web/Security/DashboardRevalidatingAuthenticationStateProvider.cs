using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using redb.Tsak.Web.Pro;

namespace redb.Tsak.Web.Security;

/// <summary>
/// Circuit-side half of session revalidation (review 2026-09-02, В14). The cookie's
/// OnValidatePrincipal covers plain HTTP requests, but a live Blazor circuit fixes its
/// AuthenticationState at connection time and never re-reads the cookie — so a deleted or
/// demoted user would keep an open dashboard indefinitely. This provider re-resolves the
/// account every <see cref="DashboardAuth.RevalidationInterval"/> and tears the circuit's
/// auth state down when the account is gone or its role changed.
/// </summary>
public sealed class DashboardRevalidatingAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DashboardRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override TimeSpan RevalidationInterval => DashboardAuth.RevalidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var principal = authenticationState.User;
        var login = principal.Identity?.Name;
        if (string.IsNullOrEmpty(login))
            return false;

        // IAuthService is scoped; the revalidation timer runs outside any circuit scope.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var user = await auth.FindAsync(login);
        return user is not null && DashboardAuth.RolesMatch(principal, user.Role);
    }
}
