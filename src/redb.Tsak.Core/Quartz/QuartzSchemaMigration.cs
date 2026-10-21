using System.Data.Common;
using Microsoft.Extensions.Logging;
using redb.Tsak.Core.Audit;

namespace redb.Tsak.Core.Quartz;

/// <summary>
/// Brings the Quartz tables of an existing installation up to the columns Quartz.NET 3.22 probes for at start.
/// <c>CREATE TABLE IF NOT EXISTS</c> leaves an existing table as it is, so a database created before 3.17 lacks them,
/// and Quartz then logs a warning and runs without what they carry:
/// <list type="bullet">
///   <item>3.17 <c>MISFIRE_ORIG_FIRE_TIME</c>: the time a misfired trigger was due, so <c>ScheduledFireTimeUtc</c>
///   (the route's <c>CamelQuartzScheduledFireTime</c>) stays that time under the fire-now misfire policies instead of
///   becoming the actual fire time.</item>
///   <item>3.18 <c>EXECUTION_GROUP</c> on triggers and fired triggers: the per-node limit of execution groups is
///   applied in the acquire query instead of by filtering in memory.</item>
///   <item>3.19 <c>PREFERRED_NODE</c> and <c>PREFERRED_NODE_AUTO</c>: node affinity, only with both.</item>
/// </list>
/// The 3.20 index alignment drops and creates indexes and is left to the administrator's script.
/// <para>
/// Every column is looked up again after the lock is taken, so nodes starting together add each column once. A role
/// that may not ALTER the tables gets a warning naming the administrator's script and the worker goes on without the
/// columns; any other failure propagates.
/// </para>
/// </summary>
internal abstract class QuartzSchemaMigration
{
    /// <summary>A column a Quartz release added to an existing table.</summary>
    internal sealed record Column(string Table, string Name, string Since, string PostgresType, string SqlServerType, string SqliteType);

    /// <summary>The columns added at start, in the order Quartz introduced them.</summary>
    internal static readonly IReadOnlyList<Column> Columns =
    [
        new("QRTZ_TRIGGERS", "MISFIRE_ORIG_FIRE_TIME", "3.17", "BIGINT NULL", "bigint NULL", "INTEGER NULL"),
        new("QRTZ_TRIGGERS", "EXECUTION_GROUP", "3.18", "VARCHAR(200) NULL", "nvarchar(200) NULL", "NVARCHAR(200) NULL"),
        new("QRTZ_FIRED_TRIGGERS", "EXECUTION_GROUP", "3.18", "VARCHAR(200) NULL", "nvarchar(200) NULL", "NVARCHAR(200) NULL"),
        new("QRTZ_TRIGGERS", "PREFERRED_NODE", "3.19", "VARCHAR(200) NULL", "nvarchar(200) NULL", "NVARCHAR(200) NULL"),
        new("QRTZ_TRIGGERS", "PREFERRED_NODE_AUTO", "3.19", "BOOL NOT NULL DEFAULT FALSE", "bit NOT NULL DEFAULT 0", "BIT NOT NULL DEFAULT 0"),
    ];

    internal static QuartzSchemaMigration For(AuditProvider provider) => provider switch
    {
        AuditProvider.SqlServer => new SqlServer(),
        AuditProvider.Sqlite => new Sqlite(),
        _ => new Postgres(),
    };

    /// <summary>File name of the administrator's script, shipped beside the worker under <c>sql/quartz/</c>.</summary>
    internal abstract string ScriptName { get; }

    /// <summary>Where the administrator's script lies in this installation.</summary>
    internal string ScriptPath => Path.Combine(AppContext.BaseDirectory, "sql", "quartz", ScriptName);

    /// <summary>
    /// Takes the lock that serializes the schema work of nodes starting together. The returned transaction, when
    /// there is one, is the lock itself (SQLite) and every command of the migration runs in it.
    /// </summary>
    internal abstract Task<DbTransaction?> LockAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Releases what <see cref="LockAsync"/> took; a lock that is a transaction commits only when the work succeeded.</summary>
    internal abstract Task UnlockAsync(DbConnection connection, DbTransaction? transaction, bool succeeded);

    internal abstract string ColumnExistsSql(Column column);

    internal abstract string AddColumnSql(Column column);

    /// <summary>True when the role may not alter the table: the only failure the migration does not propagate.</summary>
    internal abstract bool IsPermissionDenied(Exception exception);

