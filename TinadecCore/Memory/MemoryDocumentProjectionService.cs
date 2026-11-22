using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Memory;

/// <summary>
/// Manages the 4-layer memory hierarchy (session / project / principal / companion),
/// immutable DB ledger &lt;-&gt; token-capped Markdown dual-layer projection, 1-click version
/// revert, TOML policy persistence, and conscious conversation history recall.
/// </summary>
public sealed class MemoryDocumentProjectionService : IContextMemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex MemoryAnchorRegex = new(
        @"<!--\s*mem:(?<id>[0-9a-fA-F\-]{36})(?:\s+v(?<ver>\d+))?(?:\s+src:(?<src>[^>]*?))?\s*-->\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OverflowFooterRegex = new(
        @"^<!--\s*overflow:.*?-->\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TomlAssignmentRegex = new(
        @"^(?<indent>\s*)(?<key>[A-Za-z0-9_\-]+)(?<eq>\s*=\s*)(?<value>[^#]*?)(?<comment>\s*#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TomlHeaderRegex = new(
        @"^\s*\[(?<table>[A-Za-z0-9_.\-]+)\]\s*(#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Heading, string Key, string Kind)[] CompanionSections =
    [
        ("Persona & Bond", "persona_bond", "bond"),
        ("Episodic Highlights", "episodic_highlights", "episode"),
        ("Operational Focus", "operational_focus", "task_template"),
        ("Cross-Project Notes", "cross_project_notes", "fact")
    ];

    private static readonly (string Heading, string Key, string Kind)[] ProjectSections =
    [
        ("Architecture & Decisions", "architecture_decisions", "decision"),
        ("Conventions & Pitfalls", "conventions_pitfalls", "failure_pattern"),
        ("Build & Operations", "build_operations", "fact")
    ];

    private static readonly (string Heading, string Key, string Kind)[] PrincipalSections =
    [
        ("Preferences & Style", "preferences_style", "preference"),
        ("Workflow & Toolchain", "workflow_toolchain", "fact")
    ];

    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly StoragePaths _paths;
    private readonly ISessionLocator _sessions;
    private readonly IVectorStore? _vectorStore;
    private readonly IScopeConfigurationDocuments? _scopeDocuments;
    private readonly IScopeStorageLocations? _locations;
    private readonly IConfiguration? _configuration;
    private readonly IRuntimeContextSettings? _runtimeContextSettings;
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private readonly SemaphoreSlim _projectionGate = new(1, 1);
    private ContextMemoryPolicyDto? _inMemoryPolicyOverride;

    public MemoryDocumentProjectionService(
        IDbContextFactory<MemoryDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant,
        StoragePaths paths,
        ISessionLocator sessions,
        IVectorStore? vectorStore = null,
        IScopeConfigurationDocuments? scopeDocuments = null,
        IScopeStorageLocations? locations = null,
        IConfiguration? configuration = null,
        IRuntimeContextSettings? runtimeContextSettings = null)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
        _paths = paths;
        _sessions = sessions;
        _vectorStore = vectorStore;
        _scopeDocuments = scopeDocuments;
        _locations = locations;
        _configuration = configuration;
        _runtimeContextSettings = runtimeContextSettings;
    }

    public async Task<ContextMemoryPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (_inMemoryPolicyOverride is not null && !HasWritableScopeRuntimeDocument())
        {
            return _inMemoryPolicyOverride;
        }

        var (text, sourcePath, contentHash, version) = await ReadRuntimeTomlAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return ParsePolicyFromToml(text, sourcePath, contentHash, version);
        }

        var fallback = _runtimeContextSettings?.Current;
        return new ContextMemoryPolicyDto
        {
            DefaultTokenBudget = fallback?.DefaultTokenBudget ?? 131_072,
            RecentMessageLimit = fallback is not null ? Math.Clamp(fallback.RecentMessageLimit, 4, 200) : 128,
            RetrievalLimit = fallback?.ReviewedMemoryLimit ?? 8,
            SourcePath = sourcePath,
            ContentHash = contentHash,
            Version = version
        };
    }

    public async Task<ContextMemoryPolicyDto> UpdatePolicyAsync(
        UpdateContextMemoryPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _policyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            var candidate = MergeAndValidatePolicy(current, request);

            if (HasWritableScopeRuntimeDocument())
            {
                var doc = await _scopeDocuments!.ReadAsync("runtime", cancellationToken).ConfigureAwait(false);
                var rawExpectedHash = request.ExpectedContentHash ?? request.ExpectedHash;
                var expectedHash = string.IsNullOrWhiteSpace(rawExpectedHash)
                    ? doc.ContentHash
                    : rawExpectedHash.Trim();

                var baseText = string.IsNullOrWhiteSpace(doc.Text)
                    ? ReadBundledBaselineText()
                    : doc.Text;
                var updatedToml = UpsertTomlPolicySections(baseText, candidate);
                var saved = await _scopeDocuments.SaveIfMatchAsync("runtime", updatedToml, expectedHash, cancellationToken).ConfigureAwait(false);
                return ParsePolicyFromToml(saved.Text, saved.Path, saved.ContentHash, saved.Version);
            }

            var inMemExpectedHash = request.ExpectedContentHash ?? request.ExpectedHash;
            if (!string.IsNullOrWhiteSpace(inMemExpectedHash)
                && !string.Equals(inMemExpectedHash.Trim(), current.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Runtime configuration changed since it was read.");
            }

            var updatedInMemory = new ContextMemoryPolicyDto
            {
                Strategy = candidate.Strategy,
                RecallFidelity = candidate.RecallFidelity,
                CompactionTriggerRatio = candidate.CompactionTriggerRatio,
                RecentMessageLimit = candidate.RecentMessageLimit,
                DefaultTokenBudget = candidate.DefaultTokenBudget,
                AllowCrossSessionRecall = candidate.AllowCrossSessionRecall,
                MicroCompactStaleToolOutputs = true,
                OffloadLargeToolPayloadBytes = candidate.OffloadLargeToolPayloadBytes,
                EvolutionMode = candidate.EvolutionMode,
                CandidateOnly = candidate.CandidateOnly,
                RetrievalLimit = candidate.RetrievalLimit,
                CoreMarkdownMaxTokens = candidate.CoreMarkdownMaxTokens,
                ProjectMarkdownProjection = candidate.ProjectMarkdownProjection,
                ProjectMemoryPath = candidate.ProjectMemoryPath,
                CompanionCanWriteProjectMemory = candidate.CompanionCanWriteProjectMemory,
                AllowedScopes = candidate.AllowedScopes,
                AllowedKinds = candidate.AllowedKinds,
                AlwaysOnPrimitives = candidate.AlwaysOnPrimitives,
                SourcePath = current.SourcePath,
                ContentHash = ComputeHash(JsonSerializer.Serialize(candidate, JsonOptions)),
                Version = current.Version + 1
            };
            _inMemoryPolicyOverride = updatedInMemory;
            return updatedInMemory;
        }
        finally
        {
            _policyGate.Release();
        }
    }

    public async Task<MemoryDocumentDto> GetDocumentAsync(
        string scope,
        Guid? projectId = null,
        string? workspaceRoot = null,
        Guid? companionId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedScope = NormalizeDocumentScope(scope);
        ValidateScopeTarget(normalizedScope, companionId);

        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var resolvedProjectId = await ResolveProjectIdAsync(normalizedScope, projectId, workspaceRoot, cancellationToken).ConfigureAwait(false);
        var filePath = await ResolveDocumentPathAsync(normalizedScope, resolvedProjectId, workspaceRoot, companionId, cancellationToken).ConfigureAwait(false);

        var items = await LoadScopeItemsAsync(normalizedScope, resolvedProjectId, companionId, includeInactive: false, includeVersions: true, cancellationToken).ConfigureAwait(false);
        var projected = ProjectMarkdownDocument(normalizedScope, resolvedProjectId, companionId, filePath, items, policy.CoreMarkdownMaxTokens);

        if (ShouldWriteMarkdownFile(normalizedScope, policy) && !File.Exists(filePath) && items.Count == 0)
        {
            // Do not eagerly pollute workspace directories when empty unless written; return clean virtual projection.
            return projected;
        }

        return projected;
    }

    public async Task<MemoryDocumentDto> UpdateDocumentAsync(
        string scope,
        UpdateMemoryDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedScope = NormalizeDocumentScope(scope);
        ValidateScopeTarget(normalizedScope, request.CompanionId);

        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (request.IsCompanionWriter && normalizedScope == "project" && !policy.CompanionCanWriteProjectMemory)
        {
            throw new UnauthorizedAccessException("Companions have read-only access to project memory unless companion_can_write_project_memory is enabled.");
        }

        var resolvedProjectId = await ResolveProjectIdAsync(normalizedScope, request.ProjectId, request.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        var filePath = await ResolveDocumentPathAsync(normalizedScope, resolvedProjectId, request.WorkspaceRoot, request.CompanionId, cancellationToken).ConfigureAwait(false);

        await _projectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingItems = await LoadScopeItemsAsync(normalizedScope, resolvedProjectId, request.CompanionId, includeInactive: false, includeVersions: true, cancellationToken).ConfigureAwait(false);
            var currentProjection = ProjectMarkdownDocument(normalizedScope, resolvedProjectId, request.CompanionId, filePath, existingItems, policy.CoreMarkdownMaxTokens);

            var expectedDocHash = request.ExpectedContentHash ?? request.ExpectedHash;
            if (!string.IsNullOrWhiteSpace(expectedDocHash)
                && !string.Equals(expectedDocHash.Trim('"'), currentProjection.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Memory document changed since it was read. Reload before saving.");
            }

            var hotItemIds = currentProjection.Items.Where(x => x.InHotMarkdown).Select(x => x.Id).ToHashSet();
            var parsedBullets = ParseMarkdownBullets(normalizedScope, request.Markdown ?? string.Empty);

            var tenant = _tenant.Current;
            var now = DateTimeOffset.UtcNow;
            var seenIds = new HashSet<Guid>();
            var byId = existingItems.ToDictionary(x => x.Id);

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

            var vectorIndexQueue = new List<(MemoryItemRecord Item, StoredMemoryPayload Payload)>();
            var vectorDeleteQueue = new List<MemoryItemRecord>();

            foreach (var bullet in parsedBullets)
            {
                if (bullet.ItemId is { } existingId && byId.TryGetValue(existingId, out var currentDto))
                {
                    seenIds.Add(existingId);
                    var sectionChanged = !string.Equals(currentDto.Section ?? string.Empty, bullet.Section, StringComparison.OrdinalIgnoreCase);
                    var contentChanged = !string.Equals(currentDto.Content.Trim(), bullet.Content.Trim(), StringComparison.Ordinal);
                    if (!contentChanged && !sectionChanged)
                    {
                        continue;
                    }

                    var dbItem = await db.MemoryItems.SingleAsync(
                        x => x.Id == existingId && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId,
                        cancellationToken).ConfigureAwait(false);
                    var nextVersion = dbItem.CurrentVersion + 1;
                    var payload = new StoredMemoryPayload
                    {
                        SourceRunId = Guid.Empty,
                        GeneratedByInstanceId = Guid.Empty,
                        Scope = normalizedScope,
                        Kind = InferKindFromSection(normalizedScope, bullet.Section, currentDto.Kind),
                        Content = bullet.Content.Trim(),
                        Confidence = 1.0,
                        ProjectId = resolvedProjectId,
                        AgentId = normalizedScope == "companion" ? request.CompanionId : null,
                        CompanionId = normalizedScope == "companion" ? request.CompanionId : null,
                        Section = bullet.Section,
                        Action = "supersede",
                        PreviousContent = currentDto.Content,
                        SourceSessionId = request.SourceSessionId,
                        SourceMessageSeq = request.SourceMessageSeq,
                        Reason = request.Reason ?? "Markdown document edit"
                    };

                    var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, payload, cancellationToken).ConfigureAwait(false);
                    var versionRow = new MemoryVersionRecord
                    {
                        Id = Guid.NewGuid(),
                        MemoryItemId = dbItem.Id,
                        Version = nextVersion,
                        ContentReference = stored.Value,
                        ContentHash = stored.Sha256,
                        ContentLength = stored.Length,
                        CreatedByPrincipalId = tenant.PrincipalId,
                        CreatedAt = now
                    };
                    dbItem.Kind = payload.Kind;
                    dbItem.CurrentVersion = nextVersion;
                    dbItem.CurrentVersionId = versionRow.Id;
                    dbItem.UpdatedAt = now;
                    db.MemoryVersions.Add(versionRow);
                    vectorIndexQueue.Add((dbItem, payload));
                }
                else
                {
                    var itemId = Guid.NewGuid();
                    seenIds.Add(itemId);
                    var payload = new StoredMemoryPayload
                    {
                        SourceRunId = Guid.Empty,
                        GeneratedByInstanceId = Guid.Empty,
                        Scope = normalizedScope,
                        Kind = InferKindFromSection(normalizedScope, bullet.Section, "fact"),
                        Content = bullet.Content.Trim(),
                        Confidence = 1.0,
                        ProjectId = resolvedProjectId,
                        AgentId = normalizedScope == "companion" ? request.CompanionId : null,
                        CompanionId = normalizedScope == "companion" ? request.CompanionId : null,
                        Section = bullet.Section,
                        Action = "add",
                        SourceSessionId = request.SourceSessionId,
                        SourceMessageSeq = request.SourceMessageSeq,
                        Reason = request.Reason ?? "Added via Markdown document"
                    };
                    var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, payload, cancellationToken).ConfigureAwait(false);
                    var versionRow = new MemoryVersionRecord
                    {
                        Id = Guid.NewGuid(),
                        MemoryItemId = itemId,
                        Version = 1,
                        ContentReference = stored.Value,
                        ContentHash = stored.Sha256,
                        ContentLength = stored.Length,
                        CreatedByPrincipalId = tenant.PrincipalId,
                        CreatedAt = now
                    };
                    var itemRow = new MemoryItemRecord
                    {
                        Id = itemId,
                        TenantId = tenant.TenantId,
                        WorkspaceId = tenant.WorkspaceId,
                        ProjectId = resolvedProjectId,
                        PrincipalId = normalizedScope == "principal" ? tenant.PrincipalId : null,
                        AgentId = normalizedScope == "companion" ? request.CompanionId : null,
                        Scope = normalizedScope,
                        Kind = payload.Kind,
                        Status = "active",
                        CurrentVersion = 1,
                        CurrentVersionId = versionRow.Id,
                        CreatedByPrincipalId = tenant.PrincipalId,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    db.MemoryItems.Add(itemRow);
                    db.MemoryVersions.Add(versionRow);
                    vectorIndexQueue.Add((itemRow, payload));
                }
            }

            foreach (var hotId in hotItemIds)
            {
                if (seenIds.Contains(hotId)) continue;
                var dbItem = await db.MemoryItems.SingleOrDefaultAsync(
                    x => x.Id == hotId && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.Status == "active",
                    cancellationToken).ConfigureAwait(false);
                if (dbItem is null) continue;

                var oldDto = byId[hotId];
                var nextVersion = dbItem.CurrentVersion + 1;
                var revokePayload = new StoredMemoryPayload
                {
                    SourceRunId = Guid.Empty,
                    GeneratedByInstanceId = Guid.Empty,
                    Scope = normalizedScope,
                    Kind = dbItem.Kind,
                    Content = oldDto.Content,
                    Confidence = 1.0,
                    ProjectId = resolvedProjectId,
                    AgentId = dbItem.AgentId,
                    CompanionId = request.CompanionId,
                    Section = oldDto.Section,
                    Action = "revoke",
                    PreviousContent = oldDto.Content,
                    SourceSessionId = request.SourceSessionId,
                    SourceMessageSeq = request.SourceMessageSeq,
                    Reason = request.Reason ?? "Removed in Markdown document edit"
                };
                var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, revokePayload, cancellationToken).ConfigureAwait(false);
                var versionRow = new MemoryVersionRecord
                {
                    Id = Guid.NewGuid(),
                    MemoryItemId = dbItem.Id,
                    Version = nextVersion,
                    ContentReference = stored.Value,
                    ContentHash = stored.Sha256,
                    ContentLength = stored.Length,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    CreatedAt = now
                };
                dbItem.Status = "revoked";
                dbItem.RevokedAt = now;
                dbItem.RevocationReason = revokePayload.Reason;
                dbItem.CurrentVersion = nextVersion;
                dbItem.CurrentVersionId = versionRow.Id;
                dbItem.UpdatedAt = now;
                db.MemoryVersions.Add(versionRow);
                vectorDeleteQueue.Add(dbItem);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            foreach (var (item, payload) in vectorIndexQueue)
            {
                await TryIndexVectorAsync(tenant, item, payload.Content, cancellationToken).ConfigureAwait(false);
            }
            foreach (var item in vectorDeleteQueue)
            {
                await TryDeleteVectorAsync(tenant, item, cancellationToken).ConfigureAwait(false);
            }

            var updatedItems = await LoadScopeItemsAsync(normalizedScope, resolvedProjectId, request.CompanionId, includeInactive: false, includeVersions: true, cancellationToken).ConfigureAwait(false);
            var projected = ProjectMarkdownDocument(normalizedScope, resolvedProjectId, request.CompanionId, filePath, updatedItems, policy.CoreMarkdownMaxTokens);
            if (ShouldWriteMarkdownFile(normalizedScope, policy))
            {
                await WriteMarkdownFileAtomicallyAsync(filePath, projected.Markdown, cancellationToken).ConfigureAwait(false);
            }
            return projected;
        }
        finally
        {
            _projectionGate.Release();
        }
    }

    public async Task<MemoryItemListResultDto> ListItemsAsync(
        string? scope = null,
        Guid? projectId = null,
        Guid? companionId = null,
        string? status = null,
        string? kind = null,
        string? query = null,
        bool includeVersions = true,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? null : NormalizeItemScope(scope);
        var normalizedStatus = string.IsNullOrWhiteSpace(status) || string.Equals(status.Trim(), "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : NormalizeItemStatus(status);
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
        var clampedLimit = Math.Clamp(limit, 1, 200);

        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

        var rowsQuery = db.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId);
        if (normalizedScope is not null) rowsQuery = rowsQuery.Where(x => x.Scope == normalizedScope);
        if (normalizedStatus is not null) rowsQuery = rowsQuery.Where(x => x.Status == normalizedStatus);
        if (normalizedKind is not null) rowsQuery = rowsQuery.Where(x => x.Kind == normalizedKind);
        if (projectId is { } pid && pid != Guid.Empty) rowsQuery = rowsQuery.Where(x => x.ProjectId == pid);
        if (companionId is { } cid && cid != Guid.Empty) rowsQuery = rowsQuery.Where(x => x.AgentId == cid);

        var rows = await rowsQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

        var allDtos = new List<MemoryItemLedgerDto>(rows.Count);
        foreach (var row in rows)
        {
            allDtos.Add(await MaterializeItemAsync(db, row, includeVersions, cancellationToken).ConfigureAwait(false));
        }

        // Compute hot vs cold flags per (scope, project_id, agent_id) for active items.
        var hotIds = ComputeHotIdsAcrossScopes(allDtos, policy.CoreMarkdownMaxTokens);
        allDtos = allDtos.Select(dto => new MemoryItemLedgerDto
        {
            Id = dto.Id,
            Scope = dto.Scope,
            Kind = dto.Kind,
            Section = dto.Section,
            Status = dto.Status,
            CurrentVersion = dto.CurrentVersion,
            Content = dto.Content,
            PreviousContent = dto.PreviousContent,
            Action = dto.Action,
            ProjectId = dto.ProjectId,
            CompanionId = dto.CompanionId,
            SourceSessionId = dto.SourceSessionId,
            SourceMessageSeq = dto.SourceMessageSeq,
            SourceRunId = dto.SourceRunId,
            SupersededById = dto.SupersededById,
            Applicability = dto.Applicability,
            ExpiryCondition = dto.ExpiryCondition,
            RevocationReason = dto.RevocationReason,
            EstimatedTokens = dto.EstimatedTokens,
            InHotMarkdown = dto.Status == "active" && hotIds.Contains(dto.Id),
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt,
            RevokedAt = dto.RevokedAt,
            Versions = dto.Versions
        }).ToList();

        IEnumerable<MemoryItemLedgerDto> filtered = allDtos;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = EvidenceTerms.Extract(query);
            var trimmedQuery = query.Trim();
            filtered = allDtos
                .Select(item => (Item: item, Score: ScoreMemoryItem(item, trimmedQuery, terms)))
                .Where(pair => pair.Score > 0)
                .OrderByDescending(pair => pair.Score)
                .ThenByDescending(pair => pair.Item.UpdatedAt)
                .Select(pair => pair.Item);
        }

        var finalItems = filtered.Take(clampedLimit).ToList();
        var activeCount = allDtos.Count(x => x.Status == "active");
        var hotCount = allDtos.Count(x => x.InHotMarkdown);
        var coldCount = Math.Max(0, activeCount - hotCount);

        return new MemoryItemListResultDto
        {
            Items = finalItems,
            TotalCount = allDtos.Count,
            ActiveCount = activeCount,
            HotCount = hotCount,
            ColdCount = coldCount
        };
    }

    public async Task<MemoryItemLedgerDto> UpsertItemAsync(
        UpsertMemoryItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Memory content is required.", nameof(request));
        }

        var normalizedScope = NormalizeItemScope(request.Scope);
        if (normalizedScope == "companion" && (request.CompanionId is null || request.CompanionId == Guid.Empty))
        {
            throw new ArgumentException("companion_id is required for companion scope memory.", nameof(request));
        }

        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (request.IsCompanionWriter && normalizedScope == "project" && !policy.CompanionCanWriteProjectMemory)
        {
            throw new UnauthorizedAccessException("Companions have read-only access to project memory unless companion_can_write_project_memory is enabled.");
        }

        var resolvedProjectId = await ResolveProjectIdAsync(normalizedScope, request.ProjectId, request.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? "fact" : request.Kind.Trim().ToLowerInvariant();
        var section = string.IsNullOrWhiteSpace(request.Section)
            ? DefaultSectionForKind(normalizedScope, kind)
            : request.Section.Trim();

        var tenant = _tenant.Current;
        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

        string? previousContent = null;
        var action = "add";

        if ((request.SupersedeItemId ?? request.SupersedesId) is { } supersededId && supersededId != Guid.Empty)
        {
            var oldItem = await db.MemoryItems.SingleOrDefaultAsync(
                x => x.Id == supersededId && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Superseded memory item was not found.");

            var oldDto = await MaterializeItemAsync(db, oldItem, includeVersions: false, cancellationToken).ConfigureAwait(false);
            previousContent = oldDto.Content;
            action = "supersede";

            var newItemId = Guid.NewGuid();
            var payload = new StoredMemoryPayload
            {
                SourceRunId = request.SourceRunId ?? Guid.Empty,
                GeneratedByInstanceId = Guid.Empty,
                Scope = normalizedScope,
                Kind = kind,
                Content = request.Content.Trim(),
                Confidence = 1.0,
                ProjectId = resolvedProjectId ?? oldItem.ProjectId,
                AgentId = normalizedScope == "companion" ? request.CompanionId : oldItem.AgentId,
                CompanionId = normalizedScope == "companion" ? request.CompanionId : oldItem.AgentId,
                Section = section,
                Applicability = request.Applicability,
                ExpiryCondition = request.ExpiryCondition,
                Action = "supersede",
                PreviousContent = previousContent,
                SourceSessionId = request.SourceSessionId,
                SourceMessageSeq = request.SourceMessageSeq,
                Reason = request.Reason ?? "Superseded existing memory item"
            };
            var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, payload, cancellationToken).ConfigureAwait(false);
            var versionRow = new MemoryVersionRecord
            {
                Id = Guid.NewGuid(),
                MemoryItemId = newItemId,
                Version = 1,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            var newItem = new MemoryItemRecord
            {
                Id = newItemId,
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                ProjectId = payload.ProjectId,
                PrincipalId = normalizedScope == "principal" ? tenant.PrincipalId : null,
                AgentId = payload.AgentId,
                Scope = normalizedScope,
                Kind = kind,
                Status = "active",
                CurrentVersion = 1,
                CurrentVersionId = versionRow.Id,
                Applicability = request.Applicability,
                ExpiryCondition = request.ExpiryCondition,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now,
                UpdatedAt = now
            };

            oldItem.Status = "superseded";
            oldItem.SupersededById = newItem.Id;
            oldItem.UpdatedAt = now;

            db.MemoryItems.Add(newItem);
            db.MemoryVersions.Add(versionRow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await TryDeleteVectorAsync(tenant, oldItem, cancellationToken).ConfigureAwait(false);
            await TryIndexVectorAsync(tenant, newItem, payload.Content, cancellationToken).ConfigureAwait(false);
            await TrySyncScopeMarkdownAsync(normalizedScope, newItem.ProjectId, request.WorkspaceRoot, newItem.AgentId, policy, cancellationToken).ConfigureAwait(false);

            return await MaterializeItemAsync(db, newItem, includeVersions: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var newItemId = Guid.NewGuid();
            var payload = new StoredMemoryPayload
            {
                SourceRunId = request.SourceRunId ?? Guid.Empty,
                GeneratedByInstanceId = Guid.Empty,
                Scope = normalizedScope,
                Kind = kind,
                Content = request.Content.Trim(),
                Confidence = 1.0,
                ProjectId = resolvedProjectId,
                AgentId = normalizedScope == "companion" ? request.CompanionId : null,
                CompanionId = normalizedScope == "companion" ? request.CompanionId : null,
                Section = section,
                Applicability = request.Applicability,
                ExpiryCondition = request.ExpiryCondition,
                Action = action,
                PreviousContent = previousContent,
                SourceSessionId = request.SourceSessionId,
                SourceMessageSeq = request.SourceMessageSeq,
                Reason = request.Reason
            };
            var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, payload, cancellationToken).ConfigureAwait(false);
            var versionRow = new MemoryVersionRecord
            {
                Id = Guid.NewGuid(),
                MemoryItemId = newItemId,
                Version = 1,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            var newItem = new MemoryItemRecord
            {
                Id = newItemId,
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                ProjectId = resolvedProjectId,
                PrincipalId = normalizedScope == "principal" ? tenant.PrincipalId : null,
                AgentId = normalizedScope == "companion" ? request.CompanionId : null,
                Scope = normalizedScope,
                Kind = kind,
                Status = "active",
                CurrentVersion = 1,
                CurrentVersionId = versionRow.Id,
                Applicability = request.Applicability,
                ExpiryCondition = request.ExpiryCondition,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.MemoryItems.Add(newItem);
            db.MemoryVersions.Add(versionRow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await TryIndexVectorAsync(tenant, newItem, payload.Content, cancellationToken).ConfigureAwait(false);
            await TrySyncScopeMarkdownAsync(normalizedScope, resolvedProjectId, request.WorkspaceRoot, newItem.AgentId, policy, cancellationToken).ConfigureAwait(false);

            return await MaterializeItemAsync(db, newItem, includeVersions: true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<MemoryItemLedgerDto> RevokeItemAsync(
        Guid itemId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

        var item = await db.MemoryItems.SingleOrDefaultAsync(
            x => x.Id == itemId && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Memory item was not found.");

        if (item.Status != "revoked")
        {
            var currentDto = await MaterializeItemAsync(db, item, includeVersions: false, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var nextVersion = item.CurrentVersion + 1;
            var payload = new StoredMemoryPayload
            {
                SourceRunId = Guid.Empty,
                GeneratedByInstanceId = Guid.Empty,
                Scope = item.Scope,
                Kind = item.Kind,
                Content = currentDto.Content,
                Confidence = 1.0,
                ProjectId = item.ProjectId,
                AgentId = item.AgentId,
                CompanionId = item.Scope == "companion" ? item.AgentId : null,
                Section = currentDto.Section,
                Action = "revoke",
                PreviousContent = currentDto.Content,
                SourceSessionId = currentDto.SourceSessionId,
                SourceMessageSeq = currentDto.SourceMessageSeq,
                Reason = string.IsNullOrWhiteSpace(reason) ? "Revoked" : reason.Trim()
            };
            var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, payload, cancellationToken).ConfigureAwait(false);
            var versionRow = new MemoryVersionRecord
            {
                Id = Guid.NewGuid(),
                MemoryItemId = item.Id,
                Version = nextVersion,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            item.Status = "revoked";
            item.RevokedAt = now;
            item.RevocationReason = payload.Reason;
            item.CurrentVersion = nextVersion;
            item.CurrentVersionId = versionRow.Id;
            item.UpdatedAt = now;
            db.MemoryVersions.Add(versionRow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await TryDeleteVectorAsync(tenant, item, cancellationToken).ConfigureAwait(false);
            await TrySyncScopeMarkdownAsync(item.Scope, item.ProjectId, null, item.AgentId, policy, cancellationToken).ConfigureAwait(false);
        }

        return await MaterializeItemAsync(db, item, includeVersions: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryItemLedgerDto> RevertItemAsync(
        Guid itemId,
        RevertMemoryItemRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

        var item = await db.MemoryItems.SingleOrDefaultAsync(
            x => x.Id == itemId && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Memory item was not found.");

        var versions = await db.MemoryVersions.AsNoTracking()
            .Where(x => x.MemoryItemId == item.Id)
            .OrderBy(x => x.Version)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            throw new InvalidOperationException("Memory item has no version records to revert.");
        }

        var currentVersionRow = versions.FirstOrDefault(v => v.Id == item.CurrentVersionId) ?? versions[^1];
        var currentPayload = await ReadPayloadAsync(currentVersionRow, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (request?.TargetVersion is { } targetVersionNumber)
        {
            var targetRow = versions.FirstOrDefault(v => v.Version == targetVersionNumber)
                ?? throw new KeyNotFoundException($"Memory version v{targetVersionNumber} was not found.");
            var targetPayload = await ReadPayloadAsync(targetRow, cancellationToken).ConfigureAwait(false);

            await AppendRevertVersionAsync(
                db, tenant, item, currentPayload.Content, targetPayload,
                request.Reason ?? $"Reverted to v{targetVersionNumber}",
                now, cancellationToken).ConfigureAwait(false);
            await TryIndexVectorAsync(tenant, item, targetPayload.Content, cancellationToken).ConfigureAwait(false);
        }
        else if (item.Status is "revoked" or "superseded")
        {
            // Restore the last active content version (or current version content if only 1 version).
            var restorePayload = currentPayload;
            if (currentPayload.Action == "revoke" && versions.Count >= 2)
            {
                restorePayload = await ReadPayloadAsync(versions[^2], cancellationToken).ConfigureAwait(false);
            }

            // If this item had been superseded by another item, do not force-delete the successor,
            // simply re-activate this item and clear SupersededById.
            await AppendRevertVersionAsync(
                db, tenant, item, currentPayload.Content, restorePayload,
                request?.Reason ?? $"Restored from {item.Status}",
                now, cancellationToken, action: "add").ConfigureAwait(false);
            await TryIndexVectorAsync(tenant, item, restorePayload.Content, cancellationToken).ConfigureAwait(false);
        }
        else if (item.CurrentVersion > 1 && versions.Count >= 2)
        {
            var prevRow = versions[^2];
            var prevPayload = await ReadPayloadAsync(prevRow, cancellationToken).ConfigureAwait(false);
            await AppendRevertVersionAsync(
                db, tenant, item, currentPayload.Content, prevPayload,
                request?.Reason ?? $"Reverted from v{item.CurrentVersion} to v{prevRow.Version}",
                now, cancellationToken).ConfigureAwait(false);
            await TryIndexVectorAsync(tenant, item, prevPayload.Content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Initial v1 active item: 1-click revert revokes it cleanly.
            var nextVersion = item.CurrentVersion + 1;
            var revokePayload = new StoredMemoryPayload
            {
                SourceRunId = currentPayload.SourceRunId,
                GeneratedByInstanceId = currentPayload.GeneratedByInstanceId,
                Scope = item.Scope,
                Kind = item.Kind,
                Content = currentPayload.Content,
                Confidence = currentPayload.Confidence,
                ProjectId = item.ProjectId,
                AgentId = item.AgentId,
                CompanionId = currentPayload.CompanionId,
                Section = currentPayload.Section,
                Action = "revoke",
                PreviousContent = currentPayload.Content,
                SourceSessionId = currentPayload.SourceSessionId,
                SourceMessageSeq = currentPayload.SourceMessageSeq,
                Reason = request?.Reason ?? "Reverted initial v1 entry"
            };
            var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, revokePayload, cancellationToken).ConfigureAwait(false);
            var versionRow = new MemoryVersionRecord
            {
                Id = Guid.NewGuid(),
                MemoryItemId = item.Id,
                Version = nextVersion,
                ContentReference = stored.Value,
                ContentHash = stored.Sha256,
                ContentLength = stored.Length,
                CreatedByPrincipalId = tenant.PrincipalId,
                CreatedAt = now
            };
            item.Status = "revoked";
            item.RevokedAt = now;
            item.RevocationReason = revokePayload.Reason;
            item.CurrentVersion = nextVersion;
            item.CurrentVersionId = versionRow.Id;
            item.UpdatedAt = now;
            db.MemoryVersions.Add(versionRow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await TryDeleteVectorAsync(tenant, item, cancellationToken).ConfigureAwait(false);
        }

        await TrySyncScopeMarkdownAsync(item.Scope, item.ProjectId, null, item.AgentId, policy, cancellationToken).ConfigureAwait(false);
        return await MaterializeItemAsync(db, item, includeVersions: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationRecallResultDto> RecallConversationHistoryAsync(
        Guid sessionId,
        ConversationRecallRequest request,
        Guid? callerProjectId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Recall query is required.", nameof(request));
        }

        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var fidelity = NormalizeRecallFidelity(request.Fidelity ?? policy.RecallFidelity);
        var limit = Math.Clamp(request.Limit ?? 8, 1, 30);

        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

        var session = await db.Sessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");

        var crossSessionRequested = request.CrossSession ?? false;
        var crossSessionAllowed = policy.AllowCrossSessionRecall;
        var crossSessionUsed = crossSessionRequested && crossSessionAllowed;
        string? note = null;
        if (crossSessionRequested && !crossSessionAllowed)
        {
            note = "Cross-session recall is disabled by [context].allow_cross_session_recall; searched the current session only.";
        }

        var candidateSessions = new List<SessionRecord> { session };
        if (crossSessionUsed)
        {
            var effectiveProjectId = callerProjectId ?? session.ProjectId;
            var siblingSessions = await db.Sessions.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId
                    && x.WorkspaceId == tenant.WorkspaceId
                    && x.Id != sessionId
                    && x.LifecycleStatus == LifecycleStatuses.Active
                    && (effectiveProjectId == null ? x.ProjectId == null : x.ProjectId == effectiveProjectId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            siblingSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            candidateSessions.AddRange(siblingSessions.Take(11));
        }

        var terms = EvidenceTerms.Extract(request.Query);
        var queryTrimmed = request.Query.Trim();
        var scoredMatches = new List<RecallVerbatimSnippetDto>();
        var summaryNotes = new List<string>();

        foreach (var candidateSession in candidateSessions)
        {
            if (!string.IsNullOrWhiteSpace(candidateSession.Summary)
                && ScoreText(candidateSession.Summary, queryTrimmed, terms) > 0)
            {
                summaryNotes.Add($"[{candidateSession.Title}]: {ClipText(candidateSession.Summary.Trim(), 240)}");
            }

            var patches = await db.ContextPatches.AsNoTracking()
                .Where(p => p.TenantId == tenant.TenantId
                    && p.WorkspaceId == tenant.WorkspaceId
                    && p.SessionId == candidateSession.Id
                    && p.Status == "applied")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            patches.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

            foreach (var patch in patches.Take(8))
            {
                var patchText = await ReadPlainTextSafeAsync(patch.ContentReference, patch.ContentHash, patch.ContentLength, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(patchText) && ScoreText(patchText, queryTrimmed, terms) > 0)
                {
                    summaryNotes.Add($"[{candidateSession.Title} · rev {patch.AppliedRevision}]: {ClipText(patchText.Trim(), 240)}");
                }
            }

            var messageRows = await db.Messages.AsNoTracking()
                .Where(m => m.TenantId == tenant.TenantId
                    && m.WorkspaceId == tenant.WorkspaceId
                    && m.SessionId == candidateSession.Id
                    && m.RevertedAt == null)
                .OrderByDescending(m => m.Sequence)
                .Take(400)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var msg in messageRows)
            {
                var text = await ReadPlainTextSafeAsync(msg.ContentReference, msg.ContentHash, msg.ContentLength, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var score = ScoreText(text, queryTrimmed, terms);
                if (score <= 0) continue;

                var excerptMaxChars = fidelity == "verbatim_first" ? 1600 : 400;
                scoredMatches.Add(new RecallVerbatimSnippetDto
                {
                    SessionId = candidateSession.Id,
                    SessionTitle = candidateSession.Title,
                    MessageSeq = msg.Sequence,
                    Role = msg.Role,
                    CreatedAt = msg.CreatedAt,
                    Excerpt = BuildFocusedExcerpt(text, queryTrimmed, terms, excerptMaxChars),
                    Score = Math.Round(score, 4)
                });
            }
        }

        scoredMatches.Sort((a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : b.CreatedAt.CompareTo(a.CreatedAt);
        });

        var totalMatches = scoredMatches.Count;
        var selectedSnippets = scoredMatches.Take(limit).ToList();

        var summaryBuilder = new StringBuilder();
        if (totalMatches == 0 && summaryNotes.Count == 0)
        {
            summaryBuilder.Append($"No matching messages or summaries found for \"{queryTrimmed}\" across {candidateSessions.Count} session(s).");
        }
        else
        {
            summaryBuilder.Append($"Found {totalMatches} matching message(s) across {candidateSessions.Count} session(s) for \"{queryTrimmed}\" (fidelity: {fidelity}).");
            foreach (var s in summaryNotes.Take(3))
            {
                summaryBuilder.AppendLine().Append("- ").Append(s);
            }
            if (selectedSnippets.Count > 0)
            {
                summaryBuilder.AppendLine().Append("Top evidence highlights:");
                foreach (var hit in selectedSnippets.Take(3))
                {
                    summaryBuilder.AppendLine().Append($"- [{hit.SessionId.ToString("N")[..8]} #{hit.MessageSeq} {hit.Role}]: {ClipText(hit.Excerpt.Replace('\n', ' '), 180)}");
                }
            }
        }

        return new ConversationRecallResultDto
        {
            SessionId = sessionId,
            Query = queryTrimmed,
            Fidelity = fidelity,
            CrossSessionAllowed = crossSessionAllowed,
            CrossSessionUsed = crossSessionUsed,
            Summary = summaryBuilder.ToString(),
            VerbatimSnippets = fidelity == "summary_only" ? [] : selectedSnippets,
            SearchedSessions = candidateSessions.Count,
            TotalMatches = totalMatches,
            Note = note
        };
    }

    public async Task<ContextMemoryInspectDto> InspectAsync(
        Guid? projectId = null,
        string? workspaceRoot = null,
        Guid? companionId = null,
        CancellationToken cancellationToken = default)
    {
        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var docs = new List<MemoryDocumentSummaryDto>();

        foreach (var scope in new[] { "principal", "project" })
        {
            var doc = await GetDocumentAsync(scope, projectId, workspaceRoot, null, cancellationToken).ConfigureAwait(false);
            docs.Add(new MemoryDocumentSummaryDto
            {
                Scope = doc.Scope,
                FilePath = doc.FilePath,
                EstimatedTokens = doc.EstimatedTokens,
                CoreMarkdownMaxTokens = doc.CoreMarkdownMaxTokens,
                ActiveHotCount = doc.ActiveHotCount,
                ColdArchiveCount = doc.ColdArchiveCount,
                ContentHash = doc.ContentHash
            });
        }

        if (companionId is { } cid && cid != Guid.Empty)
        {
            var compDoc = await GetDocumentAsync("companion", null, null, cid, cancellationToken).ConfigureAwait(false);
            docs.Add(new MemoryDocumentSummaryDto
            {
                Scope = compDoc.Scope,
                FilePath = compDoc.FilePath,
                EstimatedTokens = compDoc.EstimatedTokens,
                CoreMarkdownMaxTokens = compDoc.CoreMarkdownMaxTokens,
                ActiveHotCount = compDoc.ActiveHotCount,
                ColdArchiveCount = compDoc.ColdArchiveCount,
                ContentHash = compDoc.ContentHash
            });
        }

        return new ContextMemoryInspectDto
        {
            Policy = policy,
            Documents = docs,
            StrategyPresets =
            [
                new ContextStrategyPresetDto
                {
                    Id = "direct",
                    Label = "轻量直通 (Direct)",
                    Description = "Hot window + always-on micro-compaction only; lowest cost and strictest KV-cache prefix stability.",
                    AsyncCompactionEnabled = false,
                    CrossSessionRecallSupported = false,
                    ActiveMemoryEvolution = false
                },
                new ContextStrategyPresetDto
                {
                    Id = "managed",
                    Label = "标准托管 (Managed)",
                    Description = "Default balanced mode: hot window + async compaction at trigger ratio + on-demand recall_conversation_history + hybrid memory.",
                    AsyncCompactionEnabled = true,
                    CrossSessionRecallSupported = false,
                    ActiveMemoryEvolution = true
                },
                new ContextStrategyPresetDto
                {
                    Id = "deep_curator",
                    Label = "深度策展 (Deep Curator)",
                    Description = "Full curator mode: cross-session recall + exploration digest + multi-scope active memory evolution.",
                    AsyncCompactionEnabled = true,
                    CrossSessionRecallSupported = true,
                    ActiveMemoryEvolution = true
                }
            ]
        };
    }

    public async Task<ContextMemoryApplyResultDto> ApplySelfConfigAsync(
        ContextMemoryApplyRequest request,
        Guid? callerSessionId = null,
        Guid? callerRunId = null,
        Guid? callerProjectId = null,
        string? callerWorkspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        switch (action)
        {
            case "update_policy":
            case "set_strategy":
            {
                if (request.Policy is null)
                {
                    throw new ArgumentException("policy payload is required for update_policy / set_strategy.");
                }
                var updated = await UpdatePolicyAsync(request.Policy, cancellationToken).ConfigureAwait(false);
                return new ContextMemoryApplyResultDto
                {
                    Action = action,
                    Applied = true,
                    Summary = $"Updated runtime context-memory policy (strategy={updated.Strategy}, evolution_mode={updated.EvolutionMode}, core_markdown_max_tokens={updated.CoreMarkdownMaxTokens}).",
                    Policy = updated
                };
            }
            case "upsert_memory":
            case "supersede_memory":
            {
                if (request.Memory is null)
                {
                    throw new ArgumentException("memory payload is required for upsert_memory / supersede_memory.");
                }
                var effectiveRequest = new UpsertMemoryItemRequest
                {
                    Scope = request.Memory.Scope,
                    Kind = request.Memory.Kind,
                    Section = request.Memory.Section,
                    Content = request.Memory.Content,
                    ProjectId = request.Memory.ProjectId ?? callerProjectId,
                    WorkspaceRoot = request.Memory.WorkspaceRoot ?? callerWorkspaceRoot,
                    CompanionId = request.Memory.CompanionId,
                    SourceSessionId = request.Memory.SourceSessionId ?? callerSessionId,
                    SourceMessageSeq = request.Memory.SourceMessageSeq,
                    SourceRunId = request.Memory.SourceRunId ?? callerRunId,
                    SupersedeItemId = request.Memory.SupersedeItemId ?? (action == "supersede_memory" ? request.ItemId : null),
                    Applicability = request.Memory.Applicability,
                    ExpiryCondition = request.Memory.ExpiryCondition,
                    Reason = request.Memory.Reason ?? request.Reason,
                    IsCompanionWriter = request.Memory.IsCompanionWriter,
                    RespectEvolutionMode = request.Memory.RespectEvolutionMode
                };
                var item = await UpsertItemAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
                return new ContextMemoryApplyResultDto
                {
                    Action = action,
                    Applied = true,
                    Summary = $"{item.Action} memory item {item.Id} in scope '{item.Scope}' (v{item.CurrentVersion}).",
                    Item = item
                };
            }
            case "revoke_memory":
            {
                if (request.ItemId is not { } id || id == Guid.Empty)
                {
                    throw new ArgumentException("item_id is required for revoke_memory.");
                }
                var item = await RevokeItemAsync(id, request.Reason, cancellationToken).ConfigureAwait(false);
                return new ContextMemoryApplyResultDto
                {
                    Action = action,
                    Applied = true,
                    Summary = $"Revoked memory item {item.Id} in scope '{item.Scope}'.",
                    Item = item
                };
            }
            case "revert_memory":
            {
                if (request.ItemId is not { } id || id == Guid.Empty)
                {
                    throw new ArgumentException("item_id is required for revert_memory.");
                }
                var item = await RevertItemAsync(id, new RevertMemoryItemRequest
                {
                    TargetVersion = request.TargetVersion,
                    Reason = request.Reason
                }, cancellationToken).ConfigureAwait(false);
                return new ContextMemoryApplyResultDto
                {
                    Action = action,
                    Applied = true,
                    Summary = $"Reverted memory item {item.Id} (status={item.Status}, version=v{item.CurrentVersion}).",
                    Item = item
                };
            }
            default:
                throw new ArgumentException($"Unknown context-memory action '{request.Action}'.");
        }
    }

    private async Task AppendRevertVersionAsync(
        MemoryDbContext db,
        TenantContext tenant,
        MemoryItemRecord item,
        string previousContent,
        StoredMemoryPayload restoredPayload,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string action = "supersede")
    {
        var nextVersion = item.CurrentVersion + 1;
        var newPayload = new StoredMemoryPayload
        {
            SourceRunId = restoredPayload.SourceRunId,
            GeneratedByInstanceId = restoredPayload.GeneratedByInstanceId,
            Scope = item.Scope,
            Kind = restoredPayload.Kind,
            Content = restoredPayload.Content,
            Confidence = restoredPayload.Confidence,
            ProjectId = item.ProjectId,
            AgentId = item.AgentId,
            CompanionId = restoredPayload.CompanionId,
            Section = restoredPayload.Section,
            Applicability = restoredPayload.Applicability,
            ExpiryCondition = restoredPayload.ExpiryCondition,
            Action = action,
            PreviousContent = previousContent,
            SourceSessionId = restoredPayload.SourceSessionId,
            SourceMessageSeq = restoredPayload.SourceMessageSeq,
            Reason = reason
        };
        var stored = await StorePayloadAsync(tenant.TenantId, tenant.WorkspaceId, newPayload, cancellationToken).ConfigureAwait(false);
        var versionRow = new MemoryVersionRecord
        {
            Id = Guid.NewGuid(),
            MemoryItemId = item.Id,
            Version = nextVersion,
            ContentReference = stored.Value,
            ContentHash = stored.Sha256,
            ContentLength = stored.Length,
            CreatedByPrincipalId = tenant.PrincipalId,
            CreatedAt = now
        };

        item.Kind = newPayload.Kind;
        item.Status = "active";
        item.RevokedAt = null;
        item.RevocationReason = null;
        item.SupersededById = null;
        item.CurrentVersion = nextVersion;
        item.CurrentVersionId = versionRow.Id;
        item.UpdatedAt = now;
        db.MemoryVersions.Add(versionRow);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool HasWritableScopeRuntimeDocument() =>
        _scopeDocuments is not null && _scopeDocuments.DocumentIds.Contains("runtime", StringComparer.Ordinal);

    private async Task<(string Text, string? SourcePath, string? ContentHash, long Version)> ReadRuntimeTomlAsync(CancellationToken cancellationToken)
    {
        if (HasWritableScopeRuntimeDocument())
        {
            var doc = await _scopeDocuments!.ReadAsync("runtime", cancellationToken).ConfigureAwait(false);
            return (doc.Text, doc.Path, doc.ContentHash, doc.Version);
        }

        var configuredPath = _configuration?["TinadecAgent:ProfileConfigPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            var text = await File.ReadAllTextAsync(configuredPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return (text, Path.GetFullPath(configuredPath), ComputeHash(text), 1);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "Configuration", "default-agent-runtime.toml");
        if (File.Exists(bundled))
        {
            var text = await File.ReadAllTextAsync(bundled, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return (text, bundled, ComputeHash(text), 1);
        }

        return (string.Empty, null, null, 1);
    }

    private static string ReadBundledBaselineText()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Configuration", "default-agent-runtime.toml");
        return File.Exists(bundled) ? File.ReadAllText(bundled, Encoding.UTF8) : string.Empty;
    }

    private static ContextMemoryPolicyDto ParsePolicyFromToml(string text, string? sourcePath, string? contentHash, long version)
    {
        var root = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        var context = TryGetTable(root, "context");
        var memory = TryGetTable(root, "memory");

        var candidateOnly = GetBool(memory, "candidate_only", false);
        var rawEvolution = GetString(memory, "evolution_mode");
        var evolutionMode = string.IsNullOrWhiteSpace(rawEvolution)
            ? (candidateOnly ? "candidate_only" : "auto_evolve")
            : rawEvolution.Trim().ToLowerInvariant();

        var scopes = GetStrings(memory, "allowed_scopes");
        var kinds = GetStrings(memory, "allowed_kinds");

        return new ContextMemoryPolicyDto
        {
            Strategy = (GetString(context, "strategy") ?? "managed").Trim().ToLowerInvariant(),
            RecallFidelity = (GetString(context, "recall_fidelity") ?? "balanced").Trim().ToLowerInvariant(),
            CompactionTriggerRatio = GetDouble(context, "compaction_trigger_ratio", 0.75),
            RecentMessageLimit = GetInt(context, "recent_message_limit", 128),
            DefaultTokenBudget = GetInt(context, "default_token_budget", 131_072),
            AllowCrossSessionRecall = GetBool(context, "allow_cross_session_recall", true),
            MicroCompactStaleToolOutputs = GetBool(context, "micro_compact_stale_tool_outputs", true),
            OffloadLargeToolPayloadBytes = GetInt(context, "offload_large_tool_payload_bytes", 16_384),
            EvolutionMode = evolutionMode,
            CandidateOnly = candidateOnly || evolutionMode == "candidate_only",
            RetrievalLimit = GetInt(memory, "retrieval_limit", 8),
            CoreMarkdownMaxTokens = GetInt(memory, "core_markdown_max_tokens", 1500),
            ProjectMarkdownProjection = GetBool(memory, "project_markdown_projection", true),
            ProjectMemoryPath = (GetString(memory, "project_memory_path") ?? ".tinadec/memory/project-core.md").Trim(),
            CompanionCanWriteProjectMemory = GetBool(memory, "companion_can_write_project_memory", false),
            AllowedScopes = scopes.Length > 0 ? scopes : ["principal", "workspace", "project", "agent", "companion"],
            AllowedKinds = kinds.Length > 0 ? kinds : ["fact", "preference", "decision", "success_pattern", "failure_pattern", "task_template", "supervision_rule", "bond", "episode"],
            SourcePath = sourcePath,
            ContentHash = contentHash,
            Version = version
        };
    }

    private static ContextMemoryPolicyDto MergeAndValidatePolicy(ContextMemoryPolicyDto current, UpdateContextMemoryPolicyRequest request)
    {
        var strategy = (request.Strategy ?? current.Strategy).Trim().ToLowerInvariant();
        if (strategy is not ("direct" or "managed" or "deep_curator"))
        {
            throw new ArgumentException($"Invalid context.strategy '{strategy}'. Expected: direct, managed, deep_curator.");
        }

        var fidelity = (request.RecallFidelity ?? current.RecallFidelity).Trim().ToLowerInvariant();
        if (fidelity is not ("summary_only" or "balanced" or "verbatim_first"))
        {
            throw new ArgumentException($"Invalid context.recall_fidelity '{fidelity}'. Expected: summary_only, balanced, verbatim_first.");
        }

        var compactionRatio = request.CompactionTriggerRatio ?? current.CompactionTriggerRatio;
        if (double.IsNaN(compactionRatio) || compactionRatio is < 0.40 or > 0.95)
        {
            throw new ArgumentException("compaction_trigger_ratio must be between 0.40 and 0.95.");
        }

        var recentLimit = request.RecentMessageLimit ?? current.RecentMessageLimit;
        if (recentLimit is < 4 or > 200)
        {
            throw new ArgumentException("recent_message_limit must be between 4 and 200.");
        }

        var tokenBudget = request.DefaultTokenBudget ?? current.DefaultTokenBudget;
        if (tokenBudget < 512)
        {
            throw new ArgumentException("default_token_budget must be at least 512.");
        }

        var offloadBytes = request.OffloadLargeToolPayloadBytes ?? current.OffloadLargeToolPayloadBytes;
        if (offloadBytes < 1024)
        {
            throw new ArgumentException("offload_large_tool_payload_bytes must be at least 1024.");
        }

        var evolutionMode = request.EvolutionMode is not null
            ? request.EvolutionMode.Trim().ToLowerInvariant()
            : request.CandidateOnly is { } explicitCandidate
                ? (explicitCandidate ? "candidate_only" : "auto_evolve")
                : current.EvolutionMode;
        if (evolutionMode is not ("auto_evolve" or "candidate_only"))
        {
            throw new ArgumentException($"Invalid memory.evolution_mode '{evolutionMode}'. Expected: auto_evolve, candidate_only.");
        }

        var candidateOnly = request.CandidateOnly ?? (evolutionMode == "candidate_only");
        var retrievalLimit = request.RetrievalLimit ?? current.RetrievalLimit;
        if (retrievalLimit < 0)
        {
            throw new ArgumentException("memory.retrieval_limit must not be negative.");
        }

        var maxTokens = request.CoreMarkdownMaxTokens ?? current.CoreMarkdownMaxTokens;
        if (maxTokens is < 256 or > 8000)
        {
            throw new ArgumentException("core_markdown_max_tokens must be between 256 and 8000.");
        }

        var projectMemoryPath = (request.ProjectMemoryPath ?? current.ProjectMemoryPath).Trim();
        if (string.IsNullOrWhiteSpace(projectMemoryPath)
            || Path.IsPathRooted(projectMemoryPath)
            || projectMemoryPath.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("project_memory_path must be a non-empty relative path without '..' traversal.");
        }

        return new ContextMemoryPolicyDto
        {
            Strategy = strategy,
            RecallFidelity = fidelity,
            CompactionTriggerRatio = Math.Round(compactionRatio, 4),
            RecentMessageLimit = recentLimit,
            DefaultTokenBudget = tokenBudget,
            AllowCrossSessionRecall = request.AllowCrossSessionRecall ?? current.AllowCrossSessionRecall,
            MicroCompactStaleToolOutputs = true,
            OffloadLargeToolPayloadBytes = offloadBytes,
            EvolutionMode = evolutionMode,
            CandidateOnly = candidateOnly,
            RetrievalLimit = retrievalLimit,
            CoreMarkdownMaxTokens = maxTokens,
            ProjectMarkdownProjection = request.ProjectMarkdownProjection ?? current.ProjectMarkdownProjection,
            ProjectMemoryPath = projectMemoryPath,
            CompanionCanWriteProjectMemory = request.CompanionCanWriteProjectMemory ?? current.CompanionCanWriteProjectMemory,
            AllowedScopes = current.AllowedScopes,
            AllowedKinds = current.AllowedKinds,
            AlwaysOnPrimitives = current.AlwaysOnPrimitives,
            SourcePath = current.SourcePath,
            ContentHash = current.ContentHash,
            Version = current.Version
        };
    }

    internal static string UpsertTomlPolicySections(string tomlText, ContextMemoryPolicyDto policy)
    {
        var updates = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["context"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["strategy"] = FormatTomlString(policy.Strategy),
                ["default_token_budget"] = policy.DefaultTokenBudget.ToString(CultureInfo.InvariantCulture),
                ["recent_message_limit"] = policy.RecentMessageLimit.ToString(CultureInfo.InvariantCulture),
                ["compaction_trigger_ratio"] = policy.CompactionTriggerRatio.ToString("0.##", CultureInfo.InvariantCulture),
                ["recall_fidelity"] = FormatTomlString(policy.RecallFidelity),
                ["allow_cross_session_recall"] = policy.AllowCrossSessionRecall ? "true" : "false",
                ["micro_compact_stale_tool_outputs"] = "true",
                ["offload_large_tool_payload_bytes"] = policy.OffloadLargeToolPayloadBytes.ToString(CultureInfo.InvariantCulture)
            },
            ["memory"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["evolution_mode"] = FormatTomlString(policy.EvolutionMode),
                ["candidate_only"] = policy.CandidateOnly ? "true" : "false",
                ["retrieval_limit"] = policy.RetrievalLimit.ToString(CultureInfo.InvariantCulture),
                ["core_markdown_max_tokens"] = policy.CoreMarkdownMaxTokens.ToString(CultureInfo.InvariantCulture),
                ["project_markdown_projection"] = policy.ProjectMarkdownProjection ? "true" : "false",
                ["project_memory_path"] = FormatTomlString(policy.ProjectMemoryPath),
                ["companion_can_write_project_memory"] = policy.CompanionCanWriteProjectMemory ? "true" : "false"
            }
        };

        var newline = tomlText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = tomlText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var result = new List<string>(lines.Count + 16);
        var seenTables = new HashSet<string>(StringComparer.Ordinal);
        var writtenKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? currentTable = null;

        void FlushPendingKeys(string? table)
        {
            if (table is null || !updates.TryGetValue(table, out var tableUpdates)) return;
            if (!writtenKeys.TryGetValue(table, out var written))
            {
                written = new HashSet<string>(StringComparer.Ordinal);
                writtenKeys[table] = written;
            }
            foreach (var (key, value) in tableUpdates)
            {
                if (written.Add(key))
                {
                    result.Add($"{key} = {value}");
                }
            }
        }

        foreach (var line in lines)
        {
            var headerMatch = TomlHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                FlushPendingKeys(currentTable);
                currentTable = headerMatch.Groups["table"].Value;
                seenTables.Add(currentTable);
                result.Add(line);
                continue;
            }

            if (currentTable is not null && updates.TryGetValue(currentTable, out var tableUpdates))
            {
                var assignMatch = TomlAssignmentRegex.Match(line);
                if (assignMatch.Success && tableUpdates.TryGetValue(assignMatch.Groups["key"].Value, out var replacementValue))
                {
                    var key = assignMatch.Groups["key"].Value;
                    writtenKeys.TryAdd(currentTable, new HashSet<string>(StringComparer.Ordinal));
                    writtenKeys[currentTable].Add(key);
                    result.Add($"{assignMatch.Groups["indent"].Value}{key}{assignMatch.Groups["eq"].Value}{replacementValue}{assignMatch.Groups["comment"].Value}");
                    continue;
                }
            }

            result.Add(line);
        }

        FlushPendingKeys(currentTable);

        foreach (var (table, tableUpdates) in updates)
        {
            if (seenTables.Contains(table)) continue;
            if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1])) result.Add(string.Empty);
            result.Add($"[{table}]");
            foreach (var (key, value) in tableUpdates)
            {
                result.Add($"{key} = {value}");
            }
        }

        return string.Join(newline, result);
    }

    private static string FormatTomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private async Task<IReadOnlyList<MemoryItemLedgerDto>> LoadScopeItemsAsync(
        string scope,
        Guid? projectId,
        Guid? companionId,
        bool includeInactive,
        bool includeVersions,
        CancellationToken cancellationToken)
    {
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

        var query = db.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId && x.Scope == scope);
        if (!includeInactive) query = query.Where(x => x.Status == "active");
        if (scope == "principal") query = query.Where(x => x.PrincipalId == tenant.PrincipalId || x.PrincipalId == null);
        if (scope == "project") query = query.Where(x => x.ProjectId == projectId);
        if (scope == "companion") query = query.Where(x => x.AgentId == companionId);

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

        var result = new List<MemoryItemLedgerDto>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await MaterializeItemAsync(db, row, includeVersions, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    private MemoryDocumentDto ProjectMarkdownDocument(
        string scope,
        Guid? projectId,
        Guid? companionId,
        string filePath,
        IReadOnlyList<MemoryItemLedgerDto> activeItems,
        int maxTokens)
    {
        var sections = SectionsForScope(scope);
        var title = scope switch
        {
            "principal" => "# User Global Profile (principal)",
            "companion" => "# Companion Private Memory (companion)",
            _ => "# Project Core Memory (project)"
        };

        var bySection = new Dictionary<string, List<MemoryItemLedgerDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (heading, _, _) in sections)
        {
            bySection[heading] = [];
        }

        foreach (var item in activeItems)
        {
            var heading = ResolveSectionHeading(scope, item.Section, item.Kind);
            if (!bySection.TryGetValue(heading, out var bucket))
            {
                bucket = [];
                bySection[heading] = bucket;
            }
            bucket.Add(item);
        }

        var sb = new StringBuilder();
        sb.AppendLine(title);
        var currentTokens = EstimateTokens(sb.ToString());
        var hotIds = new HashSet<Guid>();
        var sectionStats = new List<MemoryDocumentSectionDto>();

        foreach (var (heading, key, _) in sections)
        {
            var sectionHeader = $"\n## {heading}\n";
            var headerTokens = EstimateTokens(sectionHeader);
            sb.Append(sectionHeader);
            currentTokens += headerTokens;

            var bucket = bySection.GetValueOrDefault(heading) ?? [];
            var renderedCount = 0;
            var sectionTokens = headerTokens;

            foreach (var item in bucket)
            {
                var line = FormatMemoryBullet(item);
                var lineTokens = EstimateTokens(line + "\n");
                if (currentTokens + lineTokens > maxTokens)
                {
                    continue;
                }
                sb.AppendLine(line);
                currentTokens += lineTokens;
                sectionTokens += lineTokens;
                hotIds.Add(item.Id);
                renderedCount++;
            }

            sectionStats.Add(new MemoryDocumentSectionDto
            {
                Heading = heading,
                Key = key,
                ItemCount = renderedCount,
                EstimatedTokens = sectionTokens
            });
        }

        // Any extra custom sections created by manual Markdown headings
        foreach (var (heading, bucket) in bySection)
        {
            if (sections.Any(s => string.Equals(s.Heading, heading, StringComparison.OrdinalIgnoreCase)) || bucket.Count == 0)
            {
                continue;
            }

            var sectionHeader = $"\n## {heading}\n";
            var headerTokens = EstimateTokens(sectionHeader);
            if (currentTokens + headerTokens > maxTokens) continue;
            sb.Append(sectionHeader);
            currentTokens += headerTokens;

            var renderedCount = 0;
            var sectionTokens = headerTokens;
            foreach (var item in bucket)
            {
                var line = FormatMemoryBullet(item);
                var lineTokens = EstimateTokens(line + "\n");
                if (currentTokens + lineTokens > maxTokens) continue;
                sb.AppendLine(line);
                currentTokens += lineTokens;
                sectionTokens += lineTokens;
                hotIds.Add(item.Id);
                renderedCount++;
            }

            sectionStats.Add(new MemoryDocumentSectionDto
            {
                Heading = heading,
                Key = heading.ToLowerInvariant().Replace(' ', '_'),
                ItemCount = renderedCount,
                EstimatedTokens = sectionTokens
            });
        }

        var coldCount = Math.Max(0, activeItems.Count - hotIds.Count);
        if (coldCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"<!-- overflow: {coldCount} cold item(s) remain searchable in SQLite + VectorStore -->");
        }

        var markdown = sb.ToString().TrimEnd() + "\n";
        var estimatedTokens = EstimateTokens(markdown);
        var projectedItems = activeItems.Select(item => new MemoryItemLedgerDto
        {
            Id = item.Id,
            Scope = item.Scope,
            Kind = item.Kind,
            Section = item.Section,
            Status = item.Status,
            CurrentVersion = item.CurrentVersion,
            Content = item.Content,
            PreviousContent = item.PreviousContent,
            Action = item.Action,
            ProjectId = item.ProjectId,
            CompanionId = item.CompanionId,
            SourceSessionId = item.SourceSessionId,
            SourceMessageSeq = item.SourceMessageSeq,
            SourceRunId = item.SourceRunId,
            SupersededById = item.SupersededById,
            Applicability = item.Applicability,
            ExpiryCondition = item.ExpiryCondition,
            RevocationReason = item.RevocationReason,
            EstimatedTokens = item.EstimatedTokens,
            InHotMarkdown = hotIds.Contains(item.Id),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            RevokedAt = item.RevokedAt,
            Versions = item.Versions
        }).ToList();

        return new MemoryDocumentDto
        {
            Scope = scope,
            ProjectId = projectId,
            CompanionId = companionId,
            FilePath = filePath,
            Markdown = markdown,
            EstimatedTokens = estimatedTokens,
            CoreMarkdownMaxTokens = maxTokens,
            ActiveHotCount = hotIds.Count,
            ColdArchiveCount = coldCount,
            ContentHash = ComputeHash(markdown),
            UpdatedAt = activeItems.Count > 0 ? activeItems.Max(x => x.UpdatedAt) : DateTimeOffset.UtcNow,
            Sections = sectionStats,
            Items = projectedItems
        };
    }

    private static string FormatMemoryBullet(MemoryItemLedgerDto item)
    {
        var singleLine = item.Content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var src = item.SourceSessionId is { } sid && sid != Guid.Empty
            ? (item.SourceMessageSeq is { } seq ? $"sess-{sid.ToString("N")[..8]}#{seq}" : $"sess-{sid.ToString("N")[..8]}")
            : "manual";
        return $"- {singleLine} <!-- mem:{item.Id:D} v{item.CurrentVersion} src:{src} -->";
    }

    private static IReadOnlyList<(Guid? ItemId, string Section, string Content)> ParseMarkdownBullets(string scope, string markdown)
    {
        var defaultSection = SectionsForScope(scope)[0].Heading;
        var currentSection = defaultSection;
        var bullets = new List<(Guid?, string, string)>();

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;
            if (OverflowFooterRegex.IsMatch(trimmed)) continue;
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = trimmed[3..].Trim();
                if (heading.Length > 0) currentSection = heading;
                continue;
            }
            if (trimmed.StartsWith("# ", StringComparison.Ordinal)) continue;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                var body = trimmed[2..].Trim();
                Guid? itemId = null;
                var anchorMatch = MemoryAnchorRegex.Match(body);
                if (anchorMatch.Success)
                {
                    if (Guid.TryParse(anchorMatch.Groups["id"].Value, out var parsed))
                    {
                        itemId = parsed;
                    }
                    body = body[..anchorMatch.Index].Trim();
                }

                if (!string.IsNullOrWhiteSpace(body))
                {
                    bullets.Add((itemId, currentSection, body));
                }
            }
        }

        return bullets;
    }

    private async Task<Guid?> ResolveProjectIdAsync(
        string scope,
        Guid? projectId,
        string? workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (scope != "project") return null;
        if (projectId is { } pid && pid != Guid.Empty) return pid;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            var tenant = _tenant.Current;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
            var projects = await db.Projects.AsNoTracking()
                .Where(p => p.TenantId == tenant.TenantId && p.WorkspaceId == tenant.WorkspaceId && p.LifecycleStatus == LifecycleStatuses.Active)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var matched = projects.FirstOrDefault(p => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.RootPath)),
                normalized,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (matched is not null) return matched.Id;
        }
        return null;
    }

    private async Task<string> ResolveDocumentPathAsync(
        string scope,
        Guid? projectId,
        string? workspaceRoot,
        Guid? companionId,
        CancellationToken cancellationToken)
    {
        if (scope == "principal")
        {
            var target = Path.GetFullPath(Path.Combine(_paths.Root, "memory", "user-profile.md"));
            StorageScopePaths.RejectLinks(_paths.Root, target);
            return target;
        }

        if (scope == "companion")
        {
            var cid = companionId!.Value;
            var target = Path.GetFullPath(Path.Combine(_paths.Root, "companions", cid.ToString("D"), "memory.md"));
            StorageScopePaths.RejectLinks(_paths.Root, target);
            return target;
        }

        // scope == "project"
        var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        string? root = null;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            root = Path.GetFullPath(workspaceRoot);
        }
        else if (projectId is { } pid && pid != Guid.Empty)
        {
            var project = await _sessions.FindProjectAsync(pid, cancellationToken).ConfigureAwait(false);
            if (project is not null) root = Path.GetFullPath(project.RootPath);
        }

        root ??= !string.IsNullOrWhiteSpace(_locations?.ProjectRoot)
            ? Path.GetFullPath(_locations.ProjectRoot)
            : (!string.IsNullOrWhiteSpace(_configuration?["TinadecTools:DefaultWorkspaceRoot"])
                ? Path.GetFullPath(_configuration["TinadecTools:DefaultWorkspaceRoot"]!)
                : null);

        if (!string.IsNullOrWhiteSpace(root))
        {
            var relative = policy.ProjectMemoryPath.Replace('/', Path.DirectorySeparatorChar);
            var combined = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!combined.StartsWith(prefix, cmp))
            {
                throw new InvalidOperationException("Project memory path escaped the workspace root.");
            }
            return combined;
        }

        var fallback = Path.GetFullPath(Path.Combine(
            _paths.Root,
            "memory",
            "projects",
            projectId is { } id && id != Guid.Empty ? id.ToString("D") : "default",
            "project-core.md"));
        StorageScopePaths.RejectLinks(_paths.Root, fallback);
        return fallback;
    }

    private async Task TrySyncScopeMarkdownAsync(
        string scope,
        Guid? projectId,
        string? workspaceRoot,
        Guid? companionId,
        ContextMemoryPolicyDto policy,
        CancellationToken cancellationToken)
    {
        if (scope is not ("principal" or "project" or "companion")) return;
        if (!ShouldWriteMarkdownFile(scope, policy)) return;
        if (scope == "companion" && (companionId is null || companionId == Guid.Empty)) return;

        try
        {
            var filePath = await ResolveDocumentPathAsync(scope, projectId, workspaceRoot, companionId, cancellationToken).ConfigureAwait(false);
            var items = await LoadScopeItemsAsync(scope, projectId, companionId, includeInactive: false, includeVersions: false, cancellationToken).ConfigureAwait(false);
            var projected = ProjectMarkdownDocument(scope, projectId, companionId, filePath, items, policy.CoreMarkdownMaxTokens);
            await WriteMarkdownFileAtomicallyAsync(filePath, projected.Markdown, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Projection file sync is best-effort if the workspace directory is not mounted locally.
        }
    }

    private static bool ShouldWriteMarkdownFile(string scope, ContextMemoryPolicyDto policy) =>
        scope != "project" || policy.ProjectMarkdownProjection;

    private static async Task WriteMarkdownFileAtomicallyAsync(string filePath, string markdown, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, markdown, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temp, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<MemoryItemLedgerDto> MaterializeItemAsync(
        MemoryDbContext db,
        MemoryItemRecord row,
        bool includeVersions,
        CancellationToken cancellationToken)
    {
        var versionRows = includeVersions
            ? await db.MemoryVersions.AsNoTracking()
                .Where(x => x.MemoryItemId == row.Id)
                .OrderBy(x => x.Version)
                .ToListAsync(cancellationToken).ConfigureAwait(false)
            : await db.MemoryVersions.AsNoTracking()
                .Where(x => x.Id == row.CurrentVersionId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        var versionDtos = new List<MemoryVersionLedgerDto>(versionRows.Count);
        StoredMemoryPayload? currentPayload = null;

        foreach (var vr in versionRows)
        {
            var payload = await ReadPayloadAsync(vr, cancellationToken).ConfigureAwait(false);
            if (vr.Id == row.CurrentVersionId)
            {
                currentPayload = payload;
            }
            versionDtos.Add(new MemoryVersionLedgerDto
            {
                Id = vr.Id,
                Version = vr.Version,
                Action = string.IsNullOrWhiteSpace(payload.Action) ? "add" : payload.Action,
                Content = payload.Content,
                PreviousContent = payload.PreviousContent,
                SourceSessionId = payload.SourceSessionId,
                SourceMessageSeq = payload.SourceMessageSeq,
                SourceRunId = payload.SourceRunId == Guid.Empty ? null : payload.SourceRunId,
                Reason = payload.Reason,
                CreatedAt = vr.CreatedAt
            });
        }

        currentPayload ??= new StoredMemoryPayload { Content = string.Empty, Scope = row.Scope, Kind = row.Kind };
        var content = currentPayload.Content;

        return new MemoryItemLedgerDto
        {
            Id = row.Id,
            Scope = row.Scope,
            Kind = row.Kind,
            Section = currentPayload.Section ?? DefaultSectionForKind(row.Scope, row.Kind),
            Status = row.Status,
            CurrentVersion = row.CurrentVersion,
            Content = content,
            PreviousContent = currentPayload.PreviousContent,
            Action = string.IsNullOrWhiteSpace(currentPayload.Action) ? "add" : currentPayload.Action,
            ProjectId = row.ProjectId,
            CompanionId = row.Scope == "companion" ? row.AgentId : currentPayload.CompanionId,
            SourceSessionId = currentPayload.SourceSessionId,
            SourceMessageSeq = currentPayload.SourceMessageSeq,
            SourceRunId = currentPayload.SourceRunId == Guid.Empty ? null : currentPayload.SourceRunId,
            SupersededById = row.SupersededById,
            Applicability = row.Applicability ?? currentPayload.Applicability,
            ExpiryCondition = row.ExpiryCondition ?? currentPayload.ExpiryCondition,
            RevocationReason = row.RevocationReason,
            EstimatedTokens = EstimateTokens(content),
            InHotMarkdown = false,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            RevokedAt = row.RevokedAt,
            Versions = includeVersions ? versionDtos : []
        };
    }

    private HashSet<Guid> ComputeHotIdsAcrossScopes(IReadOnlyList<MemoryItemLedgerDto> items, int maxTokens)
    {
        var hotIds = new HashSet<Guid>();
        foreach (var group in items.Where(x => x.Status == "active").GroupBy(x => (x.Scope, x.ProjectId, x.CompanionId)))
        {
            var used = 64; // Reserve header overhead
            foreach (var item in group.OrderByDescending(x => x.UpdatedAt))
            {
                var lineTokens = EstimateTokens(FormatMemoryBullet(item) + "\n");
                if (used + lineTokens > maxTokens) continue;
                used += lineTokens;
                hotIds.Add(item.Id);
            }
        }
        return hotIds;
    }

    private async Task<ContentReference> StorePayloadAsync(
        Guid tenantId,
        Guid workspaceId,
        StoredMemoryPayload payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await _content.PutAsync(new ContentWriteRequest(tenantId, workspaceId, "memory-item", "application/json", stream), cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredMemoryPayload> ReadPayloadAsync(MemoryVersionRecord version, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(
            new ContentReference(version.ContentReference, version.ContentHash, version.ContentLength, "application/json"),
            cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<StoredMemoryPayload>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new StoredMemoryPayload();
    }

    private async Task<string?> ReadPlainTextSafeAsync(string reference, string hash, long length, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            await using var stream = await _content.OpenReadAsync(new ContentReference(reference, hash, length, "text/plain"), cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task TryIndexVectorAsync(TenantContext tenant, MemoryItemRecord item, string content, CancellationToken cancellationToken)
    {
        if (_vectorStore is null || tenant.TenantId == Guid.Empty) return;
        var bucket = item.ProjectId is { } pid && pid != Guid.Empty ? pid : item.WorkspaceId;
        if (bucket == Guid.Empty) return;
        try
        {
            await _vectorStore.IndexAsync(new VectorIndexRequest
            {
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                ProjectId = bucket,
                Namespace = MemoryVector.Namespace,
                SourceType = MemoryVector.SourceType,
                SourceId = item.Id.ToString(),
                SourceRevision = item.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                Content = content,
                Metadata = new Dictionary<string, string>
                {
                    ["scope"] = item.Scope,
                    ["kind"] = item.Kind,
                    ["applicability"] = item.Applicability ?? string.Empty,
                    ["expiry_condition"] = item.ExpiryCondition ?? string.Empty
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
        }
    }

    private async Task TryDeleteVectorAsync(TenantContext tenant, MemoryItemRecord item, CancellationToken cancellationToken)
    {
        if (_vectorStore is null || tenant.TenantId == Guid.Empty) return;
        var bucket = item.ProjectId is { } pid && pid != Guid.Empty ? pid : item.WorkspaceId;
        if (bucket == Guid.Empty) return;
        try
        {
            await _vectorStore.DeleteSourceAsync(new VectorSourceReference
            {
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                ProjectId = bucket,
                Namespace = MemoryVector.Namespace,
                SourceType = MemoryVector.SourceType,
                SourceId = item.Id.ToString()
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static (string Heading, string Key, string Kind)[] SectionsForScope(string scope) => scope switch
    {
        "companion" => CompanionSections,
        "principal" => PrincipalSections,
        _ => ProjectSections
    };

    private static string DefaultSectionForKind(string scope, string kind)
    {
        var sections = SectionsForScope(scope);
        var match = sections.FirstOrDefault(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(match.Heading)) return match.Heading;
        if (scope == "companion")
        {
            return kind switch
            {
                "preference" or "bond" => "Persona & Bond",
                "episode" => "Episodic Highlights",
                "plan" or "todo" or "task_template" => "Operational Focus",
                _ => "Cross-Project Notes"
            };
        }
        if (scope == "project")
        {
            return kind switch
            {
                "decision" => "Architecture & Decisions",
                "failure_pattern" or "supervision_rule" => "Conventions & Pitfalls",
                _ => "Build & Operations"
            };
        }
        return "Preferences & Style";
    }

    private static string ResolveSectionHeading(string scope, string? section, string kind)
    {
        if (!string.IsNullOrWhiteSpace(section))
        {
            var matched = SectionsForScope(scope).FirstOrDefault(s =>
                string.Equals(s.Heading, section.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Key, section.Trim(), StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrEmpty(matched.Heading) ? matched.Heading : section.Trim();
        }
        return DefaultSectionForKind(scope, kind);
    }

    private static string InferKindFromSection(string scope, string section, string fallback)
    {
        var matched = SectionsForScope(scope).FirstOrDefault(s =>
            string.Equals(s.Heading, section, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Key, section, StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrEmpty(matched.Kind) ? matched.Kind : fallback;
    }

    private static string NormalizeDocumentScope(string? scope)
    {
        var normalized = (scope ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("principal" or "project" or "companion"))
        {
            throw new ArgumentException($"Unknown memory document scope '{scope}'. Expected one of: principal, project, companion.");
        }
        return normalized;
    }

    private static string NormalizeItemScope(string? scope)
    {
        var normalized = (scope ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("principal" or "project" or "companion" or "workspace" or "agent"))
        {
            throw new ArgumentException($"Unknown memory scope '{scope}'. Expected one of: principal, project, companion, workspace, agent.");
        }
        return normalized;
    }

    private static string NormalizeItemStatus(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("active" or "revoked" or "superseded"))
        {
            throw new ArgumentException($"Unknown memory status '{status}'. Expected one of: active, revoked, superseded, all.");
        }
        return normalized;
    }

    private static string NormalizeRecallFidelity(string? fidelity)
    {
        var normalized = (fidelity ?? "balanced").Trim().ToLowerInvariant();
        if (normalized is not ("summary_only" or "balanced" or "verbatim_first"))
        {
            throw new ArgumentException($"Unknown recall fidelity '{fidelity}'. Expected one of: summary_only, balanced, verbatim_first.");
        }
        return normalized;
    }

    private static void ValidateScopeTarget(string scope, Guid? companionId)
    {
        if (scope == "companion" && (companionId is null || companionId == Guid.Empty))
        {
            throw new ArgumentException("companion_id is required when accessing companion memory document.");
        }
    }

    private static double ScoreMemoryItem(MemoryItemLedgerDto item, string query, IReadOnlyList<string> terms) =>
        ScoreText($"{item.Kind} {item.Section} {item.Content}", query, terms);

    private static double ScoreText(string text, string query, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var lower = text.ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();
        double score = lower.Contains(lowerQuery, StringComparison.Ordinal) ? 2.0 : 0.0;
        if (terms.Count > 0)
        {
            var matched = terms.Count(t => lower.Contains(t, StringComparison.Ordinal));
            if (matched > 0)
            {
                score += (double)matched / terms.Count;
            }
        }
        return score;
    }

    private static string BuildFocusedExcerpt(string text, string query, IReadOnlyList<string> terms, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            foreach (var term in terms)
            {
                idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) break;
            }
        }

        if (idx < 0) return string.Concat(text.AsSpan(0, maxChars), "…");

        var start = Math.Max(0, idx - maxChars / 4);
        var length = Math.Min(maxChars, text.Length - start);
        var slice = text.Substring(start, length);
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < text.Length ? "…" : string.Empty;
        return string.Concat(prefix, slice, suffix);
    }

    private static string ClipText(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");

    private static int EstimateTokens(string text) => Math.Max(1, (text.Length + 3) / 4);

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static TomlTable? TryGetTable(TomlTable root, string key) =>
        root.TryGetValue(key, out var val) && val is TomlTable table ? table : null;

    private static string? GetString(TomlTable? table, string key) =>
        table is not null && table.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static int GetInt(TomlTable? table, string key, int fallback) =>
        table is not null && table.TryGetValue(key, out var val) ? Convert.ToInt32(val, CultureInfo.InvariantCulture) : fallback;

    private static double GetDouble(TomlTable? table, string key, double fallback) =>
        table is not null && table.TryGetValue(key, out var val) ? Convert.ToDouble(val, CultureInfo.InvariantCulture) : fallback;

    private static bool GetBool(TomlTable? table, string key, bool fallback) =>
        table is not null && table.TryGetValue(key, out var val) ? Convert.ToBoolean(val, CultureInfo.InvariantCulture) : fallback;

    private static string[] GetStrings(TomlTable? table, string key) =>
        table is not null && table.TryGetValue(key, out var val) && val is TomlArray array
            ? array.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
            : [];

    private sealed class StoredMemoryPayload
    {
        public Guid SourceRunId { get; set; }
        public Guid GeneratedByInstanceId { get; set; }
        public string Scope { get; set; } = "project";
        public string Kind { get; set; } = "fact";
        public string Content { get; set; } = string.Empty;
        public double Confidence { get; set; } = 1.0;
        public Guid? ProjectId { get; set; }
        public Guid? AgentId { get; set; }
        public string? Evidence { get; set; }
        public string? Applicability { get; set; }
        public string? ExpiryCondition { get; set; }
        public string Action { get; set; } = "add";
        public string? PreviousContent { get; set; }
        public Guid? SourceSessionId { get; set; }
        public long? SourceMessageSeq { get; set; }
        public Guid? CompanionId { get; set; }
        public string? Section { get; set; }
        public string? Reason { get; set; }
    }
}
