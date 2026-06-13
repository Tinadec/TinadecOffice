using Tinadec.Contracts.Models;
using Tinadec.Contracts.Security;
using TinadecCore.Abstractions;
using TinadecCore.Services;
using TinadecCore.Storage;

namespace TinadecCore.Tests;

public sealed class OrchestratorServiceTests
{
    private const string PlannerPurpose = "planner";
    private const string PrimaryProviderId = "prov_primary";
    private const string FallbackProviderId = "prov_fallback";
    private const string TestDriver = "test-driver";

    [Fact]
    public async Task CompleteRunWithModelAsync_ChoosesHealthyProviderAndStoresAssistantResponse()
    {
        var harness = CreateHarness();
        SaveProvider(harness.Store, PrimaryProviderId, "primary-model", priority: 10);
        harness.Runtime.Responses[PrimaryProviderId] = SuccessfulResponse("primary response");
        var session = CreateSession(harness.Store);
        var userMessage = harness.Store.AddMessage(session.Id, "user", "Plan a small refactor.");
        var snapshot = harness.Orchestrator.CreateRunForMessage(session.Id, userMessage.Id, userMessage.Content);

        var completion = await harness.Orchestrator.CompleteRunWithModelAsync(snapshot);

        Assert.NotNull(completion.AssistantMessage);
        Assert.Equal("assistant", completion.AssistantMessage.Role);
        Assert.Contains("primary response", completion.AssistantMessage.Content);
        Assert.Equal(PrimaryProviderId, completion.Invocation?.Context.ProviderInstanceId);
        Assert.Contains(harness.Store.ListMessages(session.Id), message => message.Role == "assistant" && message.Content.Contains("primary response"));
    }

