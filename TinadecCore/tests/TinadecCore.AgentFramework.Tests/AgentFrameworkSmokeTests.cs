using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The trimmed composition stays trimmed. The full one is what every Api test hosts.
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
}
