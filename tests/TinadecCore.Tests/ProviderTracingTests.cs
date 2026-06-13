using System.Diagnostics;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Services;
using TinadecCore.Storage;
using TinadecCore.Tracing;

namespace TinadecCore.Tests;

public sealed class ProviderTracingTests
{
    private const string Purpose = "planner";
    private const string PrimaryProviderId = "trace_primary";
    private const string FallbackProviderId = "trace_fallback";
    private const string Driver = "trace-driver";

    [Fact]
    public async Task InvokeAsync_EmitsSafeAttributesForSuccessFallbackAndHealthUpdate()
    {
        using var collector = new ActivityCollector();
        var store = CreateStore();
        SaveProvider(store, PrimaryProviderId, "model-a", 10);
        SaveProvider(store, FallbackProviderId, "model-b", 20);
        var runtime = new TraceRuntime(new Dictionary<string, Func<ResolvedModelInvocationContextDto, ModelInvocationResultDto>>(StringComparer.OrdinalIgnoreCase)
        {
            [PrimaryProviderId] = context => Failed(context, ProviderErrorCategory.Timeout, retryable: true, "Provider request timed out."),
            [FallbackProviderId] = context => Executed(context, "safe reply")
        });

        var sut = new ModelInvocationRuntime(new ModelRouteResolver(store), new NullCredentialResolver(), [runtime], store);

        const string sessionId = "session-trace";
        var result = await sut.InvokeAsync(
            sessionId,
            Purpose,
            [new MessageDto("msg-1", sessionId, "user", "secret prompt: sk-should-not-leak", DateTimeOffset.UtcNow)]);

        Assert.Equal("executed", result.Status);
        var invocation = collector.Completed
            .Last(activity => activity.OperationName == SpanNames.ModelProviderInvocation
                && ReadTag(activity, SpanAttrs.SessionId) == sessionId);
        AssertTag(invocation, SpanAttrs.ProviderInstanceId, FallbackProviderId);
        AssertTag(invocation, SpanAttrs.ProviderId, FallbackProviderId);
        AssertTag(invocation, SpanAttrs.RoutePurpose, Purpose);
        AssertTag(invocation, SpanAttrs.Model, "model-b");
        AssertTag(invocation, SpanAttrs.Status, "executed");
        AssertTag(invocation, SpanAttrs.RetryCount, "1");
        AssertTag(invocation, SpanAttrs.FallbackProviderId, PrimaryProviderId);

        var fallbackEvent = Assert.Single(invocation.Events, evt => evt.Name == "model.fallback.selected");
        AssertEventTag(fallbackEvent, SpanAttrs.ProviderId, PrimaryProviderId);
        AssertEventTag(fallbackEvent, SpanAttrs.ProviderInstanceId, PrimaryProviderId);
        AssertEventTag(fallbackEvent, SpanAttrs.ErrorCategory, ProviderErrorCategory.Timeout.ToString());
        AssertEventTag(fallbackEvent, SpanAttrs.RetryCount, "1");

        var healthEvent = Assert.Single(invocation.Events, evt => evt.Name == "model.provider.health.updated");
        AssertEventTag(healthEvent, SpanAttrs.ProviderId, PrimaryProviderId);
        AssertEventTag(healthEvent, SpanAttrs.ProviderInstanceId, PrimaryProviderId);
        AssertEventTag(healthEvent, SpanAttrs.HealthStatus, ProviderHealthStatus.Cooldown.ToString());
        AssertEventTag(healthEvent, SpanAttrs.ErrorCategory, ProviderErrorCategory.Timeout.ToString());
        AssertNoSensitiveData(invocation, "sk-should-not-leak", "secret prompt");
    }

    [Fact]
    public async Task InvokeAsync_EmitsErrorCategoryForTimeoutAndCancellation()
    {
        using var collector = new ActivityCollector();
        var store = CreateStore();
        SaveProvider(store, PrimaryProviderId, "model-a", 10);

        var runtime = new TraceRuntime(new Dictionary<string, Func<ResolvedModelInvocationContextDto, ModelInvocationResultDto>>(StringComparer.OrdinalIgnoreCase)
        {
            [PrimaryProviderId] = context => Failed(context, ProviderErrorCategory.Timeout, retryable: false, "Provider request timed out.")
        });

        var sut = new ModelInvocationRuntime(new ModelRouteResolver(store), new NullCredentialResolver(), [runtime], store);
        var timeoutResult = await sut.InvokeAsync("session-timeout", Purpose, [Message("session-timeout", "test")]);
        Assert.Equal(ProviderErrorCategory.Timeout, timeoutResult.ErrorCategory);

        runtime.Responses[PrimaryProviderId] = context => Failed(context, ProviderErrorCategory.Cancelled, retryable: false, "Provider request was cancelled.");
        var cancelledResult = await sut.InvokeAsync("session-cancel", Purpose, [Message("session-cancel", "test")]);
        Assert.Equal(ProviderErrorCategory.Cancelled, cancelledResult.ErrorCategory);

        var invocationSpans = collector.Completed
            .Where(activity => activity.OperationName == SpanNames.ModelProviderInvocation)
            .ToArray();
        Assert.Contains(invocationSpans, span => ReadTag(span, SpanAttrs.ErrorCategory) == ProviderErrorCategory.Timeout.ToString());
        Assert.Contains(invocationSpans, span => ReadTag(span, SpanAttrs.ErrorCategory) == ProviderErrorCategory.Cancelled.ToString());
        Assert.Contains(invocationSpans, span => ReadTag(span, SpanAttrs.Status) == "failed");
        Assert.All(invocationSpans, span => AssertNoSensitiveData(span, "sk-", "secret", "Authorization"));
    }

