using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;

namespace TinadecCore.Runtime;

/// <summary>
/// Reconciles the companion identity domain with TinaChat at startup: every active companion gets
/// its named companion-kind participant, and a companion archived while the host was down loses it.
/// The bridge has to live in the composition root (the companion module may not reference TinaChat),
/// and startup is the only moment the host can see the whole roster before any turn is owed.
/// One companion's failure is logged and skipped — a broken row must not stop the host from serving
/// the others.
/// </summary>
public sealed class CompanionParticipantReconciler(
    CompanionChatBridge bridge,
    ITenantContextAccessor tenant,
    IDbContextFactory<CompanionDbContext> companions,
    ILogger<CompanionParticipantReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunPassAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down before the pass finished; the next start repeats it.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Companion participant reconciliation failed.");
        }
    }

    /// <summary>
    /// Public so a test can run the pass deterministically. The pass reads every tenant in the table
    /// so it can name the ones it cannot reach, but it only writes through the ambient tenant: the
    /// bridge registers and retires participants through <see cref="Abstractions.Ports.ITinaChatService"/>,
    /// whose only identity is the verified request scope.
    /// </summary>
    public async Task<ReconcileResult> RunPassAsync(CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await companions.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tenants = await db.Companions.AsNoTracking()
            .Select(x => x.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tenants.Count > 1)
        {
            // Not silent: a shared database with more tenants than the ambient scope needs a
            // bridge that can carry an explicit tenant, which the chat service does not expose.
            logger.LogWarning(
                "Companion reconciliation covers tenant {TenantId}; {Other} other tenant(s) are not reachable from this scope.",
                scope.TenantId, tenants.Count - 1);
        }

        var active = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.Status == "active")
            .Select(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var archived = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.Status == "archived")
            .Select(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ensured = 0;
        var retired = 0;
        foreach (var companionId in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await bridge.EnsureParticipantAsync(companionId, cancellationToken).ConfigureAwait(false);
                ensured++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Companion {CompanionId} could not be bridged to TinaChat.", companionId);
            }
        }

        foreach (var companionId in archived)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                retired += await bridge.ArchiveParticipantsAsync(companionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Archived companion {CompanionId} still has a live TinaChat participant.", companionId);
            }
        }

        if (ensured > 0 || retired > 0)
            logger.LogInformation("Companion reconciliation ensured {Ensured} participant(s) and retired {Retired}.", ensured, retired);
        return new ReconcileResult(ensured, retired);
    }
}

public sealed record ReconcileResult(int Ensured, int Retired);
