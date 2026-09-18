using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using redb.Tsak.Core.Audit;

namespace redb.Tsak.Core.Quartz;

/// <summary>
/// Ensures Quartz.NET tables exist in the database when AdoJobStore is configured.
/// Registered as <see cref="IHostedService"/> BEFORE QuartzHostedService so that
/// tables are created before Quartz validates the schema.
/// Uses raw ADO.NET connection (not redb) because redb may not be initialized yet. The provider's
/// connection string, script and ADO factory come from <see cref="TsakSqlDialect.ForQuartz"/>.
/// </summary>
internal sealed class QuartzSchemaInitializer : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<QuartzSchemaInitializer> _logger;

    public QuartzSchemaInitializer(IConfiguration configuration, ILogger<QuartzSchemaInitializer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var jobStoreType = _configuration["Quartz:quartz.jobStore.type"];
        if (string.IsNullOrEmpty(jobStoreType) || !jobStoreType.Contains("AdoJobStore", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Quartz uses RAMJobStore — skipping schema initialization");
            return;
        }

        var dialect = TsakSqlDialect.ForQuartz(_configuration);
        var connStr = _configuration.GetConnectionString(dialect.ConnectionStringName);

        if (string.IsNullOrEmpty(connStr))
        {
            _logger.LogWarning("No connection string found for Quartz schema initialization");
            return;
        }
        var resourceName = $"QuartzSchema.{dialect.ScriptSuffix}";
        var sql = ReadEmbeddedScript(resourceName);
        if (sql is null)
        {
            _logger.LogWarning("Quartz schema script '{Resource}' not found in embedded resources", resourceName);
            return;
        }

        _logger.LogInformation("Applying Quartz schema ({Provider})...",
            _configuration["Tsak:Redb:Provider"]?.ToLowerInvariant() ?? "postgres");

        await using var conn = AuditStorage.CreateConnection(dialect.Provider, connStr);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("Quartz schema applied successfully");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string? ReadEmbeddedScript(string logicalName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(logicalName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
