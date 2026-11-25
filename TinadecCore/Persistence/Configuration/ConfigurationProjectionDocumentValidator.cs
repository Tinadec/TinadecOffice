using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Persistence;

internal sealed class ConfigurationProjectionDocumentValidator(string id, IEnumerable<IConfigurationProjectionSource> sources) : IConfigurationDocumentValidator
{
    public string DocumentId => id;
    public IReadOnlyList<ConfigurationDiagnostic> Validate(string text)
    {
        if (text.Length == 0) return [new("configuration_schema", "schema_version must be 1; an empty file is not a configuration document.")];
        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(text)!;
            ConfigurationProjectionCoordinator.ValidateSchema(model, id);
            var known = new HashSet<string>(["schema_version", "version"], StringComparer.Ordinal);
            foreach (var source in sources)
            {
                if (source.ContextType.Name switch
                    { "AgentConfigurationDbContext" => id != "agents", "ModelControlDbContext" => id != "models",
                      "PromptControlDbContext" => id != "prompts", "ToolsSettingsDbContext" => id is not ("tools" or "mcp"),
                      "IntegrationDbContext" => id != "skills", _ => true }) continue;
                using var db = source.CreateRawAsync(CancellationToken.None).GetAwaiter().GetResult();
                var historical = ConfigurationProjectionCoordinator.SelectedEntities(db).Where(e => ConfigurationProjectionCoordinator.IsHistorical(e)
                    && ConfigurationProjectionCoordinator.DocumentId(db, e) == id).ToArray();
                var old = ConfigurationProjectionCoordinator.TablesExistAsync(db, historical, CancellationToken.None).GetAwaiter().GetResult()
                    ? ConfigurationProjectionCoordinator.LoadRowsAsync(db, historical, CancellationToken.None).GetAwaiter().GetResult() : [];
                foreach (var entity in ConfigurationProjectionCoordinator.SelectedEntities(db)
                    .Where(e => ConfigurationProjectionCoordinator.DocumentId(db, e) == id))
                {
                    var name = entity.GetTableName()!; known.Add(name);
                    if (!model.TryGetValue(name, out var value)) continue;
                    if (value is not TomlTableArray rows)
                        return [new("configuration_schema", name + ": expected an array of tables.")];
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    var unique = entity.GetIndexes().Where(index => index.IsUnique)
                        .Select(index => (Index: index, Applies: UniqueIndexPredicate(entity, index),
                            Values: new HashSet<string>(StringComparer.Ordinal))).ToArray();
                    Dictionary<string, object>? previousByKey = null;
                    if (ConfigurationProjectionCoordinator.IsHistorical(entity) && old.TryGetValue(entity.ClrType, out var previous))
                    {
                        previousByKey = new Dictionary<string, object>(previous.Count, StringComparer.Ordinal);
                        foreach (var r in previous)
                            previousByKey.TryAdd(ConfigurationProjectionCoordinator.EntityKey(entity, r), r);
                    }
                    var isNpgsql = db.Database.IsNpgsql();
                    foreach (var table in rows)
                    {
                        var row = ConfigurationProjectionCoordinator.DecodeRow(entity, table);
                        var key = ConfigurationProjectionCoordinator.EntityKey(entity, row);
                        if (key.Length == 0 || entity.FindPrimaryKey()!.Properties.Any(p => p.PropertyInfo!.GetValue(row) is Guid g && g == Guid.Empty))
                            return [new("configuration_schema", name + ": primary key is required.")];
                        if (!keys.Add(key)) return [new("configuration_schema", name + ": duplicate primary key.")];
                        foreach (var (index, applies, values) in unique)
                        {
                            if (!applies(row)) continue;
                            var fields = index.Properties.Select(property => property.PropertyInfo!.GetValue(row)).ToArray();
                            // Both supported SQL providers permit several null values in unique indexes.
                            if (fields.Any(field => field is null)) continue;
                            if (!values.Add(JsonSerializer.Serialize(fields)))
                                return [new("configuration_unique", name + ": duplicate unique identity (" + string.Join(", ", index.Properties.Select(property => property.Name)) + ").")];
                        }
                        foreach (var property in entity.GetProperties())
                        {
                            if (property.PropertyInfo!.GetValue(row) is string field && property.GetMaxLength() is { } max && field.Length > max)
                                return [new("configuration_schema", name + "." + property.Name + ": value exceeds the supported length.")];
                            if (property.Name.EndsWith("Json", StringComparison.Ordinal) && property.PropertyInfo.GetValue(row) is string json && !string.IsNullOrWhiteSpace(json))
                                using (JsonDocument.Parse(json)) { }
                        }
                        foreach (var contentKey in new[] { "content", "config", "manifest" })
                            if (table.TryGetValue(contentKey, out var body) && body is string bodyText)
                            {
                                using var content = JsonDocument.Parse(bodyText);
                                var hashProperty = contentKey switch { "content" => "ContentHash", "config" => "ConfigHash", _ => "ManifestHash" };
                                var expected = entity.ClrType.GetProperty(hashProperty)?.GetValue(row) as string;
                                var reference = entity.GetProperties().FirstOrDefault(p => ConfigurationProjectionCoordinator.IsContentReference(p.Name)
                                    && ConfigurationProjectionCoordinator.ContentKey(p.Name) == contentKey)?.PropertyInfo!.GetValue(row) as string;
                                // Agent pack manifests use a semantic envelope digest in ManifestHash;
                                // their content-addressed reference retains the exact stored-byte digest.
                                var integrityHash = reference?.StartsWith("content/", StringComparison.Ordinal) == true ? reference.Split('/').Last()
                                    : contentKey == "manifest" ? null : expected;
                                if (!string.IsNullOrEmpty(integrityHash) && ScopeConfigurationDocuments.Hash(bodyText) != integrityHash)
                                    return [new("configuration_content_integrity", name + "." + contentKey + ": body does not match its content hash.")];
                                if (id is "models" or "mcp" && ContainsPlainCredential(content.RootElement))
                                    return [new("configuration_secret_literal", "Credentials must use the user SecretStore reference; credential literals are not accepted in configuration files.")];
                            }
                        if (previousByKey is not null && previousByKey.TryGetValue(key, out var existing))
                        {
                            // Embedded content is materialized after validation; compare its immutable source metadata separately.
                            foreach (var reference in new[] { "ContentReference", "ConfigReference", "ManifestReference", "ManifestContentReference" })
                                if (entity.ClrType.GetProperty(reference) is { } p && string.IsNullOrEmpty(p.GetValue(row) as string)) p.SetValue(row, p.GetValue(existing));
                            if (!ConfigurationProjectionCoordinator.Same(entity, existing, row, isNpgsql))
                                return [new("configuration_version_immutable", name + ": published version ids are immutable; use a new version identity.")];
                        }
                    }
                }
            }
            var unknown = model.Keys.FirstOrDefault(key => !known.Contains(key));
            return unknown is null ? [] : [new("configuration_schema", unknown + ": unknown configuration table.")];
        }
        catch (ConfigurationDocumentException ex) { return ex.Diagnostics; }
        catch (Exception ex) when (ex is TomlException or JsonException or InvalidDataException or FormatException or OverflowException or ArgumentException)
        { return [new("configuration_schema", ex.Message)]; }
    }

    internal static Func<object, bool> UniqueIndexPredicate(IReadOnlyEntityType entity, IReadOnlyIndex index)
    {
        var filter = index.GetFilter();
        if (string.IsNullOrWhiteSpace(filter)) return _ => true;
        // These are the predicates owned by the configuration models. Do not
        // treat an unfamiliar SQL expression as either an unfiltered index or
        // an index that can be ignored: reject before the file commit instead.
        var status = Regex.IsMatch(filter, "^\\s*(?i:status|\"status\"|\\[status\\])\\s*=\\s*'draft'\\s*$",
            RegexOptions.CultureInvariant);
        var deleted = Regex.IsMatch(filter, "^\\s*(?i:deleted_at|\"deleted_at\"|\\[deleted_at\\])\\s+(?i:IS\\s+NULL)\\s*$",
            RegexOptions.CultureInvariant);
        var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
        var property = status || deleted ? entity.GetProperties().FirstOrDefault(p => table is { } store
            && p.GetColumnName(store) == (status ? "status" : "deleted_at")) : null;
        if (property?.PropertyInfo is not { } member || status && property.ClrType != typeof(string))
            throw new ConfigurationDocumentException("configuration_unique_filter_unsupported",
                "Configuration validation cannot evaluate a unique-index filter.",
                [new("configuration_unique_filter_unsupported", entity.GetTableName() + ": unsupported unique-index filter '" + filter + "'.")]);
        return status ? row => string.Equals(member.GetValue(row) as string, "draft", StringComparison.Ordinal)
            : row => member.GetValue(row) is null;
    }

    private static bool ContainsPlainCredential(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(ContainsPlainCredential);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is "api_key" or "password" or "access_token" or "refresh_token" or "client_secret" or "authorization"
                && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)) return true;
            if (ContainsPlainCredential(property.Value)) return true;
        }
        return false;
    }
}
