using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// Everything that differs between PostgreSQL, SQL Server and SQLite for Tsak's own tables (the admin
/// audit log, the dead-letter store) and for the Quartz job-store wiring: connection string and ADO
/// provider, DDL scripts, the casts a provider needs to accept a text parameter, paging, how a timestamp
/// is bound and how a column is added to an existing table.
/// <para>
/// One sealed implementation per provider and every member abstract: a member added here does not
/// compile until all three providers implement it, so a provider cannot be left out. That is the failure
/// this type exists for — the audit insert once worked only where the column type happened to accept
/// text, and nobody noticed on PostgreSQL.
/// </para>
/// </summary>
internal abstract class TsakSqlDialect
{
    public static TsakSqlDialect Postgres { get; } = new PostgresDialect();
    public static TsakSqlDialect SqlServer { get; } = new SqlServerDialect();
    public static TsakSqlDialect Sqlite { get; } = new SqliteDialect();

    /// <summary>Dialect of a configured database provider.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="AuditProvider.None"/>: no database, no dialect.</exception>
    public static TsakSqlDialect For(AuditProvider provider) => provider switch
    {
        AuditProvider.Postgres => Postgres,
        AuditProvider.SqlServer => SqlServer,
        AuditProvider.Sqlite => Sqlite,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider,
            "No database provider is configured, so there is no SQL dialect.")
    };

    /// <summary>
    /// Dialect of the Quartz job store: the configured provider, or PostgreSQL when
    /// <c>Tsak:Redb:Provider</c> is not set — the Quartz wiring has always defaulted to it.
    /// </summary>
    public static TsakSqlDialect ForQuartz(IConfiguration configuration)
    {
        var provider = AuditStorage.ResolveProvider(configuration);
        return provider == AuditProvider.None ? Postgres : For(provider);
    }

    public abstract AuditProvider Provider { get; }

    /// <summary>Name under <c>ConnectionStrings</c> — the same names redb uses.</summary>
    public abstract string ConnectionStringName { get; }

    /// <summary>Suffix of the embedded DDL scripts: <c>AuditSchema.{suffix}</c>, <c>DlqSchema.{suffix}</c>, <c>QuartzSchema.{suffix}</c>.</summary>
    public abstract string ScriptSuffix { get; }

    /// <summary>ADO.NET invariant name, used to register the factory for redb.Route.Sql.</summary>
    public abstract string InvariantName { get; }

    /// <summary>The <see cref="DbProviderFactory"/> backing <see cref="InvariantName"/>.</summary>
    public abstract DbProviderFactory Factory { get; }

    /// <summary>Value of Quartz <c>quartz.dataSource.default.provider</c>.</summary>
    public abstract string QuartzProvider { get; }

    /// <summary>Value of Quartz <c>quartz.jobStore.driverDelegateType</c>.</summary>
    public abstract string QuartzDriverDelegate { get; }

    /// <summary>False where Quartz refuses a clustered AdoJobStore.</summary>
    public abstract bool SupportsClusteredJobStore { get; }

    /// <summary>A text parameter reference assigned to a <c>uuid</c> column.</summary>
    public abstract string Uuid(string parameter);

    /// <summary>A text (ISO-8601) parameter reference assigned to a timestamp-with-offset column.</summary>
    public abstract string TextTimestamp(string parameter);

    /// <summary>A text parameter reference assigned to a JSON column.</summary>
    public abstract string Json(string parameter);

    /// <summary>The paging clause that follows <c>ORDER BY</c>.</summary>
    public abstract string Paging(string limitParameter, string offsetParameter);

    /// <summary>Binds a timestamp in the form the provider's column holds (see <see cref="DbParameterBinding"/>).</summary>
    public abstract void BindTimestamp(DbCommand cmd, string name, DateTimeOffset? value);

    /// <summary>
    /// Adds a nullable text column and an index on it to an existing table, idempotently. The DDL scripts
    /// create the column on a fresh database, where this changes nothing.
    /// </summary>
    public abstract Task EnsureNullableTextColumnAsync(
        DbConnection conn, string table, string column, int maxLength, string indexName, CancellationToken ct);

    protected static async Task ExecuteAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class PostgresDialect : TsakSqlDialect
    {
        public override AuditProvider Provider => AuditProvider.Postgres;
        public override string ConnectionStringName => "Postgres";
        public override string ScriptSuffix => "Postgres";
        public override string InvariantName => "Npgsql";
        public override DbProviderFactory Factory => Npgsql.NpgsqlFactory.Instance;
        public override string QuartzProvider => "Npgsql";
        public override string QuartzDriverDelegate => "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz";
        public override bool SupportsClusteredJobStore => true;

        // A plain string parameter cannot be assigned to uuid / timestamptz / jsonb (42804).
        public override string Uuid(string parameter) => parameter + "::uuid";
        public override string TextTimestamp(string parameter) => parameter + "::timestamptz";
        public override string Json(string parameter) => parameter + "::jsonb";

        public override string Paging(string limitParameter, string offsetParameter) =>
            $"LIMIT {limitParameter} OFFSET {offsetParameter}";

        public override void BindTimestamp(DbCommand cmd, string name, DateTimeOffset? value) =>
            DbParameterBinding.Add(cmd, name, DbType.DateTimeOffset, value?.ToUniversalTime());

        public override async Task EnsureNullableTextColumnAsync(
            DbConnection conn, string table, string column, int maxLength, string indexName, CancellationToken ct)
        {
            await ExecuteAsync(conn, $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} VARCHAR({maxLength})", ct);
            await ExecuteAsync(conn, $"CREATE INDEX IF NOT EXISTS {indexName} ON {table} ({column})", ct);
        }
    }

    private sealed class SqlServerDialect : TsakSqlDialect
    {
        public override AuditProvider Provider => AuditProvider.SqlServer;
        public override string ConnectionStringName => "MSSql";
        public override string ScriptSuffix => "SqlServer";
        public override string InvariantName => "Microsoft.Data.SqlClient";
        public override DbProviderFactory Factory => Microsoft.Data.SqlClient.SqlClientFactory.Instance;
        public override string QuartzProvider => "SqlServer";
        public override string QuartzDriverDelegate => "Quartz.Impl.AdoJobStore.SqlServerDelegate, Quartz";
        public override bool SupportsClusteredJobStore => true;

        // Text converts implicitly to uniqueidentifier / datetimeoffset / nvarchar(max) JSON.
        public override string Uuid(string parameter) => parameter;
        public override string TextTimestamp(string parameter) => parameter;
        public override string Json(string parameter) => parameter;

        public override string Paging(string limitParameter, string offsetParameter) =>
            $"OFFSET {offsetParameter} ROWS FETCH NEXT {limitParameter} ROWS ONLY";

        public override void BindTimestamp(DbCommand cmd, string name, DateTimeOffset? value) =>
            DbParameterBinding.Add(cmd, name, DbType.DateTimeOffset, value?.ToUniversalTime());

        public override async Task EnsureNullableTextColumnAsync(
            DbConnection conn, string table, string column, int maxLength, string indexName, CancellationToken ct)
        {
            await ExecuteAsync(conn,
                $"IF COL_LENGTH(N'dbo.{table}', N'{column}') IS NULL "
                + $"ALTER TABLE [dbo].[{table}] ADD [{column}] nvarchar({maxLength}) NULL;", ct);
            // A batch of its own: SQL Server compiles a batch as a whole, so an index on a column
            // added in the same batch does not compile.
            await ExecuteAsync(conn,
                $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'[dbo].[{table}]')) "
                + $"CREATE INDEX [{indexName}] ON [dbo].[{table}] ([{column}]);", ct);
        }
    }

    private sealed class SqliteDialect : TsakSqlDialect
    {
        public override AuditProvider Provider => AuditProvider.Sqlite;
        public override string ConnectionStringName => "Sqlite";
        public override string ScriptSuffix => "Sqlite";
        public override string InvariantName => "Microsoft.Data.Sqlite";
        public override DbProviderFactory Factory => Microsoft.Data.Sqlite.SqliteFactory.Instance;
        public override string QuartzProvider => "SQLite-Microsoft";
        public override string QuartzDriverDelegate => "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz";

        // Quartz hard-refuses SQLite + clustered: the QRTZ_LOCKS semaphore needs row locks SQLite lacks.
        public override bool SupportsClusteredJobStore => false;

        // Every column of Tsak's SQLite tables is TEXT-compatible.
        public override string Uuid(string parameter) => parameter;
        public override string TextTimestamp(string parameter) => parameter;
        public override string Json(string parameter) => parameter;

        public override string Paging(string limitParameter, string offsetParameter) =>
            $"LIMIT {limitParameter} OFFSET {offsetParameter}";

        // The timestamp columns are TEXT holding the ISO-8601 "o" form and compare lexicographically,
        // which is chronological for that format — the parameter is spelled the way the rows were written.
        public override void BindTimestamp(DbCommand cmd, string name, DateTimeOffset? value) =>
            DbParameterBinding.Add(cmd, name, DbType.String,
                value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

        public override async Task EnsureNullableTextColumnAsync(
            DbConnection conn, string table, string column, int maxLength, string indexName, CancellationToken ct)
        {
            // SQLite has no ADD COLUMN IF NOT EXISTS; TEXT has no length.
            if (!await ColumnExistsAsync(conn, table, column, ct))
                await ExecuteAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} TEXT", ct);
            await ExecuteAsync(conn, $"CREATE INDEX IF NOT EXISTS {indexName} ON {table} ({column})", ct);
        }

        private static async Task<bool> ColumnExistsAsync(DbConnection conn, string table, string column, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
        }
    }
}
