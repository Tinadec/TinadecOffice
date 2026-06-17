using Tinadec.Contracts.Models;
using TinadecModel.Providers;

namespace TinadecModel.Services;

public sealed record CredentialValidationResult(
    bool IsValid,
    ProviderErrorCategory? ErrorCategory,
    string? SafeMessage);

public static class ProviderCredentialValidator
{
    public static CredentialValidationResult Validate(ResolvedModelInvocationContextDto context, string? apiKey)
    {
        if (string.Equals(context.ConnectionKind, "cli", StringComparison.OrdinalIgnoreCase))
            return new CredentialValidationResult(true, null, null);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderTemplateRules.RequiresApiKey(context.Driver, context.ConnectionKind, context.Provider?.Capabilities)
                ? new CredentialValidationResult(false, ProviderErrorCategory.AuthenticationFailed, "Provider API key is missing or invalid.")
                : new CredentialValidationResult(true, null, null);
        }

        var trimmed = apiKey.Trim();
        if (!string.Equals(apiKey, trimmed, StringComparison.Ordinal) || trimmed.Length < 8 || trimmed.Any(char.IsWhiteSpace))
            return new CredentialValidationResult(false, ProviderErrorCategory.AuthenticationFailed, "Provider API key is missing or invalid.");

        return new CredentialValidationResult(true, null, null);
    }

    public static bool ContainsRawSecret(string value, string? rawSecret)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(rawSecret))
            return false;
        return value.Contains(rawSecret, StringComparison.Ordinal);
    }
}
