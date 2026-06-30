using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Configuration;
using redb.Core.Extensions;
using redb.Core.Models.Configuration;
using redb.Core.Pro.Extensions;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Creates isolated <see cref="IRedbService"/> instances from config dictionaries.
/// Each instance lives in its own mini-DI container (ServiceProvider) that the caller must dispose.
/// </summary>
internal static class RedbInstanceFactory
{
    /// <summary>
    /// Creates a named IRedbService from a config dictionary.
    /// </summary>
    /// <param name="name">Instance name (for logging).</param>
    /// <param name="config">Config dictionary (Provider, ConnectionString, UsePro, License, etc.).</param>
    /// <param name="fallbackLicense">Fallback license from Tsak:Redb:License if not specified per instance.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>Tuple of the service and its mini-container (caller must dispose container).</returns>
    public static (IRedbService Service, ServiceProvider Container) Create(
        string name,
        IDictionary<string, object?> config,
        string? fallbackLicense,
        ILogger? logger = null)
    {
        var provider = GetString(config, "Provider");
        var connectionString = GetString(config, "ConnectionString");

        if (string.IsNullOrEmpty(provider))
            throw new InvalidOperationException($"Named redb '{name}': 'Provider' is required (postgres, mssql or sqlite).");
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException($"Named redb '{name}': 'ConnectionString' is required.");

        var usePro = GetBool(config, "UsePro");
        var license = GetString(config, "License") ?? fallbackLicense;

        var services = new ServiceCollection();

        if (usePro)
        {
            services.AddRedbPro(options =>
            {
                ConfigureProvider(options, provider, connectionString, name, isPro: true);
                if (!string.IsNullOrEmpty(license))
                    options.WithLicense(license);
                options.Configure(c => ApplyConfig(c, config));
            });
        }
        else
        {
            services.AddRedb(options =>
            {
                ConfigureProvider(options, provider, connectionString, name, isPro: false);
                options.Configure(c => ApplyConfig(c, config));
            });
        }

        // Add minimal logging so internal services can resolve ILogger<T>
        services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));

        var container = services.BuildServiceProvider();

        var redbService = container.GetRequiredService<IRedbService>();

        // RedbInitHostedService won't auto-run (no IHost). If EnsureCreated, init manually.
        var ensureCreated = GetBool(config, "EnsureCreated");
        if (ensureCreated)
        {
            try
            {
                redbService.InitializeAsync(ensureCreated: true).GetAwaiter().GetResult();
                logger?.LogInformation("Named redb '{Name}': schema initialized (EnsureCreated=true)", name);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Named redb '{Name}': failed to ensure schema", name);
            }
        }

        logger?.LogInformation(
            "Created named IRedbService '{Name}' (provider={Provider}, pro={Pro})",
            name, provider, usePro);

        return (redbService, container);
    }

    private static void ConfigureProvider(RedbOptionsBuilder options, string provider, string connectionString, string name, bool isPro)
    {
        // Use explicit namespace-qualified calls to avoid ambiguity between OS and Pro extension methods
        var infrastructure = (IRedbOptionsBuilderInfrastructure)options;
        if (!string.IsNullOrEmpty(connectionString))
            infrastructure.Configuration.ConnectionString = connectionString;

        switch (provider.ToLowerInvariant())
        {
            case "postgres" or "postgresql" or "npgsql":
                if (isPro)
                    redb.Postgres.Pro.Extensions.PostgresProOptionsExtensions.UsePostgres(options, connectionString);
                else
                    redb.Postgres.Extensions.PostgresOptionsExtensions.UsePostgres(options, connectionString);
                break;
            case "mssql" or "sqlserver":
                if (isPro)
                    redb.MSSql.Pro.Extensions.MsSqlProOptionsExtensions.UseMsSql(options, connectionString);
                else
                    redb.MSSql.Extensions.MsSqlOptionsExtensions.UseMsSql(options, connectionString);
                break;
            case "sqlite":
                // redb.SQLite.Pro.Extensions.UseSqlite is tier-agnostic — it dispatches
                // on AddRedb (Free) vs AddRedbPro (Pro) at registration time, so the same
                // call works for both branches.
                redb.SQLite.Pro.Extensions.SqliteProOptionsExtensions.UseSqlite(options, connectionString);
                break;
            default:
                throw new InvalidOperationException(
                    $"Named redb '{name}': unsupported provider '{provider}'. Use 'postgres', 'mssql' or 'sqlite'.");
        }
    }

    private static void ApplyConfig(RedbServiceConfiguration c, IDictionary<string, object?> config)
    {
        // EnsureCreated defaults to false for named instances (schema assumed to exist)
        c.EnsureCreated = GetBool(config, "EnsureCreated");

        if (TryGetString(config, "PropsSaveStrategy", out var strategy))
            c.PropsSaveStrategy = Enum.Parse<PropsSaveStrategy>(strategy, ignoreCase: true);

        if (TryGetBool(config, "EnableLazyLoadingForProps", out var lazy))
            c.EnableLazyLoadingForProps = lazy;

        if (TryGetBool(config, "EnablePropsCache", out var propsCache))
            c.EnablePropsCache = propsCache;

        if (TryGetInt(config, "PropsCacheMaxSize", out var pcMax))
            c.PropsCacheMaxSize = pcMax;

        if (TryGetInt(config, "PropsCacheTtlMinutes", out var pcTtl))
            c.PropsCacheTtl = TimeSpan.FromMinutes(pcTtl);

        if (TryGetBool(config, "SkipHashValidationOnCacheCheck", out var skipHash))
            c.SkipHashValidationOnCacheCheck = skipHash;

        if (TryGetBool(config, "EnableListCache", out var listCache))
            c.EnableListCache = listCache;

        if (TryGetInt(config, "ListCacheTtlMinutes", out var lcTtl))
            c.ListCacheTtl = TimeSpan.FromMinutes(lcTtl);

        if (TryGetBool(config, "EnableMetadataCache", out var metaCache))
            c.EnableMetadataCache = metaCache;

        if (TryGetInt(config, "MetadataCacheLifetimeMinutes", out var metaTtl))
            c.MetadataCacheLifetimeMinutes = metaTtl;

        if (TryGetBool(config, "WarmupMetadataCacheOnInit", out var warmup))
            c.WarmupMetadataCacheOnInit = warmup;

        if (TryGetInt(config, "DefaultLoadDepth", out var loadDepth))
            c.DefaultLoadDepth = loadDepth;

        if (TryGetInt(config, "DefaultMaxTreeDepth", out var treeDepth))
            c.DefaultMaxTreeDepth = treeDepth;

        if (TryGetBool(config, "ThrowOnObjectNotFound", out var throwNotFound))
            c.ThrowOnObjectNotFound = throwNotFound;

        if (TryGetBool(config, "AutoSyncSchemesOnSave", out var autoSync))
            c.AutoSyncSchemesOnSave = autoSync;

        if (TryGetBool(config, "EnableSchemaValidation", out var schemaVal))
            c.EnableSchemaValidation = schemaVal;

        if (TryGetBool(config, "EnableDataValidation", out var dataVal))
            c.EnableDataValidation = dataVal;

        if (TryGetString(config, "CacheDomain", out var cacheDomain))
            c.CacheDomain = cacheDomain;
    }

    // ── Config helpers ───────────────────────────────────────────────

    private static string? GetString(IDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static bool GetBool(IDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is true or "true" or "True" or "TRUE";

    private static bool TryGetString(IDictionary<string, object?> d, string key, out string value)
    {
        if (d.TryGetValue(key, out var v) && v != null)
        {
            value = v.ToString()!;
            return true;
        }
        value = default!;
        return false;
    }

    private static bool TryGetBool(IDictionary<string, object?> d, string key, out bool value)
    {
        if (d.TryGetValue(key, out var v))
        {
            value = v is true or "true" or "True" or "TRUE";
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryGetInt(IDictionary<string, object?> d, string key, out int value)
    {
        if (d.TryGetValue(key, out var v))
        {
            if (v is int i) { value = i; return true; }
            if (v is long l) { value = (int)l; return true; }
            if (v != null && int.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }
        }
        value = default;
        return false;
    }
}
