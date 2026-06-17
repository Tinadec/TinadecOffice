using Tinadec.Contracts.Models;

namespace TinadecModel.Services;

public sealed record ProviderFailureDetails(
    ProviderErrorCategory Category,
    bool Retryable,
    int? StatusCode,
    int? ExitCode,
    string ProviderId,
    string SafeMessage);

public static class ProviderErrorMapper
{
    public static ProviderFailureDetails FromHttpStatus(string providerId, int? statusCode)
    {
        var category = MapHttpStatusCode(statusCode);
        return Create(providerId, category, statusCode, null);
    }

    public static ProviderFailureDetails FromCliExitCode(string providerId, int? exitCode)
    {
        var category = MapCliExitCode(exitCode);
        return Create(providerId, category, null, exitCode);
    }

    public static ProviderFailureDetails FromException(string providerId, Exception exception)
    {
        if (exception is OperationCanceledException)
            return Create(providerId, ProviderErrorCategory.Cancelled, null, null);
        if (exception is TimeoutException)
            return Create(providerId, ProviderErrorCategory.Timeout, null, null);
        if (exception is HttpRequestException httpRequestException)
        {
            var statusCode = httpRequestException.StatusCode is null
                ? (int?)null
                : (int)httpRequestException.StatusCode.Value;
            return FromHttpStatus(providerId, statusCode);
        }
        return Create(providerId, ProviderErrorCategory.Unknown, null, null);
    }

    public static ProviderErrorCategory MapHttpStatusCode(int? statusCode) => statusCode switch
    {
        400 or 404 or 422 => ProviderErrorCategory.InvalidRequest,
        401 or 403 => ProviderErrorCategory.AuthenticationFailed,
        408 => ProviderErrorCategory.Timeout,
        429 => ProviderErrorCategory.RateLimited,
        499 => ProviderErrorCategory.Cancelled,
        500 or 502 or 503 or 504 => ProviderErrorCategory.ProviderUnavailable,
        >= 500 => ProviderErrorCategory.ProviderUnavailable,
        >= 400 => ProviderErrorCategory.InvalidRequest,
        _ => ProviderErrorCategory.Unknown
    };

    public static ProviderErrorCategory MapCliExitCode(int? exitCode) => exitCode switch
    {
        0 => ProviderErrorCategory.Unknown,
        2 or 64 or 65 => ProviderErrorCategory.InvalidRequest,
        69 or 75 => ProviderErrorCategory.ProviderUnavailable,
        77 => ProviderErrorCategory.AuthenticationFailed,
        124 or 137 => ProviderErrorCategory.Timeout,
        130 or 143 => ProviderErrorCategory.Cancelled,
        _ => ProviderErrorCategory.Unknown
    };

    public static bool IsRetryable(ProviderErrorCategory category) => category is
        ProviderErrorCategory.RateLimited or ProviderErrorCategory.Timeout or ProviderErrorCategory.ProviderUnavailable;

    private static ProviderFailureDetails Create(string providerId, ProviderErrorCategory category, int? statusCode, int? exitCode)
        => new(category, IsRetryable(category), statusCode, exitCode, providerId, BuildSafeMessage(category));

    private static string BuildSafeMessage(ProviderErrorCategory category) => category switch
    {
        ProviderErrorCategory.AuthenticationFailed => "Provider authentication failed.",
        ProviderErrorCategory.RateLimited => "Provider rate limit exceeded.",
        ProviderErrorCategory.Timeout => "Provider request timed out.",
        ProviderErrorCategory.ProviderUnavailable => "Provider is temporarily unavailable.",
        ProviderErrorCategory.InvalidRequest => "Provider rejected the request.",
        ProviderErrorCategory.Cancelled => "Provider request was cancelled.",
        _ => "Provider request failed."
    };
}
