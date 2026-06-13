using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;
using TinadecCore.Tracing;

namespace TinadecCore.Services;

public sealed class ModelInvocationRuntime(
    IModelRouteResolver routeResolver,
    IModelCredentialResolver credentialResolver,
    IEnumerable<IModelProviderRuntime> providerRuntimes,
    CoreStore? store = null) : IModelInvocationRuntime
{
    public async Task<ModelInvocationResultDto> InvokeAsync(
        string sessionId,
        string purpose,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default,
        string? systemPrompt = null,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var requestMessages = BuildRequestMessages(sessionId, messages, systemPrompt);
        using var activity = TinadecActivitySource.Instance.StartActivity(SpanNames.ModelProviderInvocation);
        activity?
            .SetTag(SpanAttrs.SessionId, sessionId)
            .SetTag(SpanAttrs.RoutePurpose, purpose)
            .SetTag(SpanAttrs.MessageCount, requestMessages.Count);

        ModelInvocationResultDto? firstFailure = null;
        ModelInvocationResultDto? lastFailure = null;
        var attemptedProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            ModelInvocationResultDto result;
            try
            {
                result = await InvokeResolvedProviderAsync(purpose, requestMessages, cancellationToken, tools);
            }
            catch (InvalidOperationException)
            {
                var terminalContext = lastFailure?.Context ?? firstFailure?.Context
                    ?? throw new InvalidOperationException("Model invocation did not produce a result.");
                var terminalContent = lastFailure is not null
                    ? $"All model providers failed. Last error: {lastFailure.Content}. No fallback provider is available."
                    : $"No model provider is available for purpose '{purpose}'.";
                var terminalFailure = new ModelInvocationResultDto(
                    "failed",
                    terminalContent,
                    terminalContext,
                    false,
                    null,
                    ProviderErrorCategory.ProviderUnavailable,
                    false,
                    null,
                    null,
                    terminalContent,
                    null);
                SetOutcomeTags(activity, terminalFailure, attempt, lastFailure?.ErrorProviderId ?? lastFailure?.Context.ProviderInstanceId ?? firstFailure?.ErrorProviderId ?? firstFailure?.Context.ProviderInstanceId);
                return terminalFailure;
            }

            if (string.Equals(result.Status, "executed", StringComparison.OrdinalIgnoreCase))
            {
                if (firstFailure is not null && store is not null)
                {
                    var recoveredProviderId = result.Context.ProviderInstanceId;
                    store.RecordModelProviderSuccess(recoveredProviderId);
                    activity?.AddSpanEvent("model.provider.health.recovered", new[]
                    {
                        new KeyValuePair<string, object?>(SpanAttrs.ProviderId, recoveredProviderId),
                        new KeyValuePair<string, object?>(SpanAttrs.ProviderInstanceId, recoveredProviderId),
                        new KeyValuePair<string, object?>(SpanAttrs.HealthStatus, ProviderHealthStatus.Healthy.ToString())
                    });
                }

                var finalResult = firstFailure is null
                    ? result
                    : result with
                    {
                        ErrorProviderId = firstFailure.ErrorProviderId ?? firstFailure.Context.ProviderInstanceId
                    };
                SetOutcomeTags(activity, finalResult, attempt, firstFailure?.ErrorProviderId ?? firstFailure?.Context.ProviderInstanceId);
                return finalResult;
            }

            firstFailure ??= result;
            lastFailure = result;
            if (!ShouldTryFallback(result, attemptedProviderIds))
            {
                SetOutcomeTags(activity, result, attempt, null);
                return result;
            }

            attemptedProviderIds.Add(result.ErrorProviderId ?? result.Context.ProviderInstanceId);
            var failedProviderId = result.ErrorProviderId ?? result.Context.ProviderInstanceId;
            activity?
                .SetTag(SpanAttrs.Status, "fallback")
                .SetTag(SpanAttrs.RetryCount, attempt + 1)
                .SetTag(SpanAttrs.FallbackProviderId, failedProviderId)
                .SetTag(SpanAttrs.ProviderId, failedProviderId)
                .AddSpanEvent("model.fallback.selected", new[]
                {
                    new KeyValuePair<string, object?>(SpanAttrs.ProviderId, failedProviderId),
                    new KeyValuePair<string, object?>(SpanAttrs.ProviderInstanceId, failedProviderId),
                    new KeyValuePair<string, object?>(SpanAttrs.ErrorCategory, result.ErrorCategory?.ToString()),
                    new KeyValuePair<string, object?>(SpanAttrs.RetryCount, attempt + 1)
                });

            if (store is not null && result.ErrorCategory is { } category && !RuntimeRecordsRetryableFailure(result))
            {
                store.RecordModelProviderFailure(result.ErrorProviderId ?? result.Context.ProviderInstanceId, category, DateTimeOffset.UtcNow);
                activity?.AddSpanEvent("model.provider.health.updated", new[]
                {
                    new KeyValuePair<string, object?>(SpanAttrs.ProviderId, failedProviderId),
                    new KeyValuePair<string, object?>(SpanAttrs.ProviderInstanceId, failedProviderId),
                    new KeyValuePair<string, object?>(SpanAttrs.HealthStatus, ProviderHealthStatus.Cooldown.ToString()),
                    new KeyValuePair<string, object?>(SpanAttrs.ErrorCategory, category.ToString())
                });
            }
        }

        return lastFailure ?? firstFailure ?? throw new InvalidOperationException("Model invocation did not produce a result.");
    }

    private async Task<ModelInvocationResultDto> InvokeResolvedProviderAsync(
        string purpose,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var context = routeResolver.Resolve(purpose);
        using var invocationActivity = TinadecActivitySource.Instance.StartActivity(SpanNames.ModelRequest);
        invocationActivity?
            .SetTag(SpanAttrs.RoutePurpose, context.Purpose)
            .SetTag(SpanAttrs.ProviderId, context.ProviderInstanceId)
            .SetTag(SpanAttrs.ProviderInstanceId, context.ProviderInstanceId)
            .SetTag(SpanAttrs.Model, context.EffectiveModel);

        var apiKey = credentialResolver.ResolveApiKey(context);
        var credentialValidation = ProviderCredentialValidator.Validate(context, apiKey);
        if (!credentialValidation.IsValid)
        {
            invocationActivity?
                .SetTag(SpanAttrs.Status, "failed")
                .SetTag(SpanAttrs.ErrorCategory, credentialValidation.ErrorCategory?.ToString());
            return new ModelInvocationResultDto(
                "failed",
                credentialValidation.SafeMessage ?? "Provider authentication failed.",
                context,
                true,
                null,
                credentialValidation.ErrorCategory,
                false,
                null,
                null,
                credentialValidation.SafeMessage,
                context.ProviderInstanceId);
        }

        var runtime = providerRuntimes
            .Where(item => item.CanHandle(context))
            .OrderByDescending(item =>
                string.Equals(item.Id, context.ProviderInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item =>
                string.Equals(item.Id, context.Driver, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (runtime is null)
        {
            var content = $"No model runtime is registered for provider '{context.ProviderInstanceId}' (connection kind: {context.ConnectionKind}).";
            invocationActivity?
                .SetTag(SpanAttrs.Status, "failed")
                .SetTag(SpanAttrs.ErrorCategory, ProviderErrorCategory.Unknown.ToString());
            return new ModelInvocationResultDto("failed", content, context, true, null);
        }

        var result = await runtime.GenerateAsync(context, apiKey, messages, cancellationToken, tools);
        invocationActivity?
            .SetTag(SpanAttrs.Status, result.Status)
            .SetTag(SpanAttrs.ErrorCategory, result.ErrorCategory?.ToString());
        return result;
    }

    private static IReadOnlyList<MessageDto> BuildRequestMessages(
        string sessionId,
        IReadOnlyList<MessageDto> messages,
        string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return messages;
        }

        var systemMessage = new MessageDto(
            $"sys_{Guid.NewGuid():N}",
            sessionId,
            "system",
            systemPrompt.Trim(),
            DateTimeOffset.UtcNow);

        return [systemMessage, .. messages];
    }

    private static void SetOutcomeTags(
        System.Diagnostics.Activity? activity,
        ModelInvocationResultDto result,
        int attempt,
        string? fallbackProviderId)
    {
        activity?
            .SetTag(SpanAttrs.ProviderId, result.Context.ProviderInstanceId)
            .SetTag(SpanAttrs.ProviderInstanceId, result.Context.ProviderInstanceId)
            .SetTag(SpanAttrs.Model, result.Context.EffectiveModel)
            .SetTag(SpanAttrs.Status, result.Status)
            .SetTag(SpanAttrs.ErrorCategory, result.ErrorCategory?.ToString())
            .SetTag(SpanAttrs.RetryCount, attempt)
            .SetTag(SpanAttrs.FallbackProviderId, fallbackProviderId);
    }

    private static bool ShouldTryFallback(ModelInvocationResultDto result, HashSet<string> attemptedProviderIds)
    {
        var failedProviderId = result.ErrorProviderId ?? result.Context.ProviderInstanceId;
        return result.IsRetryable
            && result.ErrorCategory is not null
            && !attemptedProviderIds.Contains(failedProviderId);
    }

    private static bool RuntimeRecordsRetryableFailure(ModelInvocationResultDto result)
    {
        return result.IsRetryable
            && (string.Equals(result.RuntimeId, "openai-compatible", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.RuntimeId, "cli-provider", StringComparison.OrdinalIgnoreCase));
    }
}
