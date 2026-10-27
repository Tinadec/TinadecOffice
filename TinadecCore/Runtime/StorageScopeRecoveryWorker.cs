using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>Reopens registered service graphs after host startup without selecting a current project.</summary>
public sealed class StorageScopeRecoveryWorker(IStorageScopeRegistry registry, ILogger<StorageScopeRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Scopes mount independently and a first mount takes seconds (migrations, journal
        // reconciliation, recovery passes), so they are opened side by side instead of the
        // last registered project waiting for every one before it.
        var scopes = registry.List().Where(scope => scope.ScopeKind == "project").ToArray();
        try
        {
            await Parallel.ForEachAsync(scopes,
                new ParallelOptions { MaxDegreeOfParallelism = StorageScopeRegistry.MountParallelism, CancellationToken = stoppingToken },
                RecoverAsync).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async ValueTask RecoverAsync(StorageScopeDescriptor scope, CancellationToken stoppingToken)
    {
        var diagnostic = Path.Combine(registry.User.State, "scope-recovery", scope.StorageId + ".toml");
        try
        {
            await using var lease = await registry.AcquireAsync(scope.StorageId, stoppingToken).ConfigureAwait(false);
            if (File.Exists(diagnostic)) File.Delete(diagnostic);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Registered storage scope {StorageId} could not recover; other scopes remain available", scope.StorageId);
            StorageScopePaths.RejectLinks(registry.User.Root, diagnostic);
            await StorageScopeRegistry.AtomicWriteAsync(diagnostic, Tomlyn.TomlSerializer.Serialize(new Tomlyn.Model.TomlTable
            {
                ["storage_id"] = scope.StorageId, ["storage_root"] = scope.Root,
                ["error_type"] = ex.GetType().Name, ["reason"] = ex.Message,
                ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O")
            }), stoppingToken).ConfigureAwait(false);
        }
    }
}
