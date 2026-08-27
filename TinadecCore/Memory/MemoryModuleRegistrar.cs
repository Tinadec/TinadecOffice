using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Memory;

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
        builder.Services.AddSingleton<IConversationStore>(sp => sp.GetRequiredService<ProjectSessionStore>());
        builder.Services.AddSingleton<TinadecCore.Persistence.IStorageMigrationParticipant>(sp => sp.GetRequiredService<ProjectSessionStore>());
        builder.Services.AddSingleton<IMemoryStore, MemoryStore>();
        builder.Services.AddSingleton<ILongTermMemoryService, LongTermMemoryService>();
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
/// Retrieves only reviewed, active long-term memories. Candidate rows are deliberately absent
/// from this query. A vector provider may be layered in later; keyword ranking is the explicit
/// deterministic fallback when no embedding route is configured.
/// </summary>
internal sealed class MemoryStore : IMemoryStore
{
    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;
    private readonly ISessionLocator _sessions;

    public MemoryStore(
        IDbContextFactory<MemoryDbContext> dbFactory,
        IContentStore content,
        ITenantContextAccessor tenant,
        ISessionLocator sessions)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
        _sessions = sessions;
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
        var rows = await db.MemoryItems.AsNoTracking()
            .Where(item => item.TenantId == scope.TenantId
                && item.WorkspaceId == scope.WorkspaceId
                && item.Status == "active"
                && (item.Scope == "workspace"
                    || (item.Scope == "principal" && item.PrincipalId == scope.PrincipalId)
                    || (item.Scope == "project" && item.ProjectId == session.ProjectId)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // SQLite cannot translate DateTimeOffset ordering. Filter in the database, then
        // preserve the intended recency order after materialization.
        rows.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));

        var terms = Tokenize(query);
        var entries = new List<MemoryEntry>();
        foreach (var item in rows)
        {
            var version = await db.MemoryVersions.AsNoTracking().SingleOrDefaultAsync(version => version.Id == item.CurrentVersionId, cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                continue;
            }

            var proposal = await ReadProposalAsync(version.ContentReference, version.ContentHash, version.ContentLength, cancellationToken).ConfigureAwait(false);
            var score = KeywordScore(terms, proposal.Content);
            if (terms.Count != 0 && score <= 0)
            {
                continue;
            }

            entries.Add(new MemoryEntry
            {
                Id = item.Id.ToString(),
                SessionId = sessionId,
                Content = proposal.Content,
                Source = "reviewed_long_term_memory",
                Score = score,
                CreatedAt = item.CreatedAt,
                Provenance = new Dictionary<string, string>
                {
                    ["scope"] = item.Scope,
                    ["kind"] = item.Kind,
                    ["version"] = item.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["retrieval"] = "keyword_fallback"
                }
            });
        }

        return entries
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(maxResults, 1, 64))
            .ToArray();
    }

    public Task StoreAsync(
        string sessionId,
        MemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Long-term memory must be created as a reviewed candidate; direct memory writes are not allowed.");
    }

    private async Task<MemoryCandidateProposal> ReadProposalAsync(string reference, string hash, long length, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, hash, length, "application/json"), cancellationToken).ConfigureAwait(false);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<MemoryCandidateProposal>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Memory content is invalid.");
    }

    private static HashSet<string> Tokenize(string query) => query
        .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(term => term.Length > 1)
        .Select(term => term.ToLowerInvariant())
        .ToHashSet(StringComparer.Ordinal);

    private static float KeywordScore(IReadOnlySet<string> terms, string content)
    {
        if (terms.Count == 0)
        {
            return 0.01f;
        }

        var lower = content.ToLowerInvariant();
        var matches = terms.Count(term => lower.Contains(term, StringComparison.Ordinal));
        return matches == 0 ? 0 : (float)matches / terms.Count;
    }
}

internal sealed class LongTermMemoryService : ILongTermMemoryService
{
    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ITenantContextAccessor _tenant;

