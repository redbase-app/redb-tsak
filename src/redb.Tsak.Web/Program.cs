using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using redb.Tsak.Web.Pro;
using redb.Tsak.Web.Pro.Extensions;
using redb.Tsak.Web.Security;
using redb.Tsak.Web.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog — same pattern as Worker
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration));

// Standalone defaults — always registered
builder.Services.AddSingleton<INodeClientProvider, StandaloneClientProvider>();
builder.Services.AddScoped<IAuthService, ConfigAuthService>();
builder.Services.AddScoped<ToastService>();

// Pro — always called, mode check inside (like Core.Pro AddTsakCluster)
builder.Services.AddTsakWebPro(builder.Configuration);

// ── Authentication / authorization ─────────────────────────────────────────
// Real server-side session: an ASP.NET Core auth cookie, not a per-circuit in-memory flag. This is
// what makes the dashboard a proper BFF — the browser holds only the cookie; the server holds the
// Tsak keys and only uses them for an authenticated principal. Pages are gated by the [Authorize]
// attribute they inherit from Components/_Imports.razor (AuthorizeRouteView enforces the page's
// own authorize metadata — it is NOT a gate by itself; review 2026-09-02, К1); plain HTTP
// endpoints (e.g. the log-download proxy) carry explicit RequireAuthorization.
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

// Behind a TLS-terminating reverse proxy (the DEPLOYMENT.md nginx shape) the app must honor
// X-Forwarded-Proto/For — otherwise it sees plain HTTP, issues the auth cookie without Secure and
// never sends HSTS, and the login throttle cannot see real client addresses (review 2026-09-02,
// В12/В15). Opt-in, mirroring the worker's Tsak:Http:TrustedProxies posture.
var trustProxyHeaders = builder.Configuration.GetValue("Tsak:Web:TrustProxyHeaders", false);
if (trustProxyHeaders)
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        // The dashboard sits directly behind the operator's own proxy; which hop is trusted is the
        // operator's choice, expressed by turning the flag on.
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "tsak.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Dashboard often runs plain HTTP on loopback / behind a TLS-terminating proxy; require
        // HTTPS only when the request itself is HTTPS so local runs are not locked out. Behind a
        // proxy, Tsak:Web:TrustProxyHeaders=true makes the request scheme (and thus Secure) real.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // A deleted or demoted user must not ride an 8-hour sliding cookie (review 2026-09-02,
        // В14): re-resolve the account every 5 minutes; reject the principal when it is gone or
        // its role changed. Blazor circuits are covered by the revalidating state provider below.
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = ctx => DashboardAuth.RevalidatePrincipalAsync(ctx)
        };
    });
// Failed-login throttle (lockout) — shared across all requests on the node.
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new LoginThrottle(
        maxAttempts: cfg.GetValue("Tsak:Web:Lockout:MaxAttempts", 5),
        window: TimeSpan.FromSeconds(cfg.GetValue("Tsak:Web:Lockout:WindowSeconds", 300)),
        lockoutDuration: TimeSpan.FromSeconds(cfg.GetValue("Tsak:Web:Lockout:DurationSeconds", 60)));
});
builder.Services.AddAuthorization(options =>
{
    // Role ladder is expanded into claims at sign-in, so RequireRole is a simple membership test.
    options.AddPolicy("Viewer", p => p.RequireRole("viewer"));
    options.AddPolicy("Operator", p => p.RequireRole("operator"));
    options.AddPolicy("Admin", p => p.RequireRole("admin"));
});

// Fast shutdown on Ctrl+C (default is 30s)
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Circuit-side session revalidation (review 2026-09-02, В14) — pairs with the cookie's
// OnValidatePrincipal: a live circuit fixes its auth state at connect time and must be re-checked.
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider,
    DashboardRevalidatingAuthenticationStateProvider>();

var app = builder.Build();

