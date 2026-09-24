using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

internal sealed record ModelInvocationContext(
    Guid SessionId,
    Guid RunId,
    Guid? TurnId,
    Guid? AgentInstanceId,
    Guid? ParentInstanceId,
    Guid AgentDefinitionId,
    Guid AgentVersionId,
    Guid ModeVersionId,
    string StrategySource);

/// <summary>
/// Executes a frozen candidate chain without consulting mutable routes. The wrapper
/// is the single point that records attempts and applies the fallback taxonomy.
/// </summary>
internal sealed class ModelInvocationChatFactory : IAgentChatClientFactory
{
    /// <summary>
    /// Attempts allowed against ONE candidate before the chain moves on. Three is the
    /// smallest number that survives a busy-provider burst without meaningfully
    /// extending a doomed call: total added latency is capped at 1.6s of backoff, and
    /// only fallback-eligible categories (rate limit / 5xx / timeout / connection) ever
    /// consume one. A 4xx or an authorization failure is deterministic — retrying it
    /// would just burn the same rejection four times.
    /// </summary>
    internal const int MaxAttemptsPerCandidate = 3;

    internal static TimeSpan RetryBackoff(int index) => index switch
    {
        0 => TimeSpan.FromMilliseconds(400),
        _ => TimeSpan.FromMilliseconds(1200),
    };

    internal const int RetryBackoffCount = 2;

    /// <summary>Whether <paramref name="attemptOnCandidate"/> (1-based) earns another try.</summary>
    internal static bool ShouldRetrySame(ModelInvocationFailure classification, int attemptOnCandidate) =>
        classification.CanFallback && attemptOnCandidate < MaxAttemptsPerCandidate;

    private readonly IAgentModelResolver _resolver;
    private readonly IAgentChatClientFactory _inner;
    private readonly FrozenModelPlan _plan;
    private readonly ModelInvocationContext _context;
    private readonly Func<ModelOutputFrame, CancellationToken, Task>? _output;
    private IReadOnlyList<ChatResolution>? _resolutions;

    public ModelInvocationChatFactory(
        IAgentModelResolver resolver,
        IAgentChatClientFactory inner,
        FrozenModelPlan plan,
        ModelInvocationContext context,
        Func<ModelOutputFrame, CancellationToken, Task>? output = null)
    {
        _resolver = resolver;
        _inner = inner;
        _plan = plan;
        _context = context;
        _output = output;
    }

