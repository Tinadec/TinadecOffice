using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.VectorStore;

public sealed class VectorStoreModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "vector_store";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddSingleton<IVectorStore, CoreVectorStore>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["semantic_indexing", "embedding_generation", "project_scoped_retrieval"],
            Language = "C#",
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

internal sealed class CoreVectorStore : IVectorStore
{
    private const int ChunkLength = 1_600;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IProjectVectorDatabase _database;

    public CoreVectorStore(IEmbeddingProvider embeddings, IProjectVectorDatabase database)
    {
        _embeddings = embeddings;
        _database = database;
    }

    public async Task<VectorIndexResult> IndexAsync(VectorIndexRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, request.Content);
        var chunks = Split(request.Content);
        var embeddings = await _embeddings.GenerateAsync(new EmbeddingRequest
        {
            TenantId = request.TenantId, WorkspaceId = request.WorkspaceId, ProjectId = request.ProjectId, Inputs = chunks
        }, cancellationToken).ConfigureAwait(false);
        if (!embeddings.IsAvailable || string.IsNullOrWhiteSpace(embeddings.ModelId)) throw new InvalidOperationException(embeddings.Detail ?? "No embedding model is configured.");
        if (embeddings.Vectors.Count != chunks.Count || embeddings.Dimension <= 0 || embeddings.Vectors.Any(x => x.Length != embeddings.Dimension)) throw new InvalidOperationException("Embedding provider returned inconsistent vectors.");

        for (var index = 0; index < chunks.Count; index++)
        {
            var content = chunks[index];
            await _database.UpsertAsync(new ProjectVectorRecord
            {
                TenantId = request.TenantId, WorkspaceId = request.WorkspaceId, ProjectId = request.ProjectId,
                Namespace = request.Namespace, SourceType = request.SourceType, SourceId = request.SourceId, SourceRevision = request.SourceRevision,
                ChunkIndex = index, Content = content, ContentHash = Hash(content), ModelId = embeddings.ModelId, Embedding = embeddings.Vectors[index], Metadata = request.Metadata
            }, cancellationToken).ConfigureAwait(false);
        }
        return new VectorIndexResult { IndexedChunks = chunks.Count, ModelId = embeddings.ModelId };
    }

    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, request.Query);
        var embeddings = await _embeddings.GenerateAsync(new EmbeddingRequest
        {
            TenantId = request.TenantId, WorkspaceId = request.WorkspaceId, ProjectId = request.ProjectId, Inputs = [request.Query]
        }, cancellationToken).ConfigureAwait(false);
        if (!embeddings.IsAvailable || string.IsNullOrWhiteSpace(embeddings.ModelId) || embeddings.Vectors.Count != 1) throw new InvalidOperationException(embeddings.Detail ?? "No embedding model is configured.");
        var matches = await _database.SearchAsync(new ProjectVectorSearch
        {
            TenantId = request.TenantId, WorkspaceId = request.WorkspaceId, ProjectId = request.ProjectId, Namespace = request.Namespace,
            ModelId = embeddings.ModelId, Embedding = embeddings.Vectors[0], Limit = request.Limit, MinimumScore = request.MinimumScore, SourceTypes = request.SourceTypes
        }, cancellationToken).ConfigureAwait(false);
        return matches.Select(match => new VectorMatch
        {
            ChunkId = match.ChunkId, SourceType = match.SourceType, SourceId = match.SourceId, SourceRevision = match.SourceRevision,
            ChunkIndex = match.ChunkIndex, Content = match.Content, Score = match.Score, Metadata = match.Metadata
        }).ToList();
    }

    public Task DeleteSourceAsync(VectorSourceReference source, CancellationToken cancellationToken = default) =>
        _database.DeleteSourceAsync(new ProjectVectorSource
        {
            TenantId = source.TenantId, WorkspaceId = source.WorkspaceId, ProjectId = source.ProjectId,
            Namespace = source.Namespace, SourceType = source.SourceType, SourceId = source.SourceId
        }, cancellationToken);

    private static List<string> Split(string content)
    {
        var chunks = new List<string>();
        for (var offset = 0; offset < content.Length; offset += ChunkLength) chunks.Add(content.Substring(offset, Math.Min(ChunkLength, content.Length - offset)));
        return chunks;
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    private static void Validate(VectorProjectScope scope, string value)
    {
        if (scope.TenantId == Guid.Empty || scope.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Project scope and content are required.");
    }
}
