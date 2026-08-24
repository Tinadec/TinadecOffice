using System.Text.Json;
using Npgsql;
using Pgvector;

namespace TinadecCore.Persistence;

internal sealed class PostgresProjectVectorDatabase : IProjectVectorDatabase, IDisposable
{
    private readonly IDatabaseConnectionInfo _connection;
    private NpgsqlDataSource? _dataSource;
    private readonly object _gate = new();

    public PostgresProjectVectorDatabase(IDatabaseConnectionInfo connection)
    {
        _connection = connection;
    }

    public async Task UpsertAsync(ProjectVectorRecord record, CancellationToken cancellationToken = default)
    {
        Validate(record);
        var dataSource = GetDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureCollectionDimensionAsync(connection, record.ModelId, record.Embedding.Length, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Remove prior version of the same chunk (idempotent upsert).
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM vector_chunks WHERE tenant_id = $tenantId AND workspace_id IS NOT DISTINCT FROM $workspaceId AND project_id = $projectId AND namespace = $namespace AND source_type = $sourceType AND source_id = $sourceId AND source_revision = $sourceRevision AND chunk_index = $chunkIndex AND model_id = $modelId",
            connection, (NpgsqlTransaction)transaction))
        {
            delete.Parameters.AddWithValue("$tenantId", record.TenantId);
            delete.Parameters.AddWithValue("$workspaceId", (object?)record.WorkspaceId ?? DBNull.Value);
            delete.Parameters.AddWithValue("$projectId", record.ProjectId);
            delete.Parameters.AddWithValue("$namespace", record.Namespace);
            delete.Parameters.AddWithValue("$sourceType", record.SourceType);
            delete.Parameters.AddWithValue("$sourceId", record.SourceId);
            delete.Parameters.AddWithValue("$sourceRevision", record.SourceRevision);
            delete.Parameters.AddWithValue("$chunkIndex", record.ChunkIndex);
            delete.Parameters.AddWithValue("$modelId", record.ModelId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO vector_chunks (tenant_id, workspace_id, project_id, namespace, source_type, source_id, source_revision, chunk_index, content_hash, content, model_id, metadata_json, created_at, embedding) VALUES ($tenantId, $workspaceId, $projectId, $namespace, $sourceType, $sourceId, $sourceRevision, $chunkIndex, $contentHash, $content, $modelId, $metadata, $createdAt, $embedding)",
            connection, (NpgsqlTransaction)transaction))
        {
            insert.Parameters.AddWithValue("$tenantId", record.TenantId);
            insert.Parameters.AddWithValue("$workspaceId", (object?)record.WorkspaceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$projectId", record.ProjectId);
            insert.Parameters.AddWithValue("$namespace", record.Namespace);
            insert.Parameters.AddWithValue("$sourceType", record.SourceType);
            insert.Parameters.AddWithValue("$sourceId", record.SourceId);
            insert.Parameters.AddWithValue("$sourceRevision", record.SourceRevision);
            insert.Parameters.AddWithValue("$chunkIndex", record.ChunkIndex);
            insert.Parameters.AddWithValue("$contentHash", record.ContentHash);
            insert.Parameters.AddWithValue("$content", record.Content);
            insert.Parameters.AddWithValue("$modelId", record.ModelId);
            insert.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(record.Metadata));
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow);
            insert.Parameters.AddWithValue("$embedding", new Vector(record.Embedding));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectVectorMatch>> SearchAsync(ProjectVectorSearch search, CancellationToken cancellationToken = default)
    {
        Validate(search);
        var dataSource = GetDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureCollectionDimensionAsync(connection, search.ModelId, search.Embedding.Length, cancellationToken).ConfigureAwait(false);

        var limit = Math.Clamp(search.Limit * 4, 1, 200);
        var queryVector = new Vector(search.Embedding);

        // pgvector cosine distance is in [0,2]; convert to score as 1 - distance (clamped to [-1,1] then shift).
        // For normalized embeddings, distance in [0,2] maps to score 1..-1; we keep original sqlite-vec semantics 1 - distance.
        await using var command = new NpgsqlCommand(
            "SELECT id, source_type, source_id, source_revision, chunk_index, content, metadata_json, embedding <=> $embedding AS distance FROM vector_chunks WHERE tenant_id = $tenantId AND workspace_id IS NOT DISTINCT FROM $workspaceId AND project_id = $projectId AND namespace = $namespace AND model_id = $modelId ORDER BY distance ASC LIMIT $limit",
            connection);
        command.Parameters.AddWithValue("$embedding", queryVector);
        command.Parameters.AddWithValue("$tenantId", search.TenantId);
        command.Parameters.AddWithValue("$workspaceId", (object?)search.WorkspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", search.ProjectId);
        command.Parameters.AddWithValue("$namespace", search.Namespace);
        command.Parameters.AddWithValue("$modelId", search.ModelId);
        command.Parameters.AddWithValue("$limit", limit);

        var output = new List<ProjectVectorMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && output.Count < search.Limit)
        {
            var sourceType = reader.GetString(1);
            if (search.SourceTypes.Count != 0 && !search.SourceTypes.Contains(sourceType, StringComparer.Ordinal)) continue;
            var distance = reader.GetDouble(7);
            var score = 1f - (float)distance;
            if (search.MinimumScore is { } minimum && score < minimum) continue;
            output.Add(new ProjectVectorMatch
            {
                ChunkId = reader.GetInt64(0).ToString(),
                SourceType = sourceType,
                SourceId = reader.GetString(2),
                SourceRevision = reader.GetString(3),
                ChunkIndex = reader.GetInt32(4),
                Content = reader.GetString(5),
                Score = score,
                Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? new Dictionary<string, string>()
            });
        }

        return output;
    }

