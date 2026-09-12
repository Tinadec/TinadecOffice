using NetArchTest.Rules;
using System.Reflection;

namespace TinadecCore.Architecture.Tests;

/// <summary>
/// Dependency rules introduced with the DmaEA graph orchestration round:
/// ① MAF (Microsoft.Agents.*) types stay inside the DmaEA adapter assembly —
///    no other module may depend on them (the adapter boundary).
/// ② Graph-orchestration configuration entities (agent_templates / mode_bindings /
///    tool_definitions) live in the AgentConfiguration assembly, and the dependency
///    direction is one-way: DmaEA consumes configuration only through Abstractions /
///    Persistence ports, never through the AgentConfiguration assembly.
/// </summary>
public sealed class GraphOrchestrationArchitectureTests
{
    private static readonly Assembly DmaEAAssembly = typeof(DmaEA.DmaEAModuleRegistrar).Assembly;
    private static readonly Assembly AgentConfigurationAssembly = typeof(TinadecCore.AgentConfiguration.AgentConfigurationDbContext).Assembly;

    private static readonly Assembly[] NonAdapterModuleAssemblies =
    [
        typeof(Contracts.Dtos.HealthResponseDto).Assembly,
        typeof(Abstractions.ITinadecCoreBuilder).Assembly,
        typeof(Persistence.ServiceCollectionExtensions).Assembly,
        typeof(Strategies.ContextBudget).Assembly,
        typeof(Models.ModelsModuleRegistrar).Assembly,
        typeof(Context.ContextModuleRegistrar).Assembly,
        typeof(Prompts.PromptsModuleRegistrar).Assembly,
        typeof(Memory.MemoryModuleRegistrar).Assembly,
        typeof(Skills.SkillsModuleRegistrar).Assembly,
        typeof(LoopGuard.LoopGuardModuleRegistrar).Assembly,
        typeof(Lifecycle.LifecycleModuleRegistrar).Assembly,
        typeof(Governance.GovernanceModuleRegistrar).Assembly,
        typeof(Runtime.TinadecCoreBuilder).Assembly,
        typeof(TinadecCore.AspNetCore.TinadecCoreHttpExtensions).Assembly,
        typeof(Program).Assembly
    ];

    [Fact]
    public void MafTypesStayInsideTheDmaEAAdapter()
    {
        var failures = new List<string>();
        foreach (var assembly in NonAdapterModuleAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .Should().NotHaveDependencyOn("Microsoft.Agents.AI")
                .And().NotHaveDependencyOn("Microsoft.Agents")
                .GetResult();
            if (!result.IsSuccessful) failures.AddRange(result.FailingTypeNames.Select(name => $"{assembly.GetName().Name}: {name}"));
        }
        Assert.True(failures.Count == 0, "MAF types leaked outside the DmaEA adapter:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DmaEAAndAgentConfigurationStayDecoupled()
    {
        var dmaeaResult = Types.InAssembly(DmaEAAssembly)
            .Should().NotHaveDependencyOn("TinadecCore.AgentConfiguration")
            .GetResult();
        Assert.True(dmaeaResult.IsSuccessful,
            $"DmaEA must consume configuration through ports, not the AgentConfiguration assembly.\n{FormatFailures(dmaeaResult)}");

        var agentConfigResult = Types.InAssembly(AgentConfigurationAssembly)
            .Should().NotHaveDependencyOn("TinadecCore.DmaEA")
            .GetResult();
        Assert.True(agentConfigResult.IsSuccessful,
            $"AgentConfiguration must not depend on the DmaEA runtime.\n{FormatFailures(agentConfigResult)}");
    }

    private static string FormatFailures(TestResult result) =>
        result.IsSuccessful ? "" : string.Join("\n", result.FailingTypeNames);
}
