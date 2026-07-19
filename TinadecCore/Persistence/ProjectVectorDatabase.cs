using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace TinadecCore.Persistence;

internal sealed class ProjectVectorDatabase : IProjectVectorDatabase
{
    private readonly IDatabaseConnectionInfo _connection;
    private readonly StoragePaths _paths;

    public ProjectVectorDatabase(IDatabaseConnectionInfo connection, StoragePaths paths)
    {
        _connection = connection;
        _paths = paths;
    }

    public async Task UpsertAsync(ProjectVectorRecord record, CancellationToken cancellationToken = default)
    {
        Validate(record);
        await using var connection = await OpenAsync(record, cancellationToken).ConfigureAwait(false);
        var table = await EnsureCollectionAsync(connection, record.ModelId, record.Embedding.Length, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT id FROM vector_chunks WHERE namespace = $namespace AND source_type = $sourceType AND source_id = $sourceId AND source_revision = $sourceRevision AND chunk_index = $chunkIndex AND model_id = $modelId";
        BindSource(existing, record);
        existing.Parameters.AddWithValue("$sourceRevision", record.SourceRevision);
        existing.Parameters.AddWithValue("$chunkIndex", record.ChunkIndex);
        existing.Parameters.AddWithValue("$modelId", record.ModelId);
        var prior = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (prior is long priorId)
        {
            await DeleteRowAsync(connection, transaction, table, priorId, cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO vector_chunks (namespace, source_type, source_id, source_revision, chunk_index, content_hash, content, model_id, metadata_json, created_at) VALUES ($namespace, $sourceType, $sourceId, $sourceRevision, $chunkIndex, $contentHash, $content, $modelId, $metadata, $createdAt); SELECT last_insert_rowid();";
        BindSource(insert, record);
        insert.Parameters.AddWithValue("$sourceRevision", record.SourceRevision);
        insert.Parameters.AddWithValue("$chunkIndex", record.ChunkIndex);
        insert.Parameters.AddWithValue("$contentHash", record.ContentHash);
        insert.Parameters.AddWithValue("$content", record.Content);
        insert.Parameters.AddWithValue("$modelId", record.ModelId);
        insert.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(record.Metadata));
        insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        var id = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Vector chunk insert did not return an id."));

        await using var vector = connection.CreateCommand();
        vector.Transaction = transaction;
        vector.CommandText = $"INSERT INTO {table}(rowid, embedding) VALUES ($id, $embedding)";
        vector.Parameters.AddWithValue("$id", id);
        vector.Parameters.AddWithValue("$embedding", ToJson(record.Embedding));
        await vector.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<ProjectVectorMatch>> SearchAsync(ProjectVectorSearch search, CancellationToken cancellationToken = default)
    {
        Validate(search);
        await using var connection = await OpenAsync(search, cancellationToken).ConfigureAwait(false);
        var table = await EnsureCollectionAsync(connection, search.ModelId, search.Embedding.Length, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT c.id, c.source_type, c.source_id, c.source_revision, c.chunk_index, c.content, c.metadata_json, v.distance FROM {table} v JOIN vector_chunks c ON c.id = v.rowid WHERE v.embedding MATCH $embedding AND k = $limit AND c.namespace = $namespace AND c.model_id = $modelId ORDER BY v.distance";
        command.Parameters.AddWithValue("$embedding", ToJson(search.Embedding));
        command.Parameters.AddWithValue("$limit", Math.Clamp(search.Limit * 4, 1, 200));
        command.Parameters.AddWithValue("$namespace", search.Namespace);
        command.Parameters.AddWithValue("$modelId", search.ModelId);
        var output = new List<ProjectVectorMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && output.Count < search.Limit)
        {
            var sourceType = reader.GetString(1);
            if (search.SourceTypes.Count != 0 && !search.SourceTypes.Contains(sourceType, StringComparer.Ordinal)) continue;
            var score = 1f - Convert.ToSingle(reader.GetDouble(7));
            if (search.MinimumScore is { } minimum && score < minimum) continue;
            output.Add(new ProjectVectorMatch
            {
                ChunkId = reader.GetInt64(0).ToString(), SourceType = sourceType, SourceId = reader.GetString(2), SourceRevision = reader.GetString(3),
                ChunkIndex = reader.GetInt32(4), Content = reader.GetString(5), Score = score,
                Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? new Dictionary<string, string>()
            });
        }
        return output;
    }

    public async Task DeleteSourceAsync(ProjectVectorSource source, CancellationToken cancellationToken = default)
    {
        Validate(source);
        await using var connection = await OpenAsync(source, cancellationToken).ConfigureAwait(false);
        await using var find = connection.CreateCommand();
        find.CommandText = "SELECT id, model_id FROM vector_chunks WHERE namespace = $namespace AND source_type = $sourceType AND source_id = $sourceId";
        BindSource(find, source);
        var rows = new List<(long Id, string ModelId)>();
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add((reader.GetInt64(0), reader.GetString(1)));
        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            var dimension = await GetDimensionAsync(connection, transaction, row.ModelId, cancellationToken).ConfigureAwait(false);
            await DeleteRowAsync(connection, transaction, CollectionTable(row.ModelId, dimension), row.Id, cancellationToken).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    private async Task<SqliteConnection> OpenAsync(ProjectVectorScope scope, CancellationToken cancellationToken)
    {
        if (_connection.Provider != DatabaseProvider.Sqlite) throw new NotSupportedException("PostgreSQL vector persistence is not implemented yet.");
        var path = _paths.ProjectVectorDatabase(scope.TenantId, scope.WorkspaceId, scope.ProjectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.EnableExtensions(true);
        connection.LoadVector();
        await using var schema = connection.CreateCommand();
        schema.CommandText = "CREATE TABLE IF NOT EXISTS vector_chunks (id INTEGER PRIMARY KEY, namespace TEXT NOT NULL, source_type TEXT NOT NULL, source_id TEXT NOT NULL, source_revision TEXT NOT NULL, chunk_index INTEGER NOT NULL, content_hash TEXT NOT NULL, content TEXT NOT NULL, model_id TEXT NOT NULL, metadata_json TEXT NOT NULL, created_at TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS ix_vector_chunks_source ON vector_chunks(namespace, source_type, source_id, source_revision, chunk_index, model_id); CREATE TABLE IF NOT EXISTS vector_collections (model_id TEXT PRIMARY KEY, dimension INTEGER NOT NULL);";
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<string> EnsureCollectionAsync(SqliteConnection connection, string modelId, int dimension, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT dimension FROM vector_collections WHERE model_id = $modelId";
        command.Parameters.AddWithValue("$modelId", modelId);
        var existing = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existing is long value && value != dimension) throw new InvalidOperationException($"Embedding model '{modelId}' changed dimension from {value} to {dimension}.");
        var table = CollectionTable(modelId, dimension);
        if (existing is null)
        {
            await using var create = connection.CreateCommand();
            create.CommandText = $"INSERT INTO vector_collections(model_id, dimension) VALUES ($modelId, $dimension); CREATE VIRTUAL TABLE {table} USING vec0(embedding float[{dimension}]);";
            create.Parameters.AddWithValue("$modelId", modelId);
            create.Parameters.AddWithValue("$dimension", dimension);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return table;
    }

    private static async Task<int> GetDimensionAsync(SqliteConnection connection, SqliteTransaction transaction, string modelId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT dimension FROM vector_collections WHERE model_id = $modelId";
        command.Parameters.AddWithValue("$modelId", modelId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task DeleteRowAsync(SqliteConnection connection, SqliteTransaction transaction, string table, long id, CancellationToken cancellationToken)
    {
        await using var deleteVector = connection.CreateCommand();
        deleteVector.Transaction = transaction;
        deleteVector.CommandText = $"DELETE FROM {table} WHERE rowid = $id";
        deleteVector.Parameters.AddWithValue("$id", id);
        await deleteVector.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var deleteChunk = connection.CreateCommand();
        deleteChunk.Transaction = transaction;
        deleteChunk.CommandText = "DELETE FROM vector_chunks WHERE id = $id";
        deleteChunk.Parameters.AddWithValue("$id", id);
        await deleteChunk.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CollectionTable(string modelId, int dimension) => "vec_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelId))).ToLowerInvariant()[..16] + "_" + dimension;
    private static string ToJson(float[] values) => JsonSerializer.Serialize(values);
    private static void BindSource(SqliteCommand command, ProjectVectorSource source) { command.Parameters.AddWithValue("$namespace", source.Namespace); command.Parameters.AddWithValue("$sourceType", source.SourceType); command.Parameters.AddWithValue("$sourceId", source.SourceId); }
    private static void Validate(ProjectVectorScope scope) { if (scope.TenantId == Guid.Empty || scope.ProjectId == Guid.Empty) throw new ArgumentException("Tenant and project ids are required."); }
    private static void Validate(ProjectVectorRecord record) { Validate((ProjectVectorScope)record); if (string.IsNullOrWhiteSpace(record.ModelId) || record.Embedding.Length == 0 || string.IsNullOrWhiteSpace(record.Content)) throw new ArgumentException("Content, model id, and embedding are required."); }
    private static void Validate(ProjectVectorSearch search) { Validate((ProjectVectorScope)search); if (string.IsNullOrWhiteSpace(search.ModelId) || search.Embedding.Length == 0 || string.IsNullOrWhiteSpace(search.Namespace)) throw new ArgumentException("Namespace, model id, and embedding are required."); }
    private static void Validate(ProjectVectorSource source) { Validate((ProjectVectorScope)source); if (string.IsNullOrWhiteSpace(source.Namespace) || string.IsNullOrWhiteSpace(source.SourceType) || string.IsNullOrWhiteSpace(source.SourceId)) throw new ArgumentException("Namespace, source type, and source id are required."); }
}
