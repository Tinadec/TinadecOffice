namespace TinadecCore.Persistence;

internal sealed class DatabaseConnectionInfo : IDatabaseConnectionInfo
{
    public DatabaseConnectionInfo(DatabaseProvider provider, string? connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    public DatabaseProvider Provider { get; }

    public string? ConnectionString { get; }

    public string ProviderName => Provider switch
    {
        DatabaseProvider.PostgreSql => "postgresql",
        _ => "sqlite"
    };
}
