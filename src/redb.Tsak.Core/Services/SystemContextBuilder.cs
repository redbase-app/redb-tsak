using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Controllers;
using redb.Route.Http;
using redb.Route.Processors;
using redb.Route.Quartz;
using redb.Route.Sql;
using redb.Route.Sql.Connection;
using redb.Tsak.Core.Audit;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Dlq;
using redb.Tsak.Core.Controllers;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Security;
using redb.Core.Providers;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Builds and starts the <c>_system</c> route context that hosts the REST management API.
/// Pipeline: HTTP listener → header bridge → auth (optional) → controller dispatch.
/// Respects <c>Tsak:Api:Enabled</c> config (default: true).
/// </summary>
public class SystemContextBuilder
{
    public const string SystemContextName = "_system";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Loopback client used by the facade <c>/metrics</c> route to proxy the OTel listener.</summary>
    private static readonly HttpClient MetricsClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ITsakContextManager _contextManager;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemContextBuilder> _logger;

    public SystemContextBuilder(
        ITsakContextManager contextManager,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger<SystemContextBuilder> logger)
    {
        _contextManager = contextManager;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates and starts the _system context with the REST API if enabled in config.
    /// Returns the context or null if the API is disabled.
    /// </summary>
    public async Task<IRouteContext?> BuildAndStartAsync(
        ITsakModuleRegistry registry,
        CancellationToken ct = default)
    {
        var enabled = _configuration.GetValue("Tsak:Api:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("REST API disabled (Tsak:Api:Enabled=false)");
            return null;
        }

        var host = _configuration.GetValue("Tsak:Api:Host", "0.0.0.0");
        var port = _configuration.GetValue("Tsak:Api:Port", 9090);
        var authEnabled = _configuration.GetValue("Tsak:Auth:Enabled", false);

        // Auth-exempt patterns (config: Tsak:Api:AuthExempt). Default: K8s health probes.
        // Patterns ending in '*' use prefix match; otherwise exact (case-insensitive) match.
        var exemptPatterns = _configuration
            .GetSection("Tsak:Api:AuthExempt")
            .Get<string[]>() ?? new[] { "/api/health/*", "/api/health" };

        _logger.LogInformation("Building _system context: http://{Host}:{Port}, auth={Auth}", host, port, authEnabled);

        // 1. Create the context via context manager
        var context = _contextManager.CreateContext(SystemContextName, _serviceProvider, new Dictionary<string, object?>());
        var routeContext = (RouteContext)context;

        // 2. Add HTTP component so http: URIs resolve
        var httpComponent = _serviceProvider.GetService(typeof(HttpComponent)) as HttpComponent;
        if (httpComponent is null)
        {
            _logger.LogError("HttpComponent not available in DI — cannot start REST API. Register AddRedbRouteHttp() in DI.");
            await _contextManager.RemoveContextAsync(SystemContextName);
            return null;
        }
        routeContext.AddComponent(httpComponent);

        // 3. Register services that controllers need
        RegisterServices(routeContext, registry, authEnabled);

        // 4. Build controller registry
        var controllerRegistry = new ControllerRegistry();
        controllerRegistry.RegisterAssembly(typeof(ContextsController).Assembly);

        // 4a. Let plugins contribute additional controllers and services
        var plugins = _serviceProvider.GetService<IEnumerable<ISystemContextPlugin>>()
                      ?? Enumerable.Empty<ISystemContextPlugin>();
        foreach (var plugin in plugins)
        {
            plugin.ConfigureServices(routeContext, _serviceProvider);
            foreach (var asm in plugin.GetControllerAssemblies())
                controllerRegistry.RegisterAssembly(asm);
        }

        // 5. Build auth processor if enabled
        ApiKeyService? apiKeyService = null;
        if (authEnabled)
        {
            apiKeyService = CreateApiKeyService();
            routeContext.AddService(typeof(ApiKeyService), apiKeyService);
        }

        // 5a. Optional per-IP throttle on /api/auth/* (brute-force protection).
        // Disabled when limit <= 0. Defaults: 10 attempts per 60 s per remote IP.
        var authThrottleLimit = _configuration.GetValue("Tsak:Api:AuthThrottle:Limit", 10);
        var authThrottleWindowSec = _configuration.GetValue("Tsak:Api:AuthThrottle:WindowSeconds", 60);
        var authThrottlePathPrefix = _configuration.GetValue("Tsak:Api:AuthThrottle:PathPrefix", "/api/auth/");
        KeyedThrottleProcessor? authThrottle = null;
        if (authEnabled && authThrottleLimit > 0 && apiKeyService is not null)
        {
            // Inner processor only applies the auth check — outer KeyedThrottle gates concurrency per-IP.
            var authProcessor = new AuthorizeProcessor(apiKeyService);
            authThrottle = new KeyedThrottleProcessor(
                next: authProcessor,
                keyExtractor: ex => ex.In.Headers.TryGetValue(HttpHeaders.RemoteAddress, out var ip)
                    ? ip?.ToString() ?? "unknown" : "unknown",
                maxPerPeriod: authThrottleLimit,
                period: TimeSpan.FromSeconds(authThrottleWindowSec),
                logger: _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("Tsak.AuthThrottle"));
        }

        // 5b. Role enforcement (Tsak:Auth:EnforceRoles, default true when auth is on).
        // Runs immediately after a successful auth check and only there, so it can never
        // touch technical endpoints: auth-exempt paths (k8s probes under /api/health/*)
        // skip the whole block, and the echo / Prometheus routes have their own pipelines.
        var enforceRoles = _configuration.GetValue("Tsak:Auth:EnforceRoles", true);
        var rolelessKeysAreAdmin = _configuration.GetValue("Tsak:Auth:RolelessKeysAreAdmin", true);
        RoleAuthorizationProcessor? roleAuthorizer = null;
        if (authEnabled && enforceRoles)
        {
            roleAuthorizer = new RoleAuthorizationProcessor(controllerRegistry, rolelessKeysAreAdmin);
            _logger.LogInformation(
                "Role enforcement enabled (roleless keys treated as admin: {Legacy})", rolelessKeysAreAdmin);
        }
        else if (authEnabled)
        {
            _logger.LogWarning(
                "Role enforcement disabled (Tsak:Auth:EnforceRoles=false) — any valid API key may call any endpoint");
        }

        // 6. Add API route
        var listenUri = $"http:{host}:{port}/{{**path}}?host={host}&port={port}&inOut=true";
        var actionFilters = _serviceProvider.GetService<IEnumerable<IControllerActionFilter>>()
                            ?? Enumerable.Empty<IControllerActionFilter>();
        var dispatcher = new ControllerDispatcherProcessor(controllerRegistry, routeContext, actionFilters);
        routeContext.AddRoutes(r =>
        {
            r.From(listenUri).Process(async (exchange, ct) =>
            {
                BridgeHttpHeaders(exchange);

                if (apiKeyService is not null && !IsAuthExempt(exchange, exemptPatterns))
                {
                    // Use throttle gate when path matches the configured auth prefix.
                    var path = exchange.In.Headers.TryGetValue(HttpHeaders.Path, out var p)
                        ? p?.ToString() : null;
                    if (authThrottle is not null && path is not null
                        && path.StartsWith(authThrottlePathPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        await authThrottle.Process(exchange, ct);
                    }
                    else
                    {
                        var authorizer = new AuthorizeProcessor(apiKeyService);
                        await authorizer.Process(exchange, ct);
                    }

                    // Role check for the authenticated caller. Unreachable for auth-exempt
                    // technical endpoints — they never enter this block.
                    if (!exchange.IsStopped && roleAuthorizer is not null)
                        await roleAuthorizer.Process(exchange, ct);
                }

                if (!exchange.IsStopped)
                    await dispatcher.Process(exchange, ct);

                PrepareHttpResponse(exchange);
            });
        });

        // 6a. Echo route — auth-exempt liveness/debug probe. Reflects the request back as
        //     JSON ("am I reaching the host, and what did it receive?"). It shares the main
        //     API port: its concrete path (default /api/echo) now out-ranks the catch-all
        //     `{**path}` dispatcher thanks to SharedHttpServerManager's specificity ordering,
        //     so no separate port is needed. Ships with AutoStart(false): it stays stopped
        //     until started by hand from the Routes API / dashboard, and while running its own
        //     Process pipeline (no AuthorizeProcessor) answers it without an API key.
        var echoPath = _configuration.GetValue("Tsak:Api:Echo:Path", "/api/echo");
        var echoUri = $"http:{host}:{port}{echoPath}?host={host}&port={port}&inOut=true";
        routeContext.AddRoutes(r =>
        {
            r.From(echoUri)
                .RouteId("system-echo")
                .AutoStart(false)
                .Process((exchange, _) =>
                {
                    var echo = BuildEcho(exchange);
                    exchange.Out ??= exchange.In.Clone();
                    exchange.Out.Body = JsonSerializer.SerializeToUtf8Bytes(echo, JsonOptions);
                    exchange.Out.Headers["Content-Type"] = "application/json";
                    exchange.Out.Headers["status.code"] = 200;
                    exchange.Out.Headers[HttpHeaders.ResponseCode] = 200;
                    return Task.CompletedTask;
                });
        });
        _logger.LogInformation(
            "Echo route 'system-echo' registered on http://{Host}:{Port}{Path} (AutoStart=false, no auth)",
            host, port, echoPath);

        // 6b. Prometheus /metrics — exposes the loopback OTel scrape listener through the
        //     facade's Kestrel server (the Api port). No separate public port and no Windows
        //     URL ACL: the OTel listener is loopback-only, and this route rides Kestrel which
        //     binds sockets directly. Auth-exempt (own pipeline) and out-ranks the catch-all by
        //     path specificity. Mounted only when the Prometheus exporter is enabled.
        if (_configuration.GetValue("Tsak:Metrics:Prometheus:Enabled", false))
        {
            var metricsPort = _configuration.GetValue("Tsak:Metrics:Prometheus:Port", 9464);
            var scrapeUrl = $"http://localhost:{metricsPort}/metrics";
            var metricsUri = $"http:{host}:{port}/metrics?host={host}&port={port}&inOut=true";
            routeContext.AddRoutes(r =>
            {
                r.From(metricsUri)
                    .RouteId("system-metrics")
                    .Process(async (exchange, ct) =>
                    {
                        exchange.Out ??= exchange.In.Clone();
                        try
                        {
                            using var resp = await MetricsClient.GetAsync(scrapeUrl, ct);
                            exchange.Out.Body = await resp.Content.ReadAsByteArrayAsync(ct);
                            // The HTTP consumer reads the response content type from
                            // redbHttp.ResponseContentType (a plain "Content-Type" header is
                            // ignored and it would otherwise default to application/json, which
                            // Prometheus rejects). Pass the OTel exposition content type through
                            // verbatim — it carries the `version=0.0.4` Prometheus needs.
                            exchange.Out.Headers[HttpHeaders.ResponseContentType] =
                                resp.Content.Headers.ContentType?.ToString() ?? "text/plain; version=0.0.4; charset=utf-8";
                            exchange.Out.Headers["status.code"] = (int)resp.StatusCode;
                            exchange.Out.Headers[HttpHeaders.ResponseCode] = (int)resp.StatusCode;
                        }
                        catch (Exception ex)
                        {
                            // OTel listener not up (e.g. loopback bind was skipped) — report 503,
                            // never throw out of the scrape route.
                            exchange.Out.Body = System.Text.Encoding.UTF8.GetBytes($"# metrics unavailable: {ex.Message}\n");
                            exchange.Out.Headers[HttpHeaders.ResponseContentType] = "text/plain; charset=utf-8";
                            exchange.Out.Headers["status.code"] = 503;
                            exchange.Out.Headers[HttpHeaders.ResponseCode] = 503;
                        }
                    });
            });
            _logger.LogInformation(
                "Prometheus metrics exposed via facade http://{Host}:{Port}/metrics (proxying loopback :{MetricsPort}, no auth)",
                host, port, metricsPort);
        }

        // 6c. Admin audit writer route — direct://tsak-audit → INSERT into tsak_audit_log.
        //     Mounted only when a redb provider is configured and audit persistence is enabled;
        //     without a database the RouteAdminAuditService is never attached and the log sink
        //     keeps handling audit events.
        var auditProvider = ConfigureAuditRoute(routeContext);

        // 6d. Retention sweeps as first-class cron routes (Routes API/dashboard + scheduler page),
        //     replacing the bare Quartz jobs. Must be added before Start so the cron consumers wire up.
        ConfigureRetentionRoutes(routeContext, auditProvider);

        // 7. Start
        await context.Start(ct);
        _logger.LogInformation("REST API started on http://{Host}:{Port}", host, port);

        // 7a. Attach the persistent audit sink now that the writer route is live. Attach is a
        //     no-op when no provider is configured.
        if (auditProvider != AuditProvider.None
            && _serviceProvider.GetService<IAdminAuditService>() is RouteAdminAuditService routeAudit)
        {
            routeAudit.Attach(routeContext);
        }

        // 7b. Attach the generic broker alert channel (kafka:/amqp:/… from Tsak:Watchdog:Alerts:Endpoint).
        //     No-op unless enabled; the target component must be registered on this context by the host.
        _serviceProvider.GetService<Monitoring.Alerts.AlertDispatcher>()?.AttachEndpointChannel(routeContext);

        return context;
    }

    /// <summary>
    /// Adds the SQL component, registers the audit data source in the context registry, and
    /// mounts the <c>direct://tsak-audit</c> writer route. Returns the resolved provider, or
    /// <see cref="AuditProvider.None"/> when audit persistence is off or no database is
    /// configured — in which case nothing is mounted.
    /// </summary>
    private AuditProvider ConfigureAuditRoute(RouteContext routeContext)
    {
        if (!_configuration.GetValue("Tsak:Audit:Enabled", true))
            return AuditProvider.None;

        var provider = AuditStorage.ResolveProvider(_configuration);
        if (provider == AuditProvider.None)
            return AuditProvider.None;

        var connStr = AuditStorage.ResolveConnectionString(_configuration, provider);
        if (string.IsNullOrEmpty(connStr))
        {
            _logger.LogWarning(
                "Audit provider '{Provider}' configured but connection string missing — audit stays on the log sink",
                provider);
            return AuditProvider.None;
        }

        try
        {
            routeContext.AddComponent(new SqlComponent());

            // Register the ADO provider factory (idempotent) and publish the data source.
            var invariant = AuditStorage.ProviderInvariantName(provider);
            if (!DbProviderFactories.GetProviderInvariantNames().Contains(invariant))
                DbProviderFactories.RegisterFactory(invariant, AuditStorage.ProviderFactory(provider));

            routeContext.AddToRegistry(AuditStorage.DataSourceName, (ISqlConnectionFactory)new SqlConnectionFactory(
                new SqlConnectionOptions
                {
                    ConnectionString = connStr,
                    ProviderName = invariant,
                    ProviderFactory = AuditStorage.ProviderFactory(provider)
                }));

            routeContext.AddRoutes(new TsakAuditRouteBuilder(provider));

            _logger.LogInformation("Admin audit writer route mounted on {Endpoint} ({Provider})",
                AuditStorage.AuditEndpoint, provider);
            return provider;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mount admin audit route — falling back to the log sink");
            return AuditProvider.None;
        }
    }

    /// <summary>
    /// Mounts the retention sweeps as <c>cron://</c> routes on the <c>_system</c> context (replacing the
    /// former bare Quartz jobs) — gated exactly as before: audit needs an active provider + a positive
    /// <c>Tsak:Audit:RetentionDays</c>; DLQ needs to be available + a positive <c>Tsak:Dlq:RetentionDays</c>.
    /// The Cron component is added only when at least one sweep is registered; the shared scheduler
    /// (injected into every Tsak context) is ensured so the cron jobs show on the scheduler page too.
    /// </summary>
    private void ConfigureRetentionRoutes(RouteContext routeContext, AuditProvider auditProvider)
    {
        var config = _serviceProvider.GetService<IConfiguration>();
        var loggerFactory = _serviceProvider.GetService<ILoggerFactory>();

        var audit = _serviceProvider.GetService<AuditQueryService>();
        var auditDays = config?.GetValue("Tsak:Audit:RetentionDays", AuditRetentionRouteBuilder.DefaultRetentionDays)
                        ?? AuditRetentionRouteBuilder.DefaultRetentionDays;
        var auditOn = auditProvider != AuditProvider.None && audit is { IsAvailable: true } && auditDays > 0;

        var dlq = _serviceProvider.GetService<DlqService>();
        var dlqDays = config?.GetValue("Tsak:Dlq:RetentionDays", DlqRetentionRouteBuilder.DefaultRetentionDays)
                      ?? DlqRetentionRouteBuilder.DefaultRetentionDays;
        var dlqOn = dlq is { IsAvailable: true } && dlqDays > 0;

        if (!auditOn && !dlqOn)
            return;

        // cron:// consumers resolve IScheduler from the context; ensure it is the shared Tsak scheduler
        // so these sweeps land on the same scheduler the dashboard reads (visible on the scheduler page).
        routeContext.AddComponent(new CronComponent());
        if (_serviceProvider.GetService<global::Quartz.IScheduler>() is { } scheduler)
            routeContext.AddService(typeof(global::Quartz.IScheduler), scheduler);

        if (auditOn)
        {
            routeContext.AddRoutes(new AuditRetentionRouteBuilder(
                audit!, auditDays, loggerFactory?.CreateLogger<AuditRetentionRouteBuilder>()));
            _logger.LogInformation("Audit retention route mounted (cron://{Id}, {Days}d)",
                AuditRetentionRouteBuilder.RouteIdName, auditDays);
        }

        if (dlqOn)
        {
            routeContext.AddRoutes(new DlqRetentionRouteBuilder(
                dlq!, dlqDays, loggerFactory?.CreateLogger<DlqRetentionRouteBuilder>()));
            _logger.LogInformation("DLQ retention route mounted (cron://{Id}, {Days}d)",
                DlqRetentionRouteBuilder.RouteIdName, dlqDays);
        }
    }

    /// <summary>
    /// Bridges redbHttp.* headers to route.* headers expected by ControllerDispatcherProcessor.
    /// </summary>
    internal static void BridgeHttpHeaders(IExchange exchange)
    {
        var headers = exchange.In.Headers;
        if (headers.TryGetValue(HttpHeaders.Path, out var path))
            headers[ControllerDispatcherProcessor.PathHeader] = path;
        if (headers.TryGetValue(HttpHeaders.Method, out var method))
            headers[ControllerDispatcherProcessor.MethodHeader] = method;

        // Bridge query parameters: redbHttp.QueryParam.* → query.*
        const string httpPrefix = "redbHttp.QueryParam.";
        foreach (var key in headers.Keys.Where(k => k.StartsWith(httpPrefix)).ToList())
            headers[$"query.{key[httpPrefix.Length..]}"] = headers[key];
    }

    /// <summary>
    /// Checks if the request path is exempt from authentication.
    /// Patterns are read from <c>Tsak:Api:AuthExempt</c> (default: K8s health probes
    /// <c>/api/health/*</c> and <c>/api/health</c>). A trailing <c>*</c> means prefix
    /// match; otherwise exact case-insensitive match.
    /// </summary>
    internal static bool IsAuthExempt(IExchange exchange, IReadOnlyList<string> patterns)
    {
        var path = exchange.In.Headers.TryGetValue(HttpHeaders.Path, out var p)
            ? p?.ToString() : null;

        if (path is null || patterns.Count == 0) return false;

        foreach (var raw in patterns)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (raw.EndsWith('*'))
            {
                var prefix = raw.AsSpan(0, raw.Length - 1);
                if (path.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (path.Equals(raw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Serializes response body to JSON and maps status.code → redbHttp.ResponseCode
    /// so that the HTTP transport returns proper status codes and JSON content.
    /// </summary>
    internal static void PrepareHttpResponse(IExchange exchange)
    {
        if (exchange.Out is null) return;

        // ControllerDispatcherProcessor.WriteResult overwrites status.code to 204 for null
        // results, even when the controller already set a different code via ApiResponse.
        // Restore the intended status from ControllerErrorResponse if present.
        var body = exchange.Out.Body;
        if (body is ControllerErrorResponse errorResp)
            exchange.Out.Headers["status.code"] = errorResp.StatusCode;

        // Map status.code → redbHttp.ResponseCode for HTTP transport
        if (exchange.Out.Headers.TryGetValue("status.code", out var sc))
            exchange.Out.Headers[HttpHeaders.ResponseCode] = sc;

        // Serialize non-primitive bodies to JSON bytes
        if (body is not null && body is not byte[] && body is not string && body is not Stream)
            exchange.Out.Body = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
    }

    /// <summary>
    /// Builds the echo payload for the <c>system-echo</c> route: reflects the incoming HTTP
    /// request (method, path, query, content-type, remote address, body) back to the caller
    /// so an operator can confirm the host is reachable and see exactly what it received.
    /// </summary>
    internal static object BuildEcho(IExchange exchange)
    {
        var headers = exchange.In.Headers;
        string? H(string key) => headers.TryGetValue(key, out var v) ? v?.ToString() : null;

        // Parsed query params surface as redbHttp.QueryParam.{name}.
        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in headers)
        {
            if (kv.Key.StartsWith(HttpHeaders.QueryParamPrefix, StringComparison.Ordinal))
                query[kv.Key[HttpHeaders.QueryParamPrefix.Length..]] = kv.Value?.ToString();
        }

        // POST bodies arrive as byte[]; surface them as a string so the echo is readable.
        var body = exchange.In.Body switch
        {
            null => null,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            string s => s,
            var x => x.ToString()
        };

        return new
        {
            status = "alive",
            method = H(HttpHeaders.Method),
            path = H(HttpHeaders.Path),
            url = H(HttpHeaders.Url),
            queryString = H(HttpHeaders.Query),
            query,
            contentType = H(HttpHeaders.ContentType),
            remoteAddress = H(HttpHeaders.RemoteAddress),
            body,
            receivedAt = DateTimeOffset.UtcNow
        };
    }

    private void RegisterServices(RouteContext context, ITsakModuleRegistry registry, bool authEnabled)
    {
        context.AddService(typeof(ITsakContextManager), _contextManager);
        context.AddService(typeof(ITsakModuleRegistry), registry);
        context.AddService(typeof(IConfiguration), _configuration); // for /api/system/config

        // Optional services — register if available in DI
        TryRegisterService<HealthCheckService>(context);
        TryRegisterService<MetricsService>(context);
        TryRegisterService<LogRingBuffer>(context);
        TryRegisterService<ContextInfoCollector>(context);
        TryRegisterService<RouteWatchdogService>(context);
        TryRegisterService<LifecycleAuditService>(context);
        TryRegisterService<ITsakStateStore>(context);
        TryRegisterService<AuditQueryService>(context);
        TryRegisterService<Monitoring.Alerts.AlertDispatcher>(context);
        TryRegisterService<Modules.ModuleUploadService>(context);
        TryRegisterService<Dlq.DlqService>(context);
    }

    private void TryRegisterService<T>(RouteContext context) where T : class
    {
        var service = _serviceProvider.GetService(typeof(T)) as T;
        if (service is not null)
            context.AddService(typeof(T), service);
    }

    private ApiKeyService CreateApiKeyService()
    {
        var secret = _configuration["Tsak:Auth:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "Tsak:Auth:Secret must be configured when auth is enabled. Minimum 16 characters.");

        var store = _serviceProvider.GetService<IApiKeyStore>() ?? new ConfigApiKeyStore(_configuration);
        var userProvider = _serviceProvider.GetService<redb.Core.Providers.IUserProvider>();
        return new ApiKeyService(store, System.Text.Encoding.UTF8.GetBytes(secret), userProvider);
    }
}
