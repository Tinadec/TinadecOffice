using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TinadecCore.Persistence;

/// <summary>
/// Internal secret boundary used by one-time governance credentials.  The
/// durable store is preferred; the process-local cache is only a development
/// fallback for hosts that intentionally use a read-only environment secret
/// provider.  Relational records contain a reference and a hash, never the
/// credential itself.
/// </summary>
public interface INonceMaterialStore
{
    Task<string> PutAsync(string reference, string value, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

internal sealed class NonceMaterialStore : INonceMaterialStore
{
    private readonly ISecretStore _secrets;
    private readonly ConcurrentDictionary<string, string> _fallback = new(StringComparer.Ordinal);

    public NonceMaterialStore(ISecretStore secrets) => _secrets = secrets;

    public async Task<string> PutAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return await _secrets.PutAsync(reference, value, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            _fallback[reference] = value;
            return reference;
        }
    }

    public async Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            var value = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            if (value is not null) return value;
        }
        catch (CryptographicException)
        {
            // A protected material failure must never fall back to an in-process
            // copy. Governance callers revoke and fail closed on this path.
            return null;
        }
        catch (InvalidOperationException)
        {
            // Read-only environment stores may not contain generated material.
        }
        return _fallback.TryGetValue(reference, out var fallback) ? fallback : null;
    }

    public async Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        _fallback.TryRemove(reference, out _);
        try { await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
    }
}
