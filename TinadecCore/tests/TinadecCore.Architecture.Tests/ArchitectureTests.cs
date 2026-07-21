using NetArchTest.Rules;
using System.Reflection;

namespace TinadecCore.Architecture.Tests;

public sealed class ArchitectureTests
{
    private static readonly Assembly ContractsAssembly = typeof(Contracts.Dtos.HealthResponseDto).Assembly;
    private static readonly Assembly AbstractionsAssembly = typeof(Abstractions.ITinadecCoreBuilder).Assembly;
    private static readonly Assembly PersistenceAssembly = typeof(Persistence.ServiceCollectionExtensions).Assembly;
    private static readonly Assembly VectorStoreAssembly = typeof(VectorStore.VectorStoreModuleRegistrar).Assembly;
    private static readonly Assembly StrategiesAssembly = typeof(Strategies.ContextBudget).Assembly;
    private static readonly Assembly DmaEAAssembly = typeof(DmaEA.DmaEAModuleRegistrar).Assembly;
    private static readonly Assembly ModelsAssembly = typeof(Models.ModelsModuleRegistrar).Assembly;
    private static readonly Assembly ContextAssembly = typeof(Context.ContextModuleRegistrar).Assembly;
    private static readonly Assembly PromptsAssembly = typeof(Prompts.PromptsModuleRegistrar).Assembly;
    private static readonly Assembly MemoryAssembly = typeof(Memory.MemoryModuleRegistrar).Assembly;
    private static readonly Assembly SkillsAssembly = typeof(Skills.SkillsModuleRegistrar).Assembly;
    private static readonly Assembly LoopGuardAssembly = typeof(LoopGuard.LoopGuardModuleRegistrar).Assembly;
    private static readonly Assembly LifecycleAssembly = typeof(Lifecycle.LifecycleModuleRegistrar).Assembly;
    private static readonly Assembly RuntimeAssembly = typeof(Runtime.TinadecCoreBuilder).Assembly;
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private static readonly Assembly[] AllModuleAssemblies =
    [
        ContractsAssembly, AbstractionsAssembly, PersistenceAssembly, VectorStoreAssembly, StrategiesAssembly,
        DmaEAAssembly, ModelsAssembly, ContextAssembly, PromptsAssembly,
        MemoryAssembly, SkillsAssembly, LoopGuardAssembly, LifecycleAssembly,
        RuntimeAssembly, ApiAssembly
    ];

    private static readonly Assembly[] NonApiModuleAssemblies =
    [
        ContractsAssembly, AbstractionsAssembly, PersistenceAssembly, VectorStoreAssembly, StrategiesAssembly,
        DmaEAAssembly, ModelsAssembly, ContextAssembly, PromptsAssembly,
        MemoryAssembly, SkillsAssembly, LoopGuardAssembly, LifecycleAssembly,
        RuntimeAssembly
    ];

