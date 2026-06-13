using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Services;
using TinadecCore.Storage;

namespace TinadecCore.Tests;

public sealed class ModelInvocationRuntimeTests
{
    private const string ChatPurpose = "chat";
    private const string OpenAiPrimaryProviderId = "prov_openai_primary";
    private const string AnthropicBackupProviderId = "prov_anthropic_backup";

    [Fact]
    public async Task InvokeAsync_RetriesOnRetryableFailureAndRecordsCooldown()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", priority: 10);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", priority: 20);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var primaryRuntime = new FakeProviderRuntime(OpenAiPrimaryProviderId, retryableFailure: true);
        var backupRuntime = new FakeProviderRuntime(AnthropicBackupProviderId, success: true);
        var runtime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new FakeCredentialResolver(),
            [primaryRuntime, backupRuntime],
            store);

        var result = await runtime.InvokeAsync("sess_1", ChatPurpose, [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)]);

        Assert.Equal("executed", result.Status);
        Assert.Equal(AnthropicBackupProviderId, result.Context.ProviderInstanceId);
        Assert.True(primaryRuntime.WasCalled);
        Assert.True(backupRuntime.WasCalled);

        var primaryAfter = store.GetStoredModelProviderInstance(OpenAiPrimaryProviderId)!;
        Assert.Equal(ProviderHealthStatus.Cooldown, primaryAfter.HealthStatus);
        Assert.NotNull(primaryAfter.CooldownUntil);
        Assert.Equal(1, primaryAfter.FailureCount);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotDoubleRecordRuntimeRecordedRetryableFailure()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", priority: 10);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", priority: 20);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var primaryRuntime = new FakeProviderRuntime(OpenAiPrimaryProviderId, retryableFailure: true, runtimeId: "openai-compatible", store: store);
        var backupRuntime = new FakeProviderRuntime(AnthropicBackupProviderId, success: true);
        var runtime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new FakeCredentialResolver(),
            [primaryRuntime, backupRuntime],
            store);

        var result = await runtime.InvokeAsync("sess_1", ChatPurpose, [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)]);

        Assert.Equal("executed", result.Status);
        var primaryAfter = store.GetStoredModelProviderInstance(OpenAiPrimaryProviderId)!;
        Assert.Equal(1, primaryAfter.FailureCount);
    }

    [Fact]
    public async Task InvokeAsync_ClearsCooldownOnlyForProviderThatSucceeds()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", priority: 10);
        SaveProvider(
            store,
            AnthropicBackupProviderId,
            "Anthropic Backup",
            "claude-4",
            priority: 20,
            healthMetadata: ["health:cooldown", $"cooldown_until:{DateTimeOffset.UtcNow.AddMinutes(-1):O}", "failure_count:1"]);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var primaryRuntime = new FakeProviderRuntime(OpenAiPrimaryProviderId, retryableFailure: true);
        var backupRuntime = new FakeProviderRuntime(AnthropicBackupProviderId, success: true);
        var runtime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new FakeCredentialResolver(),
            [primaryRuntime, backupRuntime],
            store);

        var result = await runtime.InvokeAsync("sess_1", ChatPurpose, [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)]);

        Assert.Equal("executed", result.Status);

        var primaryAfter = store.GetStoredModelProviderInstance(OpenAiPrimaryProviderId)!;
        Assert.Equal(ProviderHealthStatus.Cooldown, primaryAfter.HealthStatus);
        Assert.NotNull(primaryAfter.CooldownUntil);

        var backupAfter = store.GetStoredModelProviderInstance(AnthropicBackupProviderId)!;
        Assert.Equal(ProviderHealthStatus.Healthy, backupAfter.HealthStatus);
        Assert.Null(backupAfter.CooldownUntil);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotPolluteSuccessResultWithFirstFailureMetadata()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", priority: 10);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", priority: 20);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var primaryRuntime = new FakeProviderRuntime(OpenAiPrimaryProviderId, retryableFailure: true);
        var backupRuntime = new FakeProviderRuntime(AnthropicBackupProviderId, success: true);
        var runtime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new FakeCredentialResolver(),
            [primaryRuntime, backupRuntime],
            store);

        var result = await runtime.InvokeAsync("sess_1", ChatPurpose, [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)]);

        Assert.Equal("executed", result.Status);
        Assert.Null(result.ErrorCategory);
        Assert.False(result.IsRetryable);
        Assert.Null(result.SafeErrorMessage);
        Assert.Equal(OpenAiPrimaryProviderId, result.ErrorProviderId);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsFallbackFailureWhenAllProvidersFail()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", priority: 10);
        SaveProvider(store, AnthropicBackupProviderId, "Anthropic Backup", "claude-4", priority: 20);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var primaryRuntime = new FakeProviderRuntime(OpenAiPrimaryProviderId, retryableFailure: true);
        var backupRuntime = new FakeProviderRuntime(AnthropicBackupProviderId, retryableFailure: true);
        var runtime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new FakeCredentialResolver(),
            [primaryRuntime, backupRuntime],
            store);

        var result = await runtime.InvokeAsync("sess_1", ChatPurpose, [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)]);

        Assert.Equal("failed", result.Status);
        Assert.Equal(AnthropicBackupProviderId, result.ErrorProviderId);
        Assert.Equal(ProviderErrorCategory.RateLimited, result.ErrorCategory);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsTerminalUnavailableWhenFallbackCannotBeResolved()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", priority: 10);
        // Do not create Anthropic backup; fallback resolution will have no available provider
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");

        var primaryRuntime = new FakeProviderRuntime(OpenAiPrimaryProviderId, retryableFailure: true);
        var runtime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new FakeCredentialResolver(),
            [primaryRuntime],
            store);

        var result = await runtime.InvokeAsync("sess_1", ChatPurpose, [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)]);

        Assert.Equal("failed", result.Status);
        Assert.Equal(ProviderErrorCategory.ProviderUnavailable, result.ErrorCategory);
        Assert.False(result.IsRetryable);
        Assert.Contains("All model providers failed", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_PrependsSystemPromptForProviderRuntime()
    {
        var store = CreateStore();
        SaveProvider(store, OpenAiPrimaryProviderId, "OpenAI Primary", "gpt-5.4", priority: 10);
        store.SaveModelRoute(ChatPurpose, OpenAiPrimaryProviderId, "gpt-5.4");
        var providerRuntime = new FakeProviderRuntime(OpenAiPrimaryProviderId, success: true);
        var runtime = new ModelInvocationRuntime(
            new ModelRouteResolver(store),
            new FakeCredentialResolver(),
            [providerRuntime],
            store);

        await runtime.InvokeAsync(
            "sess_1",
            ChatPurpose,
            [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)],
            systemPrompt: "system context");

        Assert.NotNull(providerRuntime.LastMessages);
        Assert.Equal("system", providerRuntime.LastMessages![0].Role);
        Assert.Equal("system context", providerRuntime.LastMessages[0].Content);
        Assert.Equal("user", providerRuntime.LastMessages[1].Role);
    }


    private static CoreStore CreateStore(string? databasePath = null)
    {
        var store = new CoreStore(databasePath ?? CreateDatabasePath());
        store.Initialize();
        store.DeleteModelProviderInstance("openai_default");
        return store;
    }

    private static string CreateDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"tinadec-invocation-{Guid.NewGuid():N}.db");
    }

    private static void SaveProvider(
        CoreStore store,
        string id,
        string displayName,
        string model,
        int priority,
        bool enabled = true,
        string connectionKind = "test-connection",
        IReadOnlyList<string>? healthMetadata = null)
    {
        var capabilities = new List<string>
        {
            "chat",
            $"route:{ChatPurpose}",
            $"priority:{priority}",
            $"clock:{DateTimeOffset.UtcNow:O}",
            "no-api-key"
        };

        if (healthMetadata is not null)
        {
            capabilities.AddRange(healthMetadata);
        }

        var baseUrl = connectionKind.Equals("local-http", StringComparison.OrdinalIgnoreCase)
            ? "http://127.0.0.1:11434/v1"
            : $"https://{id}.example.test/v1";

        store.SaveModelProviderInstance(
            new SaveModelProviderInstanceRequest(
                id,
                "openai-compatible",
                displayName,
                connectionKind,
                baseUrl,
                model,
                null,
                ClearApiKey: false,
                BinaryPath: null,
                HomePath: null,
                ServerUrl: null,
                LaunchArgs: null,
                Capabilities: capabilities,
                Enabled: enabled),
            encryptedApiKey: null);
    }

    private sealed class FakeProviderRuntime : IModelProviderRuntime
    {
        private readonly string _id;
        private readonly bool _success;
        private readonly bool _retryableFailure;
        private readonly string? _runtimeId;
        private readonly CoreStore? _store;

        public FakeProviderRuntime(string id, bool success = false, bool retryableFailure = false, string? runtimeId = null, CoreStore? store = null)
        {
            _id = id;
            _success = success;
            _retryableFailure = retryableFailure;
            _runtimeId = runtimeId;
            _store = store;
        }

        public string Id => _runtimeId ?? _id;
        public bool WasCalled { get; private set; }
        public IReadOnlyList<MessageDto>? LastMessages { get; private set; }

        public bool CanHandle(ResolvedModelInvocationContextDto context)
        {
            return string.Equals(context.ProviderInstanceId, _id, StringComparison.OrdinalIgnoreCase);
        }

        public Task<ModelInvocationResultDto> GenerateAsync(
            ResolvedModelInvocationContextDto context,
            string? apiKey,
            IReadOnlyList<MessageDto> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<ModelToolSpecDto>? tools = null)
        {
            WasCalled = true;
            LastMessages = messages;
            if (_success)
            {
                return Task.FromResult(new ModelInvocationResultDto(
                    "executed",
                    "Success",
                    context,
                    false,
                    _id));
            }

            if (_retryableFailure && _store is not null)
            {
                _store.RecordModelProviderFailure(_id, ProviderErrorCategory.RateLimited, DateTimeOffset.UtcNow);
            }

            return Task.FromResult(new ModelInvocationResultDto(
                "failed",
                "Rate limited",
                context,
                false,
                Id,
                ProviderErrorCategory.RateLimited,
                _retryableFailure,
                429,
                null,
                "Rate limited",
                _id));
        }
    }

    private sealed class FakeCredentialResolver : IModelCredentialResolver
    {
        public string? ResolveApiKey(ResolvedModelInvocationContextDto context)
        {
            return "fake-api-key";
        }
    }
}
