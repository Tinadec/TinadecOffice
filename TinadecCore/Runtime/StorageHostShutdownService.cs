using Microsoft.Extensions.Hosting;

namespace TinadecCore.Runtime;

/// <summary>Register before the workers so graceful shutdown releases mounts and the host lock last.</summary>
public sealed class StorageHostShutdownService(IStorageScopeRegistry registry, UserStorageHostLease hostLease) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Once workers have stopped, finish resource disposal even when the caller's
        // shutdown deadline expires. A restart must not overlap an old storage graph.
        try
        {
            if (registry is IAsyncDisposable owned) await owned.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            hostLease.Dispose();
        }
    }
}
