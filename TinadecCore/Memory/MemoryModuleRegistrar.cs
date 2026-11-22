using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Memory;

internal static class MemoryVector
{
    public const string Namespace = "long_term_memory";
    public const string SourceType = "memory_item";
}

internal static class MemorySchemaEnsurer
{
    private static readonly ConcurrentDictionary<string, byte> EnsuredConnections = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureAsync(MemoryDbContext db, CancellationToken cancellationToken)
    {
        var key = db.Database.GetConnectionString() ?? "default";
        if (EnsuredConnections.ContainsKey(key)) return;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (EnsuredConnections.ContainsKey(key)) return;
            await DbContextSchemaBootstrapper.EnsureColumnsAsync(db, cancellationToken).ConfigureAwait(false);
            EnsuredConnections[key] = 0;
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>
/// Memory module registrar. Registers memory store with retention policy and provenance.
/// </summary>
public sealed class MemoryModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "memory";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<MemoryDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<ProjectSessionStore>();
        builder.Services.AddSingleton<ISessionLocator>(sp => sp.GetRequiredService<ProjectSessionStore>());
        builder.Services.AddSingleton<IWorkspaceRootResolver>(sp => sp.GetRequiredService<ProjectSessionStore>());
        builder.Services.AddSingleton<IConversationStore>(sp => sp.GetRequiredService<ProjectSessionStore>());
        builder.Services.AddSingleton<ISessionWorkspaceBinder>(sp => sp.GetRequiredService<ProjectSessionStore>());
        builder.Services.AddSingleton<TinadecCore.Persistence.IStorageMigrationParticipant>(sp => sp.GetRequiredService<ProjectSessionStore>());
        builder.Services.AddSingleton<IMemoryStore, MemoryStore>();
        builder.Services.AddSingleton<ILongTermMemoryService, LongTermMemoryService>();
        builder.Services.AddSingleton<IContextMemoryService, MemoryDocumentProjectionService>();
        builder.Services.AddSingleton<IMessageAttachmentStore, MessageAttachmentStore>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "strategies"],
            Capabilities = ["session_serialization", "chat_history", "retention_policy", "provenance"],
            Language = "C#",
            MafPrimitives = ["memory", "session"],
            RegistrationStatus = ModuleRegistrationStatus.Registered
        });
    }
}

/// <summary>
/// Retrieves only reviewed, active, non-expired, and applicable long-term memories. Candidate rows
/// are deliberately absent from this query. Uses hybrid semantic vector + keyword ranking when an
/// embedding route and <see cref="IVectorStore"/> are available, falling back deterministically to
/// keyword scoring otherwise.
/// </summary>
internal sealed class MemoryStore : IMemoryStore
{
    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly ISessionLocator _sessions;
    private readonly IVectorStore? _vectorStore;

