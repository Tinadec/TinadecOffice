namespace TinadecCore.Persistence;

/// <summary>Secret material boundary. Relational records never contain plaintext or encrypted secret values.</summary>
public interface ISecretStore
{
    Task<string> PutAsync(string secretReference, string value, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string secretReference, CancellationToken cancellationToken = default);
}

/// <summary>Reads explicitly configured environment variables for development and deployment injection.</summary>
internal sealed class EnvironmentSecretStore : ISecretStore
{
    public Task<string> PutAsync(string secretReference, string value, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("EnvironmentSecretStore is read-only; configure a writable platform SecretStore.");

    public Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(secretReference)));

    public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(Environment.GetEnvironmentVariable(secretReference));

    public Task DeleteAsync(string secretReference, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class ProtectedFileSecretStore : ISecretStore
{
    private readonly string _root;
    public ProtectedFileSecretStore(StoragePaths paths) => _root = Path.Combine(paths.Root, "secrets");
    private string PathFor(string reference) => System.IO.Path.Combine(_root, reference + ".bin");

    public async Task<string> PutAsync(string secretReference, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretReference)) throw new ArgumentException("Secret reference is required.", nameof(secretReference));
        Directory.CreateDirectory(_root);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        byte[] protectedBytes;
        if (OperatingSystem.IsWindows())
            protectedBytes = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        else
            protectedBytes = bytes;
        await File.WriteAllBytesAsync(PathFor(secretReference), protectedBytes, cancellationToken).ConfigureAwait(false);
        return secretReference;
    }

    public Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(PathFor(secretReference)));

    public async Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PathFor(secretReference))) return null;
        var bytes = await File.ReadAllBytesAsync(PathFor(secretReference), cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsWindows())
            bytes = System.Security.Cryptography.ProtectedData.Unprotect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public Task DeleteAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        var path = PathFor(secretReference);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
