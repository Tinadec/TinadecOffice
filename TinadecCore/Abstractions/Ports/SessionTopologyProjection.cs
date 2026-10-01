using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The model-facing shape of a session topology: the same facts the UI draws, with the fields a
/// model reasons over and nothing else (no timestamps it cannot use, ids in the form other tools
/// accept). Shared by the in-run <c>graph_view</c> tool and a standing member's turn.
/// </summary>
public static class SessionTopologyProjection
{
    public static object ForModel(SessionTopologyDto view) => new
    {
        session_id = view.SessionId.ToString("N"),
        runs = view.Runs.Select(run => new
        {
            run_id = run.RunId.ToString("N"),
            parent_run_id = run.ParentRunId?.ToString("N"),
            parent_task_id = run.ParentTaskId?.ToString("N"),
            run_kind = run.RunKind,
            status = run.Status,
            phase = run.Phase,
            tier = run.Tier,
            tasks = run.Tasks.Select(task => new
            {
                task_id = task.TaskId.ToString("N"),
                key = task.TaskKey,
                title = task.Title,
                status = task.Status,
                worker = task.Handle,
                agent = task.AgentSlug,
                dispatched_by_task = task.DispatchedByTaskId?.ToString("N"),
                depends_on = task.Dependencies.Length == 0 ? null : task.Dependencies,
                write_scope = task.WriteScope.Length == 0 ? null : task.WriteScope,
                result = task.ResultSummary is { Length: > 240 } summary ? summary[..240] + "…" : task.ResultSummary,
            }).ToArray(),
            tasks_truncated = run.TasksTruncated ? true : (bool?)null,
            instances = run.Instances.Select(instance => new
            {
                instance_id = instance.InstanceId.ToString("N"),
                agent = instance.AgentSlug,
                layer = instance.Layer,
                status = instance.Status,
                parent = instance.ParentInstanceId?.ToString("N"),
                task_id = instance.TaskId?.ToString("N"),
                depth = instance.Depth,
            }).ToArray(),
            instances_truncated = run.InstancesTruncated ? true : (bool?)null,
        }).ToArray(),
        runs_truncated = view.RunsTruncated ? true : (bool?)null,
        leases = view.Leases.Select(lease => new
        {
            lease_id = lease.LeaseId.ToString("N"),
            kind = lease.Kind,
            resource = lease.ResourceKey,
            purpose = lease.Purpose,
            exclusive = lease.Exclusive,
            run_id = lease.RunId?.ToString("N"),
            task_id = lease.TaskId?.ToString("N"),
        }).ToArray(),
        leases_truncated = view.LeasesTruncated ? true : (bool?)null,
        members = view.Members.Select(member => new
        {
            address = member.DisplayName,
            role = member.Role,
            presence = member.Presence,
            agent = member.AgentSlug,
            instance_id = member.AgentInstanceId?.ToString("N"),
        }).ToArray(),
        members_truncated = view.MembersTruncated ? true : (bool?)null,
    };
}
