using System.Security.Cryptography;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Persistence;

internal sealed class LocalFileContentStore : IContentStore
{
    private readonly StoragePaths _paths;
    private readonly IContentLeaseRegistry _leases;

    public LocalFileContentStore(StoragePaths paths, IContentLeaseRegistry? leases = null)
    { _paths = paths; _leases = leases ?? new ContentLeaseRegistry(); }

    public async Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId == Guid.Empty) throw new ArgumentException("Tenant id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Kind)) throw new ArgumentException("Content kind is required.", nameof(request));
        using var lease = _leases.Acquire();

        if (request.Content.GetType() == typeof(MemoryStream)
            && request.Content is MemoryStream memoryStream
            && memoryStream.TryGetBuffer(out var bufferSegment))
        {
            var remaining = bufferSegment.AsSpan((int)memoryStream.Position);
            var quickHash = Convert.ToHexStringLower(SHA256.HashData(remaining));
            var quickReference = _paths.ContentReference(request.TenantId, request.WorkspaceId, request.Kind, quickHash);
            var quickDestination = _paths.ResolveContentReference(quickReference);
            if (File.Exists(quickDestination))
            {
                memoryStream.Position = memoryStream.Length;
                return new ContentReference(quickReference, quickHash, remaining.Length, request.MediaType);
            }
        }

        var temporary = _paths.ContentTemporary(request.TenantId, request.WorkspaceId, request.Kind);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            long length = 0;
            string hash;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await request.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        sha.AppendData(buffer, 0, read);
                        length += read;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            var reference = _paths.ContentReference(request.TenantId, request.WorkspaceId, request.Kind, hash);
            var destination = _paths.ResolveContentReference(reference);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                if (!File.Exists(destination)) File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // A concurrent content-addressed writer already persisted these exact bytes.
            }
            return new ContentReference(reference, hash, length, request.MediaType);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = _leases.Acquire();
        try
        {
            var path = _paths.ResolveContentReference(reference.Value);
            Stream stream = new LeasedReadStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous), lease);
            return Task.FromResult(stream);
        }
        catch { lease.Dispose(); throw; }
    }

    public Task<bool> ExistsAsync(ContentReference reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(_paths.ResolveContentReference(reference.Value)));

    public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = _leases.Acquire();
        var path = _paths.ResolveContentReference(reference.Value);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private sealed class LeasedReadStream(Stream inner, IDisposable lease) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { try { if (disposing) inner.Dispose(); } finally { if (disposing) lease.Dispose(); base.Dispose(disposing); } }
        public override async ValueTask DisposeAsync()
        { try { await inner.DisposeAsync().ConfigureAwait(false); } finally { lease.Dispose(); GC.SuppressFinalize(this); } }
    }
}
