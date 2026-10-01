using System.Text.Json;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;

namespace TinadecCore.Runtime;

/// <summary>
/// The session as a graph (architecture §3.1, todo D4): runs → the instances working them → their
/// tasks → the resources those hold, plus the organization's members. Composed here because each
/// part lives in its own module (lifecycle, engine checkpoint, agent instances, ledger, TinaChat) and
/// only the host may read across them.
///
/// Bounded at every level (<see cref="SessionTopologyQuery"/>) and truncation is always reported, so
/// neither a model nor a UI is handed a thousand-task session whole. Live runs are listed first.
/// </summary>
public sealed class SessionTopologyService : ISessionTopology
{
    private readonly ISessionLocator _sessions;
    private readonly StorageLifecycleService _runs;
    private readonly ILifecycleManager _lifecycle;
    private readonly IAgentInstanceService _instances;
    private readonly IResourceLeaseService? _leases;
    private readonly ISessionOrganization? _organization;
    private readonly ILogger<SessionTopologyService>? _logger;

    public SessionTopologyService(
        ISessionLocator sessions,
        StorageLifecycleService runs,
        ILifecycleManager lifecycle,
        IAgentInstanceService instances,
        IResourceLeaseService? leases = null,
        ISessionOrganization? organization = null,
        ILogger<SessionTopologyService>? logger = null)
    {
        _sessions = sessions;
        _runs = runs;
        _lifecycle = lifecycle;
        _instances = instances;
        _leases = leases;
        _organization = organization;
        _logger = logger;
    }

    public async Task<SessionTopologyDto?> GetAsync(Guid sessionId, SessionTopologyQuery query, CancellationToken cancellationToken = default)
    {
        query = query.Clamped();
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;

        var allRuns = await _runs.ListRunsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var candidates = allRuns
            .Where(run => query.RunId is null || run.Id == query.RunId)
            .Where(run => query.IncludeFinishedRuns || !IsTerminal(run.Status))
            .OrderBy(run => IsTerminal(run.Status) ? 1 : 0)
            .ThenByDescending(run => run.CreatedAt)
            .ToArray();
        var runs = new List<SessionTopologyRunDto>();
        foreach (var run in candidates.Take(query.MaxRuns))
            runs.Add(await RunAsync(run, query, cancellationToken).ConfigureAwait(false));

        var leases = _leases is null
            ? []
            : await _leases.ListActiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // A run-scoped graph read is the service boundary for a restricted member. Filtering only
        // the runs array still leaked another run's lease rows to graph_view callers.
        if (query.RunId is { } visibleRunId)
            leases = leases.Where(lease => lease.RunId == visibleRunId).ToArray();
        IReadOnlyList<OrganizationMemberSummary> members = [];
        if (_organization is not null)
        {
            try
            {
                members = await _organization.ListMembersAsync(sessionId, query.MaxMembers + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.TryLogDebug(ex, "Could not read the organization of session {SessionId}.", sessionId);
            }
        }
        return new SessionTopologyDto(
            sessionId,
            runs.ToArray(),
            leases.Take(query.MaxLeases).Select(lease => new SessionTopologyLeaseDto(lease.Id, lease.Kind, lease.ResourceKey, lease.Purpose,
                lease.Exclusive, lease.RunId, lease.TaskId, lease.AgentInstanceId)).ToArray(),
            members.Where(member => member.Role != OrganizationRoles.Host
                && (query.RunId is null || member.Role != OrganizationRoles.Executor || member.RunId == query.RunId))
                .Take(query.MaxMembers)
                .Select(member => new SessionTopologyMemberDto(member.ParticipantId, member.Handle, member.DisplayName, member.Role,
                    member.Presence, member.AgentSlug, member.ParentParticipantId, member.RunId, member.AgentInstanceId)).ToArray(),
            candidates.Length > query.MaxRuns,
            leases.Count > query.MaxLeases,
            members.Count > query.MaxMembers,
            DateTimeOffset.UtcNow);
    }

    private async Task<SessionTopologyRunDto> RunAsync(RunRecord run, SessionTopologyQuery query, CancellationToken cancellationToken)
    {
        CheckpointTopology.Projection projection = new(null, null, [], 0);
        try
        {
            var checkpoint = await _lifecycle.GetCurrentRunCheckpointAsync(run.Id.ToString(), cancellationToken).ConfigureAwait(false);
            projection = CheckpointTopology.Project(checkpoint?.Content, query.MaxTasksPerRun);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.TryLogDebug(ex, "Could not read the checkpoint of run {RunId}.", run.Id);
        }
        IReadOnlyList<RuntimeAgentInstance> instances = [];
        try
        {
            instances = await _instances.ListByRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.TryLogDebug(ex, "Could not read the instances of run {RunId}.", run.Id);
        }
        var slugByInstance = projection.Tasks
            .Where(task => task.WorkerInstanceId is not null && !string.IsNullOrWhiteSpace(task.AgentSlug))
            .GroupBy(task => task.WorkerInstanceId!.Value)
            .ToDictionary(group => group.Key, group => group.First().AgentSlug!);
        var orderedInstances = instances
            .OrderBy(instance => instance.Status is "running" or "created" ? 0 : 1)
            .ThenBy(instance => instance.GenerationDepth)
            .ToArray();
        return new SessionTopologyRunDto(
            run.Id, run.Status, run.CreatedAt, run.CompletedAt,
            await TierAsync(run.Id, cancellationToken).ConfigureAwait(false),
            projection.Phase,
            projection.Tasks.ToArray(),
            orderedInstances.Take(query.MaxInstancesPerRun).Select(instance => new SessionTopologyInstanceDto(
                instance.Id,
                slugByInstance.GetValueOrDefault(instance.Id) ?? (instance.Id == projection.AuthorInstanceId ? "conversation" : instance.Role),
                instance.Layer, instance.Role, instance.Status, instance.ParentInstanceId, instance.TaskId, instance.GenerationDepth)).ToArray(),
            projection.TotalTasks > projection.Tasks.Count,
            orderedInstances.Length > query.MaxInstancesPerRun,
            run.ParentRunId,
            run.ParentTaskId,
            run.RunKind);
    }

    /// <summary>The orchestration tier frozen with the run; only the one field is read out of the frozen body.</summary>
    private async Task<string?> TierAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
            if (frozen is null) return null;
            using var document = JsonDocument.Parse(frozen.Content);
            return document.RootElement.TryGetProperty("graph", out var graph) && graph.ValueKind == JsonValueKind.Object
                && graph.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String
                ? tier.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";
}
