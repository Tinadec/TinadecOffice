using Microsoft.Extensions.DependencyInjection;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Services;

namespace TinadecCore.Tests;

public sealed class ProviderModuleTests
{
    [Fact]
    public void FakeProviderModuleRegistersRuntimeAndCapabilities()
    {
        var services = new ServiceCollection();

        services.AddModelProviderModule<FakeProviderModule>();

        using var provider = services.BuildServiceProvider();
        var runtimes = provider.GetServices<IModelProviderRuntime>();
        var catalog = provider.GetRequiredService<IModelProviderModuleCatalog>();

        Assert.Contains(runtimes, runtime => runtime.Id == FakeProviderRuntime.RuntimeId);
        var module = Assert.Single(catalog.ListModules());
        Assert.Equal(FakeProviderModule.Family, module.ProviderFamily);
        Assert.True(module.Capabilities.SupportsJsonMode);
        Assert.Equal(ProviderHealthStatus.Healthy, module.Capabilities.HealthStatus);
    }

    [Fact]
    public void DuplicateProviderFamilyThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        services.AddModelProviderModule<FakeProviderModule>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddModelProviderModule<DuplicateFakeProviderModule>());

        Assert.Contains(FakeProviderModule.Family, exception.Message);
    }

    [Fact]
    public async Task ModuleRuntimeIsDiscoveredByModelInvocationRuntimeWithoutConditionalChanges()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModelRouteResolver>(new FixedRouteResolver(FakeProviderModule.Family));
        services.AddSingleton<IModelCredentialResolver, NullCredentialResolver>();
        services.AddModelProviderModule<FakeProviderModule>();
        services.AddSingleton<IModelInvocationRuntime, ModelInvocationRuntime>();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IModelInvocationRuntime>();

        var result = await runtime.InvokeAsync("session_1", "planner", []);

        Assert.Equal("executed", result.Status);
        Assert.Equal("fake provider response", result.Content);
        Assert.Equal(FakeProviderRuntime.RuntimeId, result.RuntimeId);
    }

    private sealed class FakeProviderModule : IModelProviderModule
    {
        public const string Family = "fake-provider";

        public string ProviderFamily => Family;

        public void RegisterServices(IServiceCollection services)
        {
            services.AddSingleton<IModelProviderRuntime>(new FakeProviderRuntime());
        }

        public ProviderCapabilityDto GetCapabilities()
        {
            return new ProviderCapabilityDto(
                SupportsStreaming: false,
                SupportsTools: true,
                SupportsJsonMode: true,
                SupportsSystemPrompt: true,
                MaxContextTokens: 128000,
                RequiresWorkspace: false,
                CredentialKind: "test",
                HealthStatus: ProviderHealthStatus.Healthy);
        }
    }

    private sealed class DuplicateFakeProviderModule : IModelProviderModule
    {
        public string ProviderFamily => FakeProviderModule.Family;

        public void RegisterServices(IServiceCollection services)
        {
            services.AddSingleton<IModelProviderRuntime>(new FakeProviderRuntime());
        }

        public ProviderCapabilityDto GetCapabilities()
        {
            return new ProviderCapabilityDto(false, false, false, false, null, false, "test", ProviderHealthStatus.Unknown);
        }
    }

    private sealed class FakeProviderRuntime : IModelProviderRuntime
    {
        public const string RuntimeId = "fake-runtime";

        public string Id => RuntimeId;

        public bool CanHandle(ResolvedModelInvocationContextDto context)
        {
            return string.Equals(context.ConnectionKind, FakeProviderModule.Family, StringComparison.OrdinalIgnoreCase);
        }

        public Task<ModelInvocationResultDto> GenerateAsync(
            ResolvedModelInvocationContextDto context,
            string? apiKey,
            IReadOnlyList<MessageDto> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<ModelToolSpecDto>? tools = null)
        {
            return Task.FromResult(new ModelInvocationResultDto(
                "executed",
                "fake provider response",
                context,
                false,
                RuntimeId));
        }
    }

    private sealed class FixedRouteResolver(string connectionKind) : IModelRouteResolver
    {
        public ResolvedModelInvocationContextDto Resolve(string purpose)
        {
            return new ResolvedModelInvocationContextDto(
                purpose,
                null,
                null,
                "https://fake-provider.test/v1",
                "fake-model",
                null,
                null,
                connectionKind,
                "fake_provider_1",
                false);
        }
    }

    private sealed class NullCredentialResolver : IModelCredentialResolver
    {
        public string? ResolveApiKey(ResolvedModelInvocationContextDto context)
        {
            return null;
        }
    }
}
