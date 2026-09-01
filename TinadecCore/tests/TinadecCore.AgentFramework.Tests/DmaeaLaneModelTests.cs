using System.Text;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// M1 lane data model: per-lane task-key uniqueness, the lane fields on the
/// durable task node, and the replan merge that must carry runtime lane state
/// across a planner rewrite.
/// </summary>
public sealed class DmaeaLaneModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tinadec-lane-model-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ValidateAndMaterializeGraph_AllowsSameTaskKeyAcrossLanes()
    {
        var tasks = new[]
        {
            Task("test", "Test the build", lane: "l2"),
            Task("test", "Test the build", lane: "main")
        };

        var nodes = FullDuplexRunEngine.ValidateAndMaterializeGraph(tasks, maxTasks: 8);

        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, node => Assert.Equal("test", node.TaskKey));
        Assert.Equal(["l2", "main"], nodes.Select(node => node.LaneKey).Order().ToArray());
    }

    [Fact]
    public void ValidateAndMaterializeGraph_RejectsDuplicateKeyWithinLane()
    {
        var tasks = new[]
        {
            Task("test", "First test task", lane: "l2"),
            Task("test", "Second test task", lane: "l2")
        };

        var failure = Assert.Throws<FullDuplexRunEngine.InvalidTaskGraphException>(
            () => FullDuplexRunEngine.ValidateAndMaterializeGraph(tasks, maxTasks: 8));
        Assert.Contains("not unique within lane 'l2'", failure.Message);
    }

    [Fact]
    public void ValidateAndMaterializeGraph_DefaultsMissingLaneToMain()
    {
        var tasks = new[] { Task(null, "Build the thing") };

        var nodes = FullDuplexRunEngine.ValidateAndMaterializeGraph(tasks, maxTasks: 8);

        var node = Assert.Single(nodes);
        Assert.Equal("main", node.LaneKey);
    }

    [Fact]
    public void MergeReplannedGraph_PreservesLaneKeyWaitsAndCriteriaVerdicts()
    {
        var prior = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "test",
            Title = "Test the build",
            Status = "completed",
            LaneKey = "l2",
            Waits = ["main"],
            CriteriaVerdicts =
            [
                new CriterionVerdict("全部测试通过", true, "run:pytest 42 passed"),
                new CriterionVerdict("无回归", false, null)
            ],
            Evidence = ["pytest output"]
        };
        var replacement = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "test",
            Title = "Test the build (renamed)",
            Status = "pending"
        };

        var merged = FullDuplexRunEngine.MergeReplannedGraph([prior], [replacement]);

        var node = Assert.Single(merged);
        Assert.Equal("completed", node.Status);
        Assert.Equal(prior.TaskId, node.TaskId);
        Assert.Equal("l2", node.LaneKey);
        Assert.Equal(["main"], node.Waits);
        Assert.Equal(2, node.CriteriaVerdicts.Count);
        Assert.Contains(node.CriteriaVerdicts, verdict => verdict.Satisfied);
        Assert.Contains(node.CriteriaVerdicts, verdict => !verdict.Satisfied);
    }

    [Fact]
    public void MergeReplannedGraph_KeepsReplacementFieldsForUnfinishedTasks()
    {
        var prior = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "implement",
            Title = "Implement",
            Status = "failed",
            LaneKey = "l2",
            Waits = ["main"]
        };
        var replacement = new DurableTaskNode
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "implement",
            Title = "Implement",
            Status = "pending",
            LaneKey = "l9",
            Waits = ["main", "l2"]
        };

        var merged = FullDuplexRunEngine.MergeReplannedGraph([prior], [replacement]);

        var node = Assert.Single(merged);
        // Only completed tasks keep their prior runtime state; an unfinished task
        // adopts the planner's new lane topology wholesale.
        Assert.Equal("l9", node.LaneKey);
        Assert.Equal(["main", "l2"], node.Waits);
    }

    [Fact]
    public void Orchestration_ShippedBaselineParsesCeilings()
    {
        var baseline = LocateShippedBaseline();
        Assert.True(File.Exists(baseline), $"Shipped baseline TOML was not found at {baseline}.");

        var snapshot = AgentRuntimeConfigurationStore.LoadSnapshot(baseline, version: 1);

        Assert.False(snapshot.Orchestration.LanesEnabled);
        Assert.Equal(4, snapshot.Orchestration.MaxLanesPerRun);
        Assert.Equal(6, snapshot.Orchestration.MaxTasksPerLane);
    }

    [Fact]
    public void Orchestration_MissingSectionFallsBackToDisabled()
    {
        var snapshot = LoadSnapshot(BaselineToml());

        Assert.Equal(OrchestrationPolicy.Disabled, snapshot.Orchestration);
        Assert.False(snapshot.Orchestration.LanesEnabled);
    }

    [Fact]
    public void Orchestration_TomlSectionOverridesDefaults()
    {
        var snapshot = LoadSnapshot([.. BaselineToml(), "", "[orchestration]", "lanes_enabled = true", "max_lanes_per_run = 3", "max_tasks_per_lane = 5"]);

        Assert.True(snapshot.Orchestration.LanesEnabled);
        Assert.Equal(3, snapshot.Orchestration.MaxLanesPerRun);
        Assert.Equal(5, snapshot.Orchestration.MaxTasksPerLane);
    }

    [Fact]
    public void Orchestration_RejectsInvalidCeilings()
    {
        Assert.Throws<InvalidDataException>(
            () => LoadSnapshot([.. BaselineToml(), "", "[orchestration]", "lanes_enabled = true", "max_lanes_per_run = 0"]));
        Assert.Throws<InvalidDataException>(
            () => LoadSnapshot([.. BaselineToml(), "", "[orchestration]", "max_tasks_per_lane = -1"]));
        Assert.Throws<InvalidDataException>(
            () => OrchestrationPolicy.Validate(new OrchestrationPolicy(true, 17, 6)));
    }

    private static PlannedTask Task(string? taskKey, string title, string? lane = null) => new()
    {
        TaskKey = taskKey,
        Title = title,
        LaneKey = lane,
        SuccessCriteria = ["done"],
        Dependencies = [],
        RequiredCapabilities = [],
        RequiredTools = [],
        Priority = 1,
        Risk = "low"
    };

    private static string[] BaselineToml() =>
    [
        "schema_version = 1",
        "[spawn]",
        "max_depth = 2",
        "max_agents_per_run = 8",
        "max_parallel_workers = 4",
        "[scheduling]",
        "max_active_runs_per_session = 2",
        "worker_retry_limit = 2",
        "preserve_partial_results = true",
        "[supervision]",
        "required_before_final = true",
        "max_revision_rounds = 2",
        "[context]",
        "default_token_budget = 8192",
        "recent_message_limit = 24",
        "optimistic_revision = true",
        "[memory]",
        "candidate_only = true",
        "retrieval_limit = 8",
        "allowed_scopes = [\"workspace\"]",
        "allowed_kinds = [\"fact\"]",
        "[tools]",
        "provider = \"tinadec-tools\"",
        "mutation_requires_approval = true",
        "serialize_workspace_writes = true",
        "default_timeout_seconds = 120",
        "max_tool_rounds = 4"
    ];

    private AgentRuntimeConfigurationSnapshot LoadSnapshot(IReadOnlyList<string> lines)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "agent-runtime.toml");
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return AgentRuntimeConfigurationStore.LoadSnapshot(path, version: 1);
    }

    private static string LocateShippedBaseline()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DmaEA", "Configuration", "default-agent-runtime.toml")))
        {
            directory = directory.Parent;
        }
        return directory is null
            ? Path.Combine(AppContext.BaseDirectory, "DmaEA", "Configuration", "default-agent-runtime.toml")
            : Path.Combine(directory.FullName, "DmaEA", "Configuration", "default-agent-runtime.toml");
    }
}
