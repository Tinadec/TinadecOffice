using System.Text.Json;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.DmaEA;

/// <summary>
/// Reads a run's durable checkpoint into the public topology shape, so a host can draw the task graph
/// without depending on the engine's internal checkpoint type. Read-only; a checkpoint written by a
/// newer or older engine that does not parse yields an empty view rather than an error.
/// </summary>
public static class CheckpointTopology
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Projection(string? Phase, Guid? AuthorInstanceId, IReadOnlyList<SessionTopologyTaskDto> Tasks, int TotalTasks);

    public static Projection Project(string? checkpointJson, int maxTasks)
    {
        if (string.IsNullOrWhiteSpace(checkpointJson)) return new Projection(null, null, [], 0);
        FullDuplexCheckpointV1? checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<FullDuplexCheckpointV1>(checkpointJson, Json);
        }
        catch (JsonException)
        {
            return new Projection(null, null, [], 0);
        }
        if (checkpoint is null) return new Projection(null, null, [], 0);
        // Live work first (running, waiting, pending), then the finished record, newest plan order kept.
        var ordered = checkpoint.Tasks
            .OrderBy(task => task.Status switch { "running" => 0, "waiting" => 1, "pending" => 2, "failed" => 3, "blocked" => 4, _ => 5 })
            .Take(Math.Max(1, maxTasks))
            .Select(task => new SessionTopologyTaskDto(
                task.TaskId, task.TaskKey, task.Title, task.Status, task.WorkerHandle, task.WorkerAgentSlug, task.WorkerAgentId,
                task.DispatchedByTaskId, task.Dependencies.ToArray(), task.WriteScope?.ToArray() ?? [], task.ResultSummary))
            .ToArray();
        return new Projection(checkpoint.Phase, checkpoint.PlannerAgentId, ordered, checkpoint.Tasks.Count);
    }
}
