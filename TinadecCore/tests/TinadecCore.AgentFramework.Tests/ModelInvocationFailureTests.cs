using System.Net;

using Microsoft.Extensions.AI;

using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The engine's failure reporter surfaces the message for InvalidOperationException
/// and replaces every other exception type with "Unexpected runtime failure." That
/// masking is how a plain provider 429 reached the user as an unactionable string
/// while the model invocation row next to it already said "rate limit was reached".
/// These tests pin the contract that must hold for the durable run to carry the real
/// cause: the exhausted-candidate exception IS an InvalidOperationException, and its
/// message is the classifier's curated text.
/// </summary>
public sealed class ModelInvocationFailureTests
{
    [Fact]
    public async Task StreamingRetryKeepsEachAttemptSeparate_AndDoesNotCommitFailedText()
    {
        var frames = new List<ModelOutputFrame>();
        var (factory, resolver) = FactoryWithClient([Resolution(true)], new PartialFailureClient(),
            (frame, _) => { frames.Add(frame); return Task.CompletedTask; });
        var response = await CallAsync(factory);
        Assert.Equal("Answer", response.Text);
        Assert.Equal(2, frames.Where(frame => frame.Kind == "started").Select(frame => frame.ResponseId).Distinct().Count());
        Assert.Single(frames, frame => frame.Kind == "failed");
        Assert.Single(frames, frame => frame.Kind == "completed");
        Assert.DoesNotContain(frames, frame => (frame.Delta ?? "").Contains("provider-secret"));
    }