    [Fact]
    public async Task CompleteRunWithModelAsync_FallsBackAfterRetryableProviderFailure()
    {
        var harness = CreateHarness();
        SaveProvider(harness.Store, PrimaryProviderId, "primary-model", priority: 10);
        SaveProvider(harness.Store, FallbackProviderId, "fallback-model", priority: 20);
        harness.Runtime.Responses[PrimaryProviderId] = FailedResponse("primary unavailable", ProviderErrorCategory.ProviderUnavailable, retryable: true);
        harness.Runtime.Responses[FallbackProviderId] = SuccessfulResponse("fallback response");
        var session = CreateSession(harness.Store);
        var userMessage = harness.Store.AddMessage(session.Id, "user", "Use a fallback if needed.");
        var snapshot = harness.Orchestrator.CreateRunForMessage(session.Id, userMessage.Id, userMessage.Content);

        var completion = await harness.Orchestrator.CompleteRunWithModelAsync(snapshot);

        Assert.NotNull(completion.AssistantMessage);
        Assert.Contains("fallback response", completion.AssistantMessage.Content);
        Assert.Equal(FallbackProviderId, completion.Invocation?.Context.ProviderInstanceId);
        Assert.Equal(PrimaryProviderId, completion.Invocation?.ErrorProviderId);
        var requested = Assert.Single(harness.Store.ListEvents(session.Id), item => item.Type == "model.requested");
        Assert.True(requested.Payload?["fallback_provider_selected"]?.GetValue<bool>());
        Assert.Equal(PrimaryProviderId, requested.Payload?["error_provider_instance_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task CompleteRunWithModelAsync_FailedInvocationRecordsSafeErrorWithoutAssistantCompletion()
    {
        var harness = CreateHarness();
        SaveProvider(harness.Store, PrimaryProviderId, "primary-model", priority: 10);
        const string rawPrompt = "secret prompt text must not be logged";
        const string rawCredential = "sk-test-raw-secret";
        harness.Runtime.Responses[PrimaryProviderId] = FailedResponse("Provider request timed out.", ProviderErrorCategory.Timeout, retryable: false);
        var session = CreateSession(harness.Store);
        var userMessage = harness.Store.AddMessage(session.Id, "user", rawPrompt);
        var snapshot = harness.Orchestrator.CreateRunForMessage(session.Id, userMessage.Id, userMessage.Content);

        var completion = await harness.Orchestrator.CompleteRunWithModelAsync(snapshot);

        Assert.Null(completion.AssistantMessage);
        Assert.DoesNotContain(harness.Store.ListMessages(session.Id), message => message.Role == "assistant");
        var failed = Assert.Single(harness.Store.ListEvents(session.Id), item => item.Type == "model.failed");
        var payloadText = failed.Payload?.ToJsonString() ?? string.Empty;
        Assert.Contains("Provider request timed out.", payloadText);
        Assert.DoesNotContain(rawPrompt, payloadText);
        Assert.DoesNotContain(rawCredential, payloadText);
        Assert.Contains("prompt_fragment_ids", payloadText);
        Assert.Contains("prompt_estimated_tokens", payloadText);
        Assert.Contains("prompt_warning_count", payloadText);
        Assert.DoesNotContain("TinadecCode prompt context", payloadText);
        Assert.DoesNotContain("Meeting Agent Default", payloadText);
    }

    [Fact]
    public async Task CompleteRunWithModelAsync_ReportsFallbackFailureWhenAllRetryableProvidersFail()
    {
        var harness = CreateHarness();
        SaveProvider(harness.Store, PrimaryProviderId, "primary-model", priority: 10);
        SaveProvider(harness.Store, FallbackProviderId, "fallback-model", priority: 20);
        harness.Runtime.Responses[PrimaryProviderId] = FailedResponse("primary unavailable", ProviderErrorCategory.ProviderUnavailable, retryable: true);
        harness.Runtime.Responses[FallbackProviderId] = FailedResponse("fallback timed out", ProviderErrorCategory.Timeout, retryable: true);
        var session = CreateSession(harness.Store);
        var userMessage = harness.Store.AddMessage(session.Id, "user", "Try all available providers.");
        var snapshot = harness.Orchestrator.CreateRunForMessage(session.Id, userMessage.Id, userMessage.Content);

        var completion = await harness.Orchestrator.CompleteRunWithModelAsync(snapshot);

        Assert.Null(completion.AssistantMessage);
        Assert.Equal(FallbackProviderId, completion.Invocation?.Context.ProviderInstanceId);
        Assert.Equal(FallbackProviderId, completion.Invocation?.ErrorProviderId);
        Assert.Equal(ProviderErrorCategory.Timeout, completion.Invocation?.ErrorCategory);
        var failed = Assert.Single(harness.Store.ListEvents(session.Id), item => item.Type == "model.failed");
        Assert.Equal(FallbackProviderId, failed.Payload?["provider_instance_id"]?.GetValue<string>());
        Assert.Equal(FallbackProviderId, failed.Payload?["error_provider_instance_id"]?.GetValue<string>());
        Assert.Equal("Timeout", failed.Payload?["error_category"]?.GetValue<string>());
    }

    [Fact]
    public async Task CompleteRunWithModelAsync_RecordsRouteProviderModelAndErrorCategoryInEvents()
    {
        var harness = CreateHarness();
        SaveProvider(harness.Store, PrimaryProviderId, "primary-model", priority: 10);
        harness.Runtime.Responses[PrimaryProviderId] = FailedResponse("Provider is rate limited.", ProviderErrorCategory.RateLimited, retryable: false);
        var session = CreateSession(harness.Store);
        var userMessage = harness.Store.AddMessage(session.Id, "user", "Capture safe event metadata.");
        var snapshot = harness.Orchestrator.CreateRunForMessage(session.Id, userMessage.Id, userMessage.Content);

        await harness.Orchestrator.CompleteRunWithModelAsync(snapshot);

        var failed = Assert.Single(harness.Store.ListEvents(session.Id), item => item.Type == "model.failed");
        Assert.Equal(PlannerPurpose, failed.Payload?["route_purpose"]?.GetValue<string>());
        Assert.Equal(PrimaryProviderId, failed.Payload?["provider_instance_id"]?.GetValue<string>());
        Assert.Equal("primary-model", failed.Payload?["model"]?.GetValue<string>());
        Assert.Equal("RateLimited", failed.Payload?["error_category"]?.GetValue<string>());
        Assert.False(failed.Payload?["fallback_provider_selected"]?.GetValue<bool>());
    }

    private static OrchestratorHarness CreateHarness()
    {
        var store = new CoreStore(Path.Combine(Path.GetTempPath(), $"tinadec-orchestrator-{Guid.NewGuid():N}.db"));
        store.Initialize();
        store.DeleteModelProviderInstance("openai_default");
        var events = new EventHub();
        var fakeRuntime = new FakeProviderRuntime();
        var toolRegistry = new EmptyToolRegistry();
        var modelRuntime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new NullCredentialResolver(),
            [fakeRuntime],
            store);
        var promptContext = new PromptContextService(
            store,
            toolRegistry,
            new NullPromptContextPlannerRuntime());
        var orchestrator = new OrchestratorService(
            store,
            events,
            new AgentWorkflowRuntime(toolRegistry),
            modelRuntime,
            promptContext,
            toolRegistry,
            new AllowReadOnlyCapabilityPolicy(),
            []);

        return new OrchestratorHarness(store, orchestrator, fakeRuntime);
    }

    private static SessionDto CreateSession(CoreStore store)
    {
        var project = store.CreateProject("Test Project", Directory.GetCurrentDirectory());
        return store.CreateSession(project.Id, "Test Session");
    }

    private static void SaveProvider(CoreStore store, string id, string model, int priority)
    {
        store.SaveModelProviderInstance(
            new SaveModelProviderInstanceRequest(
                id,
                TestDriver,
                id,
                "test-connection",
                $"https://{id}.example.test/v1",
                model,
                ApiKey: null,
                ClearApiKey: false,
                BinaryPath: null,
                HomePath: null,
                ServerUrl: null,
                LaunchArgs: null,
                Capabilities: ["chat", $"route:{PlannerPurpose}", $"priority:{priority}", "no-api-key"],
                Enabled: true),
            encryptedApiKey: null);
    }

    private static Func<ResolvedModelInvocationContextDto, ModelInvocationResultDto> SuccessfulResponse(string content)
    {
        return context => new ModelInvocationResultDto("executed", content, context, false, FakeProviderRuntime.RuntimeId);
    }

    private static Func<ResolvedModelInvocationContextDto, ModelInvocationResultDto> FailedResponse(
        string safeMessage,
        ProviderErrorCategory category,
        bool retryable)
    {
        return context => new ModelInvocationResultDto(
            "failed",
            safeMessage,
            context,
            false,
            FakeProviderRuntime.RuntimeId,
            category,
            retryable,
            null,
            null,
            safeMessage,
            context.ProviderInstanceId);
    }

    private sealed record OrchestratorHarness(CoreStore Store, OrchestratorService Orchestrator, FakeProviderRuntime Runtime);

    private sealed class FakeProviderRuntime : IModelProviderRuntime
    {
        public const string RuntimeId = "fake-provider-runtime";

        public Dictionary<string, Func<ResolvedModelInvocationContextDto, ModelInvocationResultDto>> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Id => RuntimeId;

        public bool CanHandle(ResolvedModelInvocationContextDto context)
        {
            return string.Equals(context.Driver, TestDriver, StringComparison.OrdinalIgnoreCase);
        }

        public Task<ModelInvocationResultDto> GenerateAsync(
            ResolvedModelInvocationContextDto context,
            string? apiKey,
            IReadOnlyList<MessageDto> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<ModelToolSpecDto>? tools = null)
        {
            var response = Responses.TryGetValue(context.ProviderInstanceId, out var handler)
                ? handler(context)
                : FailedResponse("No fake response configured.", ProviderErrorCategory.Unknown, retryable: false)(context);
            return Task.FromResult(response);
        }
    }

    private sealed class NullCredentialResolver : IModelCredentialResolver
    {
        public string? ResolveApiKey(ResolvedModelInvocationContextDto context)
        {
            return null;
        }
    }

    private sealed class NullPromptContextPlannerRuntime : IPromptContextPlannerRuntime
    {
        public Task<PromptContextPlanDto?> TryCreatePlanAsync(
            PromptContextPlanningInput input,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PromptContextPlanDto?>(null);
        }
    }

    private sealed class EmptyToolRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDescriptorDto> ListTools(string? domain = null)
        {
            return [];
        }

        public ToolDescriptorDto? Resolve(string toolId)
        {
            return null;
        }

        public ToolRegistrySummaryDto Describe(string? domain = null)
        {
            return new ToolRegistrySummaryDto(
                0,
                0,
                0,
                [],
                ["core", "code", "codex-rust", "extension"],
                "No tools are registered.");
        }

        public IReadOnlyList<ModelToolSpecDto> BuildOpenAiToolSpecs(string? domain = null)
        {
            return [];
        }
    }

    private sealed class AllowReadOnlyCapabilityPolicy : ICapabilityPolicy
    {
        public ApprovalRequirement Evaluate(string permissionMode, ToolDescriptorDto tool)
        {
            return new ApprovalRequirement(false, "read-only");
        }

        public bool IsReadOnly(ToolDescriptorDto tool)
        {
            return true;
        }
    }
}
