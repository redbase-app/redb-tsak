using redb.Tsak.Web.Pro;
using redb.Tsak.Web.Pro.Extensions;
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

// Fast shutdown on Ctrl+C (default is 30s)
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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
app.UseAntiforgery();

// BFF proxy: download log files from worker nodes without direct access
app.MapGet("/api/proxy/{nodeId}/logs/download/{filename}", async (
    string nodeId, string filename,
    INodeClientProvider nodeProvider) =>
{
    var client = nodeProvider.GetClient(nodeId);
    if (client is null)
        return Results.NotFound(new { Error = "NodeNotFound", Message = $"Node '{nodeId}' not available" });

    try
    {
        var zipBytes = await client.DownloadLogFileAsync(filename);
        return Results.File(zipBytes, "application/zip", $"{filename}.zip");
    }
    catch (redb.Tsak.Client.ApiException ex)
    {
        return Results.StatusCode(ex.StatusCode);
    }
});

app.MapRazorComponents<redb.Tsak.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
