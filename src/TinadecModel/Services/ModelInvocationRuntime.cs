using System.Diagnostics;
using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Tracing;

namespace TinadecModel.Services;

public sealed class ModelInvocationRuntime(
    IModelRouteResolver routeResolver,
    IModelCredentialResolver credentialResolver,
    IEnumerable<IModelProviderRuntime> providerRuntimes,
    IModelStore? store = null) : IModelInvocationRuntime
{
    public async Task<ModelInvocationResultDto> InvokeAsync(
        string sessionId, string purpose, IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default,
        string? systemPrompt = null, IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var requestMessages = BuildRequestMessages(sessionId, messages, systemPrompt);
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.ModelProviderInvocation);
        activity?.SetTag(ModelSpanAttrs.SessionId, sessionId)
            .SetTag(ModelSpanAttrs.RoutePurpose, purpose)
            .SetTag(ModelSpanAttrs.MessageCount, requestMessages.Count);

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
                return new ModelInvocationResultDto("failed", terminalContent, terminalContext, false, null,
                    ProviderErrorCategory.ProviderUnavailable, false, null, null, terminalContent, null);
            }

            if (string.Equals(result.Status, "executed", StringComparison.OrdinalIgnoreCase))
            {
                if (firstFailure is not null && store is not null)
                {
                    var recoveredProviderId = result.Context.ProviderInstanceId;
                    store.RecordModelProviderSuccess(recoveredProviderId);
                }
                return firstFailure is null ? result
                    : result with { ErrorProviderId = firstFailure.ErrorProviderId ?? firstFailure.Context.ProviderInstanceId };
            }

            firstFailure ??= result;
            lastFailure = result;
            if (!ShouldTryFallback(result, attemptedProviderIds))
                return result;

            attemptedProviderIds.Add(result.ErrorProviderId ?? result.Context.ProviderInstanceId);
            if (store is not null && result.ErrorCategory is { } category && !RuntimeRecordsRetryableFailure(result))
                store.RecordModelProviderFailure(result.ErrorProviderId ?? result.Context.ProviderInstanceId, category, DateTimeOffset.UtcNow);
        }

        return lastFailure ?? firstFailure ?? throw new InvalidOperationException("Model invocation did not produce a result.");
    }

    private async Task<ModelInvocationResultDto> InvokeResolvedProviderAsync(
        string purpose, IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken, IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var context = routeResolver.Resolve(purpose);
        using var activity = ModelActivitySource.Instance.StartActivity(ModelSpanNames.ModelRequest);
        activity?.SetTag(ModelSpanAttrs.RoutePurpose, context.Purpose)
            .SetTag(ModelSpanAttrs.ProviderId, context.ProviderInstanceId)
            .SetTag(ModelSpanAttrs.ProviderInstanceId, context.ProviderInstanceId)
            .SetTag(ModelSpanAttrs.Model, context.EffectiveModel);

        var apiKey = credentialResolver.ResolveApiKey(context);
        var credentialValidation = ProviderCredentialValidator.Validate(context, apiKey);
        if (!credentialValidation.IsValid)
            return new ModelInvocationResultDto("failed", credentialValidation.SafeMessage ?? "Provider authentication failed.",
                context, true, null, credentialValidation.ErrorCategory, false, null, null,
                credentialValidation.SafeMessage, context.ProviderInstanceId);

        var runtime = providerRuntimes
            .Where(r => r.CanHandle(context))
            .OrderByDescending(r => string.Equals(r.Id, context.ProviderInstanceId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => string.Equals(r.Id, context.Driver, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (runtime is null)
            return new ModelInvocationResultDto("failed",
                $"No model runtime is registered for provider '{context.ProviderInstanceId}' (connection kind: {context.ConnectionKind}).",
                context, true, null);

        var result = await runtime.GenerateAsync(context, apiKey, messages, cancellationToken, tools);
        activity?.SetTag(ModelSpanAttrs.Status, result.Status)
            .SetTag(ModelSpanAttrs.ErrorCategory, result.ErrorCategory?.ToString());
        return result;
    }

    private static IReadOnlyList<MessageDto> BuildRequestMessages(string sessionId, IReadOnlyList<MessageDto> messages, string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt)) return messages;
        var systemMessage = new MessageDto($"sys_{Guid.NewGuid():N}", sessionId, "system", systemPrompt.Trim(), DateTimeOffset.UtcNow);
        return [systemMessage, .. messages];
    }

    private static bool ShouldTryFallback(ModelInvocationResultDto result, HashSet<string> attemptedProviderIds)
    {
        var failedProviderId = result.ErrorProviderId ?? result.Context.ProviderInstanceId;
        return result.IsRetryable && result.ErrorCategory is not null && !attemptedProviderIds.Contains(failedProviderId);
    }

    private static bool RuntimeRecordsRetryableFailure(ModelInvocationResultDto result)
        => result.IsRetryable
            && (string.Equals(result.RuntimeId, "openai-compatible", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.RuntimeId, "cli-provider", StringComparison.OrdinalIgnoreCase));
}
