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
    public string DataRoot { get; set; } = Path.Combine(StorageScopePaths.UserRoot(), "data");

    /// <summary>
    /// Which <c>ISecretStore</c> protects credential material. <c>auto</c> (default) picks
    /// DPAPI on Windows and an AES-GCM file store on Linux/macOS. Use <c>environment</c>
    /// when the deployment injects credentials through the process environment instead —
    /// that store is read-only, so saving a key from the control plane fails by design.
    /// See <see cref="SecretStoreFactory"/>.
    /// </summary>
    public string SecretStore { get; set; } = SecretStoreFactory.Auto;

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
    public string DatabasePath { get; set; } = Path.Combine(StorageScopePaths.UserRoot(), "data", "tinadec.db");

    /// <summary>SQLite busy_timeout in milliseconds applied to every opened connection.</summary>
    public int BusyTimeoutMs { get; set; } = SqliteConnectionPragmaInterceptor.DefaultBusyTimeoutMs;

    /// <summary>Enables WAL journal mode + NORMAL synchronous durability on file-backed SQLite databases.</summary>
    public bool EnableWal { get; set; } = true;
}

public sealed class PostgreSqlOptions
{
    /// <summary>Named connection string key under ConnectionStrings.</summary>
    public string ConnectionStringName { get; set; } = "TinadecCore";
    /// <summary>Host-minted namespace of a mounted scope. Never taken from an ordinary business request.</summary>
    public string? Schema { get; set; }
}
