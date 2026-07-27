using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using redb.Tsak.Core.Audit;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// Creates the <c>tsak_dlq</c> table on startup for whichever provider Tsak is configured with.
/// Same shape as <c>AuditSchemaInitializer</c>/<c>QuartzSchemaInitializer</c>: raw ADO.NET, provider
/// switch over <c>Tsak:Redb:Provider</c>, one embedded idempotent script per dialect. No-ops when the
/// DLQ is disabled or the node runs without a database.
/// </summary>
internal sealed class DlqSchemaInitializer : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DlqSchemaInitializer> _logger;

    public DlqSchemaInitializer(IConfiguration configuration, ILogger<DlqSchemaInitializer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Tsak:Dlq:Enabled", true))
        {
            _logger.LogDebug("DLQ disabled (Tsak:Dlq:Enabled=false)");
            return;
        }

        var provider = DlqStorage.ResolveProvider(_configuration);
        if (provider == AuditProvider.None)
        {
            _logger.LogInformation(
                "No redb provider configured — dead-letter store disabled (no {Table} table)", DlqStorage.TableName);
            return;
        }

        var connStr = DlqStorage.ResolveConnectionString(_configuration, provider);
        if (string.IsNullOrEmpty(connStr))
        {
            _logger.LogWarning(
                "Provider '{Provider}' configured but its connection string is missing — DLQ disabled", provider);
            return;
        }

        var sql = ReadEmbeddedScript(DlqStorage.ScriptResource(provider));
        if (sql is null)
        {
            _logger.LogWarning("DLQ schema script '{Resource}' not found in embedded resources",
                DlqStorage.ScriptResource(provider));
            return;
        }

        try
        {
            await using var conn = DlqStorage.CreateConnection(provider, connStr);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("DLQ table {Table} ensured ({Provider})", DlqStorage.TableName, provider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure DLQ table {Table} ({Provider})", DlqStorage.TableName, provider);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string? ReadEmbeddedScript(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