    public async Task DeleteSourceAsync(ProjectVectorSource source, CancellationToken cancellationToken = default)
    {
        Validate(source);
        var dataSource = GetDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "DELETE FROM vector_chunks WHERE tenant_id = $tenantId AND workspace_id IS NOT DISTINCT FROM $workspaceId AND project_id = $projectId AND namespace = $namespace AND source_type = $sourceType AND source_id = $sourceId",
            connection);
        command.Parameters.AddWithValue("$tenantId", source.TenantId);
        command.Parameters.AddWithValue("$workspaceId", (object?)source.WorkspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", source.ProjectId);
        command.Parameters.AddWithValue("$namespace", source.Namespace);
        command.Parameters.AddWithValue("$sourceType", source.SourceType);
        command.Parameters.AddWithValue("$sourceId", source.SourceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureCollectionDimensionAsync(NpgsqlConnection connection, string modelId, int dimension, CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand("SELECT dimension FROM vector_collections WHERE model_id = $modelId", connection);
        select.Parameters.AddWithValue("$modelId", modelId);
        var existing = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existing is int value && value != dimension) throw new InvalidOperationException($"Embedding model '{modelId}' changed dimension from {value} to {dimension}.");
        if (existing is long longValue && (int)longValue != dimension) throw new InvalidOperationException($"Embedding model '{modelId}' changed dimension from {longValue} to {dimension}.");
        if (existing is null || existing is DBNull)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO vector_collections(model_id, dimension) VALUES ($modelId, $dimension) ON CONFLICT (model_id) DO NOTHING", connection);
            insert.Parameters.AddWithValue("$modelId", modelId);
            insert.Parameters.AddWithValue("$dimension", dimension);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            // Re-validate after insert in case of concurrent insert with different dimension.
            await using var recheck = new NpgsqlCommand("SELECT dimension FROM vector_collections WHERE model_id = $modelId", connection);
            recheck.Parameters.AddWithValue("$modelId", modelId);
            var rechecked = await recheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (rechecked is int recheckedValue && recheckedValue != dimension) throw new InvalidOperationException($"Embedding model '{modelId}' changed dimension from {recheckedValue} to {dimension}.");
            if (rechecked is long recheckedLong && (int)recheckedLong != dimension) throw new InvalidOperationException($"Embedding model '{modelId}' changed dimension from {recheckedLong} to {dimension}.");
        }
    }

    private NpgsqlDataSource GetDataSource()
    {
        if (_connection.Provider != DatabaseProvider.PostgreSql) throw new InvalidOperationException("Postgres vector store is only available with the PostgreSql provider.");
        if (string.IsNullOrWhiteSpace(_connection.ConnectionString)) throw new InvalidOperationException("PostgreSQL connection string is not configured.");
        if (_dataSource is not null) return _dataSource;
        lock (_gate)
        {
            if (_dataSource is not null) return _dataSource;
            var builder = new NpgsqlDataSourceBuilder(_connection.ConnectionString);
            builder.UseVector();
            _dataSource = builder.Build();
            return _dataSource;
        }
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
    }

    private static void Validate(ProjectVectorScope scope) { if (scope.TenantId == Guid.Empty || scope.ProjectId == Guid.Empty) throw new ArgumentException("Tenant and project ids are required."); }
    private static void Validate(ProjectVectorRecord record) { Validate((ProjectVectorScope)record); if (string.IsNullOrWhiteSpace(record.ModelId) || record.Embedding.Length == 0 || string.IsNullOrWhiteSpace(record.Content)) throw new ArgumentException("Content, model id, and embedding are required."); }
    private static void Validate(ProjectVectorSearch search) { Validate((ProjectVectorScope)search); if (string.IsNullOrWhiteSpace(search.ModelId) || search.Embedding.Length == 0 || string.IsNullOrWhiteSpace(search.Namespace)) throw new ArgumentException("Namespace, model id, and embedding are required."); }
    private static void Validate(ProjectVectorSource source) { Validate((ProjectVectorScope)source); if (string.IsNullOrWhiteSpace(source.Namespace) || string.IsNullOrWhiteSpace(source.SourceType) || string.IsNullOrWhiteSpace(source.SourceId)) throw new ArgumentException("Namespace, source type, and source id are required."); }
}
