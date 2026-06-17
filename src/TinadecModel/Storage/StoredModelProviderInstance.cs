using Tinadec.Contracts.Models;
using TinadecModel.Providers;

namespace TinadecModel.Storage;

public sealed record StoredModelProviderInstance(
    string Id,
    string Driver,
    string DisplayName,
    string ConnectionKind,
    string? BaseUrl,
    string? Model,
    string? EncryptedApiKey,
    string? BinaryPath,
    string? HomePath,
    string? ServerUrl,
    string? LaunchArgs,
    IReadOnlyList<string> Capabilities,
    bool Enabled,
    ProviderHealthStatus HealthStatus,
    DateTimeOffset? CooldownUntil,
    int FailureCount,
    DateTimeOffset? LastFailureAt,
    ProviderErrorCategory? LastErrorCategory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(EncryptedApiKey);

    public ModelProviderInstanceDto ToDto()
    {
        var (status, message) = ResolveStatus();
        return new ModelProviderInstanceDto(
            Id, Driver, DisplayName, ConnectionKind,
            BaseUrl, Model, HasApiKey,
            BinaryPath, HomePath, ServerUrl, LaunchArgs,
            Capabilities, Enabled, status, message,
            CooldownUntil, CreatedAt, UpdatedAt);
    }

    public StoredModelSettings ToModelSettings(string? routeModel = null)
    {
        return new StoredModelSettings(BaseUrl ?? string.Empty, routeModel ?? Model ?? string.Empty, EncryptedApiKey, UpdatedAt);
    }

    private (string Status, string Message) ResolveStatus()
    {
        if (!Enabled)
            return ("disabled", "Provider is disabled.");
        if (HealthStatus is ProviderHealthStatus.Disabled)
            return ("disabled", "Provider health is disabled.");

        var isCli = ConnectionKind.Equals("cli", StringComparison.OrdinalIgnoreCase);
        if (isCli && string.IsNullOrWhiteSpace(BinaryPath))
            return ("not_configured", "CLI binary path is required.");
        if (!isCli && (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(Model)))
            return ("not_configured", "Base URL and model are required.");

        if (ProviderTemplateRules.RequiresApiKey(Driver, ConnectionKind, Capabilities) && !HasApiKey)
            return ("needs_key", "API key is not set.");

        if (IsInCooldown())
        {
            return LastErrorCategory is null
                ? ("cooldown", "Provider is cooling down after a retryable failure.")
                : ("cooldown", $"Provider is cooling down after {LastErrorCategory.Value}.");
        }

        return isCli
            ? ("ready", "CLI provider is configured.")
            : ("ready", "Provider is ready.");
    }

    private bool IsInCooldown()
    {
        if (HealthStatus is not ProviderHealthStatus.Cooldown and not ProviderHealthStatus.Unhealthy)
            return false;
        return CooldownUntil is null || CooldownUntil > DateTimeOffset.UtcNow;
    }
}
