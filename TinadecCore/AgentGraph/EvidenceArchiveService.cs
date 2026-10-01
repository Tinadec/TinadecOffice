using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentGraph;

public sealed class EvidenceArchiveOptions
{
    public const string SectionName = "TinadecEvidence";

    /// <summary>False keeps the archive keyword-only (the rows are still written and recalled).</summary>
    public bool IndexEnabled { get; set; } = true;
    public int IndexIntervalSeconds { get; set; } = 10;
    public int IndexBatchSize { get; set; } = 16;
    /// <summary>One entry's text is kept verbatim up to this length; past it the cut is marked.</summary>
    public int MaxContentChars { get; set; } = 64_000;
    /// <summary>How long entries wait before the indexer asks an unavailable embedding model again.</summary>
    public int UnavailableRetryMinutes { get; set; } = 60;
}

/// <summary>
/// The session's evidence archive (todo R4). Appends are durable rows first; the semantic index is a
/// derived view the <see cref="EvidenceIndexService"/> builds behind them, so an archive with no
/// embedding model is still a complete archive. Recall fuses the semantic half (when it answers)
/// with a keyword half that is always there, by reciprocal rank — a missing model changes the mode
/// the result reports, never whether the evidence can be found.
/// </summary>
public sealed class EvidenceArchiveService : IEvidenceArchive
{
    /// <summary>Semantic-index namespace of one session's evidence.</summary>
    public static string Namespace(Guid sessionId) => $"evidence:{sessionId:N}";

    public const string SourceType = "evidence";

    private const int KeywordWindow = 400;
    private const int SnippetRadius = 180;
    private const int RankConstant = 60;
    private readonly IDbContextFactory<AgentGraphDbContext> _factory;
    private readonly IServiceProvider _services;
    private readonly EvidenceArchiveOptions _options;
    private readonly ILogger<EvidenceArchiveService>? _logger;

    public EvidenceArchiveService(
        IDbContextFactory<AgentGraphDbContext> factory,
        IServiceProvider services,
        IOptions<EvidenceArchiveOptions>? options = null,
        ILogger<EvidenceArchiveService>? logger = null)
    {
        _factory = factory;
        _services = services;
        _options = options?.Value ?? new EvidenceArchiveOptions();
        _logger = logger;
    }

