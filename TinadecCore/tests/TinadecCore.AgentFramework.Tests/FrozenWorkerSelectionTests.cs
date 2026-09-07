using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

public sealed class FrozenWorkerSelectionTests
{
    [Fact]
    public void SelectWorker_DefaultsToGeneralForUnclassifiedTask()
    {
        var selection = FullDuplexRunEngine.SelectWorker(Configuration(), Task());

        Assert.Equal("worker.general", selection.Agent.Id);
        Assert.Equal("general_default", selection.Reason);
    }

    [Fact]
    public void SelectWorker_PrefersLeastPrivilegeSpecialist()
    {
        var selection = FullDuplexRunEngine.SelectWorker(Configuration(), Task(
            capabilities: ["tool.file"],
            tools: ["read_file"]));

        Assert.Equal("worker.file", selection.Agent.Id);
        Assert.Equal("specialist_capability_and_tool_match", selection.Reason);
    }

    [Theory]
    [InlineData("worker.code", "tool.code", "write_file")]
    [InlineData("worker.document", "tool.document", "read_file")]
    [InlineData("worker.data", "tool.data", "shell.execute")]
    [InlineData("worker.browser", "tool.browser", "browser.fetch")]
    [InlineData("worker.file", "tool.file", "read_file")]
    [InlineData("worker.general", "task.execute", null)]
    [InlineData("worker.git", "tool.git", "git_status")]
    public void SelectWorker_RoutesEveryOfficeWorkerCategory(
        string expectedSlug,
        string capability,
        string? tool)
    {
        var selection = FullDuplexRunEngine.SelectWorker(
            Configuration(),
            Task(capabilities: [capability], tools: tool is null ? [] : [tool]));

        Assert.Equal(expectedSlug, selection.Agent.Id);
    }

    [Fact]
    public void SelectWorker_UsesRosterOrderThenSlugAsStableTieBreakers()
    {
        var alpha = Agent("worker.alpha", "execution", "task_executor", ["tool.shared"], ["read_file"], 4);
        var zulu = Agent("worker.zulu", "execution", "task_executor", ["tool.shared"], ["read_file"], 3);
        var byOrder = FullDuplexRunEngine.SelectWorker(
            Configuration([alpha, zulu]),
            Task(capabilities: ["tool.shared"], tools: ["read_file"]));
        Assert.Equal("worker.zulu", byOrder.Agent.Id);

        var tiedZulu = zulu with { RosterOrder = 4 };
        var bySlug = FullDuplexRunEngine.SelectWorker(
            Configuration([tiedZulu, alpha]),
            Task(capabilities: ["tool.shared"], tools: ["read_file"]));
        Assert.Equal("worker.alpha", bySlug.Agent.Id);
    }

    [Fact]
    public void SelectWorker_UsesGeneralOnlyWhenItExplicitlyCoversRequirements()
    {
        var selection = FullDuplexRunEngine.SelectWorker(Configuration(), Task(
            capabilities: ["task.execute"]));

        Assert.Equal("worker.general", selection.Agent.Id);
        Assert.Equal("general_fallback", selection.Reason);
    }

    [Fact]
    public void SelectWorker_FallsBackToAvailableWorkerWhenRosterHasNoGeneral()
    {
        // 问答/规划/规范 模式的执行层是精简 worker 子集、不含 worker.general。
        // 无要求任务（如通用提问）此前会 fail-closed 抛 WorkerUnavailableException，
        // 现应回退到在册 worker，避免整段对话不可用。
        var browser = Agent("worker.browser", "execution", "task_executor", ["tool.search", "tool.browser"], ["browser.fetch"], 4);

        var selection = FullDuplexRunEngine.SelectWorker(Configuration([browser]), Task());

        Assert.Equal("worker.browser", selection.Agent.Id);
        Assert.Equal("general_unavailable_fallback", selection.Reason);
    }

