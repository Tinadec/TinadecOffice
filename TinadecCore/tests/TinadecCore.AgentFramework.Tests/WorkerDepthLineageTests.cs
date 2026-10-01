using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// C2: the depth ceiling and the per-run instance budget on the engine-authored root
/// path. The engine creates its workers through <c>CreateRootAsync</c>, not through
/// <c>SpawnAsync</c>, so the depth check inside <c>AgentInstanceService</c> never runs
/// for them — these assertions pin the replacement that does, plus the budget counter
/// that already lived on this path.
/// </summary>
public sealed class WorkerDepthLineageTests
{
    /// <summary>
    /// A root task carries no dispatcher, so the ceiling cannot apply to it. Without this
    /// the first worker of every run would be refused at <c>max_depth = 0</c>.
    /// </summary>
    [Fact]
    public void RootTask_IsDepthZero_AndNoCeilingApplies()
    {
        var task = Task("root");
        var lineage = FullDuplexRunEngine.ResolveDispatcherLineage([], [task], task);

        Assert.False(lineage.Dispatched);
        Assert.Null(lineage.InstanceId);
        Assert.Equal(0, lineage.Depth);
        FullDuplexRunEngine.EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, maxDepth: 0, taskKey: task.TaskKey);
    }

    /// <summary>
    /// A task_dispatch sub-task sits one level below the worker that dispatched it, and a
    /// depth within the ceiling is created — the lineage parent travels with it so the next
    /// level counts from there instead of restarting at zero.
    /// </summary>
    [Fact]
    public void DispatchedSubTask_WithinCeiling_ResolvesOneLevelBelowItsDispatcher()
    {
        var (dispatcher, dispatcherInstance) = TaskWithWorker("dispatcher", depth: 0);
        var sub = SubTask(dispatcher);
        var tasks = new[] { dispatcher, sub };

        var lineage = FullDuplexRunEngine.ResolveDispatcherLineage([dispatcherInstance], tasks, sub);

        Assert.True(lineage.Dispatched);
        Assert.Equal(dispatcherInstance.Id, lineage.InstanceId);
        Assert.Equal(1, lineage.Depth);
        FullDuplexRunEngine.EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, maxDepth: 1, taskKey: sub.TaskKey);
    }

    /// <summary>
    /// The boundary: a dispatcher already AT the ceiling is refused, because the sub-task it
    /// would create one level below is what exceeds it. This is the chain-max_depth case.
    /// </summary>
    [Fact]
    public void DispatchedSubTask_FromDispatcherAtTheCeiling_IsRefused()
    {
        var (dispatcher, dispatcherInstance) = TaskWithWorker("dispatcher", depth: 1);
        var sub = SubTask(dispatcher);

        var lineage = FullDuplexRunEngine.ResolveDispatcherLineage([dispatcherInstance], [dispatcher, sub], sub);

        Assert.Equal(2, lineage.Depth);
        var failure = Assert.Throws<FullDuplexRunEngine.WorkerAssignmentException>(
            () => FullDuplexRunEngine.EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, maxDepth: 1, taskKey: sub.TaskKey));
        Assert.Contains("max_depth = 1", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The refusal names the ceiling and the task, so the failure is actionable.</summary>
    [Fact]
    public void DispatchedSubTask_ExceedingCeiling_IsRefused_AndTheErrorNamesTheLimit()
    {
        var (dispatcher, dispatcherInstance) = TaskWithWorker("dispatcher", depth: 2);
        var sub = SubTask(dispatcher);
        var tasks = new[] { dispatcher, sub };
        var lineage = FullDuplexRunEngine.ResolveDispatcherLineage([dispatcherInstance], tasks, sub);

        var failure = Assert.Throws<FullDuplexRunEngine.WorkerAssignmentException>(
            () => FullDuplexRunEngine.EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, maxDepth: 2, taskKey: sub.TaskKey));

        Assert.Contains("max_depth = 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(sub.TaskKey, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dispatched sub-task is never legitimately a root, so a dispatcher whose worker row
    /// is missing fails closed instead of quietly restarting the count at depth 0.
    /// </summary>
    [Fact]
    public void DispatchedSubTask_WithUnresolvableDispatcher_FailsClosed()
    {
        var (dispatcher, _) = TaskWithWorker("dispatcher", depth: 0);
        var sub = SubTask(dispatcher);

        var lineage = FullDuplexRunEngine.ResolveDispatcherLineage([], [dispatcher, sub], sub);

        Assert.True(lineage.Dispatched);
        Assert.Null(lineage.Depth);
        var failure = Assert.Throws<FullDuplexRunEngine.WorkerAssignmentException>(
            () => FullDuplexRunEngine.EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, maxDepth: 2, taskKey: sub.TaskKey));
        Assert.Contains("max_depth = 2", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dispatch relation points at a task the checkpoint does not carry: the same
    /// unverifiable case, and it must not be read as "root, depth 0".
    /// </summary>
    [Fact]
    public void DispatchedSubTask_WhoseDispatcherTaskIsUnknown_FailsClosed()
    {
        var sub = SubTask(Task("dispatcher"));

        var lineage = FullDuplexRunEngine.ResolveDispatcherLineage([], [sub], sub);

        Assert.True(lineage.Dispatched);
        Assert.Null(lineage.InstanceId);
        Assert.Null(lineage.Depth);
        Assert.Throws<FullDuplexRunEngine.WorkerAssignmentException>(
            () => FullDuplexRunEngine.EnsureGraphWorkerDepth(lineage.Dispatched, lineage.Depth, maxDepth: 2, taskKey: sub.TaskKey));
    }

    /// <summary>An assigned task that never got a worker instance is the same gap.</summary>
    [Fact]
    public void DispatchedSubTask_WhoseDispatcherHasNoWorkerInstance_FailsClosed()
    {
        var dispatcher = Task("dispatcher");
        dispatcher.WorkerAgentSlug = "worker.file";
        var sub = SubTask(dispatcher);

        var lineage = FullDuplexRunEngine.ResolveDispatcherLineage([], [dispatcher, sub], sub);

        Assert.True(lineage.Dispatched);
        Assert.Null(lineage.Depth);
    }

    /// <summary>Budget still refuses at the ceiling, and admits one below it.</summary>
    [Fact]
    public void Budget_RefusesAtTheCeiling_AndAdmitsBelowIt()
    {
        var under = Enumerable.Range(0, 3).Select(_ => Instance(Guid.NewGuid(), depth: 0)).ToArray();
        FullDuplexRunEngine.EnsureGraphWorkerBudget(under, maxAgentsPerRun: 4, taskKey: "under");

        var at = Enumerable.Range(0, 4).Select(_ => Instance(Guid.NewGuid(), depth: 0)).ToArray();
        var failure = Assert.Throws<FullDuplexRunEngine.WorkerAssignmentException>(
            () => FullDuplexRunEngine.EnsureGraphWorkerBudget(at, maxAgentsPerRun: 4, taskKey: "at"));

        Assert.Contains("ceiling 4", failure.Message, StringComparison.Ordinal);
        Assert.Contains("at", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A task with a worker already assigned to it, the state the engine persists in its
    /// <c>WorkersAssigned</c> batch before any of those tasks executes. The lineage walk keys
    /// off <c>WorkerAgentId</c>, so a fixture without this is not a dispatcher at all.
    /// </summary>
    private static (DurableTaskNode Task, RuntimeAgentInstance Instance) TaskWithWorker(string key, int depth)
    {
        var task = Task(key);
        var instance = Instance(task.TaskId, depth);
        task.WorkerAgentId = instance.Id;
        task.WorkerAgentSlug = "worker.file";
        task.WorkerHandle = $"{task.WorkerAgentSlug}#1";
        return (task, instance);
    }

    private static DurableTaskNode Task(string key) => new()
    {
        TaskId = Guid.NewGuid(),
        TaskKey = key,
        Title = key,
        Status = "ready"
    };

    private static DurableTaskNode SubTask(DurableTaskNode dispatcher) => new()
    {
        TaskId = Guid.NewGuid(),
        TaskKey = "sub",
        Title = "sub",
        Status = "ready",
        DispatchId = Guid.NewGuid(),
        DispatchedByTaskId = dispatcher.TaskId
    };

    private static RuntimeAgentInstance Instance(Guid taskId, int depth) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, taskId, "execution", "task_executor", depth,
        Generated: false, "running", [], [], [], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        Guid.NewGuid(), Guid.NewGuid(), "hash");
}
