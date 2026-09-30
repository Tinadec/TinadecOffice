using Microsoft.Extensions.AI;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>Hard insert (todo D2): the interrupt signal and the record an interrupted worker reads.</summary>
public sealed class RunInterruptsTests
{
    private static RunInterruptRequest Steering(string text = "stop") => new(Guid.NewGuid(), text, DateTimeOffset.UtcNow);

    [Fact]
    public void Request_CutsOffTheRunsCallsOnly_AndStaysPendingUntilTaken()
    {
        var registry = new RunInterruptRegistry();
        var run = Guid.NewGuid();
        using var mine = registry.Track(run);
        using var other = registry.Track(Guid.NewGuid());

        var request = Steering();
        Assert.Equal(1, registry.Request(run, request));
        Assert.True(mine.Token.IsCancellationRequested);
        Assert.Same(request, mine.Request);
        Assert.False(other.Token.IsCancellationRequested);

        Assert.Same(request, registry.PeekPending(run));
        Assert.Same(request, registry.TakePending(run));
        Assert.Null(registry.TakePending(run));
    }

    /// <summary>A call that starts before the engine acted on the request may have missed the steering.</summary>
    [Fact]
    public void ACallStartedWhileARequestIsPending_IsCutOffToo_ButNotOnceItWasTaken()
    {
        var registry = new RunInterruptRegistry();
        var run = Guid.NewGuid();
        Assert.Equal(0, registry.Request(run, Steering()));

        using (var early = registry.Track(run)) Assert.True(early.Interrupted);
        registry.TakePending(run);
        using var later = registry.Track(run);
        Assert.False(later.Interrupted);
        Assert.False(later.Token.IsCancellationRequested);
    }

    [Fact]
    public void AnEndedCallIsNotSignalled()
    {
        var registry = new RunInterruptRegistry();
        var run = Guid.NewGuid();
        registry.Track(run).Dispose();
        Assert.Equal(0, registry.Request(run, Steering()));
    }

    /// <summary>
    /// The interrupted round sits in the transcript where it happened — after the whole tool round
    /// before it, never between a call and its result — with what the worker had said and the steering.
    /// </summary>
    [Fact]
    public async Task TheWorkerReadsTheInterruptionWhereItHappened()
    {
        var client = new CapturingClient();
        var agent = new ExecutionAgent(new Factory(client));
        var history = new List<WorkerToolTurn>
        {
            new() { CallId = "c1", ToolId = "read_file", ArgumentsJson = "{}", ResultJson = "{\"ok\":true}" },
            new() { CallId = "c2", ToolId = "read_file", ArgumentsJson = "{}", ResultJson = "{\"ok\":true}" }
        };
        var interruptions = new[] { new WorkerInterruption(1, "STEER-9 只看 a", "我正在读第二个文件", 0, DateTimeOffset.UtcNow) };

        await agent.GetNextTurnAsync(new DmaeaRunContext { RunId = Guid.NewGuid(), SessionId = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), TriggerMessageId = Guid.NewGuid(), UserGoal = "goal" },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "worker", Layer = "execution", AgentType = "worker", Enabled = true },
            new PlannedTask { Title = "t", SuccessCriteria = ["done"] },
            history, [], null, 64, CancellationToken.None, interruptions);

        var messages = client.Messages!;
        var note = messages.FindIndex(message => message.Role == ChatRole.User && message.Text.Contains("[用户打断]", StringComparison.Ordinal));
        Assert.True(note > 0);
        Assert.Contains("STEER-9 只看 a", messages[note].Text);
        Assert.Equal("我正在读第二个文件", messages[note - 1].Text);
        // After c1's result, before c2's call.
        Assert.Contains(messages[note - 2].Contents, content => content is FunctionResultContent { CallId: "c1" });
        Assert.Contains(messages[note + 1].Contents, content => content is FunctionCallContent { CallId: "c2" });
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