    [Fact]
    public void ContractsDoesNotDependOnMaf_AspNet_FSharp()
    {
        var result = Types.InAssembly(ContractsAssembly)
            .Should().NotHaveDependencyOn("Microsoft.Agents.AI")
            .And().NotHaveDependencyOn("Microsoft.AspNetCore")
            .And().NotHaveDependencyOn("FSharp.Core")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailures(result));
    }

    [Fact]
    public void OnlyApiProjectUsesWebSdk()
    {
        // The API project should reference ASP.NET Core.
        var apiResult = Types.InAssembly(ApiAssembly)
            .That().ResideInNamespace("TinadecCore.Api")
            .Should().HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(apiResult.IsSuccessful, FormatFailures(apiResult));

        // No non-API module should reference Microsoft.AspNetCore.
        foreach (var asm in NonApiModuleAssemblies)
        {
            var result = Types.InAssembly(asm)
                .Should().NotHaveDependencyOn("Microsoft.AspNetCore")
                .GetResult();
            Assert.True(result.IsSuccessful, $"{asm.GetName().Name} should not depend on ASP.NET Core.\n{FormatFailures(result)}");
        }
    }

    [Fact]
    public void ModulesDoNotReferenceEachOtherDirectly()
    {
        var businessModules = new[]
        {
            ("DmaEA", DmaEAAssembly),
            ("Models", ModelsAssembly),
            ("Context", ContextAssembly),
            ("Prompts", PromptsAssembly),
            ("Memory", MemoryAssembly),
            ("Skills", SkillsAssembly),
            ("LoopGuard", LoopGuardAssembly),
            ("Lifecycle", LifecycleAssembly),
        };

        foreach (var (name, asm) in businessModules)
        {
            foreach (var (siblingName, siblingAsm) in businessModules)
            {
                if (name == siblingName) continue;
                var result = Types.InAssembly(asm)
                    .Should().NotHaveDependencyOn(siblingAsm.GetName().Name)
                    .GetResult();
                Assert.True(result.IsSuccessful,
                    $"{name} should not depend on {siblingName}.\n{FormatFailures(result)}");
            }
        }
    }

    [Fact]
    public void CoreModulesDoNotReferenceGateway_Desktop_TinadecTools()
    {
        var forbiddenDeps = new[] { "TinadecGateway", "TinadecTools", "desktop" };

        foreach (var asm in AllModuleAssemblies)
        {
            foreach (var dep in forbiddenDeps)
            {
                var result = Types.InAssembly(asm)
                    .Should().NotHaveDependencyOn(dep)
                    .GetResult();
                Assert.True(result.IsSuccessful,
                    $"{asm.GetName().Name} should not depend on {dep}.\n{FormatFailures(result)}");
            }
        }
    }

    [Fact]
    public void MafTypesDoNotLeakIntoContracts()
    {
        var result = Types.InAssembly(ContractsAssembly)
            .Should().NotHaveDependencyOn("Microsoft.Agents.AI")
            .And().NotHaveDependencyOn("Microsoft.Extensions.AI")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailures(result));
    }

    [Fact]
    public void PersistenceDoesNotDependOnBusinessModulesOrApi()
    {
        var forbidden = new[]
        {
            "TinadecCore.DmaEA",
            "TinadecCore.Models",
            "TinadecCore.Context",
            "TinadecCore.Prompts",
            "TinadecCore.Memory",
            "TinadecCore.Skills",
            "TinadecCore.LoopGuard",
            "TinadecCore.Lifecycle",
            "TinadecCore.Runtime",
            "TinadecCore.Api",
            "TinadecGateway",
            "TinadecTools"
        };

        foreach (var dep in forbidden)
        {
            var result = Types.InAssembly(PersistenceAssembly)
                .Should().NotHaveDependencyOn(dep)
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"Persistence should not depend on {dep}.\n{FormatFailures(result)}");
        }
    }

    [Fact]
    public void VectorStoreDependsOnlyOnFoundationCapabilities()
    {
        var forbidden = new[]
        {
            "TinadecCore.Models", "TinadecCore.Context", "TinadecCore.Memory", "TinadecCore.Runtime", "TinadecCore.Api"
        };

        foreach (var dep in forbidden)
        {
            var result = Types.InAssembly(VectorStoreAssembly).Should().NotHaveDependencyOn(dep).GetResult();
            Assert.True(result.IsSuccessful, $"VectorStore should not depend on {dep}.\n{FormatFailures(result)}");
        }
    }

    [Fact]
    public void SharedStorageModelsDoNotDependOnPostgreSqlTypes()
    {
        foreach (var assembly in new[] { MemoryAssembly, LifecycleAssembly })
        {
            var result = Types.InAssembly(assembly)
                .Should().NotHaveDependencyOn("Npgsql")
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} should retain provider-neutral shared entity mappings.\n{FormatFailures(result)}");
        }
    }

    [Fact]
    public void ContractsDoesNotDependOnEfCoreOrDatabaseProviders()
    {
        var result = Types.InAssembly(ContractsAssembly)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .And().NotHaveDependencyOn("Microsoft.Data.Sqlite")
            .And().NotHaveDependencyOn("Npgsql")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailures(result));
    }

    [Fact]
    public void EventEnvelopeDoesNotExposeMafOrFSharpTypes()
    {
        var eventEnvelopeType = typeof(Contracts.Events.EventEnvelope);
        var properties = eventEnvelopeType.GetProperties();

        foreach (var prop in properties)
        {
            var propType = prop.PropertyType;
            Assert.False(propType.Namespace?.StartsWith("Microsoft.Agents"),
                $"EventEnvelope.{prop.Name} exposes MAF type {propType.FullName}");
            Assert.False(propType.Namespace?.StartsWith("FSharp"),
                $"EventEnvelope.{prop.Name} exposes F# type {propType.FullName}");
        }
    }

    private static string FormatFailures(TestResult result) =>
        result.IsSuccessful ? "" : string.Join("\n", result.FailingTypeNames);
}
