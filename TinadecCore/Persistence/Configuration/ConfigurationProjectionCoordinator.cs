using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using TinadecCore.Abstractions.Ports;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace TinadecCore.Persistence;

/// <summary>Files are the edit commit point; current rows are rebuildable projections and historical versions remain facts.</summary>
public sealed class ConfigurationProjectionCoordinator : IConfigurationProjectionCoordinator
{
    private const string StampTableName = "_tinadec_projection_stamps";
    private const string CompileStampDocumentId = "__compile";
    private readonly ScopeConfigurationDocuments _documents;
    private readonly IScopeStorageLocations _locations;
    private readonly IContentStore _content;
    private readonly IEnumerable<IConfigurationProjectionSource> _sources;
    private readonly ITenantContextAccessor _tenant;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ScopeConfigurationDocument> _projectedDocuments = new(StringComparer.Ordinal);
    // A scope's migrations cannot become pending again inside one process lifetime
    // (the migration assembly is fixed at build time), so the history read is paid
    // once per context type instead of once per attached context.
    private readonly HashSet<Type> _migrationsSettled = new();
    private readonly ConcurrentDictionary<Type, string[]> _schemaSettled = new();
    private readonly ConcurrentDictionary<Type, string> _schemaStamps = new();
    private (string Hash, string SchemaStamp)? _storedCompileStamp;
    private volatile EffectiveConfigurationSnapshot? _compiledSnapshot;
    private static readonly MethodInfo QueryMethod = typeof(ConfigurationProjectionCoordinator).GetMethod(nameof(QueryRows), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal ConfigurationProjectionCoordinator(ScopeConfigurationDocuments documents, IScopeStorageLocations locations,
        IContentStore content, IEnumerable<IConfigurationProjectionSource> sources, ITenantContextAccessor tenant)
    { _documents = documents; _locations = locations; _content = content; _sources = sources; _tenant = tenant; }

    // DI selects only public constructors; the source port is an implementation detail.
    public ConfigurationProjectionCoordinator(ScopeConfigurationDocuments documents, IScopeStorageLocations locations,
        IContentStore content, IServiceProvider services, ITenantContextAccessor tenant)
        : this(documents, locations, content, Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<IConfigurationProjectionSource>(services), tenant) { }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            if (_schemaSettled.TryGetValue(source.ContextType, out var settledDocIds))
            {
                var allUnchanged = true;
                foreach (var docId in settledDocIds)
                {
                    if (!_projectedDocuments.TryGetValue(docId, out var applied)
                        || !await _documents.IsProjectionUnchangedAsync(docId, applied, cancellationToken).ConfigureAwait(false))
                    {
                        allUnchanged = false;
                        break;
                    }
                }
                if (allUnchanged) continue;
            }
            await using var db = await source.CreateRawAsync(cancellationToken).ConfigureAwait(false);
            await AttachAsync(db, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<EffectiveConfigurationSnapshot> CompileAsync(CancellationToken cancellationToken = default)
    {
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var cached = _compiledSnapshot;
        if (cached is not null && string.Equals(cached.StorageId, _locations.StorageId, StringComparison.Ordinal))
        {
            var matchesAll = true;
            foreach (var id in _documents.DocumentIds)
            {
                var (_, fileHash, exists) = await _documents.HashForProjectionAsync(id, cancellationToken).ConfigureAwait(false);
                if (!exists && (id is "storage" or "logging" || id == "runtime" && _documents.RequiresRuntimeDocument))
                {
                    matchesAll = false;
                    break;
                }
                if (!cached.Documents.TryGetValue(id, out var cachedHash) || cachedHash != fileHash)
                {
                    matchesAll = false;
                    break;
                }
            }
            if (matchesAll)
            {
                return cached;
            }
        }

        var fastHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var allRequiredExist = true;
        foreach (var id in _documents.DocumentIds)
        {
            var (_, fileHash, exists) = await _documents.HashForProjectionAsync(id, cancellationToken).ConfigureAwait(false);
            if (!exists && (id is "storage" or "logging" || id == "runtime" && _documents.RequiresRuntimeDocument))
            {
                allRequiredExist = false;
                break;
            }
            fastHashes[id] = fileHash;
        }
        var compileSchemaStamp = _documents.RequiresRuntimeDocument ? "compile-v1-runtime=1" : "compile-v1-runtime=0";
        if (allRequiredExist)
        {
            var compositeHash = ScopeConfigurationDocuments.Hash(string.Join('\n', fastHashes.Select(x => x.Key + ":" + x.Value)));
            if (_storedCompileStamp is null && _sources.FirstOrDefault() is { } stampSource)
            {
                await using var stampDb = await stampSource.CreateRawAsync(cancellationToken).ConfigureAwait(false);
                var stamps = await LoadProjectionStampsAsync(stampDb, cancellationToken).ConfigureAwait(false);
                if (stamps.TryGetValue(CompileStampDocumentId, out var loadedCompile))
                    _storedCompileStamp = loadedCompile;
            }
            if (_storedCompileStamp is { } storedCompile
                && storedCompile.Hash == compositeHash
                && storedCompile.SchemaStamp == compileSchemaStamp)
            {
                var storageDoc = await _documents.ReadForProjectionAsync("storage", _projectedDocuments.GetValueOrDefault("storage"), cancellationToken).ConfigureAwait(false);
                ThrowDiagnostics(storageDoc);
                VerifyActiveStorage(storageDoc);
                foreach (var id in _documents.DocumentIds)
                {
                    if (fastHashes[id].Length > 0)
                        await _documents.MarkEstablishedAsync(id, cancellationToken).ConfigureAwait(false);
                }
                var fastSnapshot = new EffectiveConfigurationSnapshot(_locations.StorageId, compositeHash, fastHashes);
                _compiledSnapshot = fastSnapshot;
                return fastSnapshot;
            }
        }

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var models = new Dictionary<string, TomlTable>(StringComparer.Ordinal);
        foreach (var id in _documents.DocumentIds)
        {
            _projectedDocuments.TryGetValue(id, out var projected);
            var document = await _documents.ReadForProjectionAsync(id, projected, cancellationToken).ConfigureAwait(false);
            ThrowDiagnostics(document);
            if (document.ContentHash.Length == 0 && (id is "storage" or "logging"
                || id == "runtime" && _documents.RequiresRuntimeDocument))
                throw MissingDocument(id);
            VerifyActiveStorage(document);
            hashes[id] = document.ContentHash;
            if (document.ContentHash.Length > 0)
            {
                if (projected is null || projected.Text.Length == 0 && projected.ContentHash == document.ContentHash)
                    _projectedDocuments[id] = document;
                await _documents.MarkEstablishedAsync(id, cancellationToken).ConfigureAwait(false);
                models[id] = TomlSerializer.Deserialize<TomlTable>(document.Text)!;
            }
        }
        ConfigurationLiveSourceValidation.Validate(models);
        var snapshot = new EffectiveConfigurationSnapshot(_locations.StorageId, ScopeConfigurationDocuments.Hash(string.Join('\n', hashes.Select(x => x.Key + ":" + x.Value))), hashes);
        _compiledSnapshot = snapshot;
        if (_sources.FirstOrDefault() is { } primarySource)
        {
            try
            {
                await using var saveDb = await primarySource.CreateRawAsync(cancellationToken).ConfigureAwait(false);
                await UpsertProjectionStampsAsync(saveDb, [(CompileStampDocumentId, snapshot.ContentHash)], compileSchemaStamp, cancellationToken).ConfigureAwait(false);
                _storedCompileStamp = (snapshot.ContentHash, compileSchemaStamp);
            }
            catch (Exception)
            {
                // Best-effort validation stamp; database may be read-only or not yet initialized.
            }
        }
        return snapshot;
    }

    public async Task VerifyAsync(EffectiveConfigurationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.StorageId != _locations.StorageId)
            throw new ConfigurationDocumentException("configuration_scope_mismatch", "The configuration snapshot belongs to a different storage scope.");
        foreach (var pair in snapshot.Documents)
        {
            _projectedDocuments.TryGetValue(pair.Key, out var projected);
            var current = await _documents.ReadForProjectionAsync(pair.Key, projected, cancellationToken).ConfigureAwait(false);
            ThrowDiagnostics(current);
            VerifyActiveStorage(current);
            if (current.ContentHash != pair.Value)
                throw new ConfigurationDocumentException("configuration_changed_during_admission", "Configuration changed while the run was being admitted. Retry the submission.");
        }
    }

    private void VerifyActiveStorage(ScopeConfigurationDocument document)
    {
        if (document.Id != "storage" || _locations is not StorageScopeDescriptor scope) return;
        var model = TomlSerializer.Deserialize<TomlTable>(document.Text)!;
        var storage = (TomlTable)model["storage"];
        var backend = storage["backend"].ToString()!;
        var reference = storage.TryGetValue("postgres_connection_reference", out var value) ? value?.ToString() : null;
        if (!string.Equals(backend, scope.Backend, StringComparison.OrdinalIgnoreCase)
            || backend == "postgresql" && reference != scope.PostgresConnectionReference)
            throw new ConfigurationDocumentException("configuration_restart_required", scope.StorageId == "user"
                ? "Apply the user storage configuration and restart Core before admitting another run."
                : "Apply the project storage configuration through the host and reopen the project before admitting another run.");
    }

    internal async Task AttachAsync(ConfigurationProjectionDbContext db, CancellationToken cancellationToken)
    {
        var dbType = db.GetType();
        if (_schemaSettled.TryGetValue(dbType, out var settledDocIds))
        {
            var allUnchanged = true;
            foreach (var docId in settledDocIds)
            {
                if (!_projectedDocuments.TryGetValue(docId, out var applied)
                    || !await _documents.IsProjectionUnchangedAsync(docId, applied, cancellationToken).ConfigureAwait(false))
                {
                    allUnchanged = false;
                    break;
                }
                db.ConfigurationDocumentHashes[docId] = applied.ContentHash;
            }
            if (allUnchanged)
            {
                db.ConfigurationCoordinator = this;
                return;
            }
            db.ConfigurationDocumentHashes.Clear();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entities = SelectedEntities(db).ToArray();
            if (!_schemaSettled.ContainsKey(dbType))
            {
                // Migrations own the schema of any context that has them. This read runs
                // before the coordinated migrations, so reconciling columns from the
                // current model here adds a column that a pending migration is about to
                // add — the column lands, the history row never does, and every later
                // mount dies on "duplicate column name". Apply the pending migrations
                // first; bootstrap-owned contexts without migrations fall through to the
                // additive reconciliation, which then only fills what no migration covers.
                if (!_migrationsSettled.Contains(dbType))
                {
                    if ((await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
                    {
                        await DbContextSchemaBootstrapper.ClearStaleSqliteMigrationsLockAsync(db, cancellationToken).ConfigureAwait(false);
                        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                        _schemaStamps.TryRemove(dbType, out _);
                    }
                    _migrationsSettled.Add(dbType);
                }
                // Projection reads use the current EF model, so an older existing table
                // may still need columns that no migration declares before LoadRowsAsync.
                await DbContextSchemaBootstrapper.EnsureColumnsAsync(db, cancellationToken).ConfigureAwait(false);
                if (!await TablesExistAsync(db, entities, cancellationToken).ConfigureAwait(false)) return;
                _schemaSettled.TryAdd(dbType, entities.Select(e => DocumentId(db, e)).Distinct(StringComparer.Ordinal).ToArray());
            }
            var schemaStamp = await GetOrComputeSchemaStampAsync(db, dbType, entities, cancellationToken).ConfigureAwait(false);
            Dictionary<string, (string Hash, string SchemaStamp)>? storedStamps = null;
            Dictionary<Type, List<object>>? existing = null;
            List<(string DocumentId, string ContentHash)>? updatedStamps = null;
            foreach (var group in entities.GroupBy(e => DocumentId(db, e)))
            {
                _projectedDocuments.TryGetValue(group.Key, out var applied);
                if (applied is not null && await _documents.IsProjectionUnchangedAsync(group.Key, applied, cancellationToken).ConfigureAwait(false))
                { db.ConfigurationDocumentHashes[group.Key] = applied.ContentHash; continue; }

                if (applied is null)
                {
                    storedStamps ??= await LoadProjectionStampsAsync(db, cancellationToken).ConfigureAwait(false);
                    if (_storedCompileStamp is null && storedStamps.TryGetValue(CompileStampDocumentId, out var loadedCompileStamp))
                        _storedCompileStamp = loadedCompileStamp;
                    var (path, fileHash, exists) = await _documents.HashForProjectionAsync(group.Key, cancellationToken).ConfigureAwait(false);
                    if (exists && fileHash.Length > 0
                        && storedStamps.TryGetValue(group.Key, out var stored)
                        && stored.Hash == fileHash
                        && stored.SchemaStamp == schemaStamp)
                    {
                        await _documents.MarkEstablishedAsync(group.Key, cancellationToken).ConfigureAwait(false);
                        _projectedDocuments[group.Key] = new(group.Key, path, string.Empty, fileHash, [], 1);
                        db.ConfigurationDocumentHashes[group.Key] = fileHash;
                        continue;
                    }
                }

                var document = await _documents.ReadForProjectionAsync(group.Key, applied, cancellationToken).ConfigureAwait(false);
                ThrowDiagnostics(document);
                if (document.ContentHash.Length > 0 && applied?.ContentHash == document.ContentHash)
                { db.ConfigurationDocumentHashes[group.Key] = document.ContentHash; continue; }
                existing ??= await LoadRowsAsync(db, entities, cancellationToken).ConfigureAwait(false);
                if (document.ContentHash.Length == 0)
                {
                    if (group.Any(entity => existing[entity.ClrType].Count > 0)) throw MissingDocument(group.Key);
                    var initial = new TomlTable { ["schema_version"] = 1L, ["version"] = 1L };
                    foreach (var entity in group)
                        initial[entity.GetTableName()!] = await EncodeRowsAsync(entity, existing[entity.ClrType], cancellationToken).ConfigureAwait(false);
                    document = await _documents.SaveIfMatchAsync(group.Key, TomlSerializer.Serialize(initial), "", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var model = TomlSerializer.Deserialize<TomlTable>(document.Text)!;
                    ValidateSchema(model, group.Key);
                    await ApplyProjectionAsync(db, group.ToArray(), existing, model, cancellationToken).ConfigureAwait(false);
                }
                await _documents.MarkEstablishedAsync(group.Key, cancellationToken).ConfigureAwait(false);
                db.ConfigurationDocumentHashes[group.Key] = document.ContentHash;
                _projectedDocuments[group.Key] = document;
                (updatedStamps ??= []).Add((group.Key, document.ContentHash));
            }
            if (updatedStamps is { Count: > 0 })
                await UpsertProjectionStampsAsync(db, updatedStamps, schemaStamp, cancellationToken).ConfigureAwait(false);
            db.ConfigurationCoordinator = this;
        }
        finally { _gate.Release(); }
    }

    private static ConfigurationDocumentException MissingDocument(string id) => new("configuration_missing",
        "Configuration file '" + id + ".toml' is missing. Restore it or save a valid replacement; SQL history cannot supply current configuration.");

    internal async Task PersistChangesAsync(ConfigurationProjectionDbContext db, CancellationToken cancellationToken)
    {
        db.ChangeTracker.DetectChanges();
        var selected = SelectedEntities(db).ToDictionary(e => e.ClrType);
        var changes = db.ChangeTracker.Entries().Where(e => selected.ContainsKey(e.Entity.GetType())
            && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (changes.Length == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var group in changes.GroupBy(e => DocumentId(db, e.Metadata)))
            {
                var id = group.Key;
                var document = await _documents.ReadAsync(id, cancellationToken).ConfigureAwait(false);
                ThrowDiagnostics(document);
                if (db.ConfigurationDocumentHashes.TryGetValue(id, out var expected) && expected != document.ContentHash)
                    throw new ConfigurationDocumentException("configuration_conflict", "Configuration changed since this edit began. Reload before saving.");
                var metadata = new TomlMetadataStore();
                var options = new TomlSerializerOptions { MetadataStore = metadata };
                var model = document.ContentHash.Length == 0 ? new TomlTable { ["schema_version"] = 1L }
                    : TomlSerializer.Deserialize<TomlTable>(document.Text, options)!;
                foreach (var entityGroup in group.GroupBy(e => e.Metadata))
                {
                    var entity = entityGroup.Key;
                    var key = entity.GetTableName()!;
                    var array = model.TryGetValue(key, out var value) && value is TomlTableArray tables ? tables : new TomlTableArray();
                    model[key] = array;
                    foreach (var entry in entityGroup)
                    {
                        var matching = array.FirstOrDefault(t => RowKey(entity, t) == EntityKey(entity, entry.Entity));
                        if (entry.State == EntityState.Deleted) { if (matching is not null) array.Remove(matching); continue; }
                        var replacement = await EncodeRowAsync(entity, entry.Entity, cancellationToken).ConfigureAwait(false);
                        if (matching is not null) MergeTable(matching, replacement); else array.Add(replacement);
                    }
                }
                model["version"] = Convert.ToInt64(model.TryGetValue("version", out var revision) ? revision : 0, CultureInfo.InvariantCulture) + 1;
                var updated = await _documents.SaveIfMatchAsync(id, TomlSerializer.Serialize(model, options), document.ContentHash, cancellationToken).ConfigureAwait(false);
                db.ConfigurationDocumentHashes[id] = updated.ContentHash;
                db.PendingProjectionStampUpdates[id] = updated.ContentHash;
                // The file is durable even if the following SQL projection update fails.
                // A later read always repairs that projection from these authoritative bytes.
                _projectedDocuments.TryRemove(id, out _);
                _compiledSnapshot = null;
                _storedCompileStamp = null;
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task RecordSavedProjectionStampsAsync(ConfigurationProjectionDbContext db, CancellationToken cancellationToken)
    {
        if (db.PendingProjectionStampUpdates.Count == 0) return;
        var updates = db.PendingProjectionStampUpdates.Select(pair => (pair.Key, pair.Value)).ToArray();
        db.PendingProjectionStampUpdates.Clear();
        var entities = SelectedEntities(db).ToArray();
        var schemaStamp = await GetOrComputeSchemaStampAsync(db, db.GetType(), entities, cancellationToken).ConfigureAwait(false);
        await UpsertProjectionStampsAsync(db, updates, schemaStamp, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyProjectionAsync(ConfigurationProjectionDbContext db, IEntityType[] entities,
        Dictionary<Type, List<object>> existing, TomlTable model, CancellationToken cancellationToken)
    {
        var incoming = new Dictionary<IEntityType, List<object>>();
        foreach (var entity in entities)
        {
            var rows = new List<object>();
            if (model.TryGetValue(entity.GetTableName()!, out var value))
            {
                if (value is not TomlTableArray array) throw Invalid(entity.GetTableName()!, "Expected an array of tables.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var table in array)
                {
                    var row = DecodeRow(entity, table);
                    await MaterializeContentAsync(entity, row, table, cancellationToken).ConfigureAwait(false);
                    if (!keys.Add(EntityKey(entity, row))) throw Invalid(entity.GetTableName()!, "Duplicate primary key.");
                    rows.Add(row);
                }
            }
            incoming[entity] = rows;
        }
        db.ApplyingConfigurationProjection = true;
        try
        {
            var isNpgsql = db.Database.IsNpgsql();
            foreach (var (entity, rows) in incoming)
            {
                var byKey = existing[entity.ClrType].ToDictionary(r => EntityKey(entity, r), StringComparer.Ordinal);
                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var key = EntityKey(entity, row); live.Add(key);
                    if (byKey.TryGetValue(key, out var current))
                    {
                        // Published history is immutable. Edits must use a fresh version identity.
                        if (IsHistorical(entity) && !Same(entity, current, row, isNpgsql))
                            throw Invalid(entity.GetTableName()!, "Published versions are immutable; create a new version id instead of editing historical content.");
                        if (AllPropertiesSame(entity, current, row, isNpgsql))
                            continue;
                        db.Attach(current);
                        db.Entry(current).CurrentValues.SetValues(row);
                    }
                    else db.Add(row);
                }
                foreach (var row in byKey.Where(p => !live.Contains(p.Key)).Select(p => p.Value))
                {
                    if (!IsHistorical(entity)) db.Remove(row);
                    else if (entity.ClrType.GetProperty("Status") is { CanWrite: true } status)
                    {
                        // Retain immutable history, but stop published-version selectors
                        // from silently using a version retired from the editing authority.
                        db.Attach(row); status.SetValue(row, "retired");
                    }
                }
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
        catch (DbUpdateException ex)
        { throw new ConfigurationDocumentException("configuration_projection_invalid", "Configuration violates its relational constraints: " + ex.GetBaseException().Message); }
        finally { db.ApplyingConfigurationProjection = false; }
    }

    private async Task<TomlTableArray> EncodeRowsAsync(IEntityType entity, IEnumerable<object> rows, CancellationToken cancellationToken)
    {
        var result = new TomlTableArray();
        foreach (var row in rows.OrderBy(r => EntityKey(entity, r), StringComparer.Ordinal)) result.Add(await EncodeRowAsync(entity, row, cancellationToken).ConfigureAwait(false));
        return result;
    }

    private async Task<TomlTable> EncodeRowAsync(IEntityType entity, object row, CancellationToken cancellationToken)
    {
        var table = new TomlTable();
        foreach (var property in entity.GetProperties())
        {
            var value = property.PropertyInfo?.GetValue(row);
            if (value is null) continue;
            if (property.Name.EndsWith("Json", StringComparison.Ordinal) && value is string json)
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                if (IsHistorical(entity)) { table[Key(property)] = json; continue; }
                using var parsed = JsonDocument.Parse(json);
                object? native;
                try { native = ConfigurationTomlValues.FromJsonElement(parsed.RootElement, IsToolSettings(entity, property)); }
                catch (InvalidDataException error) { throw Invalid(entity.GetTableName()! + "." + Key(property), error.Message); }
                if (native is null) continue;
                table[Key(property)] = native;
            }
            else table[Key(property)] = Scalar(value);
            if (IsContentReference(property.Name) && value is string reference
                && reference.StartsWith("content/", StringComparison.Ordinal))
            {
                await using var stream = await _content.OpenReadAsync(new(reference, "", 0, "application/json"), cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                table[ContentKey(property.Name)] = text;
                // Keep the immutable relative identity as metadata, while the embedded
                // body is sufficient to recreate the blob in a different physical scope.
            }
        }
        return table;
    }

    private async Task MaterializeContentAsync(IEntityType entity, object row, TomlTable table, CancellationToken cancellationToken)
    {
        foreach (var property in entity.GetProperties().Where(p => IsContentReference(p.Name)))
        {
            if (!table.TryGetValue(ContentKey(property.Name), out var native)) continue;
            var text = native is string sourceText ? sourceText : ConfigurationTomlValues.ToJsonElement(native).GetRawText();
            using var validatedJson = JsonDocument.Parse(text);
            var tenant = Get<Guid?>(row, "TenantId") ?? _tenant.Current.TenantId;
            Guid? workspace = Get<Guid?>(row, "WorkspaceId") ?? _tenant.Current.WorkspaceId;
            var kind = entity.GetTableName() switch { "model_provider_versions" => "model-config", "prompt_fragment_versions" => "prompt-fragment", _ => "configuration" };
            var original = property.PropertyInfo!.GetValue(row) as string;
            var expectedHash = string.Empty;
            if (!string.IsNullOrEmpty(original))
            {
                var segments = original.Split('/');
                if (segments.Length != 6 || segments[0] != "content" || segments[1] != "tenants"
                    || !Guid.TryParse(segments[2], out tenant) || (segments[3] != "tenant" && !Guid.TryParse(segments[3], out _)))
                    throw Invalid(entity.GetTableName()!, "Embedded content has an invalid immutable reference.");
                workspace = segments[3] == "tenant" ? null : Guid.Parse(segments[3]); kind = segments[4];
                expectedHash = segments[5];
                if (ScopeConfigurationDocuments.Hash(text) != expectedHash)
                    throw Invalid(entity.GetTableName()!, "Embedded content does not match its immutable reference hash.");
            }
            ContentReference reference;
            if (!string.IsNullOrEmpty(original)
                && await _content.ExistsAsync(new(original, expectedHash, 0, "application/json"), cancellationToken).ConfigureAwait(false))
            {
                reference = new(original, expectedHash, Encoding.UTF8.GetByteCount(text), "application/json");
            }
            else
            {
                await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                reference = await _content.PutAsync(new(tenant, workspace, kind, "application/json", stream), cancellationToken).ConfigureAwait(false);
            }
            property.PropertyInfo!.SetValue(row, reference.Value);
            if (!string.IsNullOrEmpty(original) && reference.Value != original)
                throw Invalid(entity.GetTableName()!, "Embedded content changed its immutable reference.");
            if (property.Name == "ContentReference") { Set(row, "ContentHash", reference.Sha256); Set(row, "ContentLength", reference.Length); }
            if (property.Name == "ConfigReference") Set(row, "ConfigHash", reference.Sha256);
            if (property.Name is "ManifestReference" or "ManifestContentReference")
            {
                if (string.IsNullOrEmpty(Get<string>(row, "ManifestHash"))) Set(row, "ManifestHash", reference.Sha256);
                Set(row, "ManifestLength", reference.Length);
            }
        }
    }

    internal static object DecodeRow(IEntityType entity, TomlTable table)
    {
        var row = Activator.CreateInstance(entity.ClrType)!;
        foreach (var property in entity.GetProperties())
        {
            var key = Key(property);
            if (!table.TryGetValue(key, out var value))
            {
                if (property.IsNullable) property.PropertyInfo?.SetValue(row, null);
                continue;
            }
            try
            {
                var target = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                var decoded = property.Name.EndsWith("Json", StringComparison.Ordinal) ? IsHistorical(entity) && value is string frozenJson ? frozenJson : ConfigurationTomlValues.ToJsonElement(value, IsToolSettings(entity, property)).GetRawText()
                    : target == typeof(string) ? value is string s ? s : throw new InvalidDataException("Expected a string.")
                    : target == typeof(Guid) ? Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
                    : target == typeof(DateTimeOffset) ? DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
                    : target == typeof(DateTime) ? DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
                    : target.IsEnum ? Enum.Parse(target, Convert.ToString(value, CultureInfo.InvariantCulture)!, ignoreCase: true)
                    : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
                property.PropertyInfo!.SetValue(row, decoded);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException or InvalidDataException)
            { throw Invalid(entity.GetTableName()! + "." + key, "Value does not match the configuration field type."); }
        }
        foreach (var key in table.Keys)
            if (!entity.GetProperties().Any(p => Key(p) == key || IsContentReference(p.Name) && ContentKey(p.Name) == key))
                throw Invalid(entity.GetTableName()! + "." + key, "Unknown configuration field.");
        return row;
    }

    internal static IEnumerable<IEntityType> SelectedEntities(DbContext db) => db.Model.GetEntityTypes().Where(e =>
        e.GetTableName() is not ("prompt_fragment_signals" or "extension_catalog_entries" or "market_install_proposals"
            or "agent_pack_previews" or "agent_pack_operations" or "tool_mcp_imports"));
    internal static string DocumentId(DbContext db, IEntityType entity) => db.GetType().Name switch
    {
        "AgentConfigurationDbContext" => "agents", "ModelControlDbContext" => "models", "PromptControlDbContext" => "prompts",
        "ToolsSettingsDbContext" => entity.GetTableName() == "tool_mcp_resources" ? "mcp" : "tools", "IntegrationDbContext" => "skills",
        _ => throw new InvalidOperationException("Unknown configuration projection context.")
    };
    internal static bool IsHistorical(IEntityType entity) => entity.GetTableName()!.EndsWith("_versions", StringComparison.Ordinal);
    internal static bool Same(IEntityType entity, object a, object b, bool postgreSql = false) => entity.GetProperties().Where(p => p.Name is not
        ("Status" or "Revision" or "UpdatedAt" or "UpdatedByPrincipalId" or "ArchivedAt" or "DeletedAt")).All(p =>
        PropertySame(p, a, b, postgreSql));

    private static bool AllPropertiesSame(IEntityType entity, object a, object b, bool postgreSql) =>
        entity.GetProperties().All(p => PropertySame(p, a, b, postgreSql));

    private static bool PropertySame(IProperty p, object a, object b, bool postgreSql) =>
        p.Name.EndsWith("Json", StringComparison.Ordinal)
            ? JsonSame(p.PropertyInfo?.GetValue(a) as string, p.PropertyInfo?.GetValue(b) as string)
            : ScalarSame(p.PropertyInfo?.GetValue(a), p.PropertyInfo?.GetValue(b), postgreSql);
    private static bool ScalarSame(object? left, object? right, bool postgreSql)
    {
        // PostgreSQL/Npgsql retains microseconds, while TOML retains .NET's 100ns
        // ticks. Compare at the actual database precision without rewriting the
        // source or relaxing any other immutable version field.
        const long postgresEpochTicks = 630822816000000000L;
        if (postgreSql && left is DateTimeOffset leftOffset && right is DateTimeOffset rightOffset)
            return (leftOffset.UtcTicks - postgresEpochTicks) / 10 == (rightOffset.UtcTicks - postgresEpochTicks) / 10;
        if (postgreSql && left is DateTime leftDate && right is DateTime rightDate)
            return (leftDate.Ticks - postgresEpochTicks) / 10 == (rightDate.Ticks - postgresEpochTicks) / 10;
        return Equals(left, right);
    }
    private static bool JsonSame(string? a, string? b)
    {
        if (a == b) return true;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        using var left = JsonDocument.Parse(a); using var right = JsonDocument.Parse(b);
        return JsonElement.DeepEquals(left.RootElement, right.RootElement);
    }
    private static string Key(IProperty property) => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name.EndsWith("Json", StringComparison.Ordinal) ? property.Name[..^4] : property.Name);
    internal static bool IsContentReference(string name) => name is "ContentReference" or "ConfigReference" or "ManifestReference" or "ManifestContentReference";
    internal static string ContentKey(string name) => name switch { "ContentReference" => "content", "ConfigReference" => "config", "ManifestReference" or "ManifestContentReference" => "manifest", _ => "__not_content" };
    internal static string EntityKey(IEntityType entity, object row) => string.Join('|', entity.FindPrimaryKey()!.Properties.Select(p => Convert.ToString(p.PropertyInfo!.GetValue(row), CultureInfo.InvariantCulture)));
    private static string RowKey(IEntityType entity, TomlTable row) => string.Join('|', entity.FindPrimaryKey()!.Properties.Select(p => row.TryGetValue(Key(p), out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""));
    private static object Scalar(object value) => value switch { Guid g => g.ToString(), DateTimeOffset date => date.ToString("O"), DateTime date => date.ToString("O"), Enum e => e.ToString(), int i => (long)i, _ => value };
    private static T? Get<T>(object row, string property) => (T?)row.GetType().GetProperty(property)?.GetValue(row);
    private static void Set(object row, string property, object value) => row.GetType().GetProperty(property)?.SetValue(row, value);
    private static ConfigurationDocumentException Invalid(string path, string message) => new("configuration_invalid", "Configuration validation failed.", [new("configuration_schema", path + ": " + message)]);
    private static void ThrowDiagnostics(ScopeConfigurationDocument document)
    {
        if (document.Diagnostics.Any(d => d.Severity == "error"))
            throw new ConfigurationDocumentException(document.Diagnostics.Any(d => d.Code == "configuration_missing")
                ? "configuration_missing" : "configuration_invalid", "Configuration document '" + document.Id + "' is invalid.", document.Diagnostics);
    }
    internal static void ValidateSchema(TomlTable table, string id)
    { if (!table.TryGetValue("schema_version", out var schema) || Convert.ToInt64(schema, CultureInfo.InvariantCulture) != 1) throw Invalid(id, "schema_version must be 1."); }

    private static bool IsToolSettings(IEntityType entity, IProperty property) => entity.GetTableName() == "tool_settings" && property.Name == "SettingsJson";
    private static void MergeTable(TomlTable current, TomlTable replacement)
    {
        foreach (var key in current.Keys.Where(k => !replacement.ContainsKey(k)).ToArray()) current.Remove(key);
        foreach (var (key, value) in replacement)
        {
            if (current.TryGetValue(key, out var old) && old is TomlTable oldTable && value is TomlTable newTable) MergeTable(oldTable, newTable);
            else current[key] = value;
        }
    }
    internal static async Task<Dictionary<Type, List<object>>> LoadRowsAsync(DbContext db, IEntityType[] entities, CancellationToken cancellationToken)
    { var result = new Dictionary<Type, List<object>>(); foreach (var e in entities) result[e.ClrType] = await (Task<List<object>>)QueryMethod.MakeGenericMethod(e.ClrType).Invoke(null, [db, cancellationToken])!; return result; }
    private static async Task<List<object>> QueryRows<T>(DbContext db, CancellationToken cancellationToken) where T : class =>
        (await db.Set<T>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).Cast<object>().ToList();
    internal static async Task<bool> TablesExistAsync(DbContext db, IEntityType[] entities, CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)) return false;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (db.Database.IsSqlite())
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    existing.Add(reader.GetString(0));
                return entities.All(e => existing.Contains(e.GetTableName()!));
            }
            foreach (var entity in entities)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name=@table";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@table"; parameter.Value = entity.GetTableName()!; command.Parameters.Add(parameter);
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0) return false;
            }
            return true;
        }
        finally { if (opened) await connection.CloseAsync().ConfigureAwait(false); }
    }

    private async Task<string> GetOrComputeSchemaStampAsync(
        DbContext db,
        Type dbType,
        IEntityType[] entities,
        CancellationToken cancellationToken)
    {
        if (_schemaStamps.TryGetValue(dbType, out var cached)) return cached;
        var sb = new StringBuilder();
        try
        {
            foreach (var migration in await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
                sb.Append(migration).Append('\n');
        }
        catch (Exception)
        {
            // Contexts without migration history hash solely by their current EF model signature.
        }
        foreach (var entity in entities.OrderBy(e => e.GetTableName(), StringComparer.Ordinal))
        {
            sb.Append(entity.GetTableName()).Append(':');
            foreach (var prop in entity.GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
                sb.Append(prop.Name).Append('=').Append(prop.ClrType.FullName).Append(',');
            sb.Append('\n');
        }
        var stamp = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        _schemaStamps[dbType] = stamp;
        return stamp;
    }

    private static async Task<Dictionary<string, (string Hash, string SchemaStamp)>> LoadProjectionStampsAsync(
        DbContext db,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string Hash, string SchemaStamp)>(StringComparer.Ordinal);
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var existsCmd = connection.CreateCommand();
            existsCmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            existsCmd.CommandText = db.Database.IsSqlite()
                ? "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table"
                : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name=@table";
            var tableParam = existsCmd.CreateParameter();
            tableParam.ParameterName = "@table";
            tableParam.Value = StampTableName;
            existsCmd.Parameters.Add(tableParam);
            if (Convert.ToInt64(await existsCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
                return result;

            await using var selectCmd = connection.CreateCommand();
            selectCmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            selectCmd.CommandText = $"SELECT \"document_id\", \"content_hash\", \"schema_stamp\" FROM \"{StampTableName}\"";
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
            return result;
        }
        finally { if (opened) await connection.CloseAsync().ConfigureAwait(false); }
    }

    private static async Task UpsertProjectionStampsAsync(
        DbContext db,
        IReadOnlyCollection<(string DocumentId, string ContentHash)> updates,
        string schemaStamp,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0) return;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var createCmd = connection.CreateCommand();
            createCmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            createCmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS "{StampTableName}" (
                    "document_id" TEXT NOT NULL PRIMARY KEY,
                    "content_hash" TEXT NOT NULL,
                    "schema_stamp" TEXT NOT NULL
                )
                """;
            await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var (docId, contentHash) in updates)
            {
                await using var upsertCmd = connection.CreateCommand();
                upsertCmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                upsertCmd.CommandText = $"""
                    INSERT INTO "{StampTableName}" ("document_id", "content_hash", "schema_stamp")
                    VALUES (@doc, @hash, @schema)
                    ON CONFLICT("document_id") DO UPDATE SET
                        "content_hash" = excluded."content_hash",
                        "schema_stamp" = excluded."schema_stamp"
                    """;
                var pDoc = upsertCmd.CreateParameter(); pDoc.ParameterName = "@doc"; pDoc.Value = docId; upsertCmd.Parameters.Add(pDoc);
                var pHash = upsertCmd.CreateParameter(); pHash.ParameterName = "@hash"; pHash.Value = contentHash; upsertCmd.Parameters.Add(pHash);
                var pSchema = upsertCmd.CreateParameter(); pSchema.ParameterName = "@schema"; pSchema.Value = schemaStamp; upsertCmd.Parameters.Add(pSchema);
                await upsertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { if (opened) await connection.CloseAsync().ConfigureAwait(false); }
    }
}
