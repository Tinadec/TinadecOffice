namespace TinadecCore.Persistence;

/// <summary>
/// Shared database options. Provider-agnostic configuration for modules using EF Core LINQ.
/// Does not define schemas, tables, or migrations.
/// </summary>
public sealed class TinadecPersistenceOptions
{
    public const string SectionName = "TinadecPersistence";

    /// <summary>When false, no database provider is configured.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Active backend. Default is SQLite for local desktop.</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public SqliteOptions Sqlite { get; set; } = new();

    public PostgreSqlOptions PostgreSql { get; set; } = new();

    /// <summary>Relative or absolute root for Core-owned session, event, task, and artifact files.</summary>
    public string DataRoot { get; set; } = "data";

    /// <summary>
    /// SQLite applies migrations during local startup. PostgreSQL requires this explicit opt-in
    /// so a multi-instance deployment does not race schema changes.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;

    /// <summary>Probe timeout for readiness checks (seconds).</summary>
    public int ProbeTimeoutSeconds { get; set; } = 3;
}

public sealed class SqliteOptions
{
    /// <summary>
    /// Relative or absolute path to the SQLite database file.
    /// Relative paths resolve against the host content root when available, otherwise the current directory.
    /// </summary>
    public string DatabasePath { get; set; } = "data/tinadec.db";
}

public sealed class PostgreSqlOptions
{
    /// <summary>Named connection string key under ConnectionStrings.</summary>
    public string ConnectionStringName { get; set; } = "TinadecCore";
}
