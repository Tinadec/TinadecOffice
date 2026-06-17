using TinadecModel.Abstractions;

namespace TinadecModel.Services;

public sealed record ProviderExecutionPolicy(
    int MaxAttempts,
    TimeSpan? AttemptTimeout = null,
    bool EnableRetries = true,
    bool RecordRetryableFailuresToHealth = true)
{
    public static ProviderExecutionPolicy SingleAttempt(TimeSpan? attemptTimeout = null)
        => new(1, attemptTimeout, EnableRetries: false);

    public static ProviderExecutionPolicy RetryableHttpDefault()
        => new(3);
}

public sealed record ProviderExecutionResult<T>(
    bool Succeeded,
    T? Value,
    ProviderFailureDetails? Failure,
    int AttemptsUsed)
{
    public static ProviderExecutionResult<T> Success(T value, int attemptsUsed)
        => new(true, value, null, attemptsUsed);

    public static ProviderExecutionResult<T> Failed(ProviderFailureDetails failure, int attemptsUsed)
        => new(false, default, failure, attemptsUsed);
}

public static class ProviderPolicyHelpers
{
    public static async Task<ProviderExecutionResult<T>> ExecuteAsync<T>(
        string providerId,
        Func<CancellationToken, Task<T>> execute,
        Func<Exception, ProviderFailureDetails> mapFailure,
        ProviderExecutionPolicy policy,
        IModelStore? store,
        CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, policy.MaxAttempts);
        ProviderFailureDetails? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = policy.AttemptTimeout is { } timeout
                    ? new CancellationTokenSource(timeout)
                    : null;
                using var linkedCts = timeoutCts is null
                    ? null
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var executionToken = linkedCts?.Token ?? cancellationToken;

                var value = await execute(executionToken);
                return ProviderExecutionResult<T>.Success(value, attempt);
            }
            catch (Exception exception)
            {
                var failure = mapFailure(exception);
                if (exception is OperationCanceledException
                    && !cancellationToken.IsCancellationRequested
                    && policy.AttemptTimeout is not null)
                {
                    failure = ProviderErrorMapper.FromException(providerId, new TimeoutException("Provider request timed out."));
                }

                lastFailure = failure;

                var canRetry = policy.EnableRetries
                    && attempt < maxAttempts
                    && ProviderErrorMapper.IsRetryable(failure.Category);
                if (!canRetry)
                {
                    if (policy.RecordRetryableFailuresToHealth
                        && failure.Retryable
                        && store is not null)
                    {
                        store.RecordModelProviderFailure(providerId, failure.Category, DateTimeOffset.UtcNow);
                    }

                    return ProviderExecutionResult<T>.Failed(failure, attempt);
                }
            }
        }

        return ProviderExecutionResult<T>.Failed(
            lastFailure ?? ProviderErrorMapper.FromException(providerId, new InvalidOperationException("Provider request failed.")),
            Math.Max(1, policy.MaxAttempts));
    }
}
