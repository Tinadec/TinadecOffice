namespace TinadecCore.Persistence;

/// <summary>Secret material boundary. Relational records never contain plaintext or encrypted secret values.</summary>
public interface ISecretStore
{
    Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default);
}

/// <summary>Reads explicitly configured environment variables for development and deployment injection.</summary>
internal sealed class EnvironmentSecretStore : ISecretStore
{
    public Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(secretReference)));

    public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(Environment.GetEnvironmentVariable(secretReference));
}
