using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Events;

namespace TinadecCore.DmaEA;

/// <summary>
/// Rebuilds a durable run timeline from the event journal. Serves supervision
/// replay (what the supervisor saw per round) and candidate evaluation (the
/// source-run evidence behind an evolution proposal) without re-executing
/// anything. MAF Workflows 1.18 offers no time-travel, so the journal itself
/// is the replay medium.
/// </summary>
public interface IRunReplayService
{
    Task<RunReplay?> BuildReplayAsync(string runId, CancellationToken cancellationToken = default);
}

public sealed record RunReplayTask(
    string TaskKey,
    string LaneKey,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence);

public sealed record RunReplayLaneWait(
    string WaitingTaskKey,
    string Lane,
    string Predicate);

public sealed record RunReplayLane(
    string LaneKey,
    string Status,
    bool Escalated,
    IReadOnlyList<string> TaskKeys,
    IReadOnlyList<RunReplayLaneWait> Waits);

public sealed record RunReplaySupervisionRound(
    int RevisionRound,
    string? Decision,
    IReadOnlyList<string> Reasons);

public sealed record RunReplayMilestone(
    string EventType,
    DateTimeOffset Timestamp);

public sealed record RunReplay(
    string RunId,
    string Status,
    IReadOnlyList<RunReplayTask> Tasks,
    IReadOnlyList<RunReplayLane> Lanes,
    IReadOnlyList<RunReplaySupervisionRound> SupervisionRounds,
    IReadOnlyList<RunReplayMilestone> Milestones);

internal sealed class RunReplayService : IRunReplayService
{
    private static readonly HashSet<string> MilestoneTypes = new(StringComparer.Ordinal)
    {
        "task.accepted",
        "task_graph.created",
        "worker.completed",
        "worker.failed",
        "supervision.completed",
        "supervision.user_decision",
        "context.patch.accepted",
        "context.patch.stale",
        "context.compacted",
        "capability.recommended",
        "memory.candidate_created",
        "evolution.agent_candidate_created",
        "git.steward.reviewed",
        "user.response",
        "run.failed"
    };

    private readonly ILifecycleManager _lifecycle;

    public RunReplayService(ILifecycleManager lifecycle) => _lifecycle = lifecycle;

    public async Task<RunReplay?> BuildReplayAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(runId, out var runGuid)) return null;
        RunState run;
        try
        {
            run = await _lifecycle.GetRunStateAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        if (!Guid.TryParse(run.SessionId, out var sessionGuid)) return null;

        var events = (await _lifecycle.ReplayEventsAsync(sessionGuid, 0, cancellationToken).ConfigureAwait(false))
            .Where(item => string.Equals(item.RunId, runGuid.ToString(), StringComparison.Ordinal))
            .OrderBy(item => item.Timestamp)
            .ToList();
        if (events.Count == 0 && run.Status == RunStatus.Planning) return null;

        FullDuplexCheckpointV1? checkpoint = null;
        try
        {
            var checkpointRow = await _lifecycle.GetCurrentRunCheckpointAsync(runId, cancellationToken).ConfigureAwait(false);
            if (checkpointRow is not null)
                checkpoint = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(checkpointRow.Content);
        }
        catch
        {
            // A missing or unreadable checkpoint must not invalidate the journal-based replay.
        }

        static string LaneOfTask(DurableTaskNode task) =>
            string.IsNullOrWhiteSpace(task.LaneKey) ? "main" : task.LaneKey.Trim();
        var laneByTask = new Dictionary<string, string>(StringComparer.Ordinal);
        if (checkpoint is not null)
        {
            foreach (var task in checkpoint.Tasks)
                laneByTask[task.TaskKey] = LaneOfTask(task);
        }

        var tasks = new Dictionary<string, RunReplayTask>(StringComparer.Ordinal);
        foreach (var item in events.Where(item => item.EventType is "worker.completed" or "worker.failed"))
        {
            var taskKey = PayloadString(item.Payload, "task_key") ?? PayloadString(item.Payload, "task_id") ?? item.EventId;
            tasks[taskKey] = new RunReplayTask(
                taskKey,
                laneByTask.TryGetValue(taskKey, out var laneKey) ? laneKey : "main",
                item.EventType == "worker.failed" ? "failed" : PayloadString(item.Payload, "status") ?? "completed",
                PayloadString(item.Payload, "summary") ?? string.Empty,
                PayloadArray(item.Payload, "evidence"));
        }

        var supervisionRounds = events
            .Where(item => item.EventType == "supervision.completed")
            .Select(item => new RunReplaySupervisionRound(
                PayloadInt(item.Payload, "revision_round") ?? 0,
                PayloadString(item.Payload, "decision"),
                PayloadArray(item.Payload, "reasons")))
            .ToList();

        var milestones = events
            .Where(item => MilestoneTypes.Contains(item.EventType))
            .Select(item => new RunReplayMilestone(item.EventType, item.Timestamp))
            .ToList();

        var lanes = checkpoint is null
            ? new List<RunReplayLane>()
            : checkpoint.Lanes.Count > 0
                ? checkpoint.Lanes.Select(lane => new RunReplayLane(
                        lane.LaneKey,
                        lane.Status,
                        lane.Escalated,
                        checkpoint.Tasks.Where(t => LaneOfTask(t) == lane.LaneKey).Select(t => t.TaskKey).ToList(),
                        checkpoint.Tasks.Where(t => LaneOfTask(t) == lane.LaneKey)
                            .SelectMany(t => t.Waits.Select(w => new RunReplayLaneWait(t.TaskKey, w.LaneKey, w.Predicate)))
                            .ToList()))
                    .ToList()
                : new List<RunReplayLane>
                {
                    new("main", checkpoint.Phase, false, checkpoint.Tasks.Select(t => t.TaskKey).ToList(), [])
                };

        return new RunReplay(runGuid.ToString(), run.Status.ToString().ToLowerInvariant(), tasks.Values.ToList(), lanes, supervisionRounds, milestones);
    }

    private static JsonElement PayloadRoot(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("payload", out var inner) && inner is JsonElement { ValueKind: JsonValueKind.Object } nested) return nested;
        return default;
    }

    private static string? PayloadString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? PayloadInt(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> PayloadArray(IReadOnlyDictionary<string, object?> payload, string key)
    {
        var root = PayloadRoot(payload);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToList();
    }
}
