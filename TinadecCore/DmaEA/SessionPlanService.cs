using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.DmaEA;

public sealed class SessionPlanService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, byte> EnsuredConnections = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    private readonly IDbContextFactory<SessionPlanDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenant;
    private readonly ISessionLocator _sessions;
    private readonly ILifecycleManager? _lifecycle;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionGates = new();

    public SessionPlanService(
        IDbContextFactory<SessionPlanDbContext> dbFactory,
        ITenantContextAccessor tenant,
        ISessionLocator sessions,
        ILifecycleManager? lifecycle = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _sessions = sessions;
        _lifecycle = lifecycle;
    }

    public async Task<SessionPlanDto> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var (scope, _) = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

        var row = await db.SessionPlans.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.SessionId == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

        return row is null ? EmptyPlan(sessionId) : ToDto(row);
    }

    public async Task<SessionPlanDto> PutAsync(
        Guid sessionId,
        SessionPlanPutRequestDto input,
        long? expectedRevision = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (scope, _) = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var effectiveExpected = expectedRevision ?? input.ExpectedRevision;

        var normalizedPhases = NormalizeAndValidatePhases(input.Phases);
        var explicitStatus = NormalizePlanStatus(input.Status);

        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

            var row = await db.SessionPlans.SingleOrDefaultAsync(
                x => x.SessionId == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

            if (effectiveExpected is { } expected)
            {
                var currentRev = row?.Revision ?? 0L;
                if (currentRev != expected)
                    throw new SessionPlanException(412, "revision_conflict", $"Session plan revision conflict: expected {expected}, actual {currentRev}.");
            }

            var now = DateTimeOffset.UtcNow;
            if (row is null)
            {
                row = new SessionPlanRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    SessionId = sessionId,
                    CreatedAt = now
                };
                db.SessionPlans.Add(row);
            }

            row.Title = string.IsNullOrWhiteSpace(input.Title)
                ? (string.IsNullOrWhiteSpace(row.Title) ? "Session Plan" : row.Title)
                : Truncate(input.Title.Trim(), 256);
            if (input.Goal is not null)
                row.Goal = string.IsNullOrWhiteSpace(input.Goal) ? null : Truncate(input.Goal.Trim(), 2000);
            if (runId is { } rid && rid != Guid.Empty)
                row.LastRunId = rid;
            if (explicitStatus == SessionPlanStatuses.Approved && row.ApprovedAt is null)
            {
                row.ApprovedAt = now;
                row.ApprovedByPrincipalId = scope.PrincipalId;
            }

            var recomputedPhases = RecomputePhases(normalizedPhases);
            row.Status = ComputePlanStatus(recomputedPhases, row.ApprovedAt is not null, explicitStatus);
            row.PlanJson = SerializePhases(recomputedPhases);
            row.Revision++;
            row.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var dto = ToDto(row, recomputedPhases);
            await EmitPlanUpdatedAsync(sessionId, runId ?? row.LastRunId, dto, "Session plan updated.", cancellationToken).ConfigureAwait(false);
            return dto;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SessionPlanDto> PatchAsync(
        Guid sessionId,
        SessionPlanPatchRequestDto input,
        long? expectedRevision = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (scope, _) = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var effectiveExpected = expectedRevision ?? input.ExpectedRevision;
        var explicitStatus = NormalizePlanStatus(input.Status);

        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

            var row = await db.SessionPlans.SingleOrDefaultAsync(
                x => x.SessionId == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

            if (effectiveExpected is { } expected)
            {
                var currentRev = row?.Revision ?? 0L;
                if (currentRev != expected)
                    throw new SessionPlanException(412, "revision_conflict", $"Session plan revision conflict: expected {expected}, actual {currentRev}.");
            }

            var now = DateTimeOffset.UtcNow;
            if (row is null)
            {
                row = new SessionPlanRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    SessionId = sessionId,
                    Title = "Session Plan",
                    CreatedAt = now
                };
                db.SessionPlans.Add(row);
            }

            if (!string.IsNullOrWhiteSpace(input.Title))
                row.Title = Truncate(input.Title.Trim(), 256);
            if (input.Goal is not null)
                row.Goal = string.IsNullOrWhiteSpace(input.Goal) ? null : Truncate(input.Goal.Trim(), 2000);
            if (runId is { } rid && rid != Guid.Empty)
                row.LastRunId = rid;
            if (explicitStatus == SessionPlanStatuses.Approved && row.ApprovedAt is null)
            {
                row.ApprovedAt = now;
                row.ApprovedByPrincipalId = scope.PrincipalId;
            }

            var phases = input.Phases is not null
                ? input.Phases.ToList()
                : DeserializePhases(row.PlanJson);

            if (input.StepUpdates is { Count: > 0 } updates)
            {
                phases = ApplyStepUpdates(phases, updates, now);
            }

            if (input.AddSteps is { Count: > 0 } additions)
            {
                phases = ApplyStepAdditions(phases, additions, now);
            }

            var validated = NormalizeAndValidatePhases(phases);
            var recomputed = RecomputePhases(validated);
            row.Status = ComputePlanStatus(recomputed, row.ApprovedAt is not null, explicitStatus);
            row.PlanJson = SerializePhases(recomputed);
            row.Revision++;
            row.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var dto = ToDto(row, recomputed);
            await EmitPlanUpdatedAsync(sessionId, runId ?? row.LastRunId, dto, "Session plan patched.", cancellationToken).ConfigureAwait(false);
            return dto;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SessionPlanDto> ApproveAsync(
        Guid sessionId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        var (scope, _) = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

            var row = await db.SessionPlans.SingleOrDefaultAsync(
                x => x.SessionId == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

            if (row is null)
                throw new SessionPlanException(404, "plan_not_found", $"Session '{sessionId}' does not have a plan to approve yet.");

            if (expectedRevision is { } expected && row.Revision != expected)
                throw new SessionPlanException(412, "revision_conflict", $"Session plan revision conflict: expected {expected}, actual {row.Revision}.");

            var phases = RecomputePhases(DeserializePhases(row.PlanJson));
            var now = DateTimeOffset.UtcNow;
            row.ApprovedAt ??= now;
            row.ApprovedByPrincipalId ??= scope.PrincipalId;
            if (row.Status == SessionPlanStatuses.Draft)
                row.Status = ComputePlanStatus(phases, approved: true, explicitStatus: SessionPlanStatuses.Approved);
            row.Revision++;
            row.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var dto = ToDto(row, phases);
            await EmitPlanUpdatedAsync(sessionId, row.LastRunId, dto, "Session plan approved.", cancellationToken).ConfigureAwait(false);
            return dto;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SessionPlanDto?> SyncFromPlannedTasksAsync(
        Guid sessionId,
        Guid runId,
        IReadOnlyList<DurableTaskNode> tasks,
        string? goalSummary = null,
        CancellationToken cancellationToken = default)
    {
        var syncable = tasks.Where(t => !t.IsSpacePreparation).ToArray();
        if (syncable.Length == 0) return null;

        var scope = _tenant.Current;
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var row = await db.SessionPlans.SingleOrDefaultAsync(
                x => x.SessionId == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

            if (row is null)
            {
                row = new SessionPlanRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    SessionId = sessionId,
                    Title = "Execution Plan",
                    Goal = string.IsNullOrWhiteSpace(goalSummary) ? null : Truncate(goalSummary.Trim(), 2000),
                    Status = SessionPlanStatuses.Active,
                    CreatedAt = now
                };
                db.SessionPlans.Add(row);
            }
            else if (string.IsNullOrWhiteSpace(row.Goal) && !string.IsNullOrWhiteSpace(goalSummary))
            {
                row.Goal = Truncate(goalSummary.Trim(), 2000);
            }

            row.LastRunId = runId;
            var phases = DeserializePhases(row.PlanJson);
            var existingByTaskKey = phases
                .SelectMany(p => p.Steps)
                .Where(s => !string.IsNullOrWhiteSpace(s.BoundTaskKey))
                .ToDictionary(s => s.BoundTaskKey!, s => s, StringComparer.OrdinalIgnoreCase);

            var taskKeyToStepId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in syncable)
            {
                taskKeyToStepId[task.TaskKey] = existingByTaskKey.TryGetValue(task.TaskKey, out var existing)
                    ? existing.Id
                    : $"step-{task.TaskKey}";
            }

            var execPhaseIndex = phases.FindIndex(p => string.Equals(p.Id, "phase-execution", StringComparison.OrdinalIgnoreCase));
            var execSteps = execPhaseIndex >= 0
                ? phases[execPhaseIndex].Steps.ToList()
                : new List<SessionPlanStepDto>();

            foreach (var task in syncable)
            {
                var stepId = taskKeyToStepId[task.TaskKey];
                var deps = task.Dependencies
                    .Select(d => taskKeyToStepId.TryGetValue(d, out var depId) ? depId : $"step-{d}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var mappedStatus = SessionPlanStatuses.MapTaskStatus(task.Status);
                var assignee = task.WorkerAgentSlug ?? task.RequestedAgent;

                if (existingByTaskKey.TryGetValue(task.TaskKey, out var existing))
                {
                    var updated = existing with
                    {
                        Title = string.IsNullOrWhiteSpace(existing.Title) ? task.Title : existing.Title,
                        Description = existing.Description ?? task.Description,
                        Status = mappedStatus,
                        DependsOn = deps.Length > 0 ? deps : existing.DependsOn,
                        AssignedAgentSlug = assignee ?? existing.AssignedAgentSlug,
                        VerificationCriteria = task.SuccessCriteria.Count > 0 ? [.. task.SuccessCriteria] : existing.VerificationCriteria,
                        Summary = task.ResultSummary ?? existing.Summary,
                        UpdatedAt = now
                    };
                    phases = ReplaceStepInPhases(phases, existing.Id, updated);
                    if (execPhaseIndex >= 0)
                    {
                        var idx = execSteps.FindIndex(s => string.Equals(s.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0) execSteps[idx] = updated;
                    }
                }
                else
                {
                    var step = new SessionPlanStepDto
                    {
                        Id = stepId,
                        Title = Truncate(task.Title, 300),
                        Description = task.Description,
                        Status = mappedStatus,
                        DependsOn = deps,
                        BoundTaskKey = task.TaskKey,
                        AssignedAgentSlug = assignee,
                        VerificationCriteria = task.SuccessCriteria.Count > 0 ? [.. task.SuccessCriteria] : [],
                        Artifacts = [],
                        Summary = task.ResultSummary,
                        UpdatedAt = now
                    };
                    execSteps.Add(step);
                }
            }

            var execPhase = execPhaseIndex >= 0
                ? phases[execPhaseIndex] with { Steps = execSteps }
                : new SessionPlanPhaseDto
                {
                    Id = "phase-execution",
                    Title = "Execution",
                    Order = phases.Count + 1,
                    Steps = execSteps
                };
            if (execPhaseIndex >= 0) phases[execPhaseIndex] = execPhase;
            else phases.Add(execPhase);

            var recomputed = RecomputePhases(NormalizeAndValidatePhases(phases, tolerateMissingDeps: true));
            row.Status = ComputePlanStatus(recomputed, row.ApprovedAt is not null, explicitStatus: null);
            row.PlanJson = SerializePhases(recomputed);
            row.Revision++;
            row.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var dto = ToDto(row, recomputed);
            await EmitPlanUpdatedAsync(sessionId, runId, dto, $"Synced {syncable.Length} planned task(s) to session plan.", cancellationToken).ConfigureAwait(false);
            return dto;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SessionPlanDto?> SyncTaskTransitionAsync(
        Guid sessionId,
        Guid runId,
        DurableTaskNode task,
        string? summary = null,
        IReadOnlyList<string>? evidence = null,
        string? worktreePath = null,
        string? worktreeBranch = null,
        CancellationToken cancellationToken = default)
    {
        if (task.IsSpacePreparation) return null;

        var scope = _tenant.Current;
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

            var row = await db.SessionPlans.SingleOrDefaultAsync(
                x => x.SessionId == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var phases = row is null ? new List<SessionPlanPhaseDto>() : DeserializePhases(row.PlanJson);
            var boundStep = phases
                .SelectMany(p => p.Steps)
                .FirstOrDefault(s => string.Equals(s.BoundTaskKey, task.TaskKey, StringComparison.OrdinalIgnoreCase));

            if (row is null && boundStep is null && task.DispatchedByTaskId is null)
            {
                // Avoid creating a synthetic plan for single-turn greetings with no planned steps.
                return null;
            }

            if (row is null)
            {
                row = new SessionPlanRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    SessionId = sessionId,
                    Title = "Execution Plan",
                    Status = SessionPlanStatuses.Active,
                    CreatedAt = now
                };
                db.SessionPlans.Add(row);
            }

            row.LastRunId = runId;
            var artifacts = boundStep?.Artifacts.ToList() ?? [];
            if (!string.IsNullOrWhiteSpace(worktreePath))
            {
                var label = string.IsNullOrWhiteSpace(worktreeBranch) ? "worktree" : worktreeBranch!;
                AddUniqueArtifact(artifacts, new SessionPlanArtifactDto
                {
                    Kind = "worktree",
                    Label = label,
                    Path = worktreePath
                });
            }
            if (evidence is { Count: > 0 })
            {
                foreach (var ev in evidence.Where(e => !string.IsNullOrWhiteSpace(e)).Take(8))
                {
                    var trimmed = ev.Trim();
                    AddUniqueArtifact(artifacts, new SessionPlanArtifactDto
                    {
                        Kind = "summary",
                        Label = Truncate(trimmed, 160),
                        Path = LooksLikePath(trimmed) ? trimmed : null
                    });
                }
            }

            var nextStatus = SessionPlanStatuses.MapTaskStatus(task.Status);
            var assignee = task.WorkerAgentSlug ?? task.RequestedAgent ?? boundStep?.AssignedAgentSlug;
            var nextSummary = summary ?? task.ResultSummary ?? boundStep?.Summary;

            if (boundStep is not null)
            {
                var updated = boundStep with
                {
                    Status = nextStatus,
                    AssignedAgentSlug = assignee,
                    Summary = nextSummary,
                    Artifacts = artifacts,
                    UpdatedAt = now
                };
                phases = ReplaceStepInPhases(phases, boundStep.Id, updated);
            }
            else
            {
                var newStep = new SessionPlanStepDto
                {
                    Id = $"step-{task.TaskKey}",
                    Title = Truncate(task.Title, 300),
                    Description = task.Description,
                    Status = nextStatus,
                    DependsOn = [],
                    BoundTaskKey = task.TaskKey,
                    AssignedAgentSlug = assignee,
                    VerificationCriteria = task.SuccessCriteria.Count > 0 ? [.. task.SuccessCriteria] : [],
                    Artifacts = artifacts,
                    Summary = nextSummary,
                    UpdatedAt = now
                };
                var phaseIdx = phases.FindIndex(p => string.Equals(p.Id, "phase-execution", StringComparison.OrdinalIgnoreCase));
                if (phaseIdx >= 0)
                {
                    var steps = phases[phaseIdx].Steps.ToList();
                    steps.Add(newStep);
                    phases[phaseIdx] = phases[phaseIdx] with { Steps = steps };
                }
                else
                {
                    phases.Add(new SessionPlanPhaseDto
                    {
                        Id = "phase-execution",
                        Title = "Execution",
                        Order = phases.Count + 1,
                        Steps = [newStep]
                    });
                }
            }

            var recomputed = RecomputePhases(NormalizeAndValidatePhases(phases, tolerateMissingDeps: true));
            row.Status = ComputePlanStatus(recomputed, row.ApprovedAt is not null, explicitStatus: null);
            row.PlanJson = SerializePhases(recomputed);
            row.Revision++;
            row.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var dto = ToDto(row, recomputed);
            await EmitPlanUpdatedAsync(sessionId, runId, dto, $"Task '{task.TaskKey}' transitioned to {nextStatus}.", cancellationToken).ConfigureAwait(false);
            return dto;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<SessionPlanDto> SyncFromTaskPlanToolAsync(
        Guid sessionId,
        Guid runId,
        DurableTaskNode task,
        IReadOnlyList<TaskPlanStep> steps,
        string? explanation,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.Current;
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(db, cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var row = await db.SessionPlans.SingleOrDefaultAsync(
                x => x.SessionId == sessionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

            if (row is null)
            {
                row = new SessionPlanRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    SessionId = sessionId,
                    Title = string.IsNullOrWhiteSpace(task.Title) ? "Session Plan" : Truncate(task.Title, 256),
                    Goal = string.IsNullOrWhiteSpace(explanation) ? null : Truncate(explanation.Trim(), 2000),
                    Status = SessionPlanStatuses.Active,
                    CreatedAt = now
                };
                db.SessionPlans.Add(row);
            }
            else if (!string.IsNullOrWhiteSpace(explanation))
            {
                row.Goal = Truncate(explanation.Trim(), 2000);
            }

            row.LastRunId = runId;
            var phases = DeserializePhases(row.PlanJson);
            var phaseId = $"phase-{task.TaskKey}";
            var mappedSteps = new List<SessionPlanStepDto>(steps.Count);
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var stepId = $"{task.TaskKey}-step-{i + 1}";
                mappedSteps.Add(new SessionPlanStepDto
                {
                    Id = stepId,
                    Title = step.Step,
                    Status = step.Status,
                    DependsOn = i == 0 ? [] : [$"{task.TaskKey}-step-{i}"],
                    BoundTaskKey = task.TaskKey,
                    AssignedAgentSlug = task.WorkerAgentSlug ?? task.RequestedAgent,
                    UpdatedAt = now
                });
            }

            var idx = phases.FindIndex(p => string.Equals(p.Id, phaseId, StringComparison.OrdinalIgnoreCase));
            var phase = new SessionPlanPhaseDto
            {
                Id = phaseId,
                Title = string.IsNullOrWhiteSpace(task.Title) ? "Execution" : Truncate(task.Title, 128),
                Order = idx >= 0 ? phases[idx].Order : phases.Count + 1,
                Steps = mappedSteps
            };
            if (idx >= 0) phases[idx] = phase;
            else phases.Add(phase);

            // Mirror aggregate worker progress onto any existing planned step bound to this task key
            // in another phase (e.g., phase-execution) so parent plan steps never stay stale.
            if (mappedSteps.Count > 0)
            {
                var aggregateStatus = mappedSteps.All(s => s.Status is SessionPlanStatuses.StepCompleted or SessionPlanStatuses.StepSkipped)
                    ? SessionPlanStatuses.StepCompleted
                    : mappedSteps.Any(s => s.Status is SessionPlanStatuses.StepInProgress or SessionPlanStatuses.StepCompleted)
                        ? SessionPlanStatuses.StepInProgress
                        : SessionPlanStatuses.StepPending;

                for (var pi = 0; pi < phases.Count; pi++)
                {
                    if (string.Equals(phases[pi].Id, phaseId, StringComparison.OrdinalIgnoreCase)) continue;
                    var phaseSteps = phases[pi].Steps.ToList();
                    var changed = false;
                    for (var si = 0; si < phaseSteps.Count; si++)
                    {
                        var existingStep = phaseSteps[si];
                        if (!string.Equals(existingStep.BoundTaskKey, task.TaskKey, StringComparison.OrdinalIgnoreCase)) continue;
                        phaseSteps[si] = existingStep with
                        {
                            Status = aggregateStatus,
                            Summary = string.IsNullOrWhiteSpace(explanation) ? existingStep.Summary : Truncate(explanation.Trim(), 1000),
                            UpdatedAt = now
                        };
                        changed = true;
                    }
                    if (changed) phases[pi] = phases[pi] with { Steps = phaseSteps };
                }
            }

            var recomputed = RecomputePhases(NormalizeAndValidatePhases(phases, tolerateMissingDeps: true));
            row.Status = ComputePlanStatus(recomputed, row.ApprovedAt is not null, explicitStatus: null);
            row.PlanJson = SerializePhases(recomputed);
            row.Revision++;
            row.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var dto = ToDto(row, recomputed);
            await EmitPlanUpdatedAsync(sessionId, runId, dto, $"Updated session plan ({steps.Count} step(s)).", cancellationToken).ConfigureAwait(false);
            return dto;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(TenantContext Scope, SessionReference Session)> RequireSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var scope = _tenant.Current;
        var session = await _sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.TenantId != scope.TenantId || session.WorkspaceId != scope.WorkspaceId)
            throw new SessionPlanException(404, "session_not_found", $"Session '{sessionId}' was not found.");
        return (scope, session);
    }

    private async Task EmitPlanUpdatedAsync(
        Guid sessionId,
        Guid? runId,
        SessionPlanDto plan,
        string summary,
        CancellationToken cancellationToken)
    {
        if (_lifecycle is null) return;

        var targetRunId = runId;
        if (targetRunId is null || targetRunId == Guid.Empty)
        {
            try
            {
                var activeRuns = await _lifecycle.ListActiveRunsAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (activeRuns.Count > 0 && Guid.TryParse(activeRuns[0].RunId, out var activeId))
                    targetRunId = activeId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return;
            }
        }

        if (targetRunId is null || targetRunId == Guid.Empty) return;

        try
        {
            await _lifecycle.AppendEventAsync(
                targetRunId.Value,
                "session.plan.updated",
                new
                {
                    session_id = sessionId,
                    run_id = targetRunId.Value,
                    revision = plan.Revision,
                    status = plan.Status,
                    plan
                },
                summary,
                idempotencyKey: $"session-plan:{sessionId:N}:rev:{plan.Revision}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep plan authoring/sync durable even if the referenced run no longer accepts events.
        }
    }

    private static async Task EnsureSchemaAsync(SessionPlanDbContext db, CancellationToken cancellationToken)
    {
        var key = db.Database.GetConnectionString() ?? "default";
        if (EnsuredConnections.ContainsKey(key)) return;
        await SchemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (EnsuredConnections.ContainsKey(key)) return;
            await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
            EnsuredConnections[key] = 0;
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static string? NormalizePlanStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var trimmed = status.Trim().ToLowerInvariant();
        if (!SessionPlanStatuses.AllPlanStatuses.Contains(trimmed))
            throw new SessionPlanException(400, "invalid_request", $"Invalid plan status '{status}'.");
        return trimmed;
    }

    private static List<SessionPlanPhaseDto> NormalizeAndValidatePhases(
        IReadOnlyList<SessionPlanPhaseDto> phases,
        bool tolerateMissingDeps = false)
    {
        var normalized = new List<SessionPlanPhaseDto>(phases.Count);
        var seenPhaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenStepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSteps = new List<SessionPlanStepDto>();

        for (var pIdx = 0; pIdx < phases.Count; pIdx++)
        {
            var p = phases[pIdx];
            var phaseId = string.IsNullOrWhiteSpace(p.Id) ? $"phase-{pIdx + 1}" : p.Id.Trim();
            if (!seenPhaseIds.Add(phaseId))
                throw new SessionPlanException(400, "invalid_request", $"Duplicate phase id '{phaseId}'.");

            var phaseTitle = string.IsNullOrWhiteSpace(p.Title) ? $"Phase {pIdx + 1}" : Truncate(p.Title.Trim(), 128);
            var phaseStatus = string.IsNullOrWhiteSpace(p.Status) ? SessionPlanStatuses.StepPending : p.Status.Trim().ToLowerInvariant();
            if (!SessionPlanStatuses.AllStepStatuses.Contains(phaseStatus))
                throw new SessionPlanException(400, "invalid_request", $"Invalid phase status '{p.Status}'.");

            var steps = new List<SessionPlanStepDto>(p.Steps.Count);
            for (var sIdx = 0; sIdx < p.Steps.Count; sIdx++)
            {
                var s = p.Steps[sIdx];
                var stepId = string.IsNullOrWhiteSpace(s.Id) ? $"{phaseId}-step-{sIdx + 1}" : s.Id.Trim();
                if (!seenStepIds.Add(stepId))
                    throw new SessionPlanException(400, "invalid_request", $"Duplicate step id '{stepId}'.");

                if (string.IsNullOrWhiteSpace(s.Title))
                    throw new SessionPlanException(400, "invalid_request", $"Step '{stepId}' requires a non-empty title.");

                var stepStatus = string.IsNullOrWhiteSpace(s.Status) ? SessionPlanStatuses.StepPending : s.Status.Trim().ToLowerInvariant();
                if (!SessionPlanStatuses.AllStepStatuses.Contains(stepStatus))
                    throw new SessionPlanException(400, "invalid_request", $"Step '{stepId}' has invalid status '{s.Status}'.");

                var deps = s.DependsOn
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (deps.Contains(stepId, StringComparer.OrdinalIgnoreCase))
                    throw new SessionPlanException(400, "invalid_request", $"Step '{stepId}' cannot depend on itself.");

                var cleanStep = s with
                {
                    Id = stepId,
                    Title = Truncate(s.Title.Trim(), 300),
                    Description = string.IsNullOrWhiteSpace(s.Description) ? null : Truncate(s.Description.Trim(), 2000),
                    Status = stepStatus,
                    DependsOn = deps,
                    BoundTaskKey = string.IsNullOrWhiteSpace(s.BoundTaskKey) ? null : s.BoundTaskKey.Trim(),
                    AssignedAgentSlug = string.IsNullOrWhiteSpace(s.AssignedAgentSlug) ? null : s.AssignedAgentSlug.Trim(),
                    VerificationCriteria = s.VerificationCriteria.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray(),
                    Artifacts = s.Artifacts ?? [],
                    Summary = string.IsNullOrWhiteSpace(s.Summary) ? null : Truncate(s.Summary.Trim(), 2000)
                };
                steps.Add(cleanStep);
                allSteps.Add(cleanStep);
            }

            normalized.Add(p with
            {
                Id = phaseId,
                Title = phaseTitle,
                Description = string.IsNullOrWhiteSpace(p.Description) ? null : Truncate(p.Description.Trim(), 1000),
                Order = p.Order > 0 ? p.Order : pIdx + 1,
                Status = phaseStatus,
                Steps = steps
            });
        }

        // Validate dependencies exist (when strict) and detect cycles via Kahn topological sort.
        var knownIds = allSteps.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outgoing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in allSteps)
        {
            indegree[step.Id] = 0;
            outgoing[step.Id] = [];
        }

        foreach (var step in allSteps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!knownIds.Contains(dep))
                {
                    if (!tolerateMissingDeps)
                        throw new SessionPlanException(400, "invalid_request", $"Step '{step.Id}' depends on unknown step '{dep}'.");
                    continue;
                }
                outgoing[dep].Add(step.Id);
                indegree[step.Id]++;
            }
        }

        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            visited++;
            foreach (var next in outgoing[cur])
            {
                indegree[next]--;
                if (indegree[next] == 0)
                    queue.Enqueue(next);
            }
        }

        if (visited != allSteps.Count)
            throw new SessionPlanException(400, "invalid_request", "Session plan step dependencies contain a cycle.");

        return normalized;
    }

    private static List<SessionPlanPhaseDto> ApplyStepUpdates(
        List<SessionPlanPhaseDto> phases,
        IReadOnlyList<SessionPlanStepPatchDto> updates,
        DateTimeOffset now)
    {
        var result = phases.Select(p => p with { Steps = p.Steps.ToList() }).ToList();
        foreach (var patch in updates)
        {
            if (string.IsNullOrWhiteSpace(patch.Id))
                throw new SessionPlanException(400, "invalid_request", "step_updates[].id is required.");

            var key = patch.Id.Trim();
            var matched = false;
            for (var pi = 0; pi < result.Count; pi++)
            {
                var steps = result[pi].Steps.ToList();
                var si = steps.FindIndex(s =>
                    string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(s.BoundTaskKey) && string.Equals(s.BoundTaskKey, key, StringComparison.OrdinalIgnoreCase)));
                if (si < 0) continue;

                var cur = steps[si];
                steps[si] = cur with
                {
                    Title = patch.Title ?? cur.Title,
                    Description = patch.Description ?? cur.Description,
                    Status = patch.Status ?? cur.Status,
                    DependsOn = patch.DependsOn ?? cur.DependsOn,
                    BoundTaskKey = patch.BoundTaskKey ?? cur.BoundTaskKey,
                    AssignedAgentSlug = patch.AssignedAgentSlug ?? cur.AssignedAgentSlug,
                    VerificationCriteria = patch.VerificationCriteria ?? cur.VerificationCriteria,
                    Artifacts = patch.Artifacts ?? cur.Artifacts,
                    Summary = patch.Summary ?? cur.Summary,
                    UpdatedAt = now
                };
                result[pi] = result[pi] with { Steps = steps };
                matched = true;
                break;
            }
            if (!matched)
                throw new SessionPlanException(404, "step_not_found", $"Plan step '{key}' was not found.");
        }
        return result;
    }

    private static List<SessionPlanPhaseDto> ApplyStepAdditions(
        List<SessionPlanPhaseDto> phases,
        IReadOnlyList<SessionPlanAddStepDto> additions,
        DateTimeOffset now)
    {
        var result = phases.ToList();
        foreach (var add in additions)
        {
            var targetPhaseId = string.IsNullOrWhiteSpace(add.PhaseId) ? "phase-1" : add.PhaseId.Trim();
            var pi = result.FindIndex(p => string.Equals(p.Id, targetPhaseId, StringComparison.OrdinalIgnoreCase));
            if (pi < 0)
            {
                result.Add(new SessionPlanPhaseDto
                {
                    Id = targetPhaseId,
                    Title = string.IsNullOrWhiteSpace(add.PhaseTitle) ? "Plan" : add.PhaseTitle.Trim(),
                    Order = result.Count + 1,
                    Steps = []
                });
                pi = result.Count - 1;
            }

            var steps = result[pi].Steps.ToList();
            var stepId = string.IsNullOrWhiteSpace(add.Id)
                ? $"{targetPhaseId}-step-{steps.Count + 1}-{Guid.NewGuid().ToString("N")[..6]}"
                : add.Id.Trim();
            steps.Add(new SessionPlanStepDto
            {
                Id = stepId,
                Title = add.Title,
                Description = add.Description,
                Status = string.IsNullOrWhiteSpace(add.Status) ? SessionPlanStatuses.StepPending : add.Status.Trim().ToLowerInvariant(),
                DependsOn = add.DependsOn ?? [],
                BoundTaskKey = add.BoundTaskKey,
                AssignedAgentSlug = add.AssignedAgentSlug,
                VerificationCriteria = add.VerificationCriteria ?? [],
                Artifacts = [],
                UpdatedAt = now
            });
            result[pi] = result[pi] with { Steps = steps };
        }
        return result;
    }

    private static List<SessionPlanPhaseDto> ReplaceStepInPhases(
        List<SessionPlanPhaseDto> phases,
        string stepId,
        SessionPlanStepDto replacement)
    {
        var result = phases.ToList();
        for (var pi = 0; pi < result.Count; pi++)
        {
            var steps = result[pi].Steps.ToList();
            var si = steps.FindIndex(s => string.Equals(s.Id, stepId, StringComparison.OrdinalIgnoreCase));
            if (si < 0) continue;
            steps[si] = replacement;
            result[pi] = result[pi] with { Steps = steps };
            break;
        }
        return result;
    }

    private static List<SessionPlanPhaseDto> RecomputePhases(IReadOnlyList<SessionPlanPhaseDto> phases)
    {
        var result = new List<SessionPlanPhaseDto>(phases.Count);
        for (var i = 0; i < phases.Count; i++)
        {
            var phase = phases[i];
            var phaseStatus = phase.Status;
            if (phase.Steps.Count > 0)
            {
                if (phase.Steps.Any(s => s.Status == SessionPlanStatuses.StepInProgress))
                    phaseStatus = SessionPlanStatuses.StepInProgress;
                else if (phase.Steps.Any(s => s.Status == SessionPlanStatuses.StepFailed))
                    phaseStatus = SessionPlanStatuses.StepFailed;
                else if (phase.Steps.Any(s => s.Status == SessionPlanStatuses.StepBlocked))
                    phaseStatus = SessionPlanStatuses.StepBlocked;
                else if (phase.Steps.All(s => s.Status is SessionPlanStatuses.StepCompleted or SessionPlanStatuses.StepSkipped))
                    phaseStatus = SessionPlanStatuses.StepCompleted;
                else if (phase.Steps.Any(s => s.Status == SessionPlanStatuses.StepCompleted))
                    phaseStatus = SessionPlanStatuses.StepInProgress;
                else
                    phaseStatus = SessionPlanStatuses.StepPending;
            }
            result.Add(phase with { Order = i + 1, Status = phaseStatus });
        }
        return result;
    }

    private static string ComputePlanStatus(
        IReadOnlyList<SessionPlanPhaseDto> phases,
        bool approved,
        string? explicitStatus)
    {
        var steps = phases.SelectMany(p => p.Steps).ToArray();
        if (steps.Length == 0)
            return explicitStatus ?? (approved ? SessionPlanStatuses.Approved : SessionPlanStatuses.Draft);

        if (steps.All(s => s.Status is SessionPlanStatuses.StepCompleted or SessionPlanStatuses.StepSkipped))
            return SessionPlanStatuses.Completed;
        if (steps.Any(s => s.Status is SessionPlanStatuses.StepBlocked or SessionPlanStatuses.StepFailed))
            return SessionPlanStatuses.Blocked;
        if (steps.Any(s => s.Status is SessionPlanStatuses.StepInProgress or SessionPlanStatuses.StepCompleted))
            return SessionPlanStatuses.Active;

        if (!string.IsNullOrWhiteSpace(explicitStatus))
            return explicitStatus;

        return approved ? SessionPlanStatuses.Approved : SessionPlanStatuses.Draft;
    }

    private static SessionPlanProgressDto ComputeProgress(IReadOnlyList<SessionPlanPhaseDto> phases)
    {
        var steps = phases.SelectMany(p => p.Steps).ToArray();
        return new SessionPlanProgressDto
        {
            TotalSteps = steps.Length,
            CompletedSteps = steps.Count(s => s.Status is SessionPlanStatuses.StepCompleted or SessionPlanStatuses.StepSkipped),
            InProgressSteps = steps.Count(s => s.Status == SessionPlanStatuses.StepInProgress),
            BlockedSteps = steps.Count(s => s.Status == SessionPlanStatuses.StepBlocked),
            FailedSteps = steps.Count(s => s.Status == SessionPlanStatuses.StepFailed),
            PendingSteps = steps.Count(s => s.Status == SessionPlanStatuses.StepPending)
        };
    }

    private static SessionPlanDto EmptyPlan(Guid sessionId) => new()
    {
        Id = Guid.Empty,
        SessionId = sessionId,
        Title = string.Empty,
        Goal = null,
        Status = SessionPlanStatuses.Draft,
        Revision = 0,
        ApprovedAt = null,
        LastRunId = null,
        UpdatedAt = DateTimeOffset.UtcNow,
        Phases = [],
        Progress = new SessionPlanProgressDto()
    };

    private static SessionPlanDto ToDto(SessionPlanRecord row, IReadOnlyList<SessionPlanPhaseDto>? phases = null)
    {
        var resolvedPhases = phases ?? DeserializePhases(row.PlanJson);
        return new SessionPlanDto
        {
            Id = row.Id,
            SessionId = row.SessionId,
            Title = row.Title,
            Goal = row.Goal,
            Status = row.Status,
            Revision = row.Revision,
            ApprovedAt = row.ApprovedAt,
            LastRunId = row.LastRunId,
            UpdatedAt = row.UpdatedAt,
            Phases = resolvedPhases,
            Progress = ComputeProgress(resolvedPhases)
        };
    }

    private static List<SessionPlanPhaseDto> DeserializePhases(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var doc = JsonSerializer.Deserialize<SessionPlanDocument>(json, JsonOptions);
            return doc?.Phases ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializePhases(IReadOnlyList<SessionPlanPhaseDto> phases) =>
        JsonSerializer.Serialize(new SessionPlanDocument { Phases = [.. phases] }, JsonOptions);

    private static void AddUniqueArtifact(List<SessionPlanArtifactDto> artifacts, SessionPlanArtifactDto candidate)
    {
        if (artifacts.Any(a =>
            string.Equals(a.Kind, candidate.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Label, candidate.Label, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        artifacts.Add(candidate);
    }

    private static bool LooksLikePath(string text) =>
        text.Contains('/') || text.Contains('\\');

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..maxLen];
}
