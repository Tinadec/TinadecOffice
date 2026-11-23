using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.DmaEA;

/// <summary>
/// B9: assembles one run's dossier from the records the run already left behind — its lifecycle row,
/// its replayed task outcomes, the plan ledger's intents, and the exploration graph's settled
/// findings. Read-only by construction: nothing here writes, re-plans, or re-runs anything, and
/// every section is bounded so one long run cannot turn a lookup into a context flood.
/// </summary>
/// <remarks>
/// The three optional ports are deliberate. A host without an exploration graph or a plan ledger
/// still gets a usable dossier for the sections it does have, and the response says which sources
/// were absent instead of pretending an unreadable section was empty — "no findings were recorded"
/// and "there is no graph on this host" are different facts about a run.
/// </remarks>
public sealed class RunDossierService : IRunDossier
{
    /// <summary>Hard ceiling on rows per section. A caller may ask for fewer, never more.</summary>
    public const int MaxSectionItems = CoreVirtualToolPolicy.RunDossierSectionLimit;

    /// <summary>Hard ceiling on any single free-text field (summaries, notes, finding bodies).</summary>
    public const int MaxContentChars = 600;

    private const int MaxOpenQuestions = 20;
    private const int MaxEvidenceRefs = 8;
    private const int MaxAuthorChars = 120;
    private const string Elision = "…";

    private readonly ILifecycleManager _lifecycle;
    private readonly IRunReplayService? _replay;
    private readonly IPlanLedger? _plan;
    private readonly IExplorationGraph? _exploration;
    private readonly ILogger<RunDossierService>? _logger;
    private readonly IRunContextDiagnosticsService _contextDiagnostics;

    public RunDossierService(
        ILifecycleManager lifecycle,
        IRunReplayService? replay = null,
        IPlanLedger? plan = null,
        IExplorationGraph? exploration = null,
        ILogger<RunDossierService>? logger = null,
        IRunContextDiagnosticsService? contextDiagnostics = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _replay = replay;
        _plan = plan;
        _exploration = exploration;
        _logger = logger;
        _contextDiagnostics = contextDiagnostics ?? new RunContextDiagnosticsService(_lifecycle, _replay);
    }

    public async Task<RunDossier?> BuildAsync(
        RunDossierQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, MaxSectionItems);
        var run = await ReadRunAsync(query, cancellationToken).ConfigureAwait(false);
        if (run is null) return null;

        var notes = new List<string>();
        var truncated = false;

        var (tasks, taskCount, tasksTruncated) = await ReadTasksAsync(query.RunId, limit, notes, cancellationToken)
            .ConfigureAwait(false);
        truncated |= tasksTruncated;

        var (intents, intentCount, intentsTruncated) = await ReadIntentsAsync(query, limit, notes, cancellationToken)
            .ConfigureAwait(false);
        truncated |= intentsTruncated;

        var (findings, hints, findingCount, findingsTruncated) = await ReadFindingsAsync(query, limit, notes, cancellationToken)
            .ConfigureAwait(false);
        truncated |= findingsTruncated;

        var questions = BuildOpenQuestions(intents, tasks, hints);
        if (intentsTruncated || tasksTruncated)
            notes.Add("Open questions are derived from the intents and tasks listed above; rows beyond the limit are not represented.");
        truncated |= questions.Count > MaxOpenQuestions;
        if (questions.Count > MaxOpenQuestions)
            notes.Add($"Only the first {MaxOpenQuestions} of {questions.Count} open questions are listed.");

        var evidenceRefs = findings
            .SelectMany(finding => finding.EvidenceRefs)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var contextHealth = await ReadContextHealthAsync(query, notes, cancellationToken).ConfigureAwait(false);

