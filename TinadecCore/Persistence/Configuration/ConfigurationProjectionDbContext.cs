using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Persistence;

/// <summary>
/// Configuration writes commit to the scope TOML before updating its disposable query projection.
/// Historical runtime tables use ordinary DbContexts and never enter this boundary.
/// </summary>
public abstract class ConfigurationProjectionDbContext(DbContextOptions options) : DbContext(options)
{
    internal ConfigurationProjectionCoordinator? ConfigurationCoordinator { get; set; }
    internal Dictionary<string, string> ConfigurationDocumentHashes { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> PendingProjectionStampUpdates { get; } = new(StringComparer.Ordinal);
    internal bool ApplyingConfigurationProjection { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (!ApplyingConfigurationProjection && ConfigurationCoordinator is { } coordinator)
            coordinator.PersistChangesAsync(this, CancellationToken.None).GetAwaiter().GetResult();
        var written = base.SaveChanges(acceptAllChangesOnSuccess);
        if (!ApplyingConfigurationProjection && PendingProjectionStampUpdates.Count > 0 && ConfigurationCoordinator is { } stampCoordinator)
            stampCoordinator.RecordSavedProjectionStampsAsync(this, CancellationToken.None).GetAwaiter().GetResult();
        return written;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (!ApplyingConfigurationProjection && ConfigurationCoordinator is { } coordinator)
            await coordinator.PersistChangesAsync(this, cancellationToken).ConfigureAwait(false);
        var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        if (!ApplyingConfigurationProjection && PendingProjectionStampUpdates.Count > 0 && ConfigurationCoordinator is { } stampCoordinator)
            await stampCoordinator.RecordSavedProjectionStampsAsync(this, cancellationToken).ConfigureAwait(false);
        return written;
    }
}
