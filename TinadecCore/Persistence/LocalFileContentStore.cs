using System.Security.Cryptography;

namespace TinadecCore.Persistence;

internal sealed class LocalFileContentStore : IContentStore
{
    private readonly StoragePaths _paths;

    public LocalFileContentStore(StoragePaths paths) => _paths = paths;

    public async Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId == Guid.Empty) throw new ArgumentException("Tenant id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Kind)) throw new ArgumentException("Content kind is required.", nameof(request));

        var temporary = _paths.ContentTemporary(request.TenantId, request.WorkspaceId, request.Kind);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        long length = 0;
        string hash;
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                length += read;
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        var reference = _paths.ContentReference(request.TenantId, request.WorkspaceId, request.Kind, hash);
        var destination = _paths.ResolveContentReference(reference);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            if (File.Exists(destination)) File.Delete(temporary); else File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new ContentReference(reference, hash, length, request.MediaType);
    }

    public Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken cancellationToken = default)
    {
        var path = _paths.ResolveContentReference(reference.Value);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(ContentReference reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(_paths.ResolveContentReference(reference.Value)));

    public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken = default)
    {
        var path = _paths.ResolveContentReference(reference.Value);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