        return new RunDossier(
            run,
            tasks,
            intents,
            findings,
            questions.Take(MaxOpenQuestions).ToArray(),
            notes,
            new RunDossierTotals(
                taskCount,
                intentCount,
                findingCount,
                questions.Count,
                evidenceRefs,
                truncated),
            contextHealth);
    }

    private async Task<ContextHealthSummaryDto?> ReadContextHealthAsync(
        RunDossierQuery query,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        try
        {
            var diag = await _contextDiagnostics.DiagnoseRunAsync(
                query.RunId,
                query.TenantId,
                query.WorkspaceId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (diag is null)
            {
                return null;
            }

            return new ContextHealthSummaryDto
            {
                PeakOccupancyPct = diag.WindowBuckets.PeakOccupancyPct,
                DegradationDetected = diag.WindowBuckets.DegradationDetected,
                DroppedTokens = diag.Compaction.TotalDroppedTokensAcrossPacks,
                PackCount = diag.Compaction.PackCount,
                CompactionCount = diag.Compaction.Packs.Count(p => p.HasCompactionDigest),
                MaxSameArgsRetryStreak = diag.Governance.MaxSameArgsRetryStreak,
                ActiveSignals = diag.Governance.ActiveSignals.Take(UnifiedGovernanceSignalCodes.Ordered.Count).ToArray(),
                SignalCounts = diag.Governance.SignalCounts
            };
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _logger?.LogDebug(failure, "run_dossier: context diagnostics read failed for run {RunId}", query.RunId);
            notes.Add("Context health diagnostics could not be assembled for this run.");
            return null;
        }
    }

    /// <summary>
    /// Reads the lifecycle row and refuses it unless it belongs to the caller's tenant, workspace and
    /// session. The check is also the existence check: <see cref="ILifecycleManager.GetRunStateAsync"/>
    /// synthesizes a default <see cref="RunState"/> for an unknown id rather than returning null, and
    /// that synthesized row carries no tenant/workspace/session — so "not found" and "not yours" fold
    /// into the same answer without either one pretending the other happened.
    /// </summary>
    private async Task<RunDossierRunFacts?> ReadRunAsync(
        RunDossierQuery query,
        CancellationToken cancellationToken)
    {
        var state = await _lifecycle.GetRunStateAsync(query.RunId.ToString(), cancellationToken).ConfigureAwait(false);
        if (state is null) return null;
        if (!Guid.TryParse(state.TenantId, out var tenant) || tenant != query.TenantId) return null;
        if (!Guid.TryParse(state.WorkspaceId, out var workspace) || workspace != query.WorkspaceId) return null;
        if (!Guid.TryParse(state.SessionId, out var session) || session != query.SessionId) return null;

        return new RunDossierRunFacts(
            state.RunId,
            state.Status.ToString().ToLowerInvariant(),
            state.RunKind,
            state.PermissionMode,
            state.StartedAt,
            state.CompletedAt,
            Clip(state.Summary),
            state.TerminalErrorCategory);
    }

    private async Task<(IReadOnlyList<RunDossierTask> Tasks, int Total, bool Truncated)> ReadTasksAsync(
        Guid runId,
        int limit,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (_replay is null)
        {
            notes.Add("Task replay is not configured on this host; task outcomes are absent.");
            return ([], 0, false);
        }

        RunReplay? replay;
        try
        {
            replay = await _replay.BuildReplayAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _logger?.LogDebug(failure, "run_dossier: replay read failed for run {RunId}", runId);
            notes.Add("The event journal could not be replayed for this run; task outcomes are absent.");
            return ([], 0, false);
        }

        if (replay is null)
        {
            notes.Add("The event journal has no replay for this run; task outcomes are absent.");
            return ([], 0, false);
        }

        var ordered = replay.Tasks;
        var projected = ordered
            .Take(limit)
            .Select(task => new RunDossierTask(
                task.TaskKey,
                string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey,
                task.Status,
                Clip(task.Summary) ?? string.Empty,
                task.Evidence.Take(MaxEvidenceRefs).ToArray()))
            .ToArray();

        return (projected, ordered.Count, ordered.Count > limit);
    }

    private async Task<(IReadOnlyList<RunDossierIntent> Intents, int Total, bool Truncated)> ReadIntentsAsync(
        RunDossierQuery query,
        int limit,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (_plan is null)
        {
            notes.Add("The plan ledger is not configured on this host; planned intents are absent.");
            return ([], 0, false);
        }

        IReadOnlyList<PlanIntent> rows;
        try
        {
            var scope = new PlanLedgerScope(query.TenantId, query.WorkspaceId, query.SessionId, query.RunId);
            rows = await _plan.ListAsync(scope, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _logger?.LogDebug(failure, "run_dossier: plan ledger read failed for run {RunId}", query.RunId);
            notes.Add("The plan ledger could not be read for this run; planned intents are absent.");
            return ([], 0, false);
        }

        var ordered = rows.OrderBy(intent => intent.Ordinal).ToArray();
        var projected = ordered
            .Take(limit)
            .Select(intent => new RunDossierIntent(
                intent.Ordinal,
                intent.Title,
                intent.Status,
                Clip(intent.Note),
                intent.EvidenceRefs.Take(MaxEvidenceRefs).ToArray()))
            .ToArray();

        return (projected, ordered.Length, ordered.Length > limit);
    }

    /// <summary>
    /// Three bounded reads — one per cited kind — instead of scanning the whole graph, and the hints
    /// are kept apart because a hint is what the run believed but never grounded. Mixing them into
    /// the same ordered list would let belief read like evidence.
    /// </summary>
    private async Task<(
        IReadOnlyList<RunDossierFinding> Findings,
        IReadOnlyList<ExplorationNode> Hints,
        int Total,
        bool Truncated)>
        ReadFindingsAsync(
            RunDossierQuery query,
            int limit,
            List<string> notes,
            CancellationToken cancellationToken)
    {
        if (_exploration is null)
        {
            notes.Add("The exploration graph is not configured on this host; findings and hints are absent.");
            return ([], [], 0, false);
        }

        var scope = new ExplorationScope(query.TenantId, query.WorkspaceId, query.SessionId, query.RunId);
        var fetch = limit + 1;
        var cited = new List<ExplorationNode>();
        IReadOnlyList<ExplorationNode> hints;

        try
        {
            foreach (var kind in new[] { ExplorationNodeKinds.Fact, ExplorationNodeKinds.Finding })
            {
                var rows = await _exploration
                    .QueryAsync(scope, new ExplorationQuery(kind, query.RunId, Limit: fetch), cancellationToken)
                    .ConfigureAwait(false);
                cited.AddRange(rows);
            }

            hints = await _exploration
                .QueryAsync(scope, new ExplorationQuery(ExplorationNodeKinds.Hint, query.RunId, Limit: fetch), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _logger?.LogDebug(failure, "run_dossier: exploration read failed for run {RunId}", query.RunId);
            notes.Add("The exploration graph could not be read for this run; findings and hints are absent.");
            return ([], [], 0, false);
        }

        // Facts and findings are the grounded half: newest first, because a run's later conclusions
        // usually supersede its earlier ones. Hints follow, and are never presented as grounded.
        var ordered = cited
            .OrderByDescending(node => node.CreatedAt)
            .Concat(hints.Take(fetch))
            .ToArray();

        var projected = ordered
            .Take(limit)
            .Select(node => new RunDossierFinding(
                node.Kind,
                node.Title,
                Clip(node.Content),
                node.EvidenceRefs.Take(MaxEvidenceRefs).ToArray(),
                Clip(node.Author, MaxAuthorChars)))
            .ToArray();

        return (projected, hints, ordered.Length, ordered.Length > limit || hints.Count >= fetch);
    }

    /// <summary>
    /// What the run left unresolved, read off its own records: intents that were blocked, tasks that
    /// never reached completion, and hints nothing cited. It is a projection of durable facts, not an
    /// opinion about the work — a dossier that invented questions would be worse than no dossier.
    /// </summary>
    private static List<string> BuildOpenQuestions(
        IReadOnlyList<RunDossierIntent> intents,
        IReadOnlyList<RunDossierTask> tasks,
        IReadOnlyList<ExplorationNode> hints)
    {
        var questions = new List<string>();

        foreach (var intent in intents.Where(intent => intent.Status == PlanIntentStatuses.Blocked))
        {
            questions.Add(intent.Note is { Length: > 0 } note
                ? $"blocked intent: {intent.Title} — {Clip(note, 200)}"
                : $"blocked intent: {intent.Title} (no reason recorded)");
        }

        foreach (var intent in intents.Where(intent =>
                     intent.Status is PlanIntentStatuses.Pending or PlanIntentStatuses.Running))
        {
            questions.Add($"{intent.Status} intent: {intent.Title}");
        }

        foreach (var task in tasks.Where(task => !string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase)))
        {
            questions.Add($"{task.Status} task: {task.TaskKey}");
        }

        foreach (var hint in hints)
            questions.Add($"unverified: {hint.Title}");

        return questions;
    }

    private static string? Clip(string? value, int limit = MaxContentChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + Elision;
    }
}
