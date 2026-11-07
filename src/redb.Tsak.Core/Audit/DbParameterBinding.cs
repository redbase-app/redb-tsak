using System.Data;
using System.Data.Common;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// Binds parameters for the raw ADO.NET statements of the audit log and the dead-letter store with
/// an explicit <see cref="DbType"/> on every one of them, null or not.
/// </summary>
/// <remarks>
/// <para>
/// A parameter whose value is <see cref="DBNull.Value"/> and whose type was never set is sent to
/// PostgreSQL as type <c>unknown</c>. The statements here spell every optional filter as
/// <c>(@x IS NULL OR col = @x)</c>, and the parser fixes the type of the first <c>@x</c> it meets,
/// the one in <c>IS NULL</c>, before the comparison further right can tell it what the type is.
/// The two occurrences then disagree and PostgreSQL refuses the statement with
/// <c>42P08 could not determine data type of parameter $1</c>. That is what the dashboard's Audit and
/// Dead-letter pages showed on Postgres for every request without a filter, which is every first
/// request. SQLite and SQL Server never noticed: neither infers parameter types from the statement.
/// </para>
/// <para>
/// Timestamps are bound as the type the column holds, which the provider's
/// <see cref="TsakSqlDialect.BindTimestamp"/> decides: a UTC <see cref="DateTimeOffset"/> for
/// PostgreSQL (<c>timestamptz</c>) and SQL Server (<c>datetimeoffset</c>), where a text parameter has
/// no comparison operator against the column at all, and an ISO-8601 <c>"o"</c> string for SQLite,
/// whose column is TEXT and orders lexicographically, which is chronological for that format and is
/// how the rows were written.
/// </para>
/// </remarks>
internal static class DbParameterBinding
{
    /// <summary>Binds a text parameter; null becomes a typed <see cref="DBNull"/>.</summary>
    public static void AddString(DbCommand cmd, string name, string? value)
        => Add(cmd, name, DbType.String, value);

    /// <summary>Binds a 32-bit integer.</summary>
    public static void AddInt32(DbCommand cmd, string name, int value)
        => Add(cmd, name, DbType.Int32, value);

    /// <summary>Binds a boolean.</summary>
    public static void AddBoolean(DbCommand cmd, string name, bool value)
        => Add(cmd, name, DbType.Boolean, value);

    /// <summary>
    /// Binds a timestamp in the form the provider's column expects. See the class remarks for why
    /// the form differs per provider.
    /// </summary>
    public static void AddTimestamp(DbCommand cmd, AuditProvider provider, string name, DateTimeOffset? value)
        => TsakSqlDialect.For(provider).BindTimestamp(cmd, name, value);

    /// <summary>Binds a parameter of an explicit type; null becomes a typed <see cref="DBNull"/>.</summary>
    public static void Add(DbCommand cmd, string name, DbType type, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
