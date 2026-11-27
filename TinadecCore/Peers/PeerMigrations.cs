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
        await EnsureWave2SchemaAndIndexesAsync(_dbFactory, _timeProvider, cancellationToken).ConfigureAwait(false);
        await EnsureInboundSettingsDefaultAsync(_dbFactory, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task EnsureWave2SchemaAndIndexesAsync(
        IDbContextFactory<PeerDbContext> dbFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);

        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"ix_peer_shadow_ws_identity\" ON \"peer_shadow_workspaces\" (\"peer_device_id\", \"remote_project_id\");",
                cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"ix_peer_shadow_sess_identity\" ON \"peer_shadow_sessions\" (\"peer_device_id\", \"remote_session_id\");",
                cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"ix_peer_ingress_queue_fifo\" ON \"peer_ingress_queue\" (\"session_id\", \"stage\", \"enqueued_seq\");",
                cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"ix_peer_staged_attachments_session\" ON \"peer_staged_attachments\" (\"session_id\", \"status\");",
                cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"ix_peer_staged_attachments_expires\" ON \"peer_staged_attachments\" (\"expires_at\");",
                cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"ix_peer_delegation_run\" ON \"peer_delegation_links\" (\"parent_run_id\", \"status\");",
                cancellationToken).ConfigureAwait(false);
        }

        // Recover any interrupted ingress rows left in received/processing without a run_id by a crashed host process.
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await db.IngressQueue
            .Where(x => (x.Stage == "received" || x.Stage == "processing") && x.RunId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Stage, "rejected")
                    .SetProperty(x => x.RejectErrorCode, "host_restarted")
                    .SetProperty(x => x.UpdatedAt, nowMs),
                cancellationToken)
            .ConfigureAwait(false);
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
