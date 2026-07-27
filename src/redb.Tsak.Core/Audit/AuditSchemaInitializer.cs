using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// Creates the <c>tsak_audit_log</c> table on startup for whichever provider Tsak is
/// configured with. Structured exactly like <c>QuartzSchemaInitializer</c>: raw ADO.NET
/// (redb may not be initialized yet), provider switch over <c>Tsak:Redb:Provider</c>, and an
/// embedded, idempotent DDL script per dialect.
/// <para>
/// No-ops when audit persistence is disabled or when Tsak runs without a database — in that
/// mode <see cref="Security.LogAdminAuditService"/> remains the sink.
/// </para>
/// </summary>
internal sealed class AuditSchemaInitializer : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuditSchemaInitializer> _logger;

    public AuditSchemaInitializer(IConfiguration configuration, ILogger<AuditSchemaInitializer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Tsak:Audit:Enabled", true))
        {
            _logger.LogDebug("Admin audit persistence disabled (Tsak:Audit:Enabled=false)");
            return;
        }

        var provider = AuditStorage.ResolveProvider(_configuration);
        if (provider == AuditProvider.None)
        {
            _logger.LogInformation(
                "No redb provider configured — admin audit stays on the log sink (no {Table} table)",
                AuditStorage.TableName);
            return;
        }

        var connStr = AuditStorage.ResolveConnectionString(_configuration, provider);
        if (string.IsNullOrEmpty(connStr))
        {
            _logger.LogWarning(
                "Provider '{Provider}' is configured but its connection string is missing — " +
                "admin audit stays on the log sink", provider);
            return;
        }

        var sql = ReadEmbeddedScript(AuditStorage.ScriptResource(provider));
        if (sql is null)
        {
            _logger.LogWarning("Audit schema script '{Resource}' not found in embedded resources",
                AuditStorage.ScriptResource(provider));
            return;
        }

        try
        {
            await using var conn = AuditStorage.CreateConnection(provider, connStr);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Audit table {Table} ensured ({Provider})",
                AuditStorage.TableName, provider);
        }
        catch (Exception ex)
        {
            // A missing audit table must not stop the node from serving traffic: the writer
            // route will fail per-event and the failure is visible in the logs.
            _logger.LogError(ex, "Failed to ensure audit table {Table} ({Provider})",
                AuditStorage.TableName, provider);
        }
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