    [Fact]
    public async Task Resolve_EmitsRouteSelectionAttributes()
    {
        using var collector = new ActivityCollector();
        var store = CreateStore();
        SaveProvider(store, PrimaryProviderId, "model-a", 10);

        var context = new ModelRouteResolver(store).Resolve(Purpose);

        Assert.Equal(PrimaryProviderId, context.ProviderInstanceId);
        var routeSpan = collector.Completed
            .Last(activity => activity.OperationName == SpanNames.ModelRouteSelection
                && ReadTag(activity, SpanAttrs.ProviderInstanceId) == PrimaryProviderId
                && ReadTag(activity, SpanAttrs.RoutePurpose) == Purpose);
        AssertTag(routeSpan, SpanAttrs.RoutePurpose, Purpose);
        AssertTag(routeSpan, SpanAttrs.ProviderId, PrimaryProviderId);
        AssertTag(routeSpan, SpanAttrs.ProviderInstanceId, PrimaryProviderId);
        AssertTag(routeSpan, SpanAttrs.Model, "model-a");
        AssertTag(routeSpan, SpanAttrs.Status, "selected");
    }

    private static MessageDto Message(string sessionId, string content)
    {
        return new MessageDto(Guid.NewGuid().ToString("N"), sessionId, "user", content, DateTimeOffset.UtcNow);
    }

    private static ModelInvocationResultDto Executed(ResolvedModelInvocationContextDto context, string content)
    {
        return new ModelInvocationResultDto("executed", content, context, false, TraceRuntime.RuntimeId);
    }

    private static ModelInvocationResultDto Failed(
        ResolvedModelInvocationContextDto context,
        ProviderErrorCategory category,
        bool retryable,
        string safeMessage)
    {
        return new ModelInvocationResultDto(
            "failed",
            safeMessage,
            context,
            false,
            TraceRuntime.RuntimeId,
            category,
            retryable,
            null,
            null,
            safeMessage,
            context.ProviderInstanceId);
    }

    private static CoreStore CreateStore()
    {
        var store = new CoreStore(Path.Combine(Path.GetTempPath(), $"tinadec-provider-tracing-{Guid.NewGuid():N}.db"));
        store.Initialize();
        store.DeleteModelProviderInstance("openai_default");
        return store;
    }

    private static void SaveProvider(CoreStore store, string id, string model, int priority)
    {
        store.SaveModelProviderInstance(
            new SaveModelProviderInstanceRequest(
                id,
                Driver,
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
                Capabilities: ["chat", $"route:{Purpose}", $"priority:{priority}", "no-api-key"],
                Enabled: true),
            encryptedApiKey: null);
    }

    private static string? ReadTag(Activity activity, string key)
    {
        return activity.TagObjects.FirstOrDefault(item => item.Key == key).Value?.ToString();
    }

    private static void AssertTag(Activity activity, string key, string expected)
    {
        Assert.Equal(expected, ReadTag(activity, key));
    }

    private static void AssertEventTag(ActivityEvent evt, string key, string expected)
    {
        var actual = evt.Tags.FirstOrDefault(item => item.Key == key).Value?.ToString();
        Assert.Equal(expected, actual);
    }

    private static void AssertNoSensitiveData(Activity activity, params string[] blocked)
    {
        var tagPayload = string.Join("\n", activity.Tags.Select(tag => $"{tag.Key}:{tag.Value}"));
        var eventPayload = string.Join("\n", activity.Events.Select(evt =>
            evt.Name + ":" + string.Join(",", evt.Tags.Select(tag => $"{tag.Key}={tag.Value}"))));

        foreach (var token in blocked)
        {
            Assert.DoesNotContain(token, tagPayload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, eventPayload, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class NullCredentialResolver : IModelCredentialResolver
    {
        public string? ResolveApiKey(ResolvedModelInvocationContextDto context)
        {
            return null;
        }
    }

    private sealed class TraceRuntime(Dictionary<string, Func<ResolvedModelInvocationContextDto, ModelInvocationResultDto>> responses) : IModelProviderRuntime
    {
        public const string RuntimeId = "trace-runtime";

        public Dictionary<string, Func<ResolvedModelInvocationContextDto, ModelInvocationResultDto>> Responses { get; } = responses;

        public string Id => RuntimeId;

        public bool CanHandle(ResolvedModelInvocationContextDto context)
        {
            return string.Equals(context.Driver, Driver, StringComparison.OrdinalIgnoreCase);
        }

        public Task<ModelInvocationResultDto> GenerateAsync(
            ResolvedModelInvocationContextDto context,
            string? apiKey,
            IReadOnlyList<MessageDto> messages,
            CancellationToken cancellationToken = default,
            IReadOnlyList<ModelToolSpecDto>? tools = null)
        {
            var response = Responses[context.ProviderInstanceId](context);
            return Task.FromResult(response);
        }
    }

    private sealed class ActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == TinadecActivitySource.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Completed.Add(activity)
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> Completed { get; } = [];

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
