using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Companion;

/// <summary>
/// Companion domain read/write surface. Every predicate starts from the verified tenant id;
/// a companion id that is missing or belongs to another tenant is the same null answer, so
/// endpoints cannot be used to probe other tenants. Nothing here touches work-domain tables
/// directly: work-session views route through <see cref="ICompanionWorkGateway"/> and layered
/// memory routes through <see cref="IContextMemoryService"/>.
/// </summary>
public sealed class CompanionService(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ITenantContextAccessor tenant,
    ICompanionStreamBroker? streamBroker = null,
    IContextMemoryService? contextMemory = null,
    ICompanionWorkGateway? workGateway = null)
{
    internal const string WorkSessionBindingDeletedActivityKind = "work_session_binding_deleted";
    private const int MaxPersonaBytes = 64 * 1024;
    private static readonly string[] MemorySourceKinds = ["user", "companion_run", "group_message"];
    private static readonly string[] SkillSourceKinds = ["manual", "pack"];
    private static readonly string[] ConnectionKinds = ["mcp", "server", "phone", "external_agent", "work_session", "browser"];
    private static readonly string[] BrowserDrivers = ["builtin_electron", "ego_lite"];
    private static readonly string[] AuthorizationStates = ["configured", "authorized", "revoked"];
    private static readonly string[] CompanionRoles = ["autonomous", "session_partner"];

    private Guid TenantId => tenant.Current.TenantId;

    public async Task<object> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Companions.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!includeArchived) query = query.Where(x => x.Status != "archived");
        var rows = (await query.ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();
        var counts = await LoadCountsAsync(db, tenantId, rows.Select(x => x.Id).ToList(), cancellationToken).ConfigureAwait(false);
        return rows.Select(row => CompanionContracts.ToJson(row, counts.GetValueOrDefault(row.Id, EmptyCounts))).ToList();
    }

    public async Task<object> CreateAsync(CompanionCreateRequest input, CancellationToken cancellationToken = default)
    {
        var name = RequireName(input.Name);
        var persona = input.PersonaDocument;
        EnsurePersona(persona);
        var now = DateTimeOffset.UtcNow;
        var row = new CompanionAgentRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Name = name,
            Tagline = TrimToNull(input.Tagline),
            AvatarKey = TrimToNull(input.Avatar),
            PersonaDocument = persona,
            ModelPurpose = NormalizePurpose(input.ModelPurpose, "chat"),
            Status = "active",
            Role = NormalizeChoice(input.Role, "autonomous", CompanionRoles, "role"),
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (input.Model is { } model)
        {
            var (providerId, modelName) = ParseModel(model);
            row.ModelProviderId = providerId;
            row.ModelName = modelName;
        }
        if (input.Autonomy is { } autonomy) ApplyAutonomy(row, autonomy);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Companions.Add(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToDetailJson(row, EmptyCounts);
    }

    public async Task<object?> GetAsync(Guid companionId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        var counts = await LoadCountsAsync(db, tenantId, [row.Id], cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToDetailJson(row, counts.GetValueOrDefault(row.Id, EmptyCounts));
    }

    public async Task<object?> PatchAsync(Guid companionId, long expectedRevision, CompanionPatchRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Companions.SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureRevision(row.Revision, expectedRevision);

        if (input.Name is not null) row.Name = RequireName(input.Name);
        if (input.Tagline is not null) row.Tagline = TrimToNull(input.Tagline);
        if (input.Avatar is not null) row.AvatarKey = TrimToNull(input.Avatar);
        if (input.PersonaDocument is not null)
        {
            EnsurePersona(input.PersonaDocument);
            row.PersonaDocument = input.PersonaDocument;
        }
        if (input.ModelPurpose is not null) row.ModelPurpose = NormalizePurpose(input.ModelPurpose, row.ModelPurpose);
        if (input.Role is not null) row.Role = NormalizeChoice(input.Role, CompanionContracts.NormalizeRole(row.Role), CompanionRoles, "role");
        if (input.Model is { } model)
        {
            // "Absent" and "clear" differ here: an empty string drops the pin and falls back to the
            // tenant's route purpose, a value pins one provider model.
            var (providerId, modelName) = ParseModel(model);
            row.ModelProviderId = providerId;
            row.ModelName = modelName;
        }
        if (input.Autonomy is { } autonomy) ApplyAutonomy(row, autonomy);
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);

        var counts = await LoadCountsAsync(db, tenantId, [row.Id], cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToDetailJson(row, counts.GetValueOrDefault(row.Id, EmptyCounts));
    }

    public async Task<object?> ArchiveAsync(Guid companionId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Companions.SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureRevision(row.Revision, expectedRevision);
        row.Status = "archived";
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);

        var counts = await LoadCountsAsync(db, tenantId, [row.Id], cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToDetailJson(row, counts.GetValueOrDefault(row.Id, EmptyCounts));
    }

    // ── memories (unified via IContextMemoryService with lazy legacy backfill) ──

    public async Task<object> ListMemoriesAsync(Guid companionId, string? query, int? limit, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var take = Math.Clamp(limit ?? 20, 1, 100);

        if (contextMemory is not null)
        {
            await EnsureLegacyMemoriesBackfilledAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
            var listed = await contextMemory.ListItemsAsync(
                scope: "companion",
                companionId: companionId,
                status: "active",
                query: TrimToNull(query),
                includeVersions: true,
                limit: take,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return listed.Items
                .Select(item => CompanionContracts.ToMemoryJson(item, companionId))
                .ToList();
        }

        var rowsQuery = db.Memories.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId && x.IndexStatus == "indexed");
        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term)) rowsQuery = rowsQuery.Where(x => EF.Functions.Like(x.Content, $"%{term}%"));
        var rows = (await rowsQuery.ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(CompanionContracts.ToJson)
            .ToList();
        return rows;
    }

    public async Task<object> CreateMemoryAsync(Guid companionId, CompanionMemoryCreateRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var content = input.Content?.Trim();
        if (string.IsNullOrEmpty(content)) throw Invalid("content is required.");
        var sourceKind = NormalizeChoice(input.SourceKind, "user", MemorySourceKinds, "source_kind");
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);

        if (contextMemory is not null)
        {
            await EnsureLegacyMemoriesBackfilledAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
            var created = await contextMemory.UpsertItemAsync(new UpsertMemoryItemRequest
            {
                Scope = "companion",
                CompanionId = companionId,
                Kind = MapSourceKindToMemoryKind(sourceKind),
                Content = content,
                SourceSessionId = input.SourceConversationId,
                Reason = $"source_kind:{sourceKind}",
                IsCompanionWriter = sourceKind != "user"
            }, cancellationToken).ConfigureAwait(false);
            return CompanionContracts.ToMemoryJson(created, companionId, sourceKind, input.SourceConversationId, input.SourceMessageId);
        }

        var row = new CompanionMemoryRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = companionId,
            Content = content,
            SourceKind = sourceKind,
            SourceConversationId = input.SourceConversationId,
            SourceMessageId = input.SourceMessageId,
            Visibility = "private",
            IndexStatus = "indexed",
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Memories.Add(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    public async Task<object> PatchMemoryAsync(Guid companionId, Guid memoryId, long expectedRevision, CompanionMemoryPatchRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var content = input.Content?.Trim();
        if (string.IsNullOrEmpty(content)) throw Invalid("content is required.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);

        if (contextMemory is not null)
        {
            await EnsureLegacyMemoriesBackfilledAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
            var active = await contextMemory.ListItemsAsync(
                scope: "companion",
                companionId: companionId,
                status: "active",
                includeVersions: true,
                limit: 200,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var existing = active.Items.FirstOrDefault(x => x.Id == memoryId);
            if (existing is null) throw NotFound("Memory not found.");
            EnsureRevision(Math.Max(1, existing.CurrentVersion), expectedRevision);

            if (existing.InHotMarkdown)
            {
                var doc = await contextMemory.GetDocumentAsync("companion", companionId: companionId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var singleLine = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
                var anchorPattern = new Regex($@"^-\s+.*?<!--\s*mem:{Regex.Escape(memoryId.ToString("D"))}(?:\s+[^>]*)?\s*-->\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (anchorPattern.IsMatch(doc.Markdown))
                {
                    var updatedMarkdown = anchorPattern.Replace(doc.Markdown, $"- {singleLine} <!-- mem:{memoryId:D} -->", 1);
                    var updatedDoc = await contextMemory.UpdateDocumentAsync(
                        "companion",
                        new UpdateMemoryDocumentRequest
                        {
                            CompanionId = companionId,
                            Markdown = updatedMarkdown,
                            ExpectedContentHash = doc.ContentHash,
                            Reason = "Updated via Companion Memory API"
                        },
                        cancellationToken).ConfigureAwait(false);
                    var updatedItem = updatedDoc.Items.FirstOrDefault(x => x.Id == memoryId);
                    if (updatedItem is not null)
                        return CompanionContracts.ToMemoryJson(updatedItem, companionId);
                }
            }

            var superseded = await contextMemory.UpsertItemAsync(new UpsertMemoryItemRequest
            {
                Scope = "companion",
                CompanionId = companionId,
                Kind = existing.Kind,
                Section = existing.Section,
                Content = content,
                SupersedeItemId = memoryId,
                Reason = "Updated via Companion Memory API"
            }, cancellationToken).ConfigureAwait(false);
            return CompanionContracts.ToMemoryJson(superseded, companionId);
        }

        var row = await db.Memories.SingleOrDefaultAsync(
            x => x.Id == memoryId && x.TenantId == tenantId && x.CompanionId == companionId && x.IndexStatus == "indexed",
            cancellationToken).ConfigureAwait(false);
        if (row is null) throw NotFound("Memory not found.");
        EnsureRevision(row.Revision, expectedRevision);
        row.Content = content;
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    public async Task<bool> DeleteMemoryAsync(Guid companionId, Guid memoryId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);

        if (contextMemory is not null)
        {
            await EnsureLegacyMemoriesBackfilledAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
            var active = await contextMemory.ListItemsAsync(
                scope: "companion",
                companionId: companionId,
                status: "active",
                includeVersions: false,
                limit: 200,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var existing = active.Items.FirstOrDefault(x => x.Id == memoryId);
            if (existing is null) return false;
            EnsureRevision(Math.Max(1, existing.CurrentVersion), expectedRevision);
            await contextMemory.RevokeItemAsync(memoryId, "Deleted via Companion Memory API", cancellationToken).ConfigureAwait(false);
            return true;
        }

        var row = await db.Memories.SingleOrDefaultAsync(
            x => x.Id == memoryId && x.TenantId == tenantId && x.CompanionId == companionId && x.IndexStatus == "indexed",
            cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        EnsureRevision(row.Revision, expectedRevision);
        row.IndexStatus = "deleted";
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static async Task BackfillLegacyMemoriesCoreAsync(
        CompanionDbContext db,
        IContextMemoryService memoryService,
        Guid tenantId,
        Guid companionId,
        CancellationToken cancellationToken)
    {
        var legacyRows = await db.Memories
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId && x.IndexStatus == "indexed")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (legacyRows.Count == 0) return;

        var existing = await memoryService.ListItemsAsync(
            scope: "companion",
            companionId: companionId,
            status: "active",
            includeVersions: false,
            limit: 200,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var existingContents = existing.Items
            .Select(x => x.Content.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        foreach (var legacy in legacyRows.OrderBy(x => x.CreatedAt))
        {
            var trimmed = legacy.Content.Trim();
            if (trimmed.Length > 0 && existingContents.Add(trimmed))
            {
                await memoryService.UpsertItemAsync(new UpsertMemoryItemRequest
                {
                    Scope = "companion",
                    CompanionId = companionId,
                    Kind = MapSourceKindToMemoryKind(legacy.SourceKind),
                    Content = trimmed,
                    SourceSessionId = legacy.SourceConversationId,
                    Reason = $"source_kind:{legacy.SourceKind}",
                    IsCompanionWriter = legacy.SourceKind != "user"
                }, cancellationToken).ConfigureAwait(false);
            }

            legacy.IndexStatus = "migrated";
            legacy.Revision++;
            legacy.UpdatedAt = now;
        }

        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
    }

    private Task EnsureLegacyMemoriesBackfilledAsync(
        CompanionDbContext db,
        Guid tenantId,
        Guid companionId,
        CancellationToken cancellationToken) =>
        contextMemory is null
            ? Task.CompletedTask
            : BackfillLegacyMemoriesCoreAsync(db, contextMemory, tenantId, companionId, cancellationToken);

    private static string MapSourceKindToMemoryKind(string? sourceKind) => sourceKind switch
    {
        "companion_run" => "episode",
        "group_message" => "fact",
        _ => "bond"
    };

    // ── skills ──

    public async Task<object> ListSkillsAsync(Guid companionId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var rows = (await db.SkillBindings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(CompanionContracts.ToJson)
            .ToList();
        return rows;
    }

    public async Task<object> CreateSkillAsync(Guid companionId, CompanionSkillCreateRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var name = RequireName(input.Name);
        var sourceKind = NormalizeChoice(input.SourceKind, "manual", SkillSourceKinds, "source_kind");
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        if (await db.SkillBindings.AnyAsync(x => x.TenantId == tenantId && x.CompanionId == companionId && x.Name == name, cancellationToken).ConfigureAwait(false))
            throw Duplicate("A skill binding with this name already exists for this companion.");
        var row = new CompanionSkillBindingRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = companionId,
            Name = name,
            SourceKind = sourceKind,
            PackId = TrimToNull(input.PackId),
            PackVersion = TrimToNull(input.PackVersion),
            Instructions = input.Instructions,
            Enabled = input.Enabled ?? true,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SkillBindings.Add(row);
        await SaveNewBindingAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    public async Task<object> PatchSkillAsync(Guid companionId, Guid bindingId, long expectedRevision, CompanionSkillPatchRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.SkillBindings.SingleOrDefaultAsync(
            x => x.Id == bindingId && x.TenantId == tenantId && x.CompanionId == companionId,
            cancellationToken).ConfigureAwait(false);
        if (row is null) throw NotFound("Skill binding not found.");
        EnsureRevision(row.Revision, expectedRevision);
        if (input.Enabled is { } enabled) row.Enabled = enabled;
        if (input.Instructions is not null) row.Instructions = input.Instructions;
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    public async Task<bool> DeleteSkillAsync(Guid companionId, Guid bindingId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.SkillBindings.SingleOrDefaultAsync(
            x => x.Id == bindingId && x.TenantId == tenantId && x.CompanionId == companionId,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        EnsureRevision(row.Revision, expectedRevision);
        db.SkillBindings.Remove(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ── connections & work_session / browser connectors ──

    public async Task<object> ListConnectionsAsync(Guid companionId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var rows = (await db.ConnectionBindings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(CompanionContracts.ToJson)
            .ToList();
        return rows;
    }

    public async Task<object> CreateConnectionAsync(Guid companionId, CompanionConnectionCreateRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var name = RequireName(input.Name);
        var kind = input.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !ConnectionKinds.Contains(kind, StringComparer.Ordinal))
            throw Invalid("kind must be work_session, browser, mcp, server, phone, or external_agent.");

        var defaultAuth = kind is "work_session" or "browser" ? "authorized" : "configured";
        var authorization = NormalizeChoice(input.Authorization, defaultAuth, AuthorizationStates, "authorization");
        var now = DateTimeOffset.UtcNow;

        string? endpoint = TrimToNull(input.Endpoint);
        string? credentialRef = TrimToNull(input.CredentialRef);
        string runtimeStatus = "unsupported";
        DateTimeOffset? lastCheckedAt = null;

        if (kind == "work_session")
        {
            endpoint ??= "*";
            runtimeStatus = authorization == "authorized"
                ? "connected"
                : authorization == "revoked"
                    ? "disconnected"
                    : "configured";
            lastCheckedAt = now;
        }
        else if (kind == "browser")
        {
            var (driver, profileId) = NormalizeBrowserInput(input.Driver, input.ProfileId, endpoint, credentialRef);
            endpoint = string.IsNullOrEmpty(profileId) ? driver : $"{driver}:{profileId}";
            credentialRef = profileId ?? credentialRef;
            runtimeStatus = ComputeBrowserRuntimeStatus(driver, authorization, enabled: true);
            lastCheckedAt = now;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        if (await db.ConnectionBindings.AnyAsync(x => x.TenantId == tenantId && x.CompanionId == companionId && x.Name == name, cancellationToken).ConfigureAwait(false))
            throw Duplicate("A connection binding with this name already exists for this companion.");
        var row = new CompanionConnectionBindingRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = companionId,
            Kind = kind,
            Name = name,
            Endpoint = endpoint,
            CredentialRef = credentialRef,
            Authorization = authorization,
            RuntimeStatus = runtimeStatus,
            Enabled = true,
            LastCheckedAt = lastCheckedAt,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ConnectionBindings.Add(row);
        await SaveNewBindingAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    public async Task<object> PatchConnectionAsync(Guid companionId, Guid bindingId, long expectedRevision, CompanionConnectionPatchRequest input, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var authorization = input.Authorization is null
            ? null
            : NormalizeChoice(input.Authorization, "configured", AuthorizationStates, "authorization");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.ConnectionBindings.SingleOrDefaultAsync(
            x => x.Id == bindingId && x.TenantId == tenantId && x.CompanionId == companionId,
            cancellationToken).ConfigureAwait(false);
        if (row is null) throw NotFound("Connection binding not found.");
        EnsureRevision(row.Revision, expectedRevision);
        var now = DateTimeOffset.UtcNow;
        if (authorization is not null) row.Authorization = authorization;
        if (input.Enabled is { } enabled) row.Enabled = enabled;
        if (input.Endpoint is not null && row.Kind == "work_session")
        {
            row.Endpoint = TrimToNull(input.Endpoint) ?? "*";
        }
        else if (row.Kind == "browser" && (input.Driver is not null || input.ProfileId is not null || input.Endpoint is not null))
        {
            var current = CompanionContracts.ParseBrowserBinding(row);
            var (driver, profileId) = NormalizeBrowserInput(
                input.Driver ?? current.Driver,
                input.ProfileId ?? current.ProfileId,
                input.Endpoint ?? row.Endpoint,
                row.CredentialRef);
            row.Endpoint = string.IsNullOrEmpty(profileId) ? driver : $"{driver}:{profileId}";
            row.CredentialRef = profileId;
        }

        if (row.Kind == "work_session")
        {
            row.RuntimeStatus = !row.Enabled || row.Authorization == "revoked"
                ? "disconnected"
                : row.Authorization == "authorized"
                    ? "connected"
                    : "configured";
            row.LastCheckedAt = now;
        }
        else if (row.Kind == "browser")
        {
            var (driver, _) = CompanionContracts.ParseBrowserBinding(row);
            row.RuntimeStatus = ComputeBrowserRuntimeStatus(driver ?? "builtin_electron", row.Authorization, row.Enabled);
            row.LastCheckedAt = now;
        }

        row.Revision++;
        row.UpdatedAt = now;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    public async Task<bool> DeleteConnectionAsync(Guid companionId, Guid bindingId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.ConnectionBindings.SingleOrDefaultAsync(
            x => x.Id == bindingId && x.TenantId == tenantId && x.CompanionId == companionId,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        EnsureRevision(row.Revision, expectedRevision);
        var wasWorkSession = string.Equals(row.Kind, "work_session", StringComparison.OrdinalIgnoreCase);
        var bindingName = row.Name;
        db.ConnectionBindings.Remove(row);
        if (wasWorkSession)
        {
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = WorkSessionBindingDeletedActivityKind,
                CompanionId = companionId,
                Summary = $"已移除工作会话连接「{bindingName}」",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await CompanionActivities.SaveAsync(db, tenantId, token => SaveAsync(db, token), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Lists authorized work sessions for <paramref name="companionId"/>, auto-seeding the wildcard
    /// <c>"*"</c> <c>work_session</c> grant on the default/main companion if missing.
    /// </summary>
    public async Task<object> ListWorkSessionsAsync(Guid companionId, Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
            await EnsureMainWorkSessionGrantCoreAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        }

        if (workGateway is null)
            return Array.Empty<object>();

        var result = await workGateway.ListSessionsAsync(companionId, projectId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
            return Array.Empty<object>();

        return result.Value.Select(session => new
        {
            id = session.Id,
            session_id = session.Id,
            title = session.Title,
            session_title = session.Title,
            project_id = session.ProjectId,
            project_name = session.ProjectName,
            mode_name = session.ModeName,
            view_mode = session.ViewMode ?? "flat",
            latest_run_id = session.LatestRun?.Id,
            latest_run_status = session.LatestRun?.Status,
            latest_run_summary = session.LatestRun?.Summary,
            latest_run = session.LatestRun is null
                ? null
                : new
                {
                    id = session.LatestRun.Id,
                    status = session.LatestRun.Status,
                    summary = session.LatestRun.Summary
                },
            plan_summary = session.PlanSummary,
            plan_progress = session.PlanProgress is null
                ? null
                : new
                {
                    done = session.PlanProgress.Done,
                    total = session.PlanProgress.Total,
                    current_step = session.PlanProgress.CurrentStep,
                    summary = session.PlanProgress.Summary ?? session.PlanSummary
                },
            pending_approvals = (session.PendingApprovals ?? []).Select(approval => new
            {
                approval_id = approval.ApprovalId,
                tool_id = approval.ToolId,
                summary = approval.Summary,
                risk_level = approval.RiskLevel,
                created_at = approval.CreatedAt
            }).ToList(),
            updated_at = session.UpdatedAt
        }).ToList();
    }

    public IReadOnlyList<object> DiscoverBrowserProfiles()
    {
        var profiles = new List<object>
        {
            new
            {
                id = "builtin_electron:default",
                profile_id = "default",
                name = "Built-in Electron TaskSpace",
                driver = "builtin_electron",
                is_default = true,
                available = true,
                detail = "Isolated Tinadec Desktop browser TaskSpace"
            }
        };

        var egoCliAvailable = IsEgoBrowserAvailable();
        var discoveredEgoProfiles = DiscoverEgoLiteProfileNames();
        if (discoveredEgoProfiles.Count == 0)
        {
            profiles.Add(new
            {
                id = "ego_lite:default",
                profile_id = "default",
                name = "Ego Lite (Default)",
                driver = "ego_lite",
                is_default = false,
                available = egoCliAvailable,
                detail = egoCliAvailable
                    ? "External Ego Lite persistent browser profile"
                    : "Install ego-browser CLI / Ego Lite to enable external persistent profiles"
            });
        }
        else
        {
            foreach (var profileName in discoveredEgoProfiles)
            {
                profiles.Add(new
                {
                    id = $"ego_lite:{profileName}",
                    profile_id = profileName,
                    name = $"Ego Lite ({profileName})",
                    driver = "ego_lite",
                    is_default = false,
                    available = true,
                    detail = "External Ego Lite persistent browser profile"
                });
            }
        }

        return profiles;
    }

    public static async Task EnsureMainWorkSessionGrantCoreAsync(
        CompanionDbContext db,
        Guid tenantId,
        Guid candidateCompanionId,
        CancellationToken cancellationToken)
    {
        if (candidateCompanionId == Guid.Empty) return;

        var hasAnyWorkSessionBinding = await db.ConnectionBindings.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.CompanionId == candidateCompanionId && x.Kind == "work_session", cancellationToken)
            .ConfigureAwait(false);
        if (hasAnyWorkSessionBinding) return;

        var wasExplicitlyDeleted = await db.Activities.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.CompanionId == candidateCompanionId && x.Kind == WorkSessionBindingDeletedActivityKind, cancellationToken)
            .ConfigureAwait(false);
        if (wasExplicitlyDeleted) return;

        var mainId = await db.Settings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.MainCompanionId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        bool isDefaultOrMain;
        if (mainId is { } configuredMain && configuredMain != Guid.Empty)
        {
            isDefaultOrMain = configuredMain == candidateCompanionId;
        }
        else
        {
            var activeCompanions = await db.Companions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Status == "active")
                .Select(x => new { x.Id, x.Role, x.CreatedAt })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var primary = activeCompanions
                .OrderBy(x => CompanionContracts.NormalizeRole(x.Role) == "autonomous" ? 0 : 1)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .FirstOrDefault();
            isDefaultOrMain = primary?.Id == candidateCompanionId;
        }

        if (!isDefaultOrMain) return;

        var now = DateTimeOffset.UtcNow;
        const string defaultName = "work-sessions-all";
        var nameTaken = await db.ConnectionBindings.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.CompanionId == candidateCompanionId && x.Name == defaultName, cancellationToken)
            .ConfigureAwait(false);
        db.ConnectionBindings.Add(new CompanionConnectionBindingRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = candidateCompanionId,
            Kind = "work_session",
            Name = nameTaken ? $"work-sessions-all-{Guid.NewGuid().ToString("N")[..6]}" : defaultName,
            Endpoint = "*",
            Authorization = "authorized",
            RuntimeStatus = "connected",
            Enabled = true,
            LastCheckedAt = now,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private static (string Driver, string? ProfileId) NormalizeBrowserInput(
        string? explicitDriver,
        string? explicitProfileId,
        string? rawEndpoint,
        string? rawCredentialRef)
    {
        var profileId = TrimToNull(explicitProfileId) ?? TrimToNull(rawCredentialRef);
        var driverCandidate = TrimToNull(explicitDriver)?.ToLowerInvariant();

        if (driverCandidate is null && !string.IsNullOrWhiteSpace(rawEndpoint))
        {
            var raw = rawEndpoint.Trim();
            var colon = raw.IndexOf(':');
            if (colon > 0)
            {
                driverCandidate = raw[..colon].Trim().ToLowerInvariant();
                var tail = raw[(colon + 1)..].Trim();
                if (!string.IsNullOrEmpty(tail)) profileId ??= tail;
            }
            else
            {
                driverCandidate = raw.ToLowerInvariant();
            }
        }

        var driver = driverCandidate ?? "builtin_electron";
        if (!BrowserDrivers.Contains(driver, StringComparer.Ordinal))
            throw Invalid("browser connection driver must be builtin_electron or ego_lite.");

        return (driver, profileId);
    }

    private static string ComputeBrowserRuntimeStatus(string driver, string authorization, bool enabled)
    {
        if (!enabled || authorization == "revoked") return "disconnected";
        if (authorization != "authorized") return "configured";
        if (driver == "ego_lite")
            return IsEgoBrowserAvailable() || DiscoverEgoLiteProfileNames().Count > 0 ? "connected" : "unavailable";
        return "connected";
    }

    private static bool IsEgoBrowserAvailable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            string[] candidates = OperatingSystem.IsWindows()
                ? ["ego-browser.exe", "ego-browser.cmd", "ego-browser.bat", "ego-browser"]
                : ["ego-browser"];
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var exe in candidates)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir.Trim(), exe))) return true;
                    }
                    catch
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }
        }
        return false;
    }

    private static List<string> DiscoverEgoLiteProfileNames()
    {
        var names = new List<string>();
        var roots = new List<string>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamingApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(localApp)) roots.Add(Path.Combine(localApp, "ego-lite", "profiles"));
            if (!string.IsNullOrEmpty(roamingApp)) roots.Add(Path.Combine(roamingApp, "ego-lite", "profiles"));
            if (!string.IsNullOrEmpty(home))
            {
                roots.Add(Path.Combine(home, ".ego-lite", "profiles"));
                roots.Add(Path.Combine(home, "Library", "Application Support", "ego-lite", "profiles"));
            }

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                foreach (var subDir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(subDir);
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }
        }
        catch
        {
            // Discovery is best-effort and never fails requests.
        }
        return names;
    }

    // ── settings ──

    public async Task<object> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    public async Task<object> PatchSettingsAsync(
        long expectedRevision,
        bool mainSpecified,
        Guid? mainCompanionId,
        int? recruitLimit,
        int? maxBotHopDepth,
        bool? autonomyPaused,
        CancellationToken cancellationToken = default)
    {
        if (maxBotHopDepth is < 0 or > 10)
            throw Invalid("max_bot_hop_depth must be between 0 and 10.");

        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Settings.SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        var currentRevision = row?.Revision ?? 0;
        if (expectedRevision >= 0 && expectedRevision != currentRevision) throw Conflict();
        if (mainSpecified && mainCompanionId is { } mainId)
        {
            var target = await db.Companions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == mainId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
            if (target is null || target.Status != "active")
                throw new CompanionException("companion_not_found", "The main companion must be an active companion of this tenant.", 400);
        }
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new CompanionSettingsRecord
            {
                TenantId = tenantId,
                MainCompanionId = mainSpecified ? mainCompanionId : null,
                RecruitLimit = recruitLimit is { } rLimit ? Math.Clamp(rLimit, 0, 20) : 3,
                MaxBotHopDepth = maxBotHopDepth ?? 3,
                AutonomyPaused = autonomyPaused ?? false,
                Revision = 1,
                UpdatedAt = now
            };
            db.Settings.Add(row);
        }
        else
        {
            if (mainSpecified) row.MainCompanionId = mainCompanionId;
            if (recruitLimit is { } limit) row.RecruitLimit = Math.Clamp(limit, 0, 20);
            if (maxBotHopDepth is { } hopDepth) row.MaxBotHopDepth = hopDepth;
            if (autonomyPaused is { } paused) row.AutonomyPaused = paused;
            row.Revision++;
            row.UpdatedAt = now;
        }
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "settings_changed",
            Summary = "伙伴设置已更新",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                main_companion_id = row.MainCompanionId,
                recruit_limit = row.RecruitLimit,
                max_bot_hop_depth = Math.Clamp(row.MaxBotHopDepth ?? 3, 0, 10),
                autonomy_paused = row.AutonomyPaused,
                revision = row.Revision
            }),
            CreatedAt = now
        });
        await CompanionActivities.SaveAsync(db, tenantId, token => SaveAsync(db, token), cancellationToken).ConfigureAwait(false);
        if (mainSpecified && row.MainCompanionId is { } configuredMainId)
        {
            await EnsureMainWorkSessionGrantCoreAsync(db, tenantId, configuredMainId, cancellationToken).ConfigureAwait(false);
        }
        return CompanionContracts.ToJson(row);
    }

    // ── helpers ──

    private static readonly CompanionCounts EmptyCounts = new(0, 0, 0, null);

    private async Task<Dictionary<Guid, CompanionCounts>> LoadCountsAsync(
        CompanionDbContext db,
        Guid tenantId,
        IReadOnlyList<Guid> companionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, CompanionCounts>();
        if (companionIds.Count == 0) return result;

        var memoryCounts = await db.Memories.AsNoTracking()
            .Where(x => x.TenantId == tenantId && companionIds.Contains(x.CompanionId) && x.IndexStatus == "indexed")
            .GroupBy(x => x.CompanionId)
            .Select(group => new { CompanionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.CompanionId, x => x.Count, cancellationToken).ConfigureAwait(false);
        if (contextMemory is not null)
        {
            var unified = await contextMemory.ListItemsAsync(
                scope: "companion",
                status: "active",
                includeVersions: false,
                limit: 200,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var group in unified.Items.Where(x => x.CompanionId is not null && companionIds.Contains(x.CompanionId.Value))
                         .GroupBy(x => x.CompanionId!.Value))
            {
                memoryCounts[group.Key] = memoryCounts.GetValueOrDefault(group.Key) + group.Count();
            }
        }
        var skillCounts = await db.SkillBindings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && companionIds.Contains(x.CompanionId))
            .GroupBy(x => x.CompanionId)
            .Select(group => new { CompanionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.CompanionId, x => x.Count, cancellationToken).ConfigureAwait(false);
        var connectionCounts = await db.ConnectionBindings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && companionIds.Contains(x.CompanionId))
            .GroupBy(x => x.CompanionId)
            .Select(group => new { CompanionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.CompanionId, x => x.Count, cancellationToken).ConfigureAwait(false);
        // "thinking" is a fact about a live run, not about configuration, so it is read from the
        // run table; the global autonomy switch is a tenant-wide pause that applies to every row.
        var runningIds = await db.Runs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status == "running" && companionIds.Contains(x.CompanionId))
            .Select(x => x.CompanionId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var autonomyPausedGlobally = await db.Settings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => (bool?)x.AutonomyPaused)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? false;
        // SQLite cannot ORDER BY a DateTimeOffset server-side, so the newest activity is
        // picked in memory from the rows this tenant actually has.
        var activityTimes = await db.Activities.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId != null && companionIds.Contains(x.CompanionId.Value))
            .Select(x => new { CompanionId = x.CompanionId!.Value, x.CreatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lastActivity = activityTimes
            .GroupBy(x => x.CompanionId)
            .ToDictionary(group => group.Key, group => (DateTimeOffset?)group.Max(x => x.CreatedAt));

        foreach (var id in companionIds)
        {
            result[id] = new CompanionCounts(
                memoryCounts.GetValueOrDefault(id),
                skillCounts.GetValueOrDefault(id),
                connectionCounts.GetValueOrDefault(id),
                lastActivity.GetValueOrDefault(id),
                runningIds.Contains(id),
                autonomyPausedGlobally);
        }
        return result;
    }

    private async Task RequireCompanionAsync(CompanionDbContext db, Guid companionId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var exists = await db.Companions.AsNoTracking()
            .AnyAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (!exists) throw NotFound();
    }

    private static void EnsureRevision(long currentRevision, long expectedRevision)
    {
        if (expectedRevision >= 0 && currentRevision != expectedRevision) throw Conflict();
    }

    private static void EnsurePersona(string? persona)
    {
        if (persona is not null && Encoding.UTF8.GetByteCount(persona) > MaxPersonaBytes)
            throw Invalid("persona_document must not exceed 64 KiB.");
    }

    private static void ApplyAutonomy(CompanionAgentRecord row, CompanionAutonomyRequest autonomy)
    {
        if (autonomy.Enabled is { } enabled) row.AutonomyEnabled = enabled;
        if (autonomy.Paused is { } paused) row.AutonomyPaused = paused;
        if (autonomy.IntervalMinutes is { } interval) row.AutonomyIntervalMinutes = Math.Clamp(interval, 5, 1440);
        if (autonomy.DailyActionBudget is { } budget) row.AutonomyDailyBudget = Math.Clamp(budget, 0, 50);
    }

    private static async Task SaveAsync(CompanionDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The WHERE revision = original guard lost the race: the caller's view is stale.
            throw Conflict();
        }
    }

    /// <summary>Creates are only rejected here for the (tenant, companion, name) unique index.</summary>
    private static async Task SaveNewBindingAsync(CompanionDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw new CompanionException("companion_duplicate_binding", "A binding with this name already exists for this companion.", 409);
        }
    }

    private static string RequireName(string? name)
    {
        var value = name?.Trim();
        if (string.IsNullOrEmpty(value)) throw Invalid("name is required.");
        if (value.Length > 128) throw Invalid("name must be at most 128 characters.");
        return value;
    }

    private static string NormalizePurpose(string? value, string fallback)
    {
        var purpose = value?.Trim();
        if (string.IsNullOrEmpty(purpose)) return fallback;
        if (purpose.Length > 64) throw Invalid("model_purpose must be at most 64 characters.");
        return purpose;
    }

    /// <summary>
    /// "" means "no pinned model" (null/null); otherwise the value is
    /// "&lt;providerInstanceId&gt;|&lt;model&gt;" — the exact pair the detail returns, so a stale
    /// client round-trip cannot half-overwrite one side. An empty model name pins the provider
    /// itself (CLI runtimes pick their own model). A malformed value is a 400, never a silent reset.
    /// </summary>
    private static (Guid? ProviderId, string? ModelName) ParseModel(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return (null, null);
        var separator = text.IndexOf('|');
        if (separator <= 0) throw Invalid("model must be \"<providerInstanceId>|<model>\" or an empty string.");
        var providerPart = text[..separator].Trim();
        var modelPart = text[(separator + 1)..].Trim();
        if (!Guid.TryParse(providerPart, out var providerId) || providerId == Guid.Empty)
            throw Invalid("model must be \"<providerInstanceId>|<model>\" or an empty string.");
        if (modelPart.Length > 256) throw Invalid("model must be at most 256 characters.");
        return (providerId, modelPart.Length == 0 ? null : modelPart);
    }

    private static string NormalizeChoice(string? value, string fallback, string[] allowed, string field)
    {
        var choice = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(choice)) return fallback;
        if (!allowed.Contains(choice, StringComparer.Ordinal)) throw Invalid($"{field} must be one of {string.Join(", ", allowed)}.");
        return choice;
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── mandates ──

    /// <summary>
    /// Standing authorizations for this companion's autonomous actions. A row exists only once the
    /// autonomy tick wanted to act: the proposal it recorded is what the user decides on, so an
    /// empty list means the companion has not asked for anything yet, not that it cannot.
    /// </summary>
    public async Task<object> ListMandatesAsync(Guid companionId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var rows = (await db.Mandates.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CompanionId == companionId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Action, StringComparer.Ordinal)
            .ToList();
        return rows.Select(CompanionContracts.MandateJson).ToList();
    }

    /// <summary>
    /// Approves or denies one standing authorization. Approving lets the next tick of that action
    /// class run; denying keeps it a proposal until the user changes their mind. The decision is
    /// recorded as an activity so the feed shows who allowed what, and when.
    /// </summary>
    public async Task<object?> DecideMandateAsync(
        Guid companionId, Guid mandateId, long expectedRevision, string? decision, CancellationToken cancellationToken = default)
    {
        var status = decision?.Trim().ToLowerInvariant() switch
        {
            "approved" => "approved",
            "denied" => "denied",
            _ => throw Invalid("decision must be approved or denied.")
        };
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Mandates.SingleOrDefaultAsync(
            x => x.Id == mandateId && x.TenantId == tenantId && x.CompanionId == companionId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        EnsureRevision(row.Revision, expectedRevision);

        var now = DateTimeOffset.UtcNow;
        row.Status = status;
        row.DecidedAt = now;
        row.UpdatedAt = now;
        row.Revision++;
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = status == "approved" ? "autonomy_mandate_approved" : "autonomy_mandate_denied",
            CompanionId = companionId,
            Summary = status == "approved"
                ? $"已授权伙伴执行 {row.Action}"
                : $"已拒绝伙伴执行 {row.Action}",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { action = row.Action, decided = status }),
            CreatedAt = now
        });
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.MandateJson(row);
    }

    // ── recruitments ──

    /// <summary>
    /// Recruit proposals the team has raised. A proposal is only a request: no companion exists
    /// until the user approves, and the per-tenant limit still has to have room.
    /// </summary>
    public async Task<object> ListRecruitmentsAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await db.Recruitments.AsNoTracking()
                .Where(x => x.TenantId == tenantId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToList();
        return rows.Select(CompanionContracts.RecruitmentJson).ToList();
    }

    /// <summary>
    /// Approving creates the companion the proposal described. The limit is re-read inside the same
    /// decision: a proposal recorded when there was room may have been overtaken by another one, and
    /// creating past the ceiling would be the exact growth the limit exists to stop.
    /// </summary>
    public async Task<object?> DecideRecruitmentAsync(
        Guid recruitmentId, long expectedRevision, string? decision, CancellationToken cancellationToken = default)
    {
        var status = decision?.Trim().ToLowerInvariant() switch
        {
            "approved" => "approved",
            "denied" => "denied",
            _ => throw Invalid("decision must be approved or denied.")
        };
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Recruitments.SingleOrDefaultAsync(
            x => x.Id == recruitmentId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureRevision(row.Revision, expectedRevision);
        if (row.Status != "proposed")
            throw Conflict();

        var now = DateTimeOffset.UtcNow;
        Guid? createdId = null;
        if (status == "approved")
        {
            var limit = await db.Settings.AsNoTracking()
                .Where(x => x.TenantId == tenantId)
                .Select(x => (int?)x.RecruitLimit)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 3;
            var recruited = await db.Companions.AsNoTracking()
                .CountAsync(x => x.TenantId == tenantId && x.RecruitedByCompanionId != null && x.Status != "archived",
                    cancellationToken).ConfigureAwait(false);
            if (limit <= 0 || recruited >= limit)
                throw new CompanionException("recruit_limit_reached", "The recruit limit leaves no room for another companion.", 409);

            var created = new CompanionAgentRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = row.Name,
                PersonaDocument = row.PersonaDocument,
                ModelPurpose = "chat",
                Status = "active",
                Role = "autonomous",
                RecruitedByCompanionId = row.ProposerId,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Companions.Add(created);
            createdId = created.Id;
        }

        row.Status = status;
        row.CreatedCompanionId = createdId;
        row.DecidedAt = now;
        row.UpdatedAt = now;
        row.Revision++;
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = status == "approved" ? "autonomy_recruit_approved" : "autonomy_recruit_denied",
            CompanionId = row.ProposerId,
            Summary = status == "approved"
                ? $"已批准招募新伙伴「{row.Name}」"
                : $"已拒绝招募新伙伴「{row.Name}」",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "recruit", decided = status, recruitment_id = row.Id, created_companion_id = createdId
            }),
            CreatedAt = now
        });
        await CompanionActivities.SaveAsync(db, tenantId, token => SaveAsync(db, token), cancellationToken).ConfigureAwait(false);
        return CompanionContracts.RecruitmentJson(row);
    }

    // ── emotes ──

    /// <summary>
    /// Records that the user waved at or patted the companion. The row is what makes the gesture
    /// real: it is durable, it reaches every surface through the activity feed, and the companion
    /// sees it in its next turn's facts. Nothing is written into a conversation — an emote is not
    /// something the companion said, and a fabricated line would put words in its mouth.
    /// </summary>
    public async Task<object> RecordEmoteAsync(Guid companionId, string? emote, CancellationToken cancellationToken = default)
    {
        var gesture = emote?.Trim().ToLowerInvariant() switch
        {
            "wave" => "wave",
            "pat" => "pat",
            _ => throw Invalid("emote must be wave or pat.")
        };
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        // A click is cheap; a feed of forty identical waves is not. One row per gesture per minute
        // is the honest resolution: the interaction happened, the log just does not repeat it.
        //
        // Seq is a tenant-monotonic long, so the newest row is one ordered read SQLite can actually
        // translate. Ordering by CreatedAt would have to materialise the whole emote history, which
        // grows with every click.
        var recent = await db.Activities.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId && x.Kind == "emote_received")
            .OrderByDescending(x => x.Seq)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (recent is not null && now - recent.CreatedAt < TimeSpan.FromMinutes(1)
            && recent.PayloadJson is { } payload && payload.Contains($"\"{gesture}\"", StringComparison.Ordinal))
        {
            return CompanionContracts.ActivityJson(recent, null, null);
        }

        var row = new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "emote_received",
            CompanionId = companionId,
            Summary = gesture == "wave" ? "你向伙伴打了个招呼" : "你摸了摸伙伴的头",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { emote = gesture }),
            CreatedAt = now
        };
        db.Activities.Add(row);
        await CompanionActivities.SaveAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        streamBroker?.PublishStageCue(
            tenantId,
            companionId,
            conversationId: null,
            runId: null,
            emotion: gesture == "wave" ? "happy" : "affectionate",
            speaking: false,
            intensity: 0.85,
            gesture: gesture);
        return CompanionContracts.ActivityJson(row, null, null);
    }

    private static CompanionException Invalid(string message) => new("invalid_request", message, 400);
    private static CompanionException Conflict() => new("companion_revision_conflict", "Revision conflict.", 412);
    private static CompanionException NotFound(string message = "Companion not found.") => new("companion_not_found", message, 404);
    private static CompanionException Duplicate(string message) => new("companion_duplicate_binding", message, 409);
}