var pathBase = app.Configuration["ASPNETCORE_PATHBASE"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE");
if (!string.IsNullOrEmpty(pathBase))
    app.UsePathBase(pathBase);

// Seed admin user (no-op in standalone mode)
await TsakWebProExtensions.SeedAdminUserAsync(app.Services, app.Configuration);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
if (trustProxyHeaders)
    app.UseForwardedHeaders(); // before auth: the scheme/client-IP must be real when cookies are issued
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Auth endpoints ─────────────────────────────────────────────────────────
// Sign-in must happen on a plain HTTP request (SignInAsync writes the Set-Cookie header), which a
// Blazor interactive circuit cannot do after the response has started. The login form POSTs here.
app.MapPost("/auth/login", async (
    HttpContext http, IAntiforgery antiforgery, IAuthService auth, LoginThrottle throttle) =>
{
    // ABSOLUTE path (as below): the request path is /auth/login, so a relative "login?..."
    // resolved under /auth/ and sent the browser to GET this POST-only endpoint — HTTP 405
    // instead of an error message. A distinct code, so a stale/missing token is not
    // misreported as wrong credentials.
    try { await antiforgery.ValidateRequestAsync(http); }
    catch { return Results.Redirect("/login?error=stale"); }

    var form = await http.Request.ReadFormAsync();
    var login = form["login"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    // Two throttle dimensions (review 2026-09-02, В15). Keying on the username alone let a remote
    // attacker lock the real administrator out with ~5 wrong passwords a minute, from anywhere;
    // (username, client IP) confines a lockout to the attacker's own address. The coarser IP-only
    // bucket (4x budget) slows password spraying across many usernames from one source. Behind a
    // proxy the client IP is real only with Tsak:Web:TrustProxyHeaders=true.
    var clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var userKey = $"u:{login.Trim().ToLowerInvariant()}|{clientIp}";
    var ipKey = $"ip:{clientIp}";
    if (throttle.IsLockedOut(userKey) || throttle.IsLockedOut(ipKey))
        return Results.Redirect("/login?error=locked");

    var user = string.IsNullOrEmpty(login)
        ? null
        : await auth.ValidateAsync(login, password);

    if (user is null)
    {
        throttle.RecordFailure(userKey);
        throttle.RecordFailure(ipKey, maxAttemptsOverride: throttle.MaxAttempts * 4);
        return Results.Redirect("/login?error=1");
    }

    throttle.RecordSuccess(userKey);

    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
    identity.AddClaim(new Claim(ClaimTypes.Name, user.Login));
    if (!string.IsNullOrEmpty(user.DisplayName))
        identity.AddClaim(new Claim("display_name", user.DisplayName));
    foreach (var role in DashboardAuth.ExpandRoles(user.Role))
        identity.AddClaim(new Claim(ClaimTypes.Role, role));

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });

    // Only ever redirect to a local path — never an attacker-supplied absolute URL.
    return DashboardAuth.IsLocalUrl(returnUrl) ? Results.LocalRedirect(returnUrl) : Results.Redirect("/");
}).DisableAntiforgery(); // validated manually above

app.MapPost("/auth/logout", async (HttpContext http, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(http); }
    catch { /* logout is idempotent — a bad token just means we still sign out */ }
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login"); // absolute: relative "login" resolved to /auth/login (405)
}).DisableAntiforgery();

// ── BFF proxy: download log files from worker nodes ────────────────────────
// Now behind the same auth as the rest of the dashboard: Operator+ only, node validated by the
// client provider, filename constrained to a bare name (no path traversal). Anonymous / viewer
// callers get 401/403 from the auth middleware before this handler runs.
app.MapGet("/api/proxy/{nodeId}/logs/download/{filename}", async (
    string nodeId, string filename,
    INodeClientProvider nodeProvider,
    CancellationToken ct) => // bound to HttpContext.RequestAborted — a cancelled download aborts the fetch (4.9)
{
    // Reject anything that is not a plain file name (path traversal, nested paths).
    if (!DashboardAuth.IsSafeLogFileName(filename))
        return Results.BadRequest(new { Error = "InvalidFilename" });

    var client = nodeProvider.GetClient(nodeId);
    if (client is null)
        return Results.NotFound(new { Error = "NodeNotFound", Message = $"Node '{nodeId}' not available" });

    try
    {
        var zipBytes = await client.DownloadLogFileAsync(filename, ct);
        return Results.File(zipBytes, "application/zip", $"{filename}.zip");
    }
    catch (redb.Tsak.Client.ApiException ex)
    {
        return Results.StatusCode(ex.StatusCode);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        // The download client's own (longer) timeout elapsed — not a browser disconnect (which sets `ct`
        // and is handled by ASP.NET as a client abort). Return a clean 504 instead of an unhandled 500 (4.9).
        return Results.StatusCode(504);
    }
}).RequireAuthorization("Admin"); // review item 2.8: full log files are admin-only (exfil risk)

app.MapRazorComponents<redb.Tsak.Web.Components.App>()
    .AddInteractiveServerRenderMode();

// Startup visibility: log the port(s) the dashboard actually bound to, once the server is listening.
// app.Urls is only populated after the host starts, so read it from the ApplicationStarted callback.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = app.Urls.Count > 0
        ? string.Join(", ", app.Urls)
        : "(configured via ASPNETCORE_URLS / launch profile / Kestrel config)";
    var tsakNode = app.Configuration["Tsak:Web:StandaloneUrl"] ?? "http://localhost:9090";
    app.Logger.LogInformation(
        "Tsak dashboard listening on {Urls}; proxying to Tsak management API at {Node}", urls, tsakNode);
});

app.Run();

// Exposed so integration tests can reference the entry-point assembly.
public partial class Program { }
