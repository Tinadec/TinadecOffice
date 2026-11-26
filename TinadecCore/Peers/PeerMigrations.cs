using Microsoft.EntityFrameworkCore;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

/// <summary>
/// Ensures default factory-safe <c>peer_inbound_settings</c> (<c>enabled = 0, listen_port = 48735</c>)
/// exists after <see cref="DbContextMigrationParticipant{PeerDbContext}"/> bootstraps the peer tables.
/// </summary>
public sealed class PeerMigrations : IStorageMigrationParticipant
{
    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public PeerMigrations(IDbContextFactory<PeerDbContext> dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInboundSettingsDefaultAsync(_dbFactory, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PeerInboundSettingsRecord> EnsureInboundSettingsDefaultAsync(
        IDbContextFactory<PeerDbContext> dbFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.InboundSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var record = new PeerInboundSettingsRecord
        {
            Id = 1,
            LocalDeviceId = Guid.NewGuid().ToString("N"),
            DisplayName = ResolveDefaultDisplayName(),
            Enabled = 0,
            ListenPort = PeerConstants.DefaultListenPort,
            UpdatedAt = nowMs
        };

        db.InboundSettings.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch (DbUpdateException)
        {
            await using var retryDb = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await retryDb.InboundSettings.AsNoTracking()
                .SingleAsync(x => x.Id == 1, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static string ResolveDefaultDisplayName()
    {
        try
        {
            var machine = Environment.MachineName?.Trim();
            if (!string.IsNullOrWhiteSpace(machine))
            {
                return machine.Length <= 128 ? machine : machine[..128];
            }
        }
        catch
        {
            // Fall back to deterministic label when machine name cannot be read.
        }

        return "Tinadec Host";
    }
}
