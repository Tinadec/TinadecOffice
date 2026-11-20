using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Prompts;

/// <summary>
/// Prompts module registrar. Registers prompt fragment storage, frozen fragment snapshots, and prompt assembler.
/// </summary>
public sealed class PromptsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "prompts";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<PromptControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<PromptControlDbContext>>();
        builder.Services.AddSingleton<PromptFragmentService>();
        builder.Services.AddSingleton<IPromptFragmentService>(sp => sp.GetRequiredService<PromptFragmentService>());
        builder.Services.AddSingleton<IFrozenPromptFragmentSource>(sp => sp.GetRequiredService<PromptFragmentService>());
        builder.Services.AddSingleton<IPromptAssembler, PromptAssembler>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies", "persistence"],
            Capabilities = ["fragment_assembly", "agent_instructions", "skill_contributions", "deterministic_assembly", "pipeline_graph_execution"],
            Language = "C#",
            MafPrimitives = [],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

internal static class PromptContentCodec
{
    public static string UnwrapFragmentText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var current = raw;
        for (var depth = 0; depth < 4; depth++)
        {
            var trimmed = current.Trim();
            if (trimmed.Length < 2 || (trimmed[0] != '"' && trimmed[0] != '{'))
                break;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.String)
                {
                    current = doc.RootElement.GetString() ?? string.Empty;
                    continue;
                }
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    {
                        current = contentEl.GetString() ?? string.Empty;
                        continue;
                    }
                    if (doc.RootElement.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        current = textEl.GetString() ?? string.Empty;
                        continue;
                    }
                }
                break;
            }
            catch (JsonException)
            {
                break;
            }
        }
        return current;
    }

    public static async Task<string> ReadTextAsync(
        IContentStore contentStore,
        PromptFragmentVersionRecord version,
        CancellationToken cancellationToken)
    {
        await using var stream = await contentStore.OpenReadAsync(
            new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"),
            cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return UnwrapFragmentText(raw);
    }

    public static async Task<ContentReference> WriteJsonStringAsync(
        IContentStore contentStore,
        Guid tenantId,
        Guid? workspaceId,
        string content,
        CancellationToken cancellationToken)
    {
        // Keep prompts.toml projection valid (ConfigurationProjectionDocumentValidator parses content as JSON).
        var jsonText = JsonSerializer.Serialize(content ?? string.Empty);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonText));
        return await contentStore.PutAsync(
            new ContentWriteRequest(tenantId, workspaceId, "prompt-fragment", "application/json", stream),
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class PromptFragmentService : IPromptFragmentService, IFrozenPromptFragmentSource
{
    private readonly IDbContextFactory<PromptControlDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly IAgentIdentityLookup? _agentLookup;

    public PromptFragmentService(
        IDbContextFactory<PromptControlDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant,
        IAgentIdentityLookup? agentLookup = null)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
        _agentLookup = agentLookup;
    }

    public async Task<IReadOnlyList<FrozenPromptFragment>> SnapshotActiveFragmentsAsync(
        Guid tenantId,
        Guid? workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Fragments.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.Enabled
                && x.DeletedAt == null
                && (x.WorkspaceId == null || x.WorkspaceId == workspaceId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return [];

        var versionIds = rows.Select(x => x.CurrentVersionId).Distinct().ToArray();
        var versions = await db.Versions.AsNoTracking()
            .Where(x => versionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);

        var ordered = rows
            .OrderBy(f => PromptAssembler.CategoryOrder(f.Category))
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ThenBy(f => f.Id)
            .ToArray();

        var result = new List<FrozenPromptFragment>(ordered.Length);
        foreach (var row in ordered)
        {
            if (!versions.TryGetValue(row.CurrentVersionId, out var version)) continue;
            var text = await PromptContentCodec.ReadTextAsync(_content, version, cancellationToken).ConfigureAwait(false);
            result.Add(new FrozenPromptFragment(
                row.Id,
                row.Key,
                row.Title,
                row.Scope,
                row.TargetAgentId,
                row.TargetAgentSlug,
                row.Category,
                row.Priority,
                text,
                version.Version,
                version.ContentHash));
        }
        return result;
    }

    public async Task<IReadOnlyList<PromptFragmentDto>> ListAsync(
        string? scope = null,
        Guid? targetAgentId = null,
        string? targetAgentSlug = null,
        string? category = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Fragments.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId
                && (x.WorkspaceId == null || x.WorkspaceId == tenant.WorkspaceId)
                && x.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(scope))
        {
            var s = scope.Trim();
            query = query.Where(x => x.Scope == s);
        }
        if (targetAgentId is { } aid)
            query = query.Where(x => x.TargetAgentId == aid);
        if (!string.IsNullOrWhiteSpace(targetAgentSlug))
        {
            var slug = targetAgentSlug.Trim();
            query = query.Where(x => x.TargetAgentSlug == slug);
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim();
            query = query.Where(x => x.Category == cat);
        }
        if (enabled is { } isEnabled)
            query = query.Where(x => x.Enabled == isEnabled);

        var rows = (await query.ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(f => PromptAssembler.CategoryOrder(f.Category))
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ThenBy(f => f.Id)
            .ToArray();
        if (rows.Length == 0) return [];

        var versionIds = rows.Select(x => x.CurrentVersionId).Distinct().ToArray();
        var versions = await db.Versions.AsNoTracking()
            .Where(x => versionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);

        var output = new List<PromptFragmentDto>(rows.Length);
        foreach (var row in rows)
        {
            var content = versions.TryGetValue(row.CurrentVersionId, out var version)
                ? await PromptContentCodec.ReadTextAsync(_content, version, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            output.Add(ToDto(row, content));
        }
        return output;
    }

    public async Task<PromptFragmentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Fragments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        var version = await db.Versions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        var content = version is null
            ? string.Empty
            : await PromptContentCodec.ReadTextAsync(_content, version, cancellationToken).ConfigureAwait(false);
        return ToDto(row, content);
    }

    public async Task<PromptFragmentDto> SaveAsync(
        Guid? id,
        PromptFragmentSaveRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var row = id.HasValue
            ? await db.Fragments.SingleOrDefaultAsync(
                x => x.Id == id.Value && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null,
                cancellationToken).ConfigureAwait(false)
            : null;
        if (id.HasValue && row is null)
            throw new PromptFragmentException(404, "not_found", $"Prompt fragment '{id.Value}' was not found.");
        if (row?.IsBuiltIn == true)
            throw new PromptFragmentException(409, "built_in_read_only", "Built-in prompt fragments are read-only.");
        if (row is not null && expectedRevision is { } expected && row.Revision != expected)
            throw new PromptFragmentException(412, "revision_conflict", $"Prompt fragment revision conflict: expected {expected}, actual {row.Revision}.");

        var isNew = row is null;
        string? previousContent = null;
        if (!isNew)
        {
            var prevVersion = await db.Versions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == row!.CurrentVersionId, cancellationToken).ConfigureAwait(false);
            if (prevVersion is not null)
                previousContent = await PromptContentCodec.ReadTextAsync(_content, prevVersion, cancellationToken).ConfigureAwait(false);
        }

        if (isNew)
        {
            var key = (input.Key ?? string.Empty).Trim();
            var title = (input.Title ?? string.Empty).Trim();
            if (key.Length == 0 || key.Length > 256)
                throw new PromptFragmentException(400, "invalid_request", "Prompt fragment key is required (1..256 chars).");
            if (title.Length == 0)
                title = key;
            row = new PromptFragmentRecord
            {
                Id = id ?? Guid.NewGuid(),
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                Key = key,
                Title = title,
                Scope = string.IsNullOrWhiteSpace(input.Scope) ? "workspace" : input.Scope.Trim(),
                Category = string.IsNullOrWhiteSpace(input.Category) ? "general" : input.Category.Trim(),
                Priority = input.Priority ?? 0,
                Enabled = input.Enabled ?? true,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            db.Fragments.Add(row);
        }

        var changedFields = new List<string>();
        if (!isNew)
        {
            if (input.Key is not null && !string.Equals(input.Key.Trim(), row!.Key, StringComparison.Ordinal))
            {
                var nextKey = input.Key.Trim();
                if (nextKey.Length == 0 || nextKey.Length > 256)
                    throw new PromptFragmentException(400, "invalid_request", "Prompt fragment key must be 1..256 chars.");
                row.Key = nextKey;
                changedFields.Add("key");
            }
            if (input.Title is not null && !string.Equals(input.Title.Trim(), row!.Title, StringComparison.Ordinal))
            {
                row.Title = input.Title.Trim();
                changedFields.Add("title");
            }
            if (input.Scope is not null && !string.Equals(input.Scope.Trim(), row!.Scope, StringComparison.Ordinal))
            {
                row.Scope = input.Scope.Trim();
                changedFields.Add("scope");
            }
            if (input.Category is not null && !string.Equals(input.Category.Trim(), row!.Category, StringComparison.Ordinal))
            {
                row.Category = input.Category.Trim();
                changedFields.Add("category");
            }
            if (input.Priority is { } prio && prio != row!.Priority)
            {
                row.Priority = prio;
                changedFields.Add("priority");
            }
            if (input.Enabled is { } en && en != row!.Enabled)
            {
                row.Enabled = en;
                changedFields.Add("enabled");
            }
        }

        await ApplyTargetAgentAsync(row!, input.TargetAgentId, input.TargetAgentSlug, changedFields, isNew, cancellationToken).ConfigureAwait(false);

        var content = input.Content ?? previousContent ?? string.Empty;
        if (isNew || !string.Equals(content, previousContent, StringComparison.Ordinal))
            changedFields.Add("content");
        if (changedFields.Count == 0)
            changedFields.Add("metadata");

        var storedRef = await PromptContentCodec.WriteJsonStringAsync(_content, tenant.TenantId, tenant.WorkspaceId, content, cancellationToken).ConfigureAwait(false);
        var maxVersion = isNew
            ? 0
            : await db.Versions.Where(v => v.FragmentId == row!.Id).MaxAsync(v => (int?)v.Version, cancellationToken).ConfigureAwait(false) ?? 0;

        var version = new PromptFragmentVersionRecord
        {
            Id = Guid.NewGuid(),
            FragmentId = row!.Id,
            Version = maxVersion + 1,
            ContentReference = storedRef.Value,
            ContentHash = storedRef.Sha256,
            ContentLength = storedRef.Length,
            ChangeSummary = string.IsNullOrWhiteSpace(input.ChangeSummary)
                ? (isNew ? "initial version" : "configuration update")
                : input.ChangeSummary.Trim(),
            ChangedFieldsJson = JsonSerializer.Serialize(changedFields),
            CreatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now
        };
        row.Revision++;
        row.CurrentVersionId = version.Id;
        row.UpdatedByPrincipalId = tenant.PrincipalId;
        row.UpdatedAt = now;
        db.Versions.Add(version);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToDto(row, content);
    }

    public async Task<IReadOnlyList<PromptFragmentVersionDto>> ListVersionsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Fragments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
            throw new PromptFragmentException(404, "not_found", $"Prompt fragment '{id}' was not found.");

        var versions = await db.Versions.AsNoTracking()
            .Where(x => x.FragmentId == id)
            .OrderByDescending(x => x.Version)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var output = new List<PromptFragmentVersionDto>(versions.Count);
        foreach (var v in versions)
        {
            var content = await PromptContentCodec.ReadTextAsync(_content, v, cancellationToken).ConfigureAwait(false);
            output.Add(ToVersionDto(v, content, v.Id == row.CurrentVersionId));
        }
        return output;
    }

    public async Task<PromptFragmentVersionDto> CreateVersionAsync(
        Guid id,
        PromptFragmentVersionCreateRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.Fragments.SingleOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            throw new PromptFragmentException(404, "not_found", $"Prompt fragment '{id}' was not found.");
        if (row.IsBuiltIn)
            throw new PromptFragmentException(409, "built_in_read_only", "Built-in prompt fragments are read-only.");
        if (expectedRevision is { } expected && row.Revision != expected)
            throw new PromptFragmentException(412, "revision_conflict", $"Prompt fragment revision conflict: expected {expected}, actual {row.Revision}.");

        var content = input.Content ?? string.Empty;
        var storedRef = await PromptContentCodec.WriteJsonStringAsync(_content, tenant.TenantId, tenant.WorkspaceId, content, cancellationToken).ConfigureAwait(false);
        var maxVersion = await db.Versions.Where(v => v.FragmentId == row.Id).MaxAsync(v => (int?)v.Version, cancellationToken).ConfigureAwait(false) ?? 0;
        var changedFields = input.ChangedFields is { Count: > 0 } ? input.ChangedFields : ["content"];

        var version = new PromptFragmentVersionRecord
        {
            Id = Guid.NewGuid(),
            FragmentId = row.Id,
            Version = maxVersion + 1,
            ContentReference = storedRef.Value,
            ContentHash = storedRef.Sha256,
            ContentLength = storedRef.Length,
            ChangeSummary = string.IsNullOrWhiteSpace(input.ChangeSummary) ? "new version" : input.ChangeSummary.Trim(),
            ChangedFieldsJson = JsonSerializer.Serialize(changedFields),
            CreatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now
        };
        row.Revision++;
        row.CurrentVersionId = version.Id;
        row.UpdatedByPrincipalId = tenant.PrincipalId;
        row.UpdatedAt = now;
        db.Versions.Add(version);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToVersionDto(version, content, isActive: true);
    }

    public async Task<PromptFragmentDto> RollbackAsync(
        Guid id,
        PromptFragmentRollbackRequestDto input,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tenant = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.Fragments.SingleOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            throw new PromptFragmentException(404, "not_found", $"Prompt fragment '{id}' was not found.");
        if (row.IsBuiltIn)
            throw new PromptFragmentException(409, "built_in_read_only", "Built-in prompt fragments are read-only.");
        if (expectedRevision is { } expected && row.Revision != expected)
            throw new PromptFragmentException(412, "revision_conflict", $"Prompt fragment revision conflict: expected {expected}, actual {row.Revision}.");

        PromptFragmentVersionRecord? target = null;
        if (input.VersionId is { } vid && vid != Guid.Empty)
        {
            target = await db.Versions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == vid && x.FragmentId == id, cancellationToken).ConfigureAwait(false);
        }
        else if (input.TargetVersion is { } tv && tv > 0)
        {
            target = await db.Versions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.FragmentId == id && x.Version == tv, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new PromptFragmentException(400, "invalid_request", "Rollback requires either target_version or version_id.");
        }

        if (target is null)
            throw new PromptFragmentException(404, "not_found", "Target prompt fragment version was not found.");

        var content = await PromptContentCodec.ReadTextAsync(_content, target, cancellationToken).ConfigureAwait(false);
        var maxVersion = await db.Versions.Where(v => v.FragmentId == row.Id).MaxAsync(v => (int?)v.Version, cancellationToken).ConfigureAwait(false) ?? 0;
        var version = new PromptFragmentVersionRecord
        {
            Id = Guid.NewGuid(),
            FragmentId = row.Id,
            Version = maxVersion + 1,
            ContentReference = target.ContentReference,
            ContentHash = target.ContentHash,
            ContentLength = target.ContentLength,
            ChangeSummary = string.IsNullOrWhiteSpace(input.ChangeSummary)
                ? $"Rollback to version {target.Version}"
                : input.ChangeSummary.Trim(),
            ChangedFieldsJson = JsonSerializer.Serialize(new[] { "content" }),
            CreatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now
        };
        row.Revision++;
        row.CurrentVersionId = version.Id;
        row.UpdatedByPrincipalId = tenant.PrincipalId;
        row.UpdatedAt = now;
        db.Versions.Add(version);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToDto(row, content);
    }

    public async Task<PromptFragmentDto> CloneAsync(
        Guid id,
        PromptFragmentCloneRequestDto? input = null,
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = await db.Fragments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
            throw new PromptFragmentException(404, "not_found", $"Prompt fragment '{id}' was not found.");

        var sourceVersion = await db.Versions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == source.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        var content = sourceVersion is null
            ? string.Empty
            : await PromptContentCodec.ReadTextAsync(_content, sourceVersion, cancellationToken).ConfigureAwait(false);

        var desiredKey = string.IsNullOrWhiteSpace(input?.Key) ? $"{source.Key}-copy" : input!.Key!.Trim();
        var existingKeys = (await db.Fragments.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null)
            .Select(x => x.Key)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var finalKey = desiredKey;
        var suffix = 2;
        while (existingKeys.Contains(finalKey))
        {
            finalKey = $"{desiredKey}-{suffix}";
            suffix++;
        }

        var clone = new PromptFragmentRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            ProjectId = source.ProjectId,
            Scope = string.IsNullOrWhiteSpace(input?.Scope) ? source.Scope : input!.Scope!.Trim(),
            Key = finalKey,
            Title = string.IsNullOrWhiteSpace(input?.Title) ? $"{source.Title} (Copy)" : input!.Title!.Trim(),
            TargetAgentId = source.TargetAgentId,
            TargetAgentSlug = source.TargetAgentSlug,
            Category = source.Category,
            Priority = source.Priority,
            Enabled = source.Enabled,
            IsBuiltIn = false,
            Revision = 1,
            CreatedByPrincipalId = tenant.PrincipalId,
            UpdatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (input?.TargetAgentId is not null || input?.TargetAgentSlug is not null)
        {
            await ApplyTargetAgentAsync(clone, input.TargetAgentId, input.TargetAgentSlug, [], isNew: true, cancellationToken).ConfigureAwait(false);
        }

        var storedRef = await PromptContentCodec.WriteJsonStringAsync(_content, tenant.TenantId, tenant.WorkspaceId, content, cancellationToken).ConfigureAwait(false);
        var version = new PromptFragmentVersionRecord
        {
            Id = Guid.NewGuid(),
            FragmentId = clone.Id,
            Version = 1,
            ContentReference = storedRef.Value,
            ContentHash = storedRef.Sha256,
            ContentLength = storedRef.Length,
            ChangeSummary = $"Cloned from '{source.Key}'",
            ChangedFieldsJson = JsonSerializer.Serialize(new[] { "content" }),
            CreatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now
        };
        clone.CurrentVersionId = version.Id;
        db.Fragments.Add(clone);
        db.Versions.Add(version);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToDto(clone, content);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Fragments.SingleOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        if (row.IsBuiltIn)
            throw new PromptFragmentException(409, "built_in_read_only", "Built-in prompt fragments are read-only.");
        row.DeletedAt = DateTimeOffset.UtcNow;
        row.UpdatedByPrincipalId = tenant.PrincipalId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ApplyTargetAgentAsync(
        PromptFragmentRecord row,
        string? targetAgentIdRaw,
        string? targetAgentSlugRaw,
        ICollection<string> changedFields,
        bool isNew,
        CancellationToken cancellationToken)
    {
        var prevId = row.TargetAgentId;
        var prevSlug = row.TargetAgentSlug;

        if (targetAgentIdRaw is not null)
        {
            var trimmed = targetAgentIdRaw.Trim();
            if (trimmed.Length == 0)
            {
                row.TargetAgentId = null;
                row.TargetAgentSlug = null;
            }
            else if (Guid.TryParse(trimmed, out var parsedGuid))
            {
                row.TargetAgentId = parsedGuid;
                if (_agentLookup is not null)
                {
                    var resolved = await _agentLookup.ResolveAsync(trimmed, cancellationToken).ConfigureAwait(false);
                    if (resolved is not null) row.TargetAgentSlug = resolved.Slug;
                }
            }
            else
            {
                row.TargetAgentSlug = trimmed;
                row.TargetAgentId = null;
                if (_agentLookup is not null)
                {
                    var resolved = await _agentLookup.ResolveAsync(trimmed, cancellationToken).ConfigureAwait(false);
                    if (resolved is not null) row.TargetAgentId = resolved.Id;
                }
            }
        }

        if (targetAgentSlugRaw is not null)
        {
            var trimmedSlug = targetAgentSlugRaw.Trim();
            if (trimmedSlug.Length == 0)
            {
                row.TargetAgentSlug = null;
            }
            else
            {
                row.TargetAgentSlug = trimmedSlug;
                if (row.TargetAgentId is null && _agentLookup is not null)
                {
                    var resolved = await _agentLookup.ResolveAsync(trimmedSlug, cancellationToken).ConfigureAwait(false);
                    if (resolved is not null) row.TargetAgentId = resolved.Id;
                }
            }
        }

        if (!isNew && (row.TargetAgentId != prevId || !string.Equals(row.TargetAgentSlug, prevSlug, StringComparison.Ordinal)))
            changedFields.Add("target_agent_id");
    }

    private static PromptFragmentDto ToDto(PromptFragmentRecord row, string content) => new()
    {
        Id = row.Id,
        Key = row.Key,
        Title = row.Title,
        Scope = row.Scope,
        TargetAgentId = row.TargetAgentId,
        TargetAgentSlug = row.TargetAgentSlug,
        Category = row.Category,
        Content = content,
        Priority = row.Priority,
        Enabled = row.Enabled,
        IsBuiltIn = row.IsBuiltIn,
        Revision = row.Revision,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    private static PromptFragmentVersionDto ToVersionDto(PromptFragmentVersionRecord row, string content, bool isActive)
    {
        IReadOnlyList<string> changedFields = ["content"];
        if (!string.IsNullOrWhiteSpace(row.ChangedFieldsJson))
        {
            try
            {
                changedFields = JsonSerializer.Deserialize<string[]>(row.ChangedFieldsJson) ?? ["content"];
            }
            catch (JsonException)
            {
                changedFields = ["content"];
            }
        }
        return new PromptFragmentVersionDto
        {
            Id = row.Id,
            FragmentId = row.FragmentId,
            Version = row.Version,
            Content = content,
            ChangedFields = changedFields,
            ChangeSummary = row.ChangeSummary,
            IsActive = isActive,
            CreatedAt = row.CreatedAt
        };
    }
}

/// <summary>
/// Deterministically assembles the local prompt in the following order: architecture baseline,
/// system prompt, response language, mode instructions, workspace facts, mode/agent profile,
/// topologically executed PromptPipeline graph, active/frozen PromptFragments, task context,
/// then runtime constraints.
/// </summary>
internal sealed class PromptAssembler : IPromptAssembler
{
    private static readonly Regex TokenPattern = new(@"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    private readonly IDbContextFactory<PromptControlDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly IToolSkillResourceService? _skills;
    private readonly IAgentIdentityLookup? _agentLookup;

    public PromptAssembler(
        IDbContextFactory<PromptControlDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant,
        IToolSkillResourceService? skills = null,
        IAgentIdentityLookup? agentLookup = null)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
        _skills = skills;
        _agentLookup = agentLookup;
    }

    public async Task<PromptAssemblyResult> AssembleAsync(
        string agentId,
        ContextPack? contextPack,
        CancellationToken cancellationToken = default) =>
        await AssembleAsync(new FrozenPromptAssemblyRequest(
            agentId,
            contextPack,
            IncludeLiveFragments: true), cancellationToken).ConfigureAwait(false);

    public async Task<PromptAssemblyResult> AssembleAsync(
        FrozenPromptAssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId)) throw new ArgumentException("Agent id is required.", nameof(request));
        var agentId = request.AgentId.Trim();
        var contextPack = request.ContextPack;

        // Collect all agent identifiers (GUID + slug) so TargetAgentId matches both.
        var candidateGuids = new HashSet<Guid>();
        var candidateSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Guid.TryParse(agentId, out var parsedAgentGuid) && parsedAgentGuid != Guid.Empty)
            candidateGuids.Add(parsedAgentGuid);
        else
            candidateSlugs.Add(agentId);

        if (request.AgentDefinitionId is { } explicitGuid && explicitGuid != Guid.Empty)
            candidateGuids.Add(explicitGuid);
        if (!string.IsNullOrWhiteSpace(request.AgentSlug))
            candidateSlugs.Add(request.AgentSlug.Trim());
        if (contextPack?.Metadata.TryGetValue("agent_definition_id", out var metaGuidText) == true
            && Guid.TryParse(metaGuidText, out var metaGuid) && metaGuid != Guid.Empty)
            candidateGuids.Add(metaGuid);
        if (contextPack?.Metadata.TryGetValue("agent_slug", out var metaSlugText) == true
            && !string.IsNullOrWhiteSpace(metaSlugText))
            candidateSlugs.Add(metaSlugText.Trim());

        AgentIdentitySnapshot? lookedUpAgent = null;
        if (_agentLookup is not null)
        {
            lookedUpAgent = await _agentLookup.ResolveAsync(request.AgentSlug ?? agentId, cancellationToken).ConfigureAwait(false);
            if (lookedUpAgent is not null)
            {
                candidateGuids.Add(lookedUpAgent.Id);
                if (!string.IsNullOrWhiteSpace(lookedUpAgent.Slug))
                    candidateSlugs.Add(lookedUpAgent.Slug);
            }
        }

        var effectiveAgentSlug = request.AgentSlug
            ?? (candidateSlugs.Count > 0 ? candidateSlugs.First() : lookedUpAgent?.Slug ?? agentId);
        var effectiveAgentName = request.AgentName
            ?? (contextPack?.Metadata.TryGetValue("agent_name", out var metaName) == true ? metaName : null)
            ?? lookedUpAgent?.DisplayName
            ?? effectiveAgentSlug;
        var effectiveLayer = request.AgentLayer
            ?? (contextPack?.Metadata.TryGetValue("layer", out var metaLayer) == true ? metaLayer : null)
            ?? lookedUpAgent?.Layer
            ?? string.Empty;
        var effectiveModeSlug = request.ModeSlug
            ?? (contextPack?.Metadata.TryGetValue("mode_slug", out var metaModeSlug) == true ? metaModeSlug : null)
            ?? string.Empty;
        var effectiveModeName = request.ModeName
            ?? (contextPack?.Metadata.TryGetValue("mode_name", out var metaModeName) == true ? metaModeName : null)
            ?? effectiveModeSlug;
        var effectiveCapabilities = request.Capabilities
            ?? (contextPack?.Metadata.TryGetValue("capabilities", out var metaCaps) == true
                ? metaCaps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : lookedUpAgent?.Capabilities ?? []);
        var effectiveHasTools = request.HasTools
            ?? (contextPack?.Metadata.TryGetValue("has_tools", out var metaTools) == true
                ? string.Equals(metaTools, "true", StringComparison.OrdinalIgnoreCase)
                : lookedUpAgent?.HasTools ?? false);

        var evalContext = new PipelineEvalContext(
            AgentName: effectiveAgentName,
            AgentSlug: effectiveAgentSlug,
            ModeName: effectiveModeName,
            ModeSlug: effectiveModeSlug,
            Layer: effectiveLayer,
            WorkspaceRoot: request.Workspace?.RootPath ?? string.Empty,
            HasWorkspace: request.Workspace is not null,
            HasTools: effectiveHasTools,
            Capabilities: new HashSet<string>(effectiveCapabilities, StringComparer.OrdinalIgnoreCase));

        // Load fragment pool upfront so pipeline fragment_ref nodes and trailing fragment layer share one source.
        var warnings = new List<string>();
        var pool = await LoadFragmentPoolAsync(request, warnings, cancellationToken).ConfigureAwait(false);
        var consumedFragmentIds = new HashSet<Guid>();

        var budget = contextPack?.TokenBudget ?? 8192;
        var layers = new List<PromptAssemblyLayer>();
        var fragmentIds = new List<string>();
        var used = 0;

        void AddRequired(string id, string kind, string title, string content, string source, string? fragmentId = null)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            var trimmed = content.Trim();
            var tokens = EstimateTokens(trimmed);
            layers.Add(new PromptAssemblyLayer(id, kind, title, trimmed, tokens, source));
            fragmentIds.Add(fragmentId ?? id);
            used += tokens;
        }

        bool TryAddBudgeted(string id, string kind, string title, string content, string source, string omittedWarning, string? fragmentId = null)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            var trimmed = content.Trim();
            var tokens = EstimateTokens(trimmed);
            if (tokens > budget - used)
            {
                warnings.Add(omittedWarning);
                return false;
            }
            layers.Add(new PromptAssemblyLayer(id, kind, title, trimmed, tokens, source));
            fragmentIds.Add(fragmentId ?? id);
            used += tokens;
            return true;
        }

        // Pre-execute pipeline graph so placement hints ("before_system_prompt", "after_system_prompt",
        // "before_runtime_constraints") and workspace_rules placement can position blocks deterministically.
        var pipelineBlocks = new List<PipelineRenderedBlock>();
        var pipelineHasWorkspaceRulesNode = false;
        if (request.PromptVersionId is not null || !string.IsNullOrWhiteSpace(request.PromptGraphJson))
        {
            if (request.PromptVersionId is { } promptVersionId)
                fragmentIds.Add($"prompt-version:{promptVersionId}:{request.PromptVersionContentHash}");

            pipelineBlocks = await ExecutePromptGraphAsync(
                request,
                evalContext,
                pool,
                consumedFragmentIds,
                budget,
                warnings,
                cancellationToken).ConfigureAwait(false);
            pipelineHasWorkspaceRulesNode = pipelineBlocks.Any(b => b.IsWorkspaceRulesPlacement);

            if (request.PromptVersionId is { } pvId && pipelineBlocks.Count == 0)
            {
                warnings.Add($"Prompt version '{pvId}' contains no non-empty template content.");
            }
        }

        void EmitPlacedBlocks(string placement)
        {
            foreach (var block in pipelineBlocks.Where(b => string.Equals(b.Placement, placement, StringComparison.OrdinalIgnoreCase)))
            {
                if (block.IsWorkspaceRulesPlacement)
                {
                    AddRequired(block.Id, block.Kind, block.Title, block.Content, block.Source, block.FragmentId);
                    continue;
                }
                TryAddBudgeted(
                    block.Id,
                    block.Kind,
                    block.Title,
                    block.Content,
                    block.Source,
                    $"Prompt template '{block.NodeId}' was omitted because the context budget is exhausted.",
                    block.FragmentId);
            }
        }

        AddRequired(
            "builtin:architecture-baseline",
            "architecture_baseline",
            "Architecture Baseline",
            ArchitectureBaseline(agentId),
            "builtin");

        EmitPlacedBlocks("before_system_prompt");

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            var sysId = request.AgentVersionId is { } agentVersionId
                ? $"agent-version:{agentVersionId}:{request.AgentVersionContentHash}"
                : $"agent:{agentId}:system-prompt";
            AddRequired(sysId, "system_prompt", "Agent System Prompt", request.SystemPrompt, "agent", sysId);
        }

        EmitPlacedBlocks("after_system_prompt");

        // Response language is frozen with the run: a model must not guess from the
        // latest user turn, and a resumed run keeps the language it started in.
        if (!string.IsNullOrWhiteSpace(request.ResponseLocale))
        {
            var locale = RunLocaleResolver.Normalize(request.ResponseLocale);
            var responseLanguage =
                $"Response language: reply in {RunLocaleResolver.LanguageName(locale)} ({locale}). " +
                "Keep code, identifiers, file paths, commands, logs and quoted text verbatim.";
            TryAddBudgeted(
                "builtin:response-language",
                "response_language",
                "Response Language",
                responseLanguage,
                "builtin",
                "Response language instruction could not fit in the configured context budget.");
        }

        if (!string.IsNullOrWhiteSpace(request.ModeInstructions))
        {
            TryAddBudgeted(
                "builtin:mode-instructions",
                "mode_instructions",
                "Mode Instructions",
                request.ModeInstructions,
                "mode",
                "Mode instructions could not fit in the configured context budget.");
        }

        // Workspace facts sit next to the run-level mode profile unless a workspace_rules node placed them.
        if (request.Workspace is { } workspace && !pipelineHasWorkspaceRulesNode)
        {
            var (workspaceSection, workspaceWarnings) = WorkspaceContext(workspace, budget - used);
            AddRequired("builtin:workspace-context", "workspace_context", "Workspace Context", workspaceSection, "workspace");
            warnings.AddRange(workspaceWarnings);
        }

        var runtimeProfile = contextPack?.Metadata.TryGetValue("runtime_profile_id", out var profileId) == true ? profileId : "unspecified";
        TryAddBudgeted(
            "builtin:mode-profile",
            "mode_profile",
            "Mode Profile",
            $"Mode profile: runtime_profile_id={runtimeProfile}.",
            "mode",
            "Mode profile could not fit in the configured context budget.");

        TryAddBudgeted(
            "builtin:agent-profile",
            "agent_profile",
            "Agent Profile",
            $"Agent profile: agent_id={agentId}. Follow only the capabilities and layer constraints frozen for this run.",
            "agent",
            "Agent profile could not fit in the configured context budget.");

        EmitPlacedBlocks("pipeline");

        // Append remaining unconsumed fragments that match the agent (or explicit include_fragment_ids).
        var explicitIncludeSet = request.IncludeFragmentIds is not null
            ? new HashSet<Guid>(request.IncludeFragmentIds)
            : null;
        var matchedFragments = pool
            .Where(f => !consumedFragmentIds.Contains(f.Id))
            .Where(f => explicitIncludeSet is not null
                ? explicitIncludeSet.Contains(f.Id)
                : f.Enabled && MatchesAgent(f, candidateGuids, candidateSlugs))
            .OrderBy(f => CategoryOrder(f.Category))
            .ThenByDescending(f => f.Priority)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ThenBy(f => f.Id)
            .ToArray();

        foreach (var fragment in matchedFragments)
        {
            TryAddBudgeted(
                fragment.Id.ToString(),
                "prompt_fragment",
                fragment.Title,
                fragment.Content,
                $"fragment:{fragment.Key}",
                $"Prompt fragment '{fragment.Key}' was omitted because the context budget is exhausted.");
        }

        if (contextPack is not null)
        {
            foreach (var evidence in contextPack.Evidence)
            {
                var section = $"[{evidence.Source}]\n{evidence.Content}";
                var id = $"context:{evidence.Source}";
                TryAddBudgeted(
                    id,
                    "context_evidence",
                    evidence.Source,
                    section,
                    evidence.Source,
                    $"Context evidence from '{evidence.Source}' was omitted because the context budget is exhausted.");
            }
        }

        EmitPlacedBlocks("before_runtime_constraints");

        var runtimeConstraints = "Runtime constraints: only the meeting agent may produce direct user output. Do not claim an external action unless execution evidence confirms it. Respect Core-owned approval and tool policy.";
        if (ModeRuntimePolicyRegistry.Has(request.RuntimePolicy, ModeRuntimePolicyRegistry.WorkerSettlementFeature))
        {
            runtimeConstraints += " Tool attribution: a result whose outcome is 'blocked' must not be retried with the same arguments — change the approach or report the blockage. Only a result whose outcome is 'error' and whose retryable flag is true may be retried.";
        }
        AddRequired("builtin:runtime-constraints", "runtime_constraints", "Runtime Constraints", runtimeConstraints, "builtin");

        return new PromptAssemblyResult
        {
            Instructions = string.Join("\n\n", layers.Select(l => l.Content)),
            EstimatedTokens = used,
            FragmentIds = fragmentIds,
            Warnings = warnings,
            Layers = layers
        };
    }

    private async Task<List<PoolFragment>> LoadFragmentPoolAsync(
        FrozenPromptAssemblyRequest request,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (request.FrozenFragments is not null)
        {
            return request.FrozenFragments
                .Select(f => new PoolFragment(f.Id, f.Key, f.Title, f.Scope, f.TargetAgentId, f.TargetAgentSlug, f.Category, f.Priority, true, PromptContentCodec.UnwrapFragmentText(f.Content)))
                .ToList();
        }

        if (!request.IncludeLiveFragments && string.IsNullOrWhiteSpace(request.PromptGraphJson) && request.IncludeFragmentIds is null)
            return [];

        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Fragments.AsNoTracking()
            .Where(fragment => fragment.TenantId == scope.TenantId
                && fragment.DeletedAt == null
                && (fragment.WorkspaceId == null || fragment.WorkspaceId == scope.WorkspaceId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return [];

        var versionIds = rows.Select(x => x.CurrentVersionId).Distinct().ToArray();
        var versions = await db.Versions.AsNoTracking()
            .Where(x => versionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);

        var pool = new List<PoolFragment>(rows.Count);
        foreach (var row in rows)
        {
            if (!versions.TryGetValue(row.CurrentVersionId, out var version))
            {
                if (row.Enabled) warnings.Add($"Prompt fragment '{row.Key}' has no current version.");
                continue;
            }
            var text = await PromptContentCodec.ReadTextAsync(_content, version, cancellationToken).ConfigureAwait(false);
            pool.Add(new PoolFragment(row.Id, row.Key, row.Title, row.Scope, row.TargetAgentId, row.TargetAgentSlug, row.Category, row.Priority, row.Enabled, text));
        }
        return pool;
    }

    private async Task<List<PipelineRenderedBlock>> ExecutePromptGraphAsync(
        FrozenPromptAssemblyRequest request,
        PipelineEvalContext evalContext,
        IReadOnlyList<PoolFragment> pool,
        HashSet<Guid> consumedFragmentIds,
        int budget,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var graphJson = request.PromptGraphJson;
        if (string.IsNullOrWhiteSpace(graphJson)) return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(graphJson);
        }
        catch (JsonException ex)
        {
            warnings.Add("Frozen prompt graph could not be parsed: " + ex.Message);
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryGetNodesArray(root, out var nodesElement)) return [];

            var parsedNodes = new List<GraphNode>();
            var index = 0;
            foreach (var nodeEl in nodesElement.EnumerateArray())
            {
                index++;
                var id = ReadString(nodeEl, "id") ?? ReadString(nodeEl, "node_key") ?? index.ToString(CultureInfo.InvariantCulture);
                var kind = (ReadString(nodeEl, "type") ?? ReadString(nodeEl, "kind") ?? string.Empty).Trim().ToLowerInvariant();
                var label = ReadString(nodeEl, "label") ?? ReadString(nodeEl, "title") ?? ReadString(nodeEl, "name") ?? id;
                parsedNodes.Add(new GraphNode(index - 1, id, kind, label, nodeEl.Clone()));
            }
            if (parsedNodes.Count == 0) return [];

            var edges = ParseEdges(root, parsedNodes);
            var ordered = TopologicalSort(parsedNodes, edges, warnings);

            // Track incoming predecessors so a false condition filters downstream nodes fed solely through it.
            var incomingByNode = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in edges)
            {
                if (!incomingByNode.TryGetValue(edge.Target, out var preds))
                    incomingByNode[edge.Target] = preds = [];
                preds.Add(edge.Source);
            }

            var activeNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rendered = new List<PipelineRenderedBlock>();
            var nodeTagPrefix = request.PromptVersionId is { } pvId
                ? pvId.ToString()
                : request.PromptPipelineId is { } ppId
                    ? ppId.ToString()
                    : "draft";

            foreach (var node in ordered)
            {
                var isReachable = !incomingByNode.TryGetValue(node.Id, out var preds)
                    || preds.Count == 0
                    || preds.Any(p => activeNodes.Contains(p));
                if (!isReachable) continue;

                var placement = NormalizePlacement(ReadNodeString(node.Element, "placement", "slot"));

                switch (node.Kind)
                {
                    case "condition":
                    {
                        var passed = EvaluateCondition(node.Element, evalContext);
                        if (!passed)
                        {
                            // Do not add to activeNodes so exclusively downstream nodes are skipped.
                            break;
                        }
                        activeNodes.Add(node.Id);
                        var rawText = ReadTemplateText(node.Element);
                        if (!string.IsNullOrWhiteSpace(rawText))
                        {
                            var substituted = SubstituteTokens(rawText.Trim(), evalContext);
                            rendered.Add(new PipelineRenderedBlock(
                                $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                node.Id,
                                "pipeline_condition",
                                node.Label,
                                substituted,
                                "prompt_pipeline",
                                placement,
                                FragmentId: $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                IsWorkspaceRulesPlacement: false));
                        }
                        break;
                    }

                    case "template":
                    {
                        activeNodes.Add(node.Id);
                        var rawText = ReadTemplateText(node.Element);
                        if (!string.IsNullOrWhiteSpace(rawText))
                        {
                            var substituted = SubstituteTokens(rawText.Trim(), evalContext);
                            rendered.Add(new PipelineRenderedBlock(
                                $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                node.Id,
                                "pipeline_template",
                                node.Label,
                                substituted,
                                "prompt_pipeline",
                                placement,
                                FragmentId: $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                IsWorkspaceRulesPlacement: false));
                        }
                        break;
                    }

                    case "fragment_ref":
                    {
                        activeNodes.Add(node.Id);
                        var refIdText = ReadNodeString(node.Element, "fragment_id");
                        var refKey = ReadNodeString(node.Element, "fragment_key", "key");
                        PoolFragment? match = null;
                        if (Guid.TryParse(refIdText, out var refGuid) && refGuid != Guid.Empty)
                            match = pool.FirstOrDefault(f => f.Id == refGuid);
                        if (match is null && !string.IsNullOrWhiteSpace(refKey))
                            match = pool.FirstOrDefault(f => string.Equals(f.Key, refKey.Trim(), StringComparison.OrdinalIgnoreCase));

                        if (match is null)
                        {
                            warnings.Add($"Prompt pipeline node '{node.Id}' references unknown fragment '{refIdText ?? refKey ?? "(empty)"}'.");
                            break;
                        }

                        consumedFragmentIds.Add(match.Id);
                        if (!string.IsNullOrWhiteSpace(match.Content))
                        {
                            rendered.Add(new PipelineRenderedBlock(
                                match.Id.ToString(),
                                node.Id,
                                "fragment_ref",
                                match.Title,
                                match.Content.Trim(),
                                $"fragment:{match.Key}",
                                placement,
                                FragmentId: match.Id.ToString(),
                                IsWorkspaceRulesPlacement: false));
                        }
                        break;
                    }

                    case "workspace_rules":
                    {
                        activeNodes.Add(node.Id);
                        var parts = new List<string>();
                        if (request.Workspace is { } workspace)
                        {
                            var (wsText, wsWarnings) = WorkspaceContext(workspace, budget);
                            parts.Add(wsText);
                            foreach (var w in wsWarnings) warnings.Add(w);
                        }
                        if (request.WorkspaceRules is { Count: > 0 } rules)
                        {
                            parts.Add("Workspace rules:\n" + string.Join('\n', rules.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => $"- {r.Trim()}")));
                        }
                        var customText = ReadTemplateText(node.Element);
                        if (!string.IsNullOrWhiteSpace(customText))
                            parts.Add(SubstituteTokens(customText.Trim(), evalContext));

                        var combined = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
                        if (!string.IsNullOrWhiteSpace(combined))
                        {
                            rendered.Add(new PipelineRenderedBlock(
                                $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                node.Id,
                                "workspace_rules",
                                node.Label,
                                combined,
                                "workspace",
                                placement,
                                FragmentId: $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                IsWorkspaceRulesPlacement: true));
                        }
                        break;
                    }

                    case "skills_index":
                    {
                        activeNodes.Add(node.Id);
                        var skillsText = request.SkillsIndexText;
                        if (string.IsNullOrWhiteSpace(skillsText)
                            && request.ContextPack?.Metadata.TryGetValue("skills_index", out var metaSkills) == true)
                        {
                            skillsText = metaSkills;
                        }
                        if (string.IsNullOrWhiteSpace(skillsText) && _skills is not null)
                        {
                            try
                            {
                                var projectId = request.Workspace?.ProjectId is { } pid && pid != Guid.Empty ? pid : (Guid?)null;
                                var catalog = await _skills.ListAsync(projectId, includeBuiltins: true, cancellationToken).ConfigureAwait(false);
                                var available = catalog.Skills
                                    .Where(s => s.Enabled && s.Valid && string.IsNullOrWhiteSpace(s.ShadowedBy))
                                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                                    .Take(40)
                                    .Select(s => $"- {s.Name}: {s.Description}")
                                    .ToArray();
                                if (available.Length > 0)
                                    skillsText = "Available skills:\n" + string.Join('\n', available);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                warnings.Add($"Skills index could not be loaded: {ex.Message}");
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(skillsText))
                        {
                            rendered.Add(new PipelineRenderedBlock(
                                $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                node.Id,
                                "skills_index",
                                node.Label,
                                skillsText.Trim(),
                                "skills",
                                placement,
                                FragmentId: $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                IsWorkspaceRulesPlacement: false));
                        }
                        break;
                    }

                    case "assemble":
                    {
                        activeNodes.Add(node.Id);
                        var header = ReadNodeString(node.Element, "header", "prefix");
                        var footer = ReadNodeString(node.Element, "footer", "suffix");
                        var body = ReadTemplateText(node.Element);
                        var parts = new[] { header, body, footer }
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => SubstituteTokens(p!.Trim(), evalContext))
                            .ToArray();
                        if (parts.Length > 0)
                        {
                            rendered.Add(new PipelineRenderedBlock(
                                $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                node.Id,
                                "pipeline_assemble",
                                node.Label,
                                string.Join("\n\n", parts),
                                "prompt_pipeline",
                                placement,
                                FragmentId: $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                IsWorkspaceRulesPlacement: false));
                        }
                        break;
                    }

                    default:
                    {
                        activeNodes.Add(node.Id);
                        var rawText = ReadTemplateText(node.Element);
                        if (!string.IsNullOrWhiteSpace(rawText))
                        {
                            rendered.Add(new PipelineRenderedBlock(
                                $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                node.Id,
                                $"pipeline_{node.Kind}",
                                node.Label,
                                SubstituteTokens(rawText.Trim(), evalContext),
                                "prompt_pipeline",
                                placement,
                                FragmentId: $"prompt-node:{nodeTagPrefix}:{node.Id}",
                                IsWorkspaceRulesPlacement: false));
                        }
                        break;
                    }
                }
            }

            return rendered;
        }
    }

    private static bool TryGetNodesArray(JsonElement root, out JsonElement nodes)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("nodes", out nodes) && nodes.ValueKind == JsonValueKind.Array) return true;
            if (root.TryGetProperty("prompt_nodes", out nodes) && nodes.ValueKind == JsonValueKind.Array) return true;
        }
        nodes = default;
        return false;
    }

    private static List<GraphEdge> ParseEdges(JsonElement root, IReadOnlyList<GraphNode> nodes)
    {
        if (!root.TryGetProperty("edges", out var edgesEl) || edgesEl.ValueKind != JsonValueKind.Array)
            return [];
        var knownIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string Source, string Target)>();
        var edges = new List<GraphEdge>();
        foreach (var e in edgesEl.EnumerateArray())
        {
            var src = ReadString(e, "source") ?? ReadString(e, "source_node_key");
            var tgt = ReadString(e, "target") ?? ReadString(e, "target_node_key");
            if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt)) continue;
            src = src.Trim();
            tgt = tgt.Trim();
            if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase)) continue;
            if (!knownIds.Contains(src) || !knownIds.Contains(tgt)) continue;
            if (seen.Add((src, tgt)))
                edges.Add(new GraphEdge(src, tgt));
        }
        return edges;
    }

    private static List<GraphNode> TopologicalSort(
        List<GraphNode> nodes,
        List<GraphEdge> edges,
        ICollection<string> warnings)
    {
        if (edges.Count == 0) return nodes;

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var inDegree = nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var adj = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            adj[edge.Source].Add(edge.Target);
            inDegree[edge.Target] = inDegree[edge.Target] + 1;
        }

        var ready = new PriorityQueue<GraphNode, int>();
        foreach (var node in nodes)
        {
            if (inDegree[node.Id] == 0)
                ready.Enqueue(node, node.Order);
        }

        var sorted = new List<GraphNode>(nodes.Count);
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            sorted.Add(current);
            foreach (var neighborId in adj[current.Id])
            {
                inDegree[neighborId]--;
                if (inDegree[neighborId] == 0)
                {
                    var neighbor = byId[neighborId];
                    ready.Enqueue(neighbor, neighbor.Order);
                }
            }
        }

        if (sorted.Count < nodes.Count)
        {
            warnings.Add("Prompt graph contains a cycle; falling back to node declaration order.");
            return nodes;
        }

        return sorted;
    }

    internal static string SubstituteTokens(string template, PipelineEvalContext ctx) =>
        TokenPattern.Replace(template, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "agent_name" => ctx.AgentName,
            "agent_slug" => ctx.AgentSlug,
            "mode_name" => ctx.ModeName,
            "mode_slug" => ctx.ModeSlug,
            "workspace_root" => ctx.WorkspaceRoot,
            _ => match.Value
        });

    internal static bool EvaluateCondition(JsonElement node, PipelineEvalContext ctx)
    {
        // Support nested `condition` object, `config`, `data`, or direct properties.
        foreach (var container in NodeContainers(node))
        {
            if (container.TryGetProperty("condition", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                if (!EvaluateConditionObject(nested, ctx)) return false;
            }
            if (!EvaluateConditionObject(container, ctx)) return false;
        }
        return true;
    }

    private static bool EvaluateConditionObject(JsonElement obj, PipelineEvalContext ctx)
    {
        if (obj.ValueKind != JsonValueKind.Object) return true;

        // UI inspector pair: { field: "has_workspace" | "has_tools" | "layer" | "mode_slug" | "capability", value: "..." }
        var field = ReadString(obj, "field") ?? ReadString(obj, "condition_field");
        if (!string.IsNullOrWhiteSpace(field) && obj.TryGetProperty("value", out var fieldValue))
        {
            if (!EvaluateFieldPredicate(field.Trim().ToLowerInvariant(), fieldValue, ctx))
                return false;
        }

        if (obj.TryGetProperty("layer", out var layerEl) && !EvaluateFieldPredicate("layer", layerEl, ctx))
            return false;
        if (obj.TryGetProperty("mode_slug", out var modeEl) && !EvaluateFieldPredicate("mode_slug", modeEl, ctx))
            return false;
        if (obj.TryGetProperty("has_workspace", out var wsEl) && !EvaluateFieldPredicate("has_workspace", wsEl, ctx))
            return false;
        if (obj.TryGetProperty("has_tools", out var toolsEl) && !EvaluateFieldPredicate("has_tools", toolsEl, ctx))
            return false;
        if (obj.TryGetProperty("capability", out var capEl) && !EvaluateFieldPredicate("capability", capEl, ctx))
            return false;

        return true;
    }

    private static bool EvaluateFieldPredicate(string field, JsonElement value, PipelineEvalContext ctx)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        return field switch
        {
            "layer" => MatchesStringOrArray(ctx.Layer, value),
            "mode_slug" => MatchesStringOrArray(ctx.ModeSlug, value),
            "has_workspace" => TryReadBool(value, out var bWs) && ctx.HasWorkspace == bWs,
            "has_tools" => TryReadBool(value, out var bTools) && ctx.HasTools == bTools,
            "capability" => MatchesCapability(ctx.Capabilities, value),
            _ => true
        };
    }

    private static bool MatchesStringOrArray(string actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.String)
            return string.Equals(actual, expected.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (expected.ValueKind == JsonValueKind.Array)
            return expected.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && string.Equals(actual, item.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static bool MatchesCapability(IReadOnlySet<string> actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.String)
        {
            var cap = expected.GetString()?.Trim();
            return !string.IsNullOrEmpty(cap) && actual.Contains(cap);
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in expected.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim() is { Length: > 0 } cap && actual.Contains(cap))
                    return true;
            }
            return false;
        }
        return true;
    }

    private static bool TryReadBool(JsonElement value, out bool result)
    {
        if (value.ValueKind == JsonValueKind.True) { result = true; return true; }
        if (value.ValueKind == JsonValueKind.False) { result = false; return true; }
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString()?.Trim(), out var parsed))
        {
            result = parsed;
            return true;
        }
        result = false;
        return false;
    }

    private static string NormalizePlacement(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "before_system_prompt" or "before_system" => "before_system_prompt",
        "after_system_prompt" or "after_system" => "after_system_prompt",
        "before_runtime_constraints" or "before_constraints" or "after_fragments" => "before_runtime_constraints",
        _ => "pipeline"
    };

    private static bool MatchesAgent(PoolFragment fragment, IReadOnlySet<Guid> candidateGuids, IReadOnlySet<string> candidateSlugs)
    {
        var hasId = fragment.TargetAgentId is { } tid && tid != Guid.Empty;
        var hasSlug = !string.IsNullOrWhiteSpace(fragment.TargetAgentSlug);
        if (!hasId && !hasSlug) return true;
        if (hasId && candidateGuids.Contains(fragment.TargetAgentId!.Value)) return true;
        if (hasSlug && candidateSlugs.Contains(fragment.TargetAgentSlug!.Trim())) return true;
        return false;
    }

    /// <summary>
    /// The run's workspace facts. Root, git facts and the path contract are always
    /// present — a model that does not know its workspace cannot use any
    /// path-taking tool, which is exactly the failure this section exists to
    /// prevent. Only the optional top-level listing is budget-trimmed.
    /// </summary>
    internal static (string Text, IReadOnlyList<string> Warnings) WorkspaceContext(
        FrozenWorkspaceBinding workspace,
        int remainingBudget)
    {
        var warnings = new List<string>();
        var lines = new List<string>
        {
            "Workspace: exactly one workspace is in scope for this run.",
            $"- Root (absolute): {workspace.RootPath}",
            workspace.IsGitRepository
                ? workspace.GitBranch is { } branch
                    ? $"- Git repository: yes (branch {branch})"
                    : "- Git repository: yes (detached HEAD)"
                : "- Git repository: no",
            $"- Path contract ({workspace.PathContract}): every path argument must be an absolute path inside the root; a relative path resolves against the root. Nothing outside the root is readable or writable.",
            "- Never ask the user for the workspace path; use the root above. When a tool reports a path outside the root, correct the path instead of guessing another location."
        };
        if (workspace.ReadOnlyRoots.Count > 0)
        {
            lines.Insert(3, $"- Additional read-only roots: {string.Join(", ", workspace.ReadOnlyRoots)}");
        }

        if (workspace.TopLevelEntries.Count > 0)
        {
            var suffix = workspace.TopLevelTruncated ? " [truncated]" : string.Empty;
            var listing = $"- Top-level entries: {string.Join(", ", workspace.TopLevelEntries)}{suffix}";
            if (EstimateTokens(string.Join('\n', lines.Append(listing))) <= remainingBudget)
            {
                lines.Add(listing);
            }
            else
            {
                warnings.Add("The workspace top-level listing was omitted because the context budget is exhausted.");
            }
        }

        return (string.Join('\n', lines), warnings);
    }

    private static string ArchitectureBaseline(string agentId) =>
        $"TinadecOffice uses a Core-owned operation/execution agent harness. You are '{agentId}'. The meeting agent is the only formal user-facing agent; all other agents submit auditable evidence to Core.";

    private static string? ReadTemplateText(JsonElement node) =>
        ReadNodeString(node, "content", "template", "text", "value");

    private static string? ReadNodeString(JsonElement node, params string[] keys)
    {
        foreach (var container in NodeContainers(node))
        {
            foreach (var key in keys)
            {
                if (ReadString(container, key) is { } value) return value;
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> NodeContainers(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
            yield return config;
        if (node.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            yield return data;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int CategoryOrder(string category) => category.Trim().ToLowerInvariant() switch
    {
        "mode_profile" or "mode" => 1,
        "agent_profile" or "agent" => 2,
        "task" or "task_context" => 3,
        _ => 4
    };

    /// <summary>
    /// CJK-aware token estimation (~1.5 CJK characters per token vs ~4 ASCII characters per token).
    /// </summary>
    internal static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = 0;
        foreach (var ch in text)
        {
            if (IsCjkChar(ch)) cjk++;
        }
        var nonCjk = text.Length - cjk;
        var estimated = (int)Math.Ceiling(cjk / 1.5d + nonCjk / 4.0d);
        return Math.Max(1, estimated);
    }

    private static bool IsCjkChar(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\u3000' and <= '\u303F')
            or (>= '\u3040' and <= '\u309F')
            or (>= '\u30A0' and <= '\u30FF')
            or (>= '\uAC00' and <= '\uD7AF')
            or (>= '\uFF00' and <= '\uFFEF');

    private sealed record GraphNode(int Order, string Id, string Kind, string Label, JsonElement Element);
    private sealed record GraphEdge(string Source, string Target);
    private sealed record PoolFragment(Guid Id, string Key, string Title, string Scope, Guid? TargetAgentId, string? TargetAgentSlug, string Category, int Priority, bool Enabled, string Content);
    private sealed record PipelineRenderedBlock(string Id, string NodeId, string Kind, string Title, string Content, string Source, string Placement, string? FragmentId, bool IsWorkspaceRulesPlacement);

    internal sealed record PipelineEvalContext(
        string AgentName,
        string AgentSlug,
        string ModeName,
        string ModeSlug,
        string Layer,
        string WorkspaceRoot,
        bool HasWorkspace,
        bool HasTools,
        IReadOnlySet<string> Capabilities);
}
