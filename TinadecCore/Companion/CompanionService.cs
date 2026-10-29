using System.Text;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Companion;

/// <summary>
/// Companion domain read/write surface. Every predicate starts from the verified tenant id;
/// a companion id that is missing or belongs to another tenant is the same null answer, so
/// endpoints cannot be used to probe other tenants. Nothing here touches work-domain tables
/// or a model.
/// </summary>
public sealed class CompanionService(IDbContextFactory<CompanionDbContext> dbFactory, ITenantContextAccessor tenant)
{
    private const int MaxPersonaBytes = 64 * 1024;
    private static readonly string[] MemorySourceKinds = ["user", "companion_run", "group_message"];
    private static readonly string[] SkillSourceKinds = ["manual", "pack"];
    private static readonly string[] ConnectionKinds = ["mcp", "server", "phone", "external_agent"];
    private static readonly string[] AuthorizationStates = ["configured", "authorized", "revoked"];

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

    // ── memories ──

    public async Task<object> ListMemoriesAsync(Guid companionId, string? query, int? limit, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, companionId, cancellationToken).ConfigureAwait(false);
        var take = Math.Clamp(limit ?? 20, 1, 100);
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

    // ── connections ──

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
            throw Invalid("kind must be mcp, server, phone, or external_agent.");
        var authorization = NormalizeChoice(input.Authorization, "configured", AuthorizationStates, "authorization");
        var now = DateTimeOffset.UtcNow;
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
            Endpoint = TrimToNull(input.Endpoint),
            CredentialRef = TrimToNull(input.CredentialRef),
            Authorization = authorization,
            // No probe exists in this piece: never claim online, and there is no check time.
            RuntimeStatus = "unsupported",
            Enabled = true,
            LastCheckedAt = null,
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
        if (authorization is not null) row.Authorization = authorization;
        if (input.Enabled is { } enabled) row.Enabled = enabled;
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
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
        db.ConnectionBindings.Remove(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return true;
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
        bool? autonomyPaused,
        CancellationToken cancellationToken = default)
    {
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
                AutonomyPaused = autonomyPaused ?? false,
                Revision = 1,
                UpdatedAt = now
            };
            db.Settings.Add(row);
        }
        else
        {
            if (mainSpecified) row.MainCompanionId = mainCompanionId;
            if (autonomyPaused is { } paused) row.AutonomyPaused = paused;
            row.Revision++;
            row.UpdatedAt = now;
        }
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ToJson(row);
    }

    // ── helpers ──

    private static readonly CompanionCounts EmptyCounts = new(0, 0, 0, null);

    private static async Task<Dictionary<Guid, CompanionCounts>> LoadCountsAsync(
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

    private static CompanionException Invalid(string message) => new("invalid_request", message, 400);
    private static CompanionException Conflict() => new("companion_revision_conflict", "Revision conflict.", 412);
    private static CompanionException NotFound(string message = "Companion not found.") => new("companion_not_found", message, 404);
    private static CompanionException Duplicate(string message) => new("companion_duplicate_binding", message, 409);
}