    public async Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default)
    {
        _resolutions ??= await _resolver.ResolveInvocationCandidatesAsync(
            _plan, _context.ParentInstanceId, cancellationToken).ConfigureAwait(false);
        if (_resolutions.Count == 0)
            return new ChatResolution { IsAvailable = false, Error = "The frozen model plan has no candidates." };

        // The tracking client owns candidate availability and fallback. Returning an
        // available marker ensures callers do not bypass attempt recording.
        var first = _resolutions[0];
        return new ChatResolution
        {
            IsAvailable = true,
            Model = first.Model,
            ModelId = first.ModelId,
            Protocol = first.Protocol,
            ProviderInstanceId = first.ProviderInstanceId,
            ProviderVersionId = first.ProviderVersionId,
            RouteId = first.RouteId,
            RouteVersionId = first.RouteVersionId,
            CandidatePosition = first.CandidatePosition,
            StrategySource = first.StrategySource
        };
    }

    public async Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default)
    {
        _resolutions ??= await _resolver.ResolveInvocationCandidatesAsync(
            _plan, _context.ParentInstanceId, cancellationToken).ConfigureAwait(false);
        return new TrackingChatClient(_resolver, _inner, _resolutions, _context, _output);
    }

    private sealed class TrackingChatClient : IChatClient
    {
        private readonly IAgentModelResolver _resolver;
        private readonly IAgentChatClientFactory _inner;
        private readonly IReadOnlyList<ChatResolution> _resolutions;
        private readonly ModelInvocationContext _context;
        private readonly Func<ModelOutputFrame, CancellationToken, Task>? _output;

        public TrackingChatClient(
            IAgentModelResolver resolver,
            IAgentChatClientFactory inner,
            IReadOnlyList<ChatResolution> resolutions,
            ModelInvocationContext context,
            Func<ModelOutputFrame, CancellationToken, Task>? output)
        {
            _resolver = resolver;
            _inner = inner;
            _resolutions = resolutions;
            _context = context;
            _output = output;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
            var callId = Guid.NewGuid();
            Exception? lastError = null;
            ModelInvocationFailure? lastFailure = null;
            // (call_id, attempt) is unique, so attempt is a running counter across the whole
            // call rather than a candidate position: retries of the same candidate are
            // separately recorded attempts, which is what the column was always meant for.
            var attempt = 0;
            for (var index = 0; index < _resolutions.Count; index++)
            {
                var resolution = _resolutions[index];
                for (var attemptOnCandidate = 1; ; attemptOnCandidate++)
                {
                    var invocationId = await StartAsync(callId, ++attempt, resolution, cancellationToken).ConfigureAwait(false);
                    if (!resolution.IsAvailable)
                    {
                        lastError = new ModelCandidateUnavailableException(resolution.Error ?? "Model candidate is unavailable.");
                        lastFailure = new ModelInvocationFailure(true, false, "candidate_unavailable", lastError.Message);
                        await _resolver.CompleteInvocationAsync(invocationId, "failed", errorCategory: "candidate_unavailable",
                            safeErrorMessage: lastError.Message, cancellationToken: cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    try
                    {
                        var client = await _inner.CreateAsync(resolution, cancellationToken).ConfigureAwait(false);
                        var response = _output is null
                            ? await client.GetResponseAsync(materialized, options, cancellationToken).ConfigureAwait(false)
                            : await ModelOutputStream.ReadAsync(client, materialized, options, invocationId, _output, cancellationToken).ConfigureAwait(false);
                        await _resolver.CompleteInvocationAsync(invocationId, "succeeded",
                            Maf18RuntimeAdapter.NormalizeUsage(response.Usage), cancellationToken: cancellationToken).ConfigureAwait(false);
                        return response;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        var classification = ModelInvocationFailure.Classify(ex, cancellationToken);
                        await _resolver.CompleteInvocationAsync(invocationId, classification.Cancelled ? "cancelled" : "failed",
                            errorCategory: classification.Category, safeErrorMessage: classification.SafeMessage,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        // Cancellation-shaped exceptions escape raw: upstream handlers (including
                        // the stop path) key off OperationCanceledException, and re-typing one
                        // would turn a cancel into a failure.
                        if (classification.Cancelled || ex is OperationCanceledException) throw;
                        lastFailure = classification;
                        // A rate limit or a 5xx is usually gone in a second. Retrying the SAME
                        // candidate before walking the chain is what makes a single-candidate
                        // route survivable — and multi-candidate routes often point at the same
                        // provider, where the delay buys more than the switch does.
                        if (ShouldRetrySame(classification, attemptOnCandidate))
                        {
                            await Task.Delay(RetryBackoff(Math.Min(attemptOnCandidate - 1, RetryBackoffCount - 1)), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        break;   // next candidate, or the chain is exhausted
                    }
                }
            }
            throw new ModelInvocationExhaustedException(lastFailure?.Category, lastFailure?.SafeMessage, lastError);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
            var callId = Guid.NewGuid();
            Exception? lastError = null;
            ModelInvocationFailure? lastFailure = null;
            var attempt = 0;
            IReadOnlyList<ChatResponseUpdate>? completed = null;
            for (var index = 0; index < _resolutions.Count; index++)
            {
                var resolution = _resolutions[index];
                for (var attemptOnCandidate = 1; ; attemptOnCandidate++)
                {
                    var invocationId = await StartAsync(callId, ++attempt, resolution, cancellationToken).ConfigureAwait(false);
                    if (!resolution.IsAvailable)
                    {
                        lastError = new ModelCandidateUnavailableException(resolution.Error ?? "Model candidate is unavailable.");
                        lastFailure = new ModelInvocationFailure(true, false, "candidate_unavailable", lastError.Message);
                        await _resolver.CompleteInvocationAsync(invocationId, "failed", errorCategory: "candidate_unavailable",
                            safeErrorMessage: lastError.Message, cancellationToken: cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    try
                    {
                        var client = await _inner.CreateAsync(resolution, cancellationToken).ConfigureAwait(false);
                        var updates = new List<ChatResponseUpdate>();
                        await foreach (var update in client.GetStreamingResponseAsync(materialized, options, cancellationToken).ConfigureAwait(false))
                            updates.Add(update);
                        var usage = updates
                            .SelectMany(update => update.Contents.OfType<UsageContent>())
                            .Select(content => Maf18RuntimeAdapter.NormalizeUsage(content.Details))
                            .Aggregate<ModelUsage?, ModelUsage?>(null, Maf18RuntimeAdapter.AddUsage);
                        await _resolver.CompleteInvocationAsync(invocationId, "succeeded", usage, cancellationToken: cancellationToken).ConfigureAwait(false);
                        completed = updates;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        var classification = ModelInvocationFailure.Classify(ex, cancellationToken);
                        await _resolver.CompleteInvocationAsync(invocationId, classification.Cancelled ? "cancelled" : "failed",
                            errorCategory: classification.Category, safeErrorMessage: classification.SafeMessage,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        // See GetResponseAsync: cancellation-shaped exceptions escape raw.
                        if (classification.Cancelled || ex is OperationCanceledException) throw;
                        lastFailure = classification;
                        if (ShouldRetrySame(classification, attemptOnCandidate))
                        {
                            await Task.Delay(RetryBackoff(Math.Min(attemptOnCandidate - 1, RetryBackoffCount - 1)), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        break;   // next candidate, or the chain is exhausted
                    }
                }
            }

            // Streamed to a local buffer on purpose: a candidate that fails midway must not
            // have already emitted a partial completion to the caller's durable stream.
            if (completed is null) throw new ModelInvocationExhaustedException(lastFailure?.Category, lastFailure?.SafeMessage, lastError);
            foreach (var update in completed) yield return update;
        }

        private Task<Guid> StartAsync(Guid callId, int attempt, ChatResolution resolution, CancellationToken cancellationToken) =>
            _resolver.StartInvocationAsync(new ModelInvocationStart(
                callId,
                attempt,
                _context.SessionId,
                _context.RunId,
                _context.TurnId,
                _context.AgentInstanceId,
                _context.AgentDefinitionId,
                _context.AgentVersionId,
                _context.ModeVersionId,
                resolution.StrategySource ?? _context.StrategySource,
                resolution), cancellationToken);

        public TService? GetService<TService>(object? key = null) where TService : class =>
            typeof(TService) == typeof(IChatClient) ? (TService)(object)this : null;

        public object? GetService(Type serviceType, object? key = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }
}

internal sealed class ModelCandidateUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The frozen candidate chain is exhausted: every candidate either was unavailable or
/// failed a call. Carries the CLASSIFIED failure so the durable run can record WHY
/// instead of collapsing to <c>runtime</c>.
///
/// It derives from <see cref="InvalidOperationException"/> deliberately. The engine's
/// failure reporter surfaces the message for exactly that family and replaces every
/// other exception type with "Unexpected runtime failure." — which is how a plain
/// provider 429 used to reach the user as an unactionable string while the model
/// invocation row right next to it already said "rate limit was reached". The message
/// is the classifier's curated text, never the raw provider payload.
/// </summary>
internal sealed class ModelInvocationExhaustedException : InvalidOperationException
{
    public ModelInvocationExhaustedException(string? category, string? safeMessage, Exception? inner)
        : base(safeMessage ?? "The frozen model plan has no usable candidate.", inner) =>
        Category = category ?? "candidate_unavailable";

    /// <summary>The classifier's category for the last failure (429 → <c>rate_limited</c>, …).</summary>
    public string Category { get; }
}

internal sealed record ModelInvocationFailure(bool CanFallback, bool Cancelled, string Category, string SafeMessage)
{
    public static ModelInvocationFailure Classify(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
            return new(false, true, "cancelled", "The model call was cancelled.");
        if (exception is TimeoutException or TaskCanceledException)
            return new(true, false, "timeout", "The model provider timed out.");
        if (exception is HttpRequestException http)
            return FromStatus(http.StatusCode, "connection_failed", "The model provider connection failed.");
        if (exception is IOException)
            return new(true, false, "connection_failed", "The model provider connection failed.");

        var status = TryReadStatus(exception);
        if (status is not null) return FromStatus(status, "provider_error", "The model provider rejected the call.");
        return new(false, false, "request_error", "The model request failed before a retryable provider response was received.");
    }

    private static ModelInvocationFailure FromStatus(HttpStatusCode? status, string fallbackCategory, string fallbackMessage)
    {
        var value = status is null ? 0 : (int)status.Value;
        if (value is 401 or 403) return new(false, false, "authentication_or_authorization", "The model provider rejected authentication or authorization.");
        if (value == 429) return new(true, false, "rate_limited", "The model provider rate limit was reached.");
        if (value >= 500) return new(true, false, "provider_server_error", "The model provider returned a server error.");
        if (value >= 400) return new(false, false, "request_error", "The model provider rejected the request.");
        return new(true, false, fallbackCategory, fallbackMessage);
    }

    private static HttpStatusCode? TryReadStatus(Exception exception)
    {
        foreach (var name in new[] { "Status", "StatusCode" })
        {
            var value = exception.GetType().GetProperty(name)?.GetValue(exception);
            if (value is HttpStatusCode status) return status;
            if (value is int number && number is >= 100 and <= 599) return (HttpStatusCode)number;
        }
        return exception.InnerException is null ? null : TryReadStatus(exception.InnerException);
    }
}
