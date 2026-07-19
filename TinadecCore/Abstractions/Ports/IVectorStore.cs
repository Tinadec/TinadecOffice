namespace TinadecCore.Abstractions.Ports;

/// <summary>Core-owned semantic indexing and retrieval capability.</summary>
public interface IVectorStore
{
    Task<VectorIndexResult> IndexAsync(VectorIndexRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken = default);
    Task DeleteSourceAsync(VectorSourceReference source, CancellationToken cancellationToken = default);
}

public class VectorProjectScope
{
    public Guid TenantId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public Guid ProjectId { get; init; }
}

public class VectorSourceReference : VectorProjectScope
{
    public string Namespace { get; init; } = "default";
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
}

public sealed class VectorIndexRequest : VectorSourceReference
{
    public string SourceRevision { get; init; } = "1";
    public string Content { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed class VectorSearchRequest : VectorProjectScope
{
    public string Namespace { get; init; } = "default";
    public string Query { get; init; } = string.Empty;
    public int Limit { get; init; } = 8;
    public float? MinimumScore { get; init; }
    public IReadOnlyList<string> SourceTypes { get; init; } = [];
}

public sealed class VectorIndexResult
{
    public int IndexedChunks { get; init; }
    public int SkippedChunks { get; init; }
    public string? ModelId { get; init; }
}

public sealed class VectorMatch
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
