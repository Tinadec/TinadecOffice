namespace TinadecCore.Persistence;

/// <summary>Provider-specific persistence for a project's semantic index.</summary>
public interface IProjectVectorDatabase
{
    Task UpsertAsync(ProjectVectorRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectVectorMatch>> SearchAsync(ProjectVectorSearch search, CancellationToken cancellationToken = default);
    Task DeleteSourceAsync(ProjectVectorSource source, CancellationToken cancellationToken = default);
}

public class ProjectVectorScope
{
    public Guid TenantId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public Guid ProjectId { get; init; }
}

public class ProjectVectorSource : ProjectVectorScope
{
    public string Namespace { get; init; } = "default";
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
}

public sealed class ProjectVectorRecord : ProjectVectorSource
{
    public string SourceRevision { get; init; } = "1";
    public int ChunkIndex { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public float[] Embedding { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed class ProjectVectorSearch : ProjectVectorScope
{
    public string Namespace { get; init; } = "default";
    public string ModelId { get; init; } = string.Empty;
    public float[] Embedding { get; init; } = [];
    public int Limit { get; init; } = 8;
    public float? MinimumScore { get; init; }
    public IReadOnlyList<string> SourceTypes { get; init; } = [];
}

public sealed class ProjectVectorMatch
{
    public string ChunkId { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string SourceRevision { get; init; } = string.Empty;
    public int ChunkIndex { get; init; }
    public string Content { get; init; } = string.Empty;
    public float Score { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