    private sealed class PartialFailureClient : IChatClient
    {
        private int _attempts;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (++_attempts == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "failed partial");
                throw new IOException("provider-secret");
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Answer");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    [Fact]
    public async Task RateLimitedCall_SurfacesClassifierMessage_NotAnOpaqueOne()
    {
        // The exact shape recorded in production: the chat route had ONE candidate, the
        // provider answered 429, the run died four seconds after admission.
        var (factory, resolver) = FactoryWithClient(
            [Resolution(available: true)],
            Client(() => throw new HttpRequestException(
                "Too Many Requests", inner: null, statusCode: HttpStatusCode.TooManyRequests)));

        var ex = await Assert.ThrowsAsync<ModelInvocationExhaustedException>(
            () => CallAsync(factory));

        // InvalidOperationException is the family the engine's reporter trusts, so the
        // curated text reaches the run summary and the user instead of being masked.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("The model provider rate limit was reached.", ex.Message);
        Assert.Equal("rate_limited", ex.Category);
        // And the diagnosis is the same one already written to model_invocations — the
        // user must never learn less than the record does. Three attempts: the bounded
        // same-candidate retry ran before the chain was declared exhausted.
        Assert.Equal(3, resolver.CompletedCategories.Count);
        Assert.All(resolver.CompletedCategories, category => Assert.Equal("rate_limited", category));
        // The original provider exception is preserved for diagnostics.
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ServerErrorCall_SurfacesClassifierMessage()
    {
        var (factory, _) = FactoryWithClient(
            [Resolution(available: true)],
            Client(() => throw new HttpRequestException(
                "Internal Server Error", inner: null, statusCode: HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<ModelInvocationExhaustedException>(() => CallAsync(factory));

        Assert.Equal("The model provider returned a server error.", ex.Message);
        Assert.Equal("provider_server_error", ex.Category);
    }

    [Fact]
    public async Task UnavailableCandidate_NamesTheReason()
    {
        // A route whose only candidate has no stored key: this must read as a
        // configuration problem, not as an engine fault.
        var (factory, _) = FactoryWithClient(
            [new ChatResolution { IsAvailable = false, Error = "Provider API key is not stored." }],
            Client(() => throw new InvalidOperationException("never reached")));

        var ex = await Assert.ThrowsAsync<ModelInvocationExhaustedException>(() => CallAsync(factory));

        Assert.Equal("Provider API key is not stored.", ex.Message);
        Assert.Equal("candidate_unavailable", ex.Category);
    }

    [Fact]
    public async Task CancellationPropagatesUnwrapped()
    {
        // The stop path keys off OperationCanceledException. Re-typing it would turn a
        // user cancel into a run failure, so it must escape the wrapper untouched.
        var (factory, _) = FactoryWithClient(
            [Resolution(available: true)],
            Client(() => throw new OperationCanceledException("cancelled by user")));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CallAsync(factory));
    }

    [Fact]
    public async Task FallbackWalksToSecondCandidate_BeforeExhausting()
    {
        // Fallback only exists when the route declares a second candidate. A 5xx on the
        // first must move to it rather than end the run — this is the behaviour a
        // multi-candidate route is configured for.
        var attempts = 0;
        var (factory, _) = FactoryWithClient(
            [Resolution(available: true), Resolution(available: true, model: "second")],
            Client(() =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new HttpRequestException(
                        "Service Unavailable", inner: null, statusCode: HttpStatusCode.ServiceUnavailable);
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            }));

        var response = await CallAsync(factory);

        Assert.Equal(2, attempts);
        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task RateLimitOnSingleCandidateRoute_RetriesAndSurvives()
    {
        // THE production shape: one candidate, the provider answered 429, the run died
        // four seconds after admission. A rate limit is transient, so a same-candidate
        // retry must rescue the turn instead of ending the run.
        var attempts = 0;
        var (factory, resolver) = FactoryWithClient(
            [Resolution(available: true)],
            Client(() =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new HttpRequestException(
                        "Too Many Requests", inner: null, statusCode: HttpStatusCode.TooManyRequests);
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered"));
            }));

        var response = await CallAsync(factory);

        Assert.Equal(2, attempts);
        Assert.Equal("recovered", response.Text);
        // Each attempt is a separately recorded invocation on the same call.
        Assert.Equal(["rate_limited"], resolver.CompletedCategories);
    }

    [Fact]
    public async Task DeterministicClientError_FailsFast_WithoutRetrying()
    {
        // A 4xx is not transient: retrying it would burn the same rejection. The run must
        // still learn the real reason (request rejected), but without the wasted delay.
        var attempts = 0;
        var (factory, _) = FactoryWithClient(
            [Resolution(available: true)],
            Client(() =>
            {
                attempts++;
                throw new HttpRequestException(
                    "Unprocessable Entity", inner: null, statusCode: HttpStatusCode.UnprocessableEntity);
            }));

        var ex = await Assert.ThrowsAsync<ModelInvocationExhaustedException>(() => CallAsync(factory));

        Assert.Equal(1, attempts);
        Assert.Equal("The model provider rejected the request.", ex.Message);
    }

    private static async Task<ChatResponse> CallAsync(IAgentChatClientFactory factory)
    {
        var resolution = await factory.ResolveChatAsync("chat");
        Assert.True(resolution.IsAvailable);
        var client = await factory.CreateAsync(resolution!);
        return await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
    }

    private static ChatResolution Resolution(bool available, string? model = null) => available
        ? new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = model ?? "fake", ApiKey = "x", ModelId = "openai/fake" }
        : new ChatResolution { IsAvailable = false, Error = "Model candidate is unavailable." };

    private static (IAgentChatClientFactory, RecordingResolver) Factory(
        params ChatResolution[] resolutions) =>
        FactoryWithClient([.. resolutions], Client(() =>
            throw new InvalidOperationException("The test configured no response script.")));

    private static (IAgentChatClientFactory, RecordingResolver) FactoryWithClient(
        ChatResolution[] resolutions, IChatClient client, Func<ModelOutputFrame, CancellationToken, Task>? output = null)
    {
        var resolver = new RecordingResolver(resolutions);
        var plan = new FrozenModelPlan("fixed", "test", []);
        var context = new ModelInvocationContext(
            Guid.NewGuid(), Guid.NewGuid(), null, null, null,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test");
        return (new ModelInvocationChatFactory(resolver, new StaticClientFactory(client), plan, context, output), resolver);
    }

    /// <summary>A chat client whose call runs <paramref name="script"/>: returning is success, throwing is failure.</summary>
    private static IChatClient Client(Func<ChatResponse> script) => new SequenceClient(script);

    /// <summary>
    /// A chat client whose calls run <paramref name="script"/>; a returned response
    /// counts as success, a thrown exception as failure. Sequence state lives in the
    /// caller's closure so a test can assert how many candidates were actually tried.
    /// </summary>
    private sealed class SequenceClient(Func<ChatResponse> script) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(script());

        // Streaming mirrors the failing path so a failure thrown mid-stream is classified
        // the same way. No test asserts the yielded shape, only that a failure surfaces.
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = script();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text ?? string.Empty);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StaticClientFactory(IChatClient client) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, Model = "fake" });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
    }

    private sealed class RecordingResolver(ChatResolution[] resolutions) : IAgentModelResolver
    {
        public List<string> CompletedCategories { get; } = [];

        public Task<ModelResolutionPreviewDto> PreviewAsync(ModelResolutionPreviewRequestDto request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<FrozenModelPlan> FreezeAsync(AgentModelFreezeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(
            FrozenModelPlan plan, Guid? parentInstanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatResolution>>(resolutions);

        public Task<Guid> StartInvocationAsync(ModelInvocationStart request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task CompleteInvocationAsync(
            Guid invocationId, string status, ModelUsage? usage = null, string? errorCategory = null,
            string? safeErrorMessage = null, CancellationToken cancellationToken = default)
        {
            if (errorCategory is not null) CompletedCategories.Add(errorCategory);
            return Task.CompletedTask;
        }
    }
}
