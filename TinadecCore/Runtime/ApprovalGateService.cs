using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentGraph;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Governance;
using TinadecCore.Lifecycle;

namespace TinadecCore.Runtime;

public sealed class ApprovalGateOptions
{
    public const string SectionName = "TinadecApprovalGates";

    /// <summary>False leaves delegated approvals pending for the person (the gates are never asked).</summary>
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 2;
    /// <summary>Approvals advanced per pass (one gate each).</summary>
    public int BatchSize { get; set; } = 8;
    /// <summary>Gates asked at once across all runs: the model calls a pass may have in flight.</summary>
    public int Parallelism { get; set; } = 4;
    /// <summary>Gate decisions one run may consume; past it every approval of the run goes to the person.</summary>
    public int MaxGateDecisionsPerRun { get; set; } = 200;
    /// <summary>A gate claimed longer ago than this is taken again: the host that claimed it died mid-call.</summary>
    public int ClaimLapseSeconds { get; set; } = 300;
}

/// <summary>
/// Decides the approvals a user delegated (the <c>delegate-*</c> permission modes, architecture §7.2,
/// todo R1/R2). A delegated run's mutating call is released by the PDP to the approval layer and parks
/// there exactly as in <c>ask</c>; this service then asks the mode's gates in order — one gate per
/// approval per pass — and applies the outcome through the same transition a person's click makes:
/// <list type="bullet">
/// <item>every gate approved → the approval is approved (and consumed once, against its request hash, when the call runs);</item>
/// <item>any gate rejects → rejected, and the later gates are never asked;</item>
/// <item>any gate escalates (unsure, no route, a malformed answer, its own call) → the approval stays
/// pending for the person, who can always decide it; a gate never approves by default.</item>
/// </list>
/// Human-only tools and risks above the delegable ceiling are never decided here even if they reached
/// the approval layer another way. The durable state is the gate rows plus the approval row, so a host
/// that stops mid-pass resumes where it was; a claimed gate whose host died is taken again after the lapse.
/// </summary>
public sealed class ApprovalGateService : BackgroundService, IApprovalGateLedger
{
    private const int CandidateWindow = 512;
    private readonly IServiceProvider _services;
    private readonly IDbContextFactory<LifecycleDbContext> _lifecycleDb;
    private readonly IDbContextFactory<AgentGraphDbContext> _gatesDb;
    private readonly ITenantContextAccessor _tenant;
    private readonly ILogger<ApprovalGateService> _logger;
    private readonly ApprovalGateOptions _options;
    private readonly AutoApproveOptions _approval;