    /// <summary>
    /// Adds the columns the tables lack. Run under the lock, after the create script. Returns the columns added; when
    /// the role may not alter the tables, logs a warning, stops and returns what was added before.
    /// </summary>
    internal async Task<IReadOnlyList<Column>> AddMissingColumnsAsync(
        DbConnection connection, DbTransaction? transaction, ILogger logger, CancellationToken ct)
    {
        var added = new List<Column>();
        for (var i = 0; i < Columns.Count; i++)
        {
            var column = Columns[i];
            if (await ExistsAsync(connection, transaction, column, ct).ConfigureAwait(false))
                continue;

            try
            {
                await ExecuteAsync(connection, transaction, AddColumnSql(column), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPermissionDenied(ex))
            {
                var missing = Columns.Skip(i).Select(c => $"{c.Table}.{c.Name} ({c.Since})");
                logger.LogWarning(ex,
                    "Quartz schema: the database role may not alter the Quartz tables, so {Columns} stay missing and "
                    + "Quartz runs without them. Have the administrator run {Script}",
                    string.Join(", ", missing), ScriptPath);
                return added;
            }

            added.Add(column);
            logger.LogInformation("Quartz schema: added {Table}.{Column} (Quartz {Since})", column.Table, column.Name, column.Since);
        }

        return added;
    }

    private async Task<bool> ExistsAsync(DbConnection connection, DbTransaction? transaction, Column column, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = ColumnExistsSql(column);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null and not DBNull && Convert.ToInt64(result) > 0;
    }

    internal static async Task ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed class Postgres : QuartzSchemaMigration
    {
        // pg_advisory_lock key of the Quartz schema work: "redb_qrz" in ASCII.
        private const long LockKey = 0x726564625F71727A;

        internal override string ScriptName => "quartz_3.22_postgres.sql";

        internal override async Task<DbTransaction?> LockAsync(DbConnection connection, CancellationToken ct)
        {
            await ExecuteAsync(connection, null, $"SELECT pg_advisory_lock({LockKey})", ct).ConfigureAwait(false);
            return null;
        }

        internal override Task UnlockAsync(DbConnection connection, DbTransaction? transaction, bool succeeded)
            => ExecuteAsync(connection, null, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);

        // Tsak creates the tables unqualified, so they are in current_schema(); a namesake in another schema is not ours.
        internal override string ColumnExistsSql(Column column) =>
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() "
            + $"AND table_name = '{column.Table.ToLowerInvariant()}' AND column_name = '{column.Name.ToLowerInvariant()}'";

        internal override string AddColumnSql(Column column) =>
            $"ALTER TABLE {column.Table.ToLowerInvariant()} ADD COLUMN {column.Name.ToLowerInvariant()} {column.PostgresType}";

        // 42501 insufficient_privilege: also what ALTER TABLE answers to a role that does not own the table.
        internal override bool IsPermissionDenied(Exception exception)
            => exception is Npgsql.PostgresException { SqlState: "42501" };
    }

    private sealed class SqlServer : QuartzSchemaMigration
    {
        private const string LockResource = "redb.tsak.quartz.schema";

        internal override string ScriptName => "quartz_3.22_sqlServer.sql";

        internal override async Task<DbTransaction?> LockAsync(DbConnection connection, CancellationToken ct)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "DECLARE @result int; "
                + $"EXEC @result = sp_getapplock @Resource = N'{LockResource}', @LockMode = 'Exclusive', "
                + "@LockOwner = 'Session', @LockTimeout = 120000; SELECT @result;";
            var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (result < 0)
                throw new TimeoutException(
                    $"Quartz schema: sp_getapplock on '{LockResource}' returned {result}; another node holds the schema lock.");
            return null;
        }

        internal override Task UnlockAsync(DbConnection connection, DbTransaction? transaction, bool succeeded)
            => ExecuteAsync(connection, null,
                $"EXEC sp_releaseapplock @Resource = N'{LockResource}', @LockOwner = 'Session'", CancellationToken.None);

        internal override string ColumnExistsSql(Column column) =>
            $"SELECT CASE WHEN COL_LENGTH('dbo.{column.Table}', '{column.Name}') IS NULL THEN 0 ELSE 1 END";

        internal override string AddColumnSql(Column column) =>
            $"ALTER TABLE [dbo].[{column.Table}] ADD [{column.Name}] {column.SqlServerType}";

        internal override bool IsPermissionDenied(Exception exception)
            => exception is Microsoft.Data.SqlClient.SqlException sql && IsPermissionDeniedNumber(sql.Number);

        // 229: permission denied on the object; 4902: ALTER TABLE on an object the role cannot see or may not alter.
        internal static bool IsPermissionDeniedNumber(int number) => number is 229 or 4902;
    }

    private sealed class Sqlite : QuartzSchemaMigration
    {
        // SQLITE_READONLY: the database file or its directory is read-only to the worker.
        private const int ReadOnly = 8;

        internal override string ScriptName => "quartz_3.22_sqlite.sql";

        // One file, one writer: an immediate transaction is the lock.
        internal override Task<DbTransaction?> LockAsync(DbConnection connection, CancellationToken ct)
            => Task.FromResult<DbTransaction?>(((Microsoft.Data.Sqlite.SqliteConnection)connection).BeginTransaction(deferred: false));

        internal override async Task UnlockAsync(DbConnection connection, DbTransaction? transaction, bool succeeded)
        {
            if (transaction is null) return;
            if (succeeded)
                await transaction.CommitAsync().ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
        }

        internal override string ColumnExistsSql(Column column) =>
            $"SELECT count(*) FROM pragma_table_info('{column.Table}') WHERE name = '{column.Name}' COLLATE NOCASE";

        internal override string AddColumnSql(Column column) =>
            $"ALTER TABLE {column.Table} ADD COLUMN {column.Name} {column.SqliteType}";

        internal override bool IsPermissionDenied(Exception exception)
            => exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: ReadOnly };
    }
}
