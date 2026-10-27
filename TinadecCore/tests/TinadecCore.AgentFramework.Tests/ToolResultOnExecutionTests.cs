using Microsoft.Extensions.AI;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// A large tool result stays on its execution row: the checkpoint keeps a bounded head, and the
/// worker conversation is handed the full text back.
/// </summary>
public sealed class ToolResultOnExecutionTests
{
    [Fact]
    public void Head_IsBounded_NamesTheExecution_AndNeverSplitsASurrogatePair()
    {
        // The emoji's high surrogate sits exactly at the cut.
        var result = new string('a', 2047) + "😀" + new string('b', 100_000);

        var head = FullDuplexRunEngine.ToolResultHead(result, "exec-1");

        Assert.True(head.Length < 2_200, $"head is {head.Length} characters");
        Assert.Equal(new string('a', 2047), head[..2047]);
        Assert.Equal('\n', head[2047]);
        Assert.Contains("exec-1", head);
        Assert.Contains(result.Length.ToString(), head);
    }

    [Fact]
    public async Task TheWorkerReadsTheFullText_OfATurnKeptOnItsExecution()
    {
        var full = "{\"lines\":\"" + new string('x', 40_000) + "-END-OF-FULL\"}";
        var client = new CapturingClient();
        var history = new List<WorkerToolTurn>
        {
            new() { CallId = "big", ToolId = "read_file", ArgumentsJson = "{}", ExecutionId = Guid.NewGuid().ToString(),
                ResultJson = FullDuplexRunEngine.ToolResultHead(full, "exec-big"), ResultOnExecution = true },
            // Not kept on its execution: a stray entry for it must not replace what the turn holds.
            new() { CallId = "small", ToolId = "ls", ArgumentsJson = "{}", ResultJson = "{\"ok\":true}" }
        };
        var fullResults = new Dictionary<string, string> { ["big"] = full, ["small"] = "{\"replaced\":true}" };

        await new ExecutionAgent(new Factory(client)).GetNextTurnAsync(
            new DmaeaRunContext { RunId = Guid.NewGuid(), SessionId = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), TriggerMessageId = Guid.NewGuid(), UserGoal = "goal" },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "worker", Layer = "execution", AgentType = "worker", Enabled = true },
            new PlannedTask { Title = "t", SuccessCriteria = ["done"] },
            history, [], null, 64, CancellationToken.None, fullResults: fullResults);

        var results = client.Messages!.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .ToDictionary(result => result.CallId, result => result.Result?.ToString() ?? string.Empty);
        Assert.Contains("-END-OF-FULL", results["big"]);
        Assert.DoesNotContain("kept with tool execution", results["big"]);
        Assert.Contains("\"ok\"", results["small"]);
        Assert.DoesNotContain("replaced", results["small"]);
    }

    [Fact]
    public async Task AMissingExecution_LeavesTheHeadInPlace()
    {
        var client = new CapturingClient();
        var head = FullDuplexRunEngine.ToolResultHead("{\"lines\":\"" + new string('x', 40_000) + "\"}", "exec-gone");
        var history = new List<WorkerToolTurn>
        {
            new() { CallId = "big", ToolId = "read_file", ArgumentsJson = "{}", ResultJson = head, ResultOnExecution = true }
        };

        await new ExecutionAgent(new Factory(client)).GetNextTurnAsync(
            new DmaeaRunContext { RunId = Guid.NewGuid(), SessionId = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), TriggerMessageId = Guid.NewGuid(), UserGoal = "goal" },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "worker", Layer = "execution", AgentType = "worker", Enabled = true },
            new PlannedTask { Title = "t", SuccessCriteria = ["done"] },
            history, [], null, 64, CancellationToken.None);

        var result = Assert.Single(client.Messages!.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Contains("kept with tool execution exec-gone", result.Result?.ToString());
    }

    private sealed class Factory(IChatClient client) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, Model = "fake", ModelId = "openai/fake" });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) => Task.FromResult(client);
    }

    private sealed class CapturingClient : IChatClient
    {
        public List<ChatMessage>? Messages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