    [Fact]
    public void SelectWorker_FailsClosedForUnsupportedCapabilityOrTool()
    {
        var capability = Assert.Throws<WorkerUnavailableException>(() =>
            FullDuplexRunEngine.SelectWorker(Configuration(), Task(capabilities: ["tool.presentation"])));
        Assert.Contains("No frozen execution specialist", capability.Message, StringComparison.Ordinal);

        var tool = Assert.Throws<InvalidDataException>(() =>
            FullDuplexRunEngine.SelectWorker(Configuration(), Task(tools: ["missing_tool"])));
        Assert.Contains("not in the frozen run manifest", tool.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveWorker_PreservesDurableAssignmentAndRejectsDrift()
    {
        var configuration = Configuration();
        var task = Task(capabilities: ["tool.file"], tools: ["read_file"]);
        var selected = FullDuplexRunEngine.SelectWorker(configuration, task);
        task.WorkerAgentSlug = selected.Agent.Id;
        task.WorkerAgentDefinitionId = selected.Agent.AgentDefinitionId;
        task.WorkerAgentVersionId = selected.Agent.AgentVersionId;
        task.WorkerAgentVersionHash = selected.Agent.VersionContentHash;
        task.WorkerAssignmentReason = selected.Reason;

        var recovered = FullDuplexRunEngine.ResolveOrSelectWorker(configuration, task);
        Assert.Equal("worker.file", recovered.Agent.Id);
        Assert.Equal(selected.Agent.AgentVersionId, recovered.Agent.AgentVersionId);

        task.WorkerAgentVersionHash = "tampered";
        Assert.Throws<InvalidDataException>(() => FullDuplexRunEngine.ResolveOrSelectWorker(configuration, task));
    }

    private static FrozenRunConfigurationV1 Configuration(IReadOnlyList<RuntimeAgentDefinition>? workers = null)
    {
        var meeting = Agent("meeting", "operation", "session_coordinator", [], [], 0);
        var supervisor = Agent("supervisor", "operation", "quality_controller", ["supervision.review"], [], 1);
        var planner = Agent("task_planner", "execution", "execution_coordinator", ["task.plan", "agent.create_temporary"], ["*"], 0);
        var code = Agent("worker.code", "execution", "task_executor", ["tool.code", "tool.file"], ["read_file", "write_file"], 1);
        var document = Agent("worker.document", "execution", "task_executor", ["tool.document"], ["read_file", "write_file"], 2);
        var data = Agent("worker.data", "execution", "task_executor", ["tool.data"], ["read_file", "shell.execute"], 3);
        var browser = Agent("worker.browser", "execution", "task_executor", ["tool.search", "tool.browser"], ["browser.search", "browser.fetch", "mcp_search", "mcp_invoke"], 4);
        var file = Agent("worker.file", "execution", "task_executor", ["tool.file"], ["read_file", "write_file"], 5);
        var general = Agent("worker.general", "execution", "task_executor", ["task.execute"], ["*"], 6);
        var git = Agent("worker.git", "execution", "git_specialist", ["tool.git"], ["git_status", "git_diff", "git_commit"], 7);
        workers ??= [code, document, data, browser, file, general, git];
        return new FrozenRunConfigurationV1(
            "frozen-run-configuration/v1",
            "baseline",
            1,
            "space",
            "auto",
            "mode:test:1",
            "ask",
            new SpawnPolicy(2, 8, 4),
            new SchedulingPolicy(2, 2, true),
            new SupervisionPolicy(true, 2),
            new ContextPolicy(8192, 24, true),
            new MemoryPolicy(true, 8, ["workspace"], ["fact"]),
            new ToolRuntimePolicy("tinadec-tools", true, true, 120, 4),
            [meeting, supervisor],
            [planner, .. workers],
            null,
            [])
        {
            ToolManifestProtocolVersion = 2,
            ToolManifest = [
                Tool("read_file"), Tool("write_file"), Tool("shell.execute"), Tool("mcp_invoke"),
                Tool("browser.search"), Tool("browser.fetch"), Tool("mcp_search"),
                Tool("git_status"), Tool("git_diff"), Tool("git_commit")
            ]
        };
    }

    private static FrozenToolManifestEntry Tool(string id) =>
        new(
            id,
            $"{id} test tool.",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            "read",
            false,
            false,
            "safe",
            []);

    private static RuntimeAgentDefinition Agent(
        string id,
        string layer,
        string role,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> tools,
        int order) =>
        new(id, layer, role, id.StartsWith("worker.", StringComparison.Ordinal) ? "on_demand" : "task", capabilities,
            DirectUserOutput: id == "meeting", "read")
        {
            AgentDefinitionId = StableGuid("definition:" + id),
            AgentVersionId = StableGuid("version:" + id),
            VersionContentHash = "hash-" + id,
            AllowedTools = tools,
            RosterOrder = order
        };

    private static DurableTaskNode Task(
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? tools = null) =>
        new()
        {
            TaskId = Guid.NewGuid(),
            TaskKey = "task-1",
            Title = "Test task",
            RequiredCapabilities = capabilities?.ToList() ?? [],
            RequiredTools = tools?.ToList() ?? []
        };

    private static Guid StableGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
