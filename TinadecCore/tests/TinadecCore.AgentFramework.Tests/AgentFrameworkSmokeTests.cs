using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// What the composition roots must and must not register. The full one is what every Api test
/// hosts, so only what those tests cannot notice is checked here.
/// </summary>
public sealed class AgentFrameworkSmokeTests
{
    [Fact]
    public void MinimalCompositionRegistersTenancyAndThreeRuntimeModules()
    {
        // Trimming test: only DmaEA, Models, Lifecycle are registered.
        // Memory/Skills/etc. are not runtime-required dependencies.
        var services = new ServiceCollection();
        var builder = services.AddTinadecCoreMinimal();

        var modules = builder.GetRegisteredModules();
        Assert.Equal(4, modules.Count);

        var moduleIds = modules.Select(m => m.ModuleId).ToHashSet();
        Assert.Contains("dma_ea", moduleIds);
        Assert.Contains("models", moduleIds);
        Assert.Contains("lifecycle", moduleIds);
        Assert.Contains("tenancy", moduleIds);

        // Verify that Memory/Skills/Context/Prompts/LoopGuard are NOT registered.
        Assert.DoesNotContain("memory", moduleIds);
        Assert.DoesNotContain("skills", moduleIds);
        Assert.DoesNotContain("context", moduleIds);
        Assert.DoesNotContain("prompts", moduleIds);
        Assert.DoesNotContain("loop_guard", moduleIds);
    }

    /// <summary>
    /// The engine takes its loop guard optionally (<c>GetService</c>, not required) and skips every
    /// veto without one, so a full composition that lost the registration would still run, just
    /// without iteration, budget or repeat-call protection — and no end-to-end test would notice.
    /// </summary>
    [Fact]
    public async Task FullComposition_ProvidesTheLoopGuardTheEngineTakesOptionally()
    {
        var services = new ServiceCollection();
        services.AddTinadecCore();
        using var provider = services.BuildServiceProvider();
        var loopGuard = provider.GetService<ILoopGuard>();
        Assert.NotNull(loopGuard);

        var decision = await loopGuard.EvaluateAsync("session-1", "run-1", new LoopGuardContext
        {
            Iteration = 25, MaxIterations = 25, TokenBudget = 8192, TokensUsed = 100,
            ToolCallCount = 2, MaxToolCalls = 50, ConsecutiveErrors = 0, MaxConsecutiveErrors = 3,
        });
        Assert.False(decision.ShouldContinue);
    }
}