    public async Task<Guid> AppendAsync(EvidenceEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SessionId == Guid.Empty) throw new ArgumentException("Evidence belongs to a session.", nameof(entry));
        if (!EvidenceKinds.All.Contains(entry.Kind, StringComparer.Ordinal)) throw new ArgumentException($"Unknown evidence kind '{entry.Kind}'.", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Content)) throw new ArgumentException("Evidence needs content.", nameof(entry));
        var sourceKey = string.IsNullOrWhiteSpace(entry.SourceKey) ? $"{entry.Kind}:{Guid.NewGuid():N}" : entry.SourceKey.Trim();
        if (sourceKey.Length > 256) sourceKey = sourceKey[..256];

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Evidence.AsNoTracking()
            .Where(row => row.SessionId == entry.SessionId && row.SourceKey == sourceKey)
            .Select(row => row.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing != Guid.Empty) return existing;

        var projectId = await ProjectOfAsync(entry.SessionId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var row = new EvidenceEntryRecord
        {
            Id = Guid.NewGuid(),
            TenantId = entry.TenantId,
            WorkspaceId = entry.WorkspaceId,
            ProjectId = projectId,
            SessionId = entry.SessionId,
            RunId = entry.RunId,
            TaskId = entry.TaskId,
            Kind = entry.Kind,
            Title = Clip(string.IsNullOrWhiteSpace(entry.Title) ? entry.Kind : entry.Title.Trim(), 500),
            Author = entry.Author is null ? null : Clip(entry.Author.Trim(), 128),
            Content = Clip(entry.Content, Math.Max(1_000, _options.MaxContentChars)),
            SourceKey = sourceKey,
            VectorStatus = projectId is null ? EvidenceVectorStatuses.NotApplicable : EvidenceVectorStatuses.Pending,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            CreatedAt = now
        };
        db.Evidence.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return row.Id;
        }
        catch (DbUpdateException)
        {
            // A concurrent append of the same source won the unique index; its entry is the one.
            await using var retry = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var winner = await retry.Evidence.AsNoTracking()
                .Where(item => item.SessionId == entry.SessionId && item.SourceKey == sourceKey)
                .Select(item => item.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (winner == Guid.Empty) throw;
            return winner;
        }
    }

    public async Task<EvidenceRecallResult> RecallAsync(EvidenceRecallQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 20);
        var terms = EvidenceTerms.Extract(query.Query);
        var kinds = (query.Kinds ?? []).Where(kind => EvidenceKinds.All.Contains(kind, StringComparer.Ordinal)).Distinct().ToArray();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scoped = db.Evidence.AsNoTracking().Where(row => row.TenantId == query.TenantId && row.WorkspaceId == query.WorkspaceId && row.SessionId == query.SessionId);
        if (kinds.Length > 0) scoped = scoped.Where(row => kinds.Contains(row.Kind));
        if (query.RunId is { } runId) scoped = scoped.Where(row => row.RunId == runId);

        // Keyword half: always available. Rows mentioning any of the leading terms, newest first and
        // bounded, then scored here by how many terms they carry and where.
        var keyword = new List<(Guid Id, double Score)>();
        if (terms.Count > 0)
        {
            var candidates = await scoped.Where(AnyTerm(terms.Take(6).ToArray()))
                .OrderByDescending(row => row.CreatedAtUnixMs)
                .Take(KeywordWindow)
                .Select(row => new { row.Id, row.Title, row.Content })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            keyword = candidates
                .Select(item => (item.Id, Score: Score(terms, item.Title, item.Content)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .Take(limit * 3)
                .ToList();
        }

        // Semantic half: only when the session has a project, the host has an index, and a model answers.
        var semantic = new List<(Guid Id, string Chunk)>();
        var mode = "keyword";
        string? note;
        var projectId = await scoped.Where(row => row.ProjectId != null).Select(row => row.ProjectId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (projectId is null)
        {
            note = "This session has no project, so there is no semantic index: keyword recall only.";
        }
        else if (_services.GetService<IVectorStore>() is not { } vectors)
        {
            note = "This host has no semantic index: keyword recall only.";
        }
        else
        {
            try
            {
                var matches = await vectors.SearchAsync(new VectorSearchRequest
                {
                    TenantId = query.TenantId,
                    WorkspaceId = query.WorkspaceId,
                    ProjectId = projectId.Value,
                    Namespace = Namespace(query.SessionId),
                    Query = string.IsNullOrWhiteSpace(query.Query) ? "?" : query.Query,
                    Limit = limit * 3,
                    SourceTypes = [SourceType]
                }, cancellationToken).ConfigureAwait(false);
                foreach (var match in matches)
                {
                    if (Guid.TryParse(match.SourceId, out var id) && semantic.All(item => item.Id != id)) semantic.Add((id, match.Content));
                }
                mode = "hybrid";
                note = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // No embedding model, a provider that cannot serve this host, or one that is down right now:
                // the keyword half stands alone. Recall must never fail because the semantic half did.
                _logger?.TryLogDebug(ex, "Semantic evidence recall unavailable for session {SessionId}.", query.SessionId);
                note = ex is InvalidOperationException or NotSupportedException
                    ? "No embedding model is configured for semantic recall: keyword recall only."
                    : "The semantic index did not answer just now: keyword recall only.";
            }
        }

        // Reciprocal-rank fusion: a hit both halves agree on outranks one either half alone found.
        var fused = new Dictionary<Guid, (double Score, bool Keyword, bool Semantic, string? Chunk)>();
        for (var rank = 0; rank < keyword.Count; rank++)
            fused[keyword[rank].Id] = (1.0 / (RankConstant + rank + 1), true, false, null);
        for (var rank = 0; rank < semantic.Count; rank++)
        {
            var (id, chunk) = semantic[rank];
            var add = 1.0 / (RankConstant + rank + 1);
            fused[id] = fused.TryGetValue(id, out var prior) ? (prior.Score + add, prior.Keyword, true, chunk) : (add, false, true, chunk);
        }
        var ranked = fused.OrderByDescending(item => item.Value.Score).Select(item => item.Key).ToArray();
        if (ranked.Length == 0)
            return new EvidenceRecallResult(mode, [], note ?? (terms.Count == 0 ? "The query has no searchable words." : null));

        // Loaded through the same scope, so a semantic hit outside the session, kinds or run is dropped here.
        var rows = await scoped.Where(row => ranked.Contains(row.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = rows.ToDictionary(row => row.Id);
        var hits = new List<EvidenceHit>();
        foreach (var id in ranked)
        {
            if (hits.Count >= limit) break;
            if (!byId.TryGetValue(id, out var row)) continue;
            var match = fused[id];
            hits.Add(new EvidenceHit(row.Id, row.Kind, row.Title, row.Author, row.RunId, row.TaskId,
                match.Keyword ? Snippet(row.Content, terms) : Clip(match.Chunk ?? row.Content, SnippetRadius * 2),
                Math.Round(match.Score * 1000, 2),
                match.Keyword && match.Semantic ? "both" : match.Keyword ? "keyword" : "semantic",
                row.CreatedAt));
        }
        return new EvidenceRecallResult(mode, hits, note);
    }

    private readonly Dictionary<Guid, Guid?> _projects = new();
    private readonly object _projectsGate = new();

    /// <summary>The session's project, cached (a session never moves between projects); null when projectless or unknown.</summary>
    private async Task<Guid?> ProjectOfAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        lock (_projectsGate)
        {
            if (_projects.TryGetValue(sessionId, out var known)) return known;
        }
        if (_services.GetService<ISessionLocator>() is not { } sessions) return null;
        Guid? projectId;
        try
        {
            projectId = (await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false))?.ProjectId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (projectId == Guid.Empty) projectId = null;
        lock (_projectsGate)
        {
            if (_projects.Count > 4096) _projects.Clear();
            _projects[sessionId] = projectId;
        }
        return projectId;
    }

    private static Expression<Func<EvidenceEntryRecord, bool>> AnyTerm(IReadOnlyList<string> terms)
    {
        var row = Expression.Parameter(typeof(EvidenceEntryRecord), "row");
        var toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression? body = null;
        foreach (var term in terms)
        {
            var value = Expression.Constant(term);
            var inTitle = Expression.Call(Expression.Call(Expression.Property(row, nameof(EvidenceEntryRecord.Title)), toLower), contains, value);
            var inContent = Expression.Call(Expression.Call(Expression.Property(row, nameof(EvidenceEntryRecord.Content)), toLower), contains, value);
            var either = Expression.OrElse(inTitle, inContent);
            body = body is null ? either : Expression.OrElse(body, either);
        }
        return Expression.Lambda<Func<EvidenceEntryRecord, bool>>(body ?? Expression.Constant(false), row);
    }

    /// <summary>Terms in the title count thrice; each term's count is capped so one repeated word cannot win; covering more terms wins.</summary>
    internal static double Score(IReadOnlyList<string> terms, string title, string content)
    {
        var lowerTitle = title.ToLowerInvariant();
        var lowerContent = content.ToLowerInvariant();
        double score = 0;
        var covered = 0;
        foreach (var term in terms)
        {
            var inTitle = Math.Min(Count(lowerTitle, term), 3);
            var inContent = Math.Min(Count(lowerContent, term), 5);
            if (inTitle + inContent == 0) continue;
            covered++;
            score += inTitle * 3 + inContent;
        }
        return covered == 0 ? 0 : score + 5.0 * covered / terms.Count;
    }

    private static int Count(string text, string term)
    {
        var count = 0;
        for (var index = text.IndexOf(term, StringComparison.Ordinal); index >= 0 && count < 16; index = text.IndexOf(term, index + term.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Snippet(string content, IReadOnlyList<string> terms)
    {
        var lower = content.ToLowerInvariant();
        var at = terms.Select(term => lower.IndexOf(term, StringComparison.Ordinal)).Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - SnippetRadius);
        var end = Math.Min(content.Length, at + SnippetRadius);
        return (start > 0 ? "…" : "") + content[start..end].Trim() + (end < content.Length ? "…" : "");
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + " …(truncated)";
}

/// <summary>
/// Builds the semantic view behind the evidence archive: embeds due entries in batches, oldest first.
/// With no embedding model an entry is marked unavailable and asked again later (so configuring a model
/// afterwards indexes the backlog); a failing entry backs off and is given up after a few attempts.
/// The archive itself never waits for this — recall works on the rows the moment they are written.
/// </summary>
public sealed class EvidenceIndexService : BackgroundService
{
    private const int MaxAttempts = 6;
    private readonly IDbContextFactory<AgentGraphDbContext> _factory;
    private readonly IServiceProvider _services;
    private readonly EvidenceArchiveOptions _options;
    private readonly ILogger<EvidenceIndexService>? _logger;

    public EvidenceIndexService(
        IDbContextFactory<AgentGraphDbContext> factory,
        IServiceProvider services,
        IOptions<EvidenceArchiveOptions>? options = null,
        ILogger<EvidenceIndexService>? logger = null)
    {
        _factory = factory;
        _services = services;
        _options = options?.Value ?? new EvidenceArchiveOptions();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IndexEnabled) return;
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.IndexIntervalSeconds, 1, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.TryLogWarning(ex, "Evidence index pass failed.");
            }
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Public for deterministic tests: one batch. Returns how many entries were indexed.</summary>
    public async Task<int> RunPassAsync(CancellationToken cancellationToken = default)
    {
        if (_services.GetService<IVectorStore>() is not { } vectors) return 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var due = await db.Evidence
            .Where(row => row.ProjectId != null
                && (row.VectorStatus == EvidenceVectorStatuses.Pending || row.VectorStatus == EvidenceVectorStatuses.Unavailable || row.VectorStatus == EvidenceVectorStatuses.Failed)
                && (row.VectorNextAttemptAtUnixMs == null || row.VectorNextAttemptAtUnixMs <= now))
            .OrderBy(row => row.CreatedAtUnixMs)
            .Take(Math.Clamp(_options.IndexBatchSize, 1, 256))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var indexed = 0;
        for (var index = 0; index < due.Count; index++)
        {
            var row = due[index];
            try
            {
                var result = await vectors.IndexAsync(new VectorIndexRequest
                {
                    TenantId = row.TenantId,
                    WorkspaceId = row.WorkspaceId,
                    ProjectId = row.ProjectId!.Value,
                    Namespace = EvidenceArchiveService.Namespace(row.SessionId),
                    SourceType = EvidenceArchiveService.SourceType,
                    SourceId = row.Id.ToString("N"),
                    SourceRevision = "1",
                    Content = row.Title + "\n\n" + row.Content,
                    Metadata = new Dictionary<string, string>
                    {
                        ["kind"] = row.Kind,
                        ["run_id"] = row.RunId?.ToString("N") ?? string.Empty,
                        ["task_id"] = row.TaskId?.ToString("N") ?? string.Empty
                    }
                }, cancellationToken).ConfigureAwait(false);
                row.VectorStatus = EvidenceVectorStatuses.Indexed;
                row.VectorModelId = result.ModelId;
                row.VectorNextAttemptAtUnixMs = null;
                indexed++;
            }
            catch (InvalidOperationException)
            {
                // No embedding model (or an inconsistent one): nothing in this batch can be indexed now.
                var retryAt = now + (long)TimeSpan.FromMinutes(Math.Max(1, _options.UnavailableRetryMinutes)).TotalMilliseconds;
                foreach (var rest in due.Skip(index))
                {
                    rest.VectorStatus = EvidenceVectorStatuses.Unavailable;
                    rest.VectorNextAttemptAtUnixMs = retryAt;
                }
                break;
            }
            catch (NotSupportedException)
            {
                // A storage provider without a vector implementation: this entry stays keyword-only.
                row.VectorStatus = EvidenceVectorStatuses.NotApplicable;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                row.VectorAttempts++;
                row.VectorStatus = EvidenceVectorStatuses.Failed;
                row.VectorNextAttemptAtUnixMs = row.VectorAttempts >= MaxAttempts
                    ? long.MaxValue
                    : now + (long)TimeSpan.FromMinutes(Math.Min(60, 1 << row.VectorAttempts)).TotalMilliseconds;
                _logger?.TryLogDebug(ex, "Evidence entry {EvidenceId} could not be indexed (attempt {Attempt}).", row.Id, row.VectorAttempts);
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return indexed;
    }
}
