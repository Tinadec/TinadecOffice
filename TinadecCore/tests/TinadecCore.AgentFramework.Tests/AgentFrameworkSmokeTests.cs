using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// MAF smoke tests using a fake IChatClient. No external model access.
/// </summary>
public sealed class AgentFrameworkSmokeTests
{
    [Fact]
    public void CanCreateFakeChatClient()
    {
        // Verify that a fake IChatClient can be created and returns expected response.
        var fakeClient = new FakeChatClient();
        Assert.NotNull(fakeClient);
    }

    [Fact]
    public async Task FakeChatClientReturnsResponse()
    {
        var fakeClient = new FakeChatClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello")
        };

        var response = await fakeClient.GetResponseAsync(messages);
        Assert.NotNull(response);
        Assert.Equal("Mock response", response.Text);
    }

    [Fact]
    public void FullCompositionRegistersAllEightModules()
    {
        var services = new ServiceCollection();
        var builder = services.AddTinadecCore();

        var modules = builder.GetRegisteredModules();
        Assert.Equal(8, modules.Count);

        var moduleIds = modules.Select(m => m.ModuleId).ToHashSet();
        Assert.Contains("dma_ea", moduleIds);
        Assert.Contains("models", moduleIds);
        Assert.Contains("context", moduleIds);
        Assert.Contains("prompts", moduleIds);
        Assert.Contains("memory", moduleIds);
        Assert.Contains("skills", moduleIds);
        Assert.Contains("loop_guard", moduleIds);
        Assert.Contains("lifecycle", moduleIds);
    }

    [Fact]
    public void MinimalCompositionRegistersOnlyThreeModules()
    {
        // Trimming test: only DmaEA, Models, Lifecycle are registered.
        // Memory/Skills/etc. are not runtime-required dependencies.
        var services = new ServiceCollection();
        var builder = services.AddTinadecCoreMinimal();

        var modules = builder.GetRegisteredModules();
        Assert.Equal(3, modules.Count);

        var moduleIds = modules.Select(m => m.ModuleId).ToHashSet();
        Assert.Contains("dma_ea", moduleIds);
        Assert.Contains("models", moduleIds);
        Assert.Contains("lifecycle", moduleIds);

        // Verify that Memory/Skills/Context/Prompts/LoopGuard are NOT registered.
        Assert.DoesNotContain("memory", moduleIds);
        Assert.DoesNotContain("skills", moduleIds);
        Assert.DoesNotContain("context", moduleIds);
        Assert.DoesNotContain("prompts", moduleIds);
        Assert.DoesNotContain("loop_guard", moduleIds);
    }

    [Fact]
    public async Task LoopGuardEvaluatesContext()
    {
        var services = new ServiceCollection();
        services.AddTinadecCore();
        var provider = services.BuildServiceProvider();
        var loopGuard = provider.GetRequiredService<ILoopGuard>();

        // Under limits: should continue.
        var context = new LoopGuardContext
        {
            Iteration = 1,
            MaxIterations = 25,
            TokenBudget = 8192,
            TokensUsed = 100,
            ToolCallCount = 2,
            MaxToolCalls = 50,
            ConsecutiveErrors = 0,
            MaxConsecutiveErrors = 3
        };

        var decision = await loopGuard.EvaluateAsync("session-1", "run-1", context);
        Assert.True(decision.ShouldContinue);
    }

    [Fact]
    public async Task LoopGuardStopsOnIterationLimit()
    {
        var services = new ServiceCollection();
        services.AddTinadecCore();
        var provider = services.BuildServiceProvider();
        var loopGuard = provider.GetRequiredService<ILoopGuard>();

        var context = new LoopGuardContext
        {
            Iteration = 25,
            MaxIterations = 25,
            TokenBudget = 8192,
            TokensUsed = 100,
            ToolCallCount = 2,
            MaxToolCalls = 50,
            ConsecutiveErrors = 0,
            MaxConsecutiveErrors = 3
        };

        var decision = await loopGuard.EvaluateAsync("session-1", "run-1", context);
        Assert.False(decision.ShouldContinue);
        Assert.Contains("Iteration limit", decision.Reason);
    }

    [Fact]
    public async Task LifecycleManagerStartsAndCompletesRun()
    {
        var services = new ServiceCollection();
        services.AddTinadecCore();
        var provider = services.BuildServiceProvider();
        var lifecycle = provider.GetRequiredService<ILifecycleManager>();

        var runId = await lifecycle.StartRunAsync("session-1");
        Assert.False(string.IsNullOrEmpty(runId));

        var state = await lifecycle.GetRunStateAsync(runId);
        Assert.Equal(RunStatus.Running, state.Status);

        await lifecycle.CompleteRunAsync(runId);
        var completedState = await lifecycle.GetRunStateAsync(runId);
        Assert.Equal(RunStatus.Completed, completedState.Status);
        Assert.NotNull(completedState.CompletedAt);
    }

    /// <summary>
    /// Simple fake IChatClient for smoke testing. No external model access.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Mock response"));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return GetStreamingResponseAsyncCore(cancellationToken);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsyncCore(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Mock");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " response");
        }
    }
}
