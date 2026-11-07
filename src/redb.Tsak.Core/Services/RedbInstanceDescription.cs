using System.Security.Cryptography;
using System.Text;
using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Describes a redb instance for the storage page, capability flags included.
/// <para>
/// The flags are decided by the provider, not by whether a value came back null: SQLite has no schemas, no
/// usage counters and no record of when statistics were refreshed, so those columns and the per-table
/// analyze button are not drawn at all. Reading it the other way round — hiding a column because this run
/// returned nulls — would hide a column on PostgreSQL the moment a database happened to have no reads yet.
/// </para>
/// </summary>
internal static class RedbInstanceDescription
{
    /// <summary>Builds the description of one instance from its configured provider and connection string.</summary>
    internal static RedbInstanceInfo Describe(
        string name, string? contextName, string? provider, string? connectionString, bool usePro, bool isActive)
    {
        var normalized = (provider ?? "").Trim().ToLowerInvariant();

        // Anything that is not one of the two schema-bearing engines is treated as the poorest case: an
        // unknown provider gets no columns rather than empty ones.
        var rich = normalized is "postgres" or "postgresql" or "mssql" or "sqlserver";

        return new RedbInstanceInfo
        {
            Name = name,
            ContextName = contextName,
            Provider = normalized,
            UsePro = usePro,
            IsActive = isActive,
            Fingerprint = Fingerprint(normalized, connectionString),
            SupportsSchemas = rich,
            SupportsUsageCounters = rich,
            SupportsAnalyzeTimestamps = rich
        };
    }

    /// <summary>
    /// Identifies the database behind an instance without disclosing it: the first bytes of a hash over the
    /// provider and the connection string. Contexts that declare the same database get the same fingerprint,
    /// which is how the dashboard collapses fifteen rows of five contexts into the three databases they are.
    /// A connection string carries credentials and never leaves the node, so it is not sent instead.
    /// </summary>
    private static string Fingerprint(string provider, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(provider + "|" + connectionString));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
