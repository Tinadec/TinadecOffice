using System.Collections.Concurrent;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// In-memory ISecretStore double. Values round-trip per reference so governance
/// lease nonces survive a put/get cycle; references that were never written
/// stand in for pre-configured model provider API keys so readiness checks see
/// a usable credential.
/// </summary>
internal sealed class TestModelSecretStore(bool available = true) : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string> PutAsync(string secretReference, string value, CancellationToken cancellationToken = default)
    {
        _values[secretReference] = value;
        return Task.FromResult(secretReference);
    }

    public Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(available);

    public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(
            available && _values.TryGetValue(secretReference, out var value) ? value
            : available ? "test-api-key"
            : null);

    public Task DeleteAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(secretReference, out _);
        return Task.CompletedTask;
    }
}
