namespace TinadecCore.Persistence;

public enum DatabaseReadinessState
{
    NotConfigured,
    Ready,
    Unavailable
}

public sealed class DatabaseReadinessResult
{
    public string Provider { get; init; } = "sqlite";
    public DatabaseReadinessState State { get; init; } = DatabaseReadinessState.NotConfigured;
    public string? Detail { get; init; }

    public string StateName => State switch
    {
        DatabaseReadinessState.Ready => "ready",
        DatabaseReadinessState.Unavailable => "unavailable",
        _ => "not_configured"
    };
}
