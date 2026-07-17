namespace TinadecCore.Persistence;

/// <summary>
/// Resolved connection information for the active database provider.
/// </summary>
public interface IDatabaseConnectionInfo
{
    bool IsConfigured { get; }

    DatabaseProvider Provider { get; }

    /// <summary>EF-compatible connection string for the active provider, or null when not configured.</summary>
    string? ConnectionString { get; }

    /// <summary>Provider name for readiness receipts: sqlite | postgresql.</summary>
    string ProviderName { get; }
}