    public ApprovalGateService(
        IServiceProvider services,
        IDbContextFactory<LifecycleDbContext> lifecycleDb,
        IDbContextFactory<AgentGraphDbContext> gatesDb,
        ITenantContextAccessor tenant,
        ILogger<ApprovalGateService> logger,
        IOptions<ApprovalGateOptions>? options = null,
        IOptions<AutoApproveOptions>? approval = null)
    {
        _services = services;
        _lifecycleDb = lifecycleDb;
        _gatesDb = gatesDb;
        _tenant = tenant;
        _logger = logger;
        _options = options?.Value ?? new ApprovalGateOptions();
        _approval = approval?.Value ?? new AutoApproveOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.TryLogInformation("Delegated approval gates are disabled; delegated approvals wait for the person.");
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.IntervalSeconds, 1, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.TryLogWarning(ex, "Delegated approval gate pass failed.");
            }
            try
            {
                // A pass that moved something goes again almost at once: the second gate of a two-gate
                // approval should not wait a whole poll behind the first.
                await Task.Delay(handled > 0 ? TimeSpan.FromMilliseconds(50) : interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Public for deterministic tests: one pass, at most one gate per approval, failures contained per approval.</summary>
    public async Task<int> RunPassAsync(CancellationToken ct = default)
    {
        var candidates = await CandidatesAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;
        var handled = 0;
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_options.Parallelism, 1, 16), CancellationToken = ct },
            async (candidate, token) =>
            {
                try
                {
                    if (await AdvanceAsync(candidate, token).ConfigureAwait(false)) Interlocked.Increment(ref handled);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    _logger.TryLogWarning(ex, "Delegated approval gate failed for approval {ApprovalId}.", candidate.ApprovalId);
                }
            }).ConfigureAwait(false);
        return handled;
    }

    private sealed record Candidate(
        Guid ApprovalId,
        Guid RunId,
        Guid SessionId,
        Guid ExecutionId,
        Guid TaskId,
        string ToolId,
        string Risk,
        string PermissionMode,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// Pending tool approvals of live delegated runs whose gates can move: never opened, a gate still
    /// pending (or claimed by a host that died), or every gate approved but the decision not yet applied.
    /// An approval a gate refused or escalated is the person's now and is left alone.
    /// </summary>
    private async Task<IReadOnlyList<Candidate>> CandidatesAsync(CancellationToken ct)
    {
        var scope = _tenant.Current;
        var modes = ApprovalDelegationModes.All.ToArray();
        var target = Math.Clamp(_options.BatchSize, 1, 64);
        var ready = new List<Candidate>(target);
        Guid? afterApprovalId = null;
        var lapse = LapseBefore();

        // Gate state lives in the AgentGraph context, so terminal/escalated gates cannot be filtered
        // in the lifecycle SQL query. Page by a stable approval id instead of taking the first 512
        // rows forever; a large prefix of already-escalated approvals must not starve newer work.
        while (ready.Count < target)
        {
            List<Candidate> rows;
            await using (var db = await _lifecycleDb.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                rows = await (
                    from approval in db.ApprovalRequests.AsNoTracking()
                    join execution in db.ToolExecutions.AsNoTracking() on (Guid?)approval.Id equals execution.ApprovalId
                    join run in db.Runs.AsNoTracking() on execution.RunId equals run.Id
                    where approval.TenantId == scope.TenantId && approval.WorkspaceId == scope.WorkspaceId
                        && approval.Status == "pending" && approval.Kind == "tool" && approval.UserToolActionId == null
                        && modes.Contains(run.PermissionMode)
                        && run.Status != "completed" && run.Status != "failed" && run.Status != "cancelled"
                        && (afterApprovalId == null || approval.Id > afterApprovalId.Value)
                    // Order entity columns before constructing a record. EF cannot bind a member
                    // access on the positional Candidate constructor back to its SQL projection.
                    orderby approval.Id
                    select new Candidate(approval.Id, run.Id, run.SessionId, execution.Id, execution.TaskId, execution.ToolId,
                        execution.Risk, run.PermissionMode, approval.CreatedAt))
                    .Take(CandidateWindow)
                    .ToListAsync(ct).ConfigureAwait(false);
            }
            if (rows.Count == 0) break;
            afterApprovalId = rows[^1].ApprovalId;

            var ids = rows.Select(row => row.ApprovalId).ToArray();
            await using var gates = await _gatesDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            var states = await gates.ApprovalGates.AsNoTracking()
                .Where(gate => ids.Contains(gate.PermissionRequestId))
                .Select(gate => new { gate.PermissionRequestId, gate.Status, gate.ClaimedAtUnixMs })
                .ToListAsync(ct).ConfigureAwait(false);
            var byApproval = states.GroupBy(state => state.PermissionRequestId).ToDictionary(group => group.Key, group => group.ToList());
            ready.AddRange(rows.Where(row =>
            {
                if (!byApproval.TryGetValue(row.ApprovalId, out var own)) return true;
                if (own.Any(state => state.Status is not (ApprovalGateStatuses.Pending or ApprovalGateStatuses.Evaluating or ApprovalGateStatuses.Approved)))
                    return false;
                return own.All(state => state.Status == ApprovalGateStatuses.Approved)
                    || own.Any(state => state.Status == ApprovalGateStatuses.Pending)
                    || own.Any(state => state.Status == ApprovalGateStatuses.Evaluating && (state.ClaimedAtUnixMs ?? 0) < lapse);
            }));

            if (rows.Count < CandidateWindow) break;
        }

        return ready.OrderBy(row => row.CreatedAt).Take(target).ToArray();
    }

    private async Task<bool> AdvanceAsync(Candidate candidate, CancellationToken ct)
    {
        var kinds = ApprovalDelegationModes.GatesFor(candidate.PermissionMode);
        if (kinds.Count == 0) return false;
        await using var db = await _gatesDb.CreateDbContextAsync(ct).ConfigureAwait(false);
        var gates = await GatesOfAsync(db, candidate.ApprovalId, ct).ConfigureAwait(false);
        if (gates.Count == 0) gates = await OpenAsync(db, candidate, kinds, ct).ConfigureAwait(false);
        if (gates.Count == 0) return false;

        // Defense in depth: the PDP never releases these to a delegated run, but a call can reach the
        // approval layer by another path (an older grant), and a gate must still not be the one to decide it.
        if (!DelegatedApprovalRules.Delegable(_approval, candidate.ToolId, candidate.Risk))
        {
            // Shell (human-only by default) reaches a delegated gate only when this run's session
            // opted it in; the opt-in is verified against the run's own session, never the call's
            // arguments, so a rule from another session is never honored.
            var optedIn = _services.GetService(typeof(Abstractions.Ports.IApprovalRules)) is Abstractions.Ports.IApprovalRules rules
                && await rules.IsDelegatedToolAsync(candidate.RunId, candidate.ToolId, ct).ConfigureAwait(false);
            if (!DelegatedApprovalRules.Delegable(_approval, candidate.ToolId, candidate.Risk, optedIn))
                return await EscalateAsync(db, candidate, gates, $"'{candidate.ToolId}' ({candidate.Risk} risk) always stays with the person.", ct).ConfigureAwait(false);
        }

        if (gates.All(gate => gate.Status == ApprovalGateStatuses.Approved))
            return await ApplyAsync(candidate, gates, "approved", ct).ConfigureAwait(false);

        var next = gates.FirstOrDefault(gate => ApprovalGateStatuses.IsOpen(gate.Status));
        if (next is null) return false;

        var budget = Math.Max(1, _options.MaxGateDecisionsPerRun);
        var spent = await db.ApprovalGates.AsNoTracking().CountAsync(gate => gate.RunId == candidate.RunId
            && (gate.Status == ApprovalGateStatuses.Approved || gate.Status == ApprovalGateStatuses.Rejected || gate.Status == ApprovalGateStatuses.Escalated), ct).ConfigureAwait(false);
        if (spent >= budget)
            return await EscalateAsync(db, candidate, gates, $"This run has used its {budget} delegated decisions; the person decides the rest.", ct).ConfigureAwait(false);

        var claim = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lapse = LapseBefore();
        var claimed = await db.ApprovalGates
            .Where(gate => gate.Id == next.Id && (gate.Status == ApprovalGateStatuses.Pending
                || (gate.Status == ApprovalGateStatuses.Evaluating && (gate.ClaimedAtUnixMs == null || gate.ClaimedAtUnixMs < lapse))))
            .ExecuteUpdateAsync(set => set
                .SetProperty(gate => gate.Status, ApprovalGateStatuses.Evaluating)
                .SetProperty(gate => gate.ClaimedAtUnixMs, claim)
                .SetProperty(gate => gate.Revision, gate => gate.Revision + 1), ct).ConfigureAwait(false);
        if (claimed != 1) return false;

        var judge = _services.GetService<IApprovalGateJudge>();
        var verdict = judge is null
            ? new ApprovalGateVerdict("escalate", "No approval gate judge is available in this host; the person decides.", "{}", null)
            : await judge.JudgeAsync(new ApprovalGateRequest(candidate.RunId, candidate.SessionId, candidate.ApprovalId, candidate.ExecutionId,
                next.GateKind, candidate.PermissionMode), ct).ConfigureAwait(false);
        var status = verdict.Decision switch
        {
            "approve" => ApprovalGateStatuses.Approved,
            "reject" => ApprovalGateStatuses.Rejected,
            _ => ApprovalGateStatuses.Escalated
        };
        var rationale = Clip(verdict.Rationale, 1024);
        var recorded = await db.ApprovalGates
            .Where(gate => gate.Id == next.Id && gate.Status == ApprovalGateStatuses.Evaluating && gate.ClaimedAtUnixMs == claim)
            .ExecuteUpdateAsync(set => set
                .SetProperty(gate => gate.Status, status)
                .SetProperty(gate => gate.Reason, rationale)
                .SetProperty(gate => gate.ContextEvidenceJson, string.IsNullOrWhiteSpace(verdict.EvidenceJson) ? "{}" : verdict.EvidenceJson)
                .SetProperty(gate => gate.DeciderAgent, verdict.DeciderAgent)
                .SetProperty(gate => gate.DecidedAt, DateTimeOffset.UtcNow)
                .SetProperty(gate => gate.Revision, gate => gate.Revision + 1), ct).ConfigureAwait(false);
        // Our claim lapsed and another host took the gate over: its answer is the one that counts.
        if (recorded != 1) return false;
        await AppendGateEventAsync(candidate, next, status, verdict.DeciderAgent, rationale, ct).ConfigureAwait(false);

        if (status == ApprovalGateStatuses.Approved)
        {
            // Only the last gate's approval decides; an earlier one just lets the next gate be asked.
            if (gates.Any(gate => gate.GateIndex > next.GateIndex)) return true;
            await ApplyAsync(candidate, await GatesOfAsync(db, candidate.ApprovalId, ct).ConfigureAwait(false), "approved", ct).ConfigureAwait(false);
            return true;
        }
        await SkipAfterAsync(db, candidate.ApprovalId, next.GateIndex, ct).ConfigureAwait(false);
        if (status == ApprovalGateStatuses.Rejected)
            await ApplyAsync(candidate, await GatesOfAsync(db, candidate.ApprovalId, ct).ConfigureAwait(false), "rejected", ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<List<ApprovalGateRecord>> GatesOfAsync(AgentGraphDbContext db, Guid approvalId, CancellationToken ct) =>
        (await db.ApprovalGates.AsNoTracking().Where(gate => gate.PermissionRequestId == approvalId).ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(gate => gate.GateIndex).ToList();

    /// <summary>Opens the mode's gates for an approval, once: a concurrent host that opened them first wins.</summary>
    private async Task<List<ApprovalGateRecord>> OpenAsync(AgentGraphDbContext db, Candidate candidate, IReadOnlyList<string> kinds, CancellationToken ct)
    {
        var scope = _tenant.Current;
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < kinds.Count; index++)
        {
            db.ApprovalGates.Add(new ApprovalGateRecord
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                PermissionRequestId = candidate.ApprovalId,
                RunId = candidate.RunId,
                GateIndex = index,
                GateKind = kinds[index],
                ToolId = candidate.ToolId,
                Status = ApprovalGateStatuses.Pending,
                Revision = 1,
                CreatedAt = now
            });
        }
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
        return await GatesOfAsync(db, candidate.ApprovalId, ct).ConfigureAwait(false);
    }

    /// <summary>Hands the approval back to the person: the first open gate says why, the later ones are never asked.</summary>
    private async Task<bool> EscalateAsync(AgentGraphDbContext db, Candidate candidate, IReadOnlyList<ApprovalGateRecord> gates, string reason, CancellationToken ct)
    {
        var open = gates.FirstOrDefault(gate => ApprovalGateStatuses.IsOpen(gate.Status));
        if (open is null) return false;
        var changed = await db.ApprovalGates
            .Where(gate => gate.Id == open.Id && gate.Status == open.Status && gate.Revision == open.Revision)
            .ExecuteUpdateAsync(set => set
                .SetProperty(gate => gate.Status, ApprovalGateStatuses.Escalated)
                .SetProperty(gate => gate.Reason, reason)
                .SetProperty(gate => gate.DecidedAt, DateTimeOffset.UtcNow)
                .SetProperty(gate => gate.Revision, gate => gate.Revision + 1), ct).ConfigureAwait(false);
        if (changed != 1) return false;
        await SkipAfterAsync(db, candidate.ApprovalId, open.GateIndex, ct).ConfigureAwait(false);
        await AppendGateEventAsync(candidate, open, ApprovalGateStatuses.Escalated, null, reason, ct).ConfigureAwait(false);
        return true;
    }

    private static Task<int> SkipAfterAsync(AgentGraphDbContext db, Guid approvalId, int gateIndex, CancellationToken ct) =>
        db.ApprovalGates
            .Where(gate => gate.PermissionRequestId == approvalId && gate.GateIndex > gateIndex && gate.Status == ApprovalGateStatuses.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(gate => gate.Status, ApprovalGateStatuses.Skipped)
                .SetProperty(gate => gate.Revision, gate => gate.Revision + 1), ct);

    /// <summary>
    /// Applies the gates' outcome through the same transition a person's click makes (decision row,
    /// <c>approval.decided</c>, run wake-up). The person may have decided first — then nothing of ours applies.
    /// </summary>
    private async Task<bool> ApplyAsync(Candidate candidate, IReadOnlyList<ApprovalGateRecord> gates, string decision, CancellationToken ct)
    {
        var approvals = _services.GetRequiredService<IToolApprovalCoordinator>();
        var deciders = string.Join(", ", gates.Where(gate => gate.Status is ApprovalGateStatuses.Approved or ApprovalGateStatuses.Rejected)
            .Select(gate => $"{gate.GateKind}:{gate.Status}"));
        ToolApprovalDecision decided;
        try
        {
            decided = await approvals.DecideDelegatedAsync(candidate.ApprovalId, decision, $"delegated_gates ({deciders})", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            await using var db = await _gatesDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.ApprovalGates
                .Where(gate => gate.PermissionRequestId == candidate.ApprovalId
                    && (gate.Status == ApprovalGateStatuses.Pending || gate.Status == ApprovalGateStatuses.Evaluating))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(gate => gate.Status, ApprovalGateStatuses.Superseded)
                    .SetProperty(gate => gate.Revision, gate => gate.Revision + 1), ct).ConfigureAwait(false);
            return false;
        }
        if (_services.GetService<ILifecycleManager>() is { } lifecycle)
        {
            await lifecycle.AppendEventAsync(candidate.RunId, "approval.decided", new
            {
                approval_id = candidate.ApprovalId,
                execution_id = decided.ExecutionId,
                task_id = decided.TaskId,
                decision = decided.Status,
                decided_by = "delegated_gates",
                permission_mode = candidate.PermissionMode
            }, $"Approval {decided.Status} by the delegated gates ({candidate.PermissionMode}).",
                decided.Status == "approved" ? "info" : "warning", decided.TaskId, candidate.ApprovalId, candidate.ToolId,
                cancellationToken: ct).ConfigureAwait(false);
        }
        if (_services.GetService<IFullDuplexRunEngine>() is { } engine)
            await engine.EnqueueAsync(candidate.RunId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task AppendGateEventAsync(Candidate candidate, ApprovalGateRecord gate, string status, string? decider, string? reason, CancellationToken ct)
    {
        if (_services.GetService<ILifecycleManager>() is not { } lifecycle) return;
        try
        {
            await lifecycle.AppendEventAsync(candidate.RunId, "approval.gate_decided", new
            {
                approval_id = candidate.ApprovalId,
                execution_id = candidate.ExecutionId,
                gate_index = gate.GateIndex,
                gate_kind = gate.GateKind,
                decision = status,
                decider,
                rationale = reason,
                permission_mode = candidate.PermissionMode
            }, status switch
            {
                ApprovalGateStatuses.Approved => $"Gate {gate.GateKind} approved '{candidate.ToolId}'.",
                ApprovalGateStatuses.Rejected => $"Gate {gate.GateKind} rejected '{candidate.ToolId}'.",
                _ => $"Gate {gate.GateKind} handed '{candidate.ToolId}' back to you: {reason}"
            }, status == ApprovalGateStatuses.Approved ? "info" : "warning", candidate.TaskId, candidate.ApprovalId, candidate.ToolId,
                cancellationToken: ct, idempotencyKey: $"approval-gate:{gate.Id:N}:{status}").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.TryLogDebug(ex, "Could not record gate {Gate} of approval {ApprovalId}.", gate.GateKind, candidate.ApprovalId);
        }
    }

    public async Task<ApprovalGatesDto?> GetAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        await using var db = await _gatesDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var gates = (await db.ApprovalGates.AsNoTracking()
                .Where(gate => gate.PermissionRequestId == approvalId && gate.TenantId == scope.TenantId && gate.WorkspaceId == scope.WorkspaceId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(gate => gate.GateIndex).ToList();
        if (gates.Count == 0) return null;
        await using var lifecycle = await _lifecycleDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var approval = await lifecycle.ApprovalRequests.AsNoTracking()
            .Where(row => row.Id == approvalId && row.TenantId == scope.TenantId && row.WorkspaceId == scope.WorkspaceId)
            .Select(row => new { row.Status, row.Decision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var stillPending = approval?.Status == "pending";
        // Resolved at read time against the approval itself, so a decision the person made while a gate
        // was still thinking is shown as what happened rather than as the gate's stale answer.
        var outcome = gates.Any(gate => gate.Status == ApprovalGateStatuses.Rejected) ? "rejected"
            : gates.Any(gate => gate.Status == ApprovalGateStatuses.Escalated) ? "escalated"
            : gates.Any(gate => gate.Status == ApprovalGateStatuses.Superseded) ? "superseded"
            : gates.All(gate => gate.Status == ApprovalGateStatuses.Approved) ? "approved"
            : "evaluating";
        var status = outcome switch
        {
            "evaluating" when !stillPending => "superseded",
            "approved" when approval?.Decision == "rejected" => "superseded",
            "rejected" when approval?.Decision == "approved" => "superseded",
            _ => outcome
        };
        return new ApprovalGatesDto(approvalId, gates[0].RunId, status, gates.Select(gate => new ApprovalGateDto(
            gate.GateIndex,
            gate.GateKind,
            !stillPending && ApprovalGateStatuses.IsOpen(gate.Status) ? ApprovalGateStatuses.Superseded : gate.Status,
            gate.DeciderAgent,
            gate.Reason,
            Evidence(gate.ContextEvidenceJson),
            gate.CreatedAt,
            gate.DecidedAt)).ToArray());
    }

    private long LapseBefore() =>
        DateTimeOffset.UtcNow.AddSeconds(-Math.Max(30, _options.ClaimLapseSeconds)).ToUnixTimeMilliseconds();

    private static JsonElement? Evidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
