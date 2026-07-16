namespace TinadecCore.Persistence;

/// <summary>Resolves Core-owned files from identifiers below the configured data root.</summary>
public sealed class StoragePaths
{
    public StoragePaths(string contentRootPath, Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions> options)
    {
        var configuredRoot = options.Value.DataRoot;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException("TinadecPersistence:DataRoot must be configured.");
        }

        Root = Path.GetFullPath(Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(contentRootPath, configuredRoot));
    }

    public string Root { get; }

    public string SessionHistory(Guid sessionId) => Under("sessions", sessionId + ".json");
    public string TaskSnapshot(Guid runId) => Under("tasks", runId + ".tasks.json");
    public string EventLog(Guid runId) => Under("events", runId + ".events.jsonl");
    public string Artifacts(Guid runId) => Under("artifacts", runId.ToString());

    private string Under(string directory, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(Root, directory, fileName));
        var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Core storage path escaped the configured data root.");
        }

        return path;
    }
}
