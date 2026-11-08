using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Companion;

/// <summary>
/// Sequence allocation for the tenant-wide activity feed. Activities are written from request
/// handlers, the autonomy tick and the run loop, so the unique (tenant, seq) index is the real
/// guard: a writer that loses the race re-numbers its rows and retries instead of failing the
/// action it was recording. The feed only moves forward, which is what makes one cursor enough.
/// </summary>
internal static class CompanionActivities
{
    internal static async Task<long> NextSeqAsync(CompanionDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        (await db.Activities.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => (long?)x.Seq)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0) + 1;

    /// <summary>Numbers every activity this context is about to insert, in insertion order.</summary>
    internal static async Task AssignSeqAsync(CompanionDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var pending = db.ChangeTracker.Entries<CompanionActivityRecord>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.Seq == 0)
            .Select(entry => entry.Entity)
            .ToList();
        if (pending.Count == 0) return;
        var next = await NextSeqAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        foreach (var row in pending) row.Seq = next++;
    }

    /// <summary>
    /// Saves the context after numbering its activities. <paramref name="save"/> is the caller's own
    /// write path so its retry rules (message sequence reallocation) still run; a collision on the
    /// activity index only clears the numbers and asks for a fresh allocation.
    /// </summary>
    internal static async Task SaveAsync(
        CompanionDbContext db, Guid tenantId, Func<CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await AssignSeqAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
            try
            {
                await save(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException exception) when (attempt < 2 && exception is not DbUpdateConcurrencyException)
            {
                foreach (var entry in db.ChangeTracker.Entries<CompanionActivityRecord>()
                    .Where(entry => entry.State == EntityState.Added))
                    entry.Entity.Seq = 0;
            }
        }
    }

    internal static Task SaveAsync(CompanionDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        SaveAsync(db, tenantId, token => db.SaveChangesAsync(token), cancellationToken);
}