    public LongTermMemoryService(IDbContextFactory<MemoryDbContext> dbFactory, IContentStore content, ITenantContextAccessor tenant)
    {
        _dbFactory = dbFactory;
        _content = content;
        _tenant = tenant;
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
            ContentLength = stored.Length, CreatedByPrincipalId = scope.PrincipalId, CreatedAt = now, UpdatedAt = now
        };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.MemoryCandidates.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ToCandidateAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryCandidate>> ListCandidatesAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.MemoryCandidates.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim().ToLowerInvariant());
        // SQLite cannot translate DateTimeOffset ordering; sort on the client instead.
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        var result = new List<MemoryCandidate>(rows.Count);
        foreach (var row in rows) result.Add(await ToCandidateAsync(row, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public async Task<MemoryCandidate> DecideCandidateAsync(Guid candidateId, string decision, string? reason, CancellationToken cancellationToken = default)
    {
        if (decision is not ("promoted" or "rejected")) throw new ArgumentException("Decision must be promoted or rejected.", nameof(decision));
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await db.MemoryCandidates.SingleOrDefaultAsync(x => x.Id == candidateId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Memory candidate was not found.");
        if (candidate.Status != "proposed") throw new InvalidOperationException("Memory candidate has already been decided.");
        candidate.Status = decision;
        candidate.DecisionReason = reason;
        candidate.DecidedByPrincipalId = scope.PrincipalId;
        candidate.UpdatedAt = DateTimeOffset.UtcNow;
        if (decision == "promoted")
        {
            var item = new MemoryItemRecord
            {
                Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = candidate.ProjectId, AgentId = candidate.AgentId,
                PrincipalId = candidate.Scope == "principal" ? scope.PrincipalId : null,
                Scope = candidate.Scope, Kind = candidate.Kind, Status = "active", CurrentVersion = 1, CreatedByPrincipalId = scope.PrincipalId,
                CreatedAt = candidate.UpdatedAt, UpdatedAt = candidate.UpdatedAt
            };
            var version = new MemoryVersionRecord
            {
                Id = Guid.NewGuid(), MemoryItemId = item.Id, Version = 1, SourceCandidateId = candidate.Id, ContentReference = candidate.ContentReference,
                ContentHash = candidate.ContentHash, ContentLength = candidate.ContentLength, CreatedByPrincipalId = scope.PrincipalId, CreatedAt = candidate.UpdatedAt
            };
            item.CurrentVersionId = version.Id;
            candidate.PromotedMemoryItemId = item.Id;
            db.MemoryItems.Add(item);
            db.MemoryVersions.Add(version);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ToCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LongTermMemoryItem>> ListItemsAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        rows.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        var result = new List<LongTermMemoryItem>(rows.Count);
        foreach (var row in rows) result.Add(await ToItemAsync(db, row, cancellationToken).ConfigureAwait(false));
        return result;
    }

    public async Task<LongTermMemoryItem> RevokeAsync(Guid itemId, string? reason, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var item = await db.MemoryItems.SingleOrDefaultAsync(x => x.Id == itemId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Memory item was not found.");
        if (item.Status == "revoked") return await ToItemAsync(db, item, cancellationToken).ConfigureAwait(false);
        item.Status = "revoked";
        item.RevokedAt = DateTimeOffset.UtcNow;
        item.UpdatedAt = item.RevokedAt.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ToItemAsync(db, item, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateProposal(MemoryCandidateProposal proposal)
    {
        if (proposal.SourceRunId == Guid.Empty || proposal.GeneratedByInstanceId == Guid.Empty) throw new ArgumentException("Candidate provenance is required.", nameof(proposal));
        if (proposal.Scope is not ("principal" or "workspace" or "project" or "agent")) throw new ArgumentException("Invalid memory scope.", nameof(proposal));
        if (string.IsNullOrWhiteSpace(proposal.Kind) || string.IsNullOrWhiteSpace(proposal.Content)) throw new ArgumentException("Memory kind and content are required.", nameof(proposal));
    }

    private async Task<ContentReference> StoreTextAsync(Guid tenantId, Guid workspaceId, string kind, string content, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return await _content.PutAsync(new ContentWriteRequest(tenantId, workspaceId, kind, "application/json", stream), cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryCandidate> ToCandidateAsync(MemoryCandidateRecord row, CancellationToken cancellationToken)
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<MemoryCandidateProposal>(await ReadTextAsync(row.ContentReference, row.ContentHash, row.ContentLength, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("Memory candidate content is invalid.");
        return new MemoryCandidate(row.Id, row.SourceRunId, row.GeneratedByInstanceId, row.Scope, row.Kind, row.Status, row.Confidence, data.Content, row.DecisionReason, row.PromotedMemoryItemId, row.CreatedAt, row.UpdatedAt);
    }

    private async Task<LongTermMemoryItem> ToItemAsync(MemoryDbContext db, MemoryItemRecord row, CancellationToken cancellationToken)
    {
        var version = await db.MemoryVersions.AsNoTracking().SingleAsync(x => x.Id == row.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        var data = System.Text.Json.JsonSerializer.Deserialize<MemoryCandidateProposal>(await ReadTextAsync(version.ContentReference, version.ContentHash, version.ContentLength, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("Memory item content is invalid.");
        return new LongTermMemoryItem(row.Id, row.Scope, row.Kind, row.Status, row.CurrentVersion, data.Content, row.CreatedAt, row.UpdatedAt, row.RevokedAt);
    }

    private async Task<string> ReadTextAsync(string reference, string hash, long length, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, hash, length, "application/json"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SerializeProposal(MemoryCandidateProposal proposal) => System.Text.Json.JsonSerializer.Serialize(proposal);
}