    public MemoryStore(
        IDbContextFactory<MemoryDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant,
        ISessionLocator sessions,
        IVectorStore? vectorStore = null)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
        _sessions = sessions;
        _vectorStore = vectorStore;
    }

    public async Task<MemoryEntry[]> RetrieveAsync(
        string sessionId,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(sessionId, out var parsedSessionId) || maxResults <= 0)
        {
            return [];
        }

        var scope = _tenant.Current;
        var session = await _sessions.FindAsync(parsedSessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);

        var rows = await db.MemoryItems.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId
                && item.WorkspaceId == scope.WorkspaceId
                && item.Status == "active"
                && item.RevokedAt == null
                && (item.Scope == "workspace"
                    || (item.Scope == "principal" && item.PrincipalId == scope.PrincipalId)
                    || (item.Scope == "project" && item.ProjectId == session.ProjectId)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // SQLite cannot translate DateTimeOffset ordering. Filter in the database, then
        // preserve the intended recency order after materialization.
        rows.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
        if (rows.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var eligible = new List<(MemoryItemRecord Row, MemoryCandidateProposal Proposal, string? Applicability, string? Expiry)>();
        foreach (var item in rows)
        {
            var version = await db.MemoryVersions.AsNoTracking().SingleOrDefaultAsync(version => version.Id == item.CurrentVersionId, cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                continue;
            }

            var proposal = await ReadProposalAsync(version.ContentReference, version.ContentHash, version.ContentLength, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(proposal.Content))
            {
                continue;
            }
            var applicability = string.IsNullOrWhiteSpace(item.Applicability) ? proposal.Applicability : item.Applicability;
            var expiry = string.IsNullOrWhiteSpace(item.ExpiryCondition) ? proposal.ExpiryCondition : item.ExpiryCondition;

            if (IsExpired(expiry, now) || !MatchesApplicability(applicability, session, item))
            {
                continue;
            }

            eligible.Add((item, proposal, applicability, expiry));
        }

        if (eligible.Count == 0)
        {
            return [];
        }

        var semanticScores = await SearchSemanticScoresAsync(scope, session, eligible.Select(x => x.Row).ToArray(), query, maxResults, cancellationToken).ConfigureAwait(false);
        var terms = Tokenize(query);
        var entries = new List<MemoryEntry>();

        foreach (var (item, proposal, applicability, expiry) in eligible)
        {
            var keywordScore = KeywordScore(terms, proposal.Content, applicability);
            var hasSemantic = semanticScores.TryGetValue(item.Id, out var semanticScore) && semanticScore > 0f;
            if (!hasSemantic && terms.Count != 0 && keywordScore <= 0f)
            {
                continue;
            }

            var score = hasSemantic
                ? 0.65f * Math.Clamp(semanticScore, 0f, 1f) + 0.35f * keywordScore
                : keywordScore;

            var provenance = new Dictionary<string, string>
            {
                ["scope"] = item.Scope,
                ["kind"] = item.Kind,
                ["version"] = item.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                ["retrieval"] = hasSemantic ? "hybrid_vector_keyword" : "keyword_fallback"
            };
            if (!string.IsNullOrWhiteSpace(applicability)) provenance["applicability"] = applicability;
            if (!string.IsNullOrWhiteSpace(expiry)) provenance["expiry_condition"] = expiry;

            entries.Add(new MemoryEntry
            {
                Id = item.Id.ToString(),
                SessionId = sessionId,
                Content = proposal.Content,
                Source = "reviewed_long_term_memory",
                Score = score,
                CreatedAt = item.CreatedAt,
                Provenance = provenance
            });
        }

        return entries
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(maxResults, 1, 64))
            .ToArray();
    }

    private async Task<Dictionary<Guid, float>> SearchSemanticScoresAsync(
        TenantContext scope,
        SessionReference session,
        IReadOnlyList<MemoryItemRecord> items,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<Guid, float>();
        if (_vectorStore is null || string.IsNullOrWhiteSpace(query) || scope.TenantId == Guid.Empty)
        {
            return scores;
        }

        var buckets = new HashSet<Guid>();
        if (session.ProjectId is { } spid && spid != Guid.Empty)
            buckets.Add(spid);
        foreach (var item in items)
        {
            var bucket = item.ProjectId is { } pid && pid != Guid.Empty ? pid : item.WorkspaceId;
            if (bucket != Guid.Empty) buckets.Add(bucket);
        }
        if (buckets.Count == 0)
        {
            return scores;
        }

        var limit = Math.Clamp(maxResults * 2, 8, 64);
        foreach (var bucket in buckets)
        {
            try
            {
                var matches = await _vectorStore.SearchAsync(new VectorSearchRequest
                {
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    ProjectId = bucket,
                    Namespace = MemoryVector.Namespace,
                    Query = query,
                    Limit = limit,
                    SourceTypes = [MemoryVector.SourceType]
                }, cancellationToken).ConfigureAwait(false);

                foreach (var match in matches)
                {
                    if (!Guid.TryParse(match.SourceId, out var itemId)) continue;
                    if (!scores.TryGetValue(itemId, out var existing) || match.Score > existing)
                        scores[itemId] = match.Score;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Embedding model route may be unconfigured; gracefully fall back to keyword scoring.
            }
        }

        return scores;
    }

    internal static bool IsExpired(string? expiryCondition, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(expiryCondition)) return false;
        var trimmed = expiryCondition.Trim();
        if (trimmed.Equals("never", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.Equals("expired", StringComparison.OrdinalIgnoreCase))
            return true;

        var candidate = trimmed;
        foreach (var prefix in new[] { "expires_at:", "before:", "until:" })
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[prefix.Length..].Trim();
                break;
            }
        }

        if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed <= now;

        return false;
    }

    internal static bool MatchesApplicability(string? applicability, SessionReference session, MemoryItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(applicability)) return true;
        var trimmed = applicability.Trim();
        if (trimmed.StartsWith("project_id:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = trimmed["project_id:".Length..].Trim();
            return Guid.TryParse(raw, out var requiredProject) && session.ProjectId == requiredProject;
        }
        if (trimmed.StartsWith("view_mode:", StringComparison.OrdinalIgnoreCase))
        {
            var mode = trimmed["view_mode:".Length..].Trim();
            return string.Equals(session.ViewMode, mode, StringComparison.OrdinalIgnoreCase);
        }
        if (trimmed.StartsWith("scope_only:", StringComparison.OrdinalIgnoreCase))
        {
            var reqScope = trimmed["scope_only:".Length..].Trim();
            return string.Equals(item.Scope, reqScope, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public Task StoreAsync(
        string sessionId,
        MemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Long-term memory must be created as a reviewed candidate; direct memory writes are not allowed.");
    }

    private static readonly System.Text.Json.JsonSerializerOptions ProposalReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private async Task<MemoryCandidateProposal> ReadProposalAsync(string reference, string hash, long length, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, hash, length, "application/json"), cancellationToken).ConfigureAwait(false);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<MemoryCandidateProposal>(stream, ProposalReadJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Memory content is invalid.");
    }

    private static HashSet<string> Tokenize(string query) => query
        .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(term => term.Length > 1)
        .Select(term => term.ToLowerInvariant())
        .ToHashSet(StringComparer.Ordinal);

    private static float KeywordScore(IReadOnlySet<string> terms, string content, string? applicability = null)
    {
        if (terms.Count == 0)
        {
            return 0.01f;
        }

        var lower = string.IsNullOrWhiteSpace(applicability)
            ? content.ToLowerInvariant()
            : string.Concat(content, "\n", applicability).ToLowerInvariant();
        var matches = terms.Count(term => lower.Contains(term, StringComparison.Ordinal));
        return matches == 0 ? 0 : (float)matches / terms.Count;
    }
}

internal sealed class LongTermMemoryService : ILongTermMemoryService
{
    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly IVectorStore? _vectorStore;

    public LongTermMemoryService(
        IDbContextFactory<MemoryDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant,
        IVectorStore? vectorStore = null)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
        _vectorStore = vectorStore;
    }

    public async Task<MemoryCandidate> CreateCandidateAsync(MemoryCandidateProposal proposal, CancellationToken cancellationToken = default)
    {
        ValidateProposal(proposal);
        var scope = _tenant.Current;
        var stored = await StoreTextAsync(scope.TenantId, scope.WorkspaceId, "memory-candidate", SerializeProposal(proposal), cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var row = new MemoryCandidateRecord
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = proposal.ProjectId, AgentId = proposal.AgentId,
            SourceRunId = proposal.SourceRunId, GeneratedByInstanceId = proposal.GeneratedByInstanceId, Scope = proposal.Scope, Kind = proposal.Kind,
            Status = "proposed", Confidence = Math.Clamp(proposal.Confidence, 0, 1), ContentReference = stored.Value, ContentHash = stored.Sha256,
            ContentLength = stored.Length, Applicability = proposal.Applicability, ExpiryCondition = proposal.ExpiryCondition,
            CreatedByPrincipalId = scope.PrincipalId, CreatedAt = now, UpdatedAt = now
        };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
        db.MemoryCandidates.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ToCandidateAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryCandidate>> ListCandidatesAsync(MemoryCandidateQuery? query = null, CancellationToken cancellationToken = default)
    {
        var filter = query ?? new MemoryCandidateQuery();
        var status = ReviewVocabulary.Normalize(filter.Status, ReviewVocabulary.CandidateStatuses, "status");
        var scope = ReviewVocabulary.Normalize(filter.Scope, ReviewVocabulary.Scopes, "scope");
        var kind = ReviewVocabulary.Normalize(filter.Kind, null, "kind");
        var limit = ReviewVocabulary.ClampLimit(filter.Limit);
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
        var rowsQuery = db.MemoryCandidates.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId);
        if (status is not null) rowsQuery = rowsQuery.Where(x => x.Status == status);
        if (scope is not null) rowsQuery = rowsQuery.Where(x => x.Scope == scope);
        if (kind is not null) rowsQuery = rowsQuery.Where(x => x.Kind == kind);
        if (filter.RunId is { } runId) rowsQuery = rowsQuery.Where(x => x.SourceRunId == runId);
        if (filter.ProjectId is { } projectId) rowsQuery = rowsQuery.Where(x => x.ProjectId == projectId);
        // SQLite cannot translate DateTimeOffset ordering; sort on the client instead.
        var rows = await rowsQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        if (limit is { } candidateLimit) rows = rows.Take(candidateLimit).ToList();
        var result = new List<MemoryCandidate>(rows.Count);
        foreach (var row in rows) result.Add(await ToCandidateAsync(row, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public async Task<MemoryCandidate> DecideCandidateAsync(Guid candidateId, string decision, string? reason, CancellationToken cancellationToken = default)
    {
        if (decision is not ("promoted" or "rejected")) throw new ArgumentException("Decision must be promoted or rejected.", nameof(decision));
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
        var candidate = await db.MemoryCandidates.SingleOrDefaultAsync(x => x.Id == candidateId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Memory candidate was not found.");
        if (candidate.Status != "proposed") throw new InvalidOperationException("Memory candidate has already been decided.");
        candidate.Status = decision;
        candidate.DecisionReason = reason;
        candidate.DecidedByPrincipalId = scope.PrincipalId;
        candidate.UpdatedAt = DateTimeOffset.UtcNow;

        MemoryItemRecord? promotedItem = null;
        MemoryCandidateProposal? promotedProposal = null;
        if (decision == "promoted")
        {
            promotedProposal = System.Text.Json.JsonSerializer.Deserialize<MemoryCandidateProposal>(
                await ReadTextAsync(candidate.ContentReference, candidate.ContentHash, candidate.ContentLength, cancellationToken).ConfigureAwait(false));
            var applicability = candidate.Applicability ?? promotedProposal?.Applicability;
            var expiryCondition = candidate.ExpiryCondition ?? promotedProposal?.ExpiryCondition;

            promotedItem = new MemoryItemRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = candidate.ProjectId, AgentId = candidate.AgentId,
                PrincipalId = candidate.Scope == "principal" ? scope.PrincipalId : null,
                Scope = candidate.Scope, Kind = candidate.Kind, Status = "active", CurrentVersion = 1,
                Applicability = applicability, ExpiryCondition = expiryCondition,
                CreatedByPrincipalId = scope.PrincipalId, CreatedAt = candidate.UpdatedAt, UpdatedAt = candidate.UpdatedAt
            };
            var version = new MemoryVersionRecord
            {
                Id = Guid.NewGuid(), MemoryItemId = promotedItem.Id, Version = 1, SourceCandidateId = candidate.Id, ContentReference = candidate.ContentReference,
                ContentHash = candidate.ContentHash, ContentLength = candidate.ContentLength, CreatedByPrincipalId = scope.PrincipalId, CreatedAt = candidate.UpdatedAt
            };
            promotedItem.CurrentVersionId = version.Id;
            candidate.PromotedMemoryItemId = promotedItem.Id;
            db.MemoryItems.Add(promotedItem);
            db.MemoryVersions.Add(version);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (promotedItem is not null && promotedProposal is not null && _vectorStore is not null)
        {
            var bucket = promotedItem.ProjectId is { } pid && pid != Guid.Empty ? pid : promotedItem.WorkspaceId;
            if (scope.TenantId != Guid.Empty && bucket != Guid.Empty)
            {
                try
                {
                    await _vectorStore.IndexAsync(new VectorIndexRequest
                    {
                        TenantId = scope.TenantId,
                        WorkspaceId = scope.WorkspaceId,
                        ProjectId = bucket,
                        Namespace = MemoryVector.Namespace,
                        SourceType = MemoryVector.SourceType,
                        SourceId = promotedItem.Id.ToString(),
                        SourceRevision = promotedItem.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                        Content = promotedProposal.Content,
                        Metadata = new Dictionary<string, string>
                        {
                            ["scope"] = promotedItem.Scope,
                            ["kind"] = promotedItem.Kind,
                            ["applicability"] = promotedItem.Applicability ?? string.Empty,
                            ["expiry_condition"] = promotedItem.ExpiryCondition ?? string.Empty
                        }
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    // Embedding route may be unconfigured; keyword retrieval remains deterministic fallback.
                }
            }
        }

        return await ToCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LongTermMemoryItem>> ListItemsAsync(MemoryItemQuery? query = null, CancellationToken cancellationToken = default)
    {
        var filter = query ?? new MemoryItemQuery();
        var status = ReviewVocabulary.Normalize(filter.Status, ReviewVocabulary.ItemStatuses, "status");
        var scope = ReviewVocabulary.Normalize(filter.Scope, ReviewVocabulary.Scopes, "scope");
        var kind = ReviewVocabulary.Normalize(filter.Kind, null, "kind");
        var limit = ReviewVocabulary.ClampLimit(filter.Limit);
        var tenant = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
        var rowsQuery = db.MemoryItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId);
        if (status is not null) rowsQuery = rowsQuery.Where(x => x.Status == status);
        if (scope is not null) rowsQuery = rowsQuery.Where(x => x.Scope == scope);
        if (kind is not null) rowsQuery = rowsQuery.Where(x => x.Kind == kind);
        if (filter.ProjectId is { } projectId) rowsQuery = rowsQuery.Where(x => x.ProjectId == projectId);
        var rows = await rowsQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        if (limit is { } itemLimit) rows = rows.Take(itemLimit).ToList();
        var result = new List<LongTermMemoryItem>(rows.Count);
        foreach (var row in rows) result.Add(await ToItemAsync(db, row, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public async Task<LongTermMemoryItem> RevokeAsync(Guid itemId, string? reason, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchemaEnsurer.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
        var item = await db.MemoryItems.SingleOrDefaultAsync(x => x.Id == itemId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Memory item was not found.");
        if (item.Status == "revoked") return await ToItemAsync(db, item, cancellationToken).ConfigureAwait(false);
        item.Status = "revoked";
        item.RevokedAt = DateTimeOffset.UtcNow;
        item.RevocationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        item.UpdatedAt = item.RevokedAt.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (_vectorStore is not null)
        {
            var bucket = item.ProjectId is { } pid && pid != Guid.Empty ? pid : item.WorkspaceId;
            if (scope.TenantId != Guid.Empty && bucket != Guid.Empty)
            {
                try
                {
                    await _vectorStore.DeleteSourceAsync(new VectorSourceReference
                    {
                        TenantId = scope.TenantId,
                        WorkspaceId = scope.WorkspaceId,
                        ProjectId = bucket,
                        Namespace = MemoryVector.Namespace,
                        SourceType = MemoryVector.SourceType,
                        SourceId = item.Id.ToString()
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                }
            }
        }

        return await ToItemAsync(db, item, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateProposal(MemoryCandidateProposal proposal)
    {
        if (proposal.SourceRunId == Guid.Empty || proposal.GeneratedByInstanceId == Guid.Empty) throw new ArgumentException("Candidate provenance is required.", nameof(proposal));
        if (!ReviewVocabulary.Scopes.Contains(proposal.Scope)) throw new ArgumentException("Invalid memory scope.", nameof(proposal));
        if (string.IsNullOrWhiteSpace(proposal.Kind) || string.IsNullOrWhiteSpace(proposal.Content)) throw new ArgumentException("Memory kind and content are required.", nameof(proposal));
    }

    private async Task<ContentReference> StoreTextAsync(Guid tenantId, Guid workspaceId, string kind, string content, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return await _content.PutAsync(new ContentWriteRequest(tenantId, workspaceId, kind, "application/json", stream), cancellationToken).ConfigureAwait(false);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ProposalReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private async Task<MemoryCandidate> ToCandidateAsync(MemoryCandidateRecord row, CancellationToken cancellationToken)
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<MemoryCandidateProposal>(await ReadTextAsync(row.ContentReference, row.ContentHash, row.ContentLength, cancellationToken).ConfigureAwait(false), ProposalReadJsonOptions)
            ?? throw new InvalidDataException("Memory candidate content is invalid.");
        return new MemoryCandidate(row.Id, row.SourceRunId, row.GeneratedByInstanceId, row.Scope, row.Kind, row.Status, row.Confidence, data.Content ?? string.Empty, row.DecisionReason, row.PromotedMemoryItemId, row.CreatedAt, row.UpdatedAt, data.Evidence, row.Applicability ?? data.Applicability, row.ExpiryCondition ?? data.ExpiryCondition);
    }

    private async Task<LongTermMemoryItem> ToItemAsync(MemoryDbContext db, MemoryItemRecord row, CancellationToken cancellationToken)
    {
        var version = await db.MemoryVersions.AsNoTracking().SingleAsync(x => x.Id == row.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        var data = System.Text.Json.JsonSerializer.Deserialize<MemoryCandidateProposal>(await ReadTextAsync(version.ContentReference, version.ContentHash, version.ContentLength, cancellationToken).ConfigureAwait(false), ProposalReadJsonOptions)
            ?? throw new InvalidDataException("Memory item content is invalid.");
        return new LongTermMemoryItem(row.Id, row.Scope, row.Kind, row.Status, row.CurrentVersion, data.Content ?? string.Empty, row.CreatedAt, row.UpdatedAt, row.RevokedAt, row.Applicability ?? data.Applicability, row.ExpiryCondition ?? data.ExpiryCondition);
    }

    private async Task<string> ReadTextAsync(string reference, string hash, long length, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, hash, length, "application/json"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SerializeProposal(MemoryCandidateProposal proposal) => System.Text.Json.JsonSerializer.Serialize(proposal);
}
