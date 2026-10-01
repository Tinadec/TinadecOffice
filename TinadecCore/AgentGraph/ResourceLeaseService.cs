using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentGraph;

/// <summary>
/// The durable resource ledger. Written by the run engine when a task is dispatched with a declared
/// write scope and before a tool touches a named file or worktree; read by governance roles that
/// arbitrate collisions.
///
/// Why the ledger is the source of truth rather than each role's own memory: "who is using what" is a
/// fact about the whole session, and a role that reconstructed it from events would race with the
/// writes it is meant to police.
///
/// Built for a cluster of runs: every read is an index lookup on the normalized key (the candidate
/// and its ancestors, or a prefix for its descendants), never a scan of all active leases; acquisition
/// is serialized by the database, not a process lock, so several hosts can share one ledger; and a
/// lease whose run is gone is reclaimed by whoever next collides with it.
/// </summary>
internal sealed class ResourceLeaseService(
    IDbContextFactory<AgentGraphDbContext> factory,
    ITenantContextAccessor tenant,
    IRunLivenessProbe? liveness = null,
    ILogger<ResourceLeaseService>? logger = null) : IResourceLeaseService
{
    private const int MaxAttempts = 4;

    public async Task<ResourceLeaseDecision> AcquireAsync(ResourceAcquireRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var claim = request.Claim;
        if (string.IsNullOrWhiteSpace(claim.ResourceKey))
            throw new ArgumentException("A resource claim must name its resource.", nameof(request));

        var scope = tenant.Current;
        var canonical = ResourceLeasePolicy.Canonical(claim.ResourceKey);
        var normalized = ResourceLeasePolicy.Normalize(claim.ResourceKey);

        // Two phases per attempt. Reclaiming dead holders happens OUTSIDE the acquire transaction
        // because it asks the run lifecycle (another module's store); the acquire itself is one
        // serializable read-decide-write, so two runs claiming overlapping resources at once cannot
        // both see "free".
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                var overlapping = await OverlappingAsync(db, scope, claim.Kind, normalized, cancellationToken).ConfigureAwait(false);

                // Re-entering a claim this owner already holds is not a conflict: a retried tool call
                // must not fail against its own lease, and a task that re-runs after a resume holds the
                // same paths.
                var mine = overlapping.FirstOrDefault(lease => lease.RunId == request.RunId
                    && lease.TaskId == request.TaskId
                    && lease.KeyNorm == normalized
                    && string.Equals(lease.Kind, claim.Kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(lease.Purpose, request.Purpose, StringComparison.Ordinal));
                if (mine is not null) return new ResourceLeaseDecision(ToInfo(mine), []);

                var conflicts = ResourceLeasePolicy.Conflicts(claim, request.RunId, request.TaskId, overlapping.Select(ToInfo).ToArray());
                if (conflicts.Count > 0)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    if (attempt < MaxAttempts && await ReclaimDeadAsync(conflicts, cancellationToken).ConfigureAwait(false) > 0)
                        continue;
                    return new ResourceLeaseDecision(null, conflicts);
                }

                var record = new ResourceLeaseRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    WorkspaceId = scope.WorkspaceId,
                    SessionId = request.SessionId,
                    RunId = request.RunId,
                    TaskId = request.TaskId,
                    AgentInstanceId = request.AgentInstanceId,
                    Kind = claim.Kind,
                    ResourceKey = canonical,
                    KeyNorm = normalized,
                    Exclusive = claim.Exclusive,
                    Status = ResourceLeaseStatuses.Active,
                    Purpose = request.Purpose,
                    Reason = Bound(request.Reason, 512),
                    Revision = 1,
                    AcquiredAt = DateTimeOffset.UtcNow
                };
                db.ResourceLeases.Add(record);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ResourceLeaseDecision(ToInfo(record), []);
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is DbUpdateException or DbException)
            {
                // Lost a serialization race with another acquirer (SQLite busy, PostgreSQL 40001).
                // The retry rereads, so the loser reports the winner as its conflict, not a raw error.
                logger?.LogDebug(ex, "Resource lease acquire for {Kind} {Key} retried after contention.", claim.Kind, canonical);
                await Task.Delay(15 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<ResourceLeaseInfo>> ProbeAsync(ResourceClaim claim, Guid? runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (string.IsNullOrWhiteSpace(claim.ResourceKey)) return [];
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var overlapping = await OverlappingAsync(db, scope, claim.Kind, ResourceLeasePolicy.Normalize(claim.ResourceKey), cancellationToken).ConfigureAwait(false);
        var conflicts = ResourceLeasePolicy.Conflicts(claim, runId, overlapping.Select(ToInfo).ToArray());
        if (conflicts.Count == 0) return conflicts;
        // A probe reports only live holders; dead ones are reclaimed on the way.
        if (await ReclaimDeadAsync(conflicts, cancellationToken).ConfigureAwait(false) == 0) return conflicts;
        return await ProbeAsync(claim, runId, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> ReleaseRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        ReleaseWhereAsync(lease => lease.RunId == runId, cancellationToken);

    public Task<int> ReleaseTaskAsync(Guid runId, Guid taskId, CancellationToken cancellationToken = default) =>
        ReleaseWhereAsync(lease => lease.RunId == runId && lease.TaskId == taskId, cancellationToken);

    public Task<int> ReleaseAsync(Guid leaseId, CancellationToken cancellationToken = default) =>
        ReleaseWhereAsync(lease => lease.Id == leaseId, cancellationToken);

    public async Task<IReadOnlyList<ResourceLeaseInfo>> ListActiveAsync(Guid? sessionId = null, CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.ResourceLeases.AsNoTracking()
            .Where(lease => lease.TenantId == scope.TenantId && lease.WorkspaceId == scope.WorkspaceId
                && lease.Status == ResourceLeaseStatuses.Active);
        if (sessionId is { } id) query = query.Where(lease => lease.SessionId == id);
        // SQLite cannot order by DateTimeOffset in SQL; active leases per session are few.
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(lease => lease.AcquiredAt).Select(ToInfo).ToArray();
    }

    public async Task<IReadOnlyList<ResourceLeaseInfo>> ListTaskAsync(Guid runId, Guid taskId, CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ResourceLeases.AsNoTracking()
            .Where(lease => lease.TenantId == scope.TenantId && lease.WorkspaceId == scope.WorkspaceId
                && lease.RunId == runId && lease.TaskId == taskId && lease.Status == ResourceLeaseStatuses.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToInfo).ToArray();
    }

    /// <summary>
    /// Every active lease that could overlap a claim on <paramref name="normalizedKey"/>. For the
    /// filesystem kinds that is the key itself, any ancestor directory, and anything beneath it — all
    /// three are index lookups on <see cref="ResourceLeaseRecord.KeyNorm"/>. Opaque kinds match only
    /// their own kind and key.
    /// </summary>
    private static async Task<List<ResourceLeaseRecord>> OverlappingAsync(
        AgentGraphDbContext db, TenantContext scope, string kind, string normalizedKey, CancellationToken cancellationToken)
    {
        var active = db.ResourceLeases.AsNoTracking()
            .Where(lease => lease.TenantId == scope.TenantId && lease.WorkspaceId == scope.WorkspaceId
                && lease.Status == ResourceLeaseStatuses.Active);
        if (!ResourceLeasePolicy.IsFileSystem(kind))
        {
            return await active.Where(lease => lease.Kind == kind && lease.KeyNorm == normalizedKey)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        var covering = ResourceLeasePolicy.SelfAndAncestors(normalizedKey);
        var descendantPrefix = normalizedKey + "/";
        var rows = await active
            .Where(lease => (lease.Kind == ResourceLeaseKinds.Path || lease.Kind == ResourceLeaseKinds.Worktree)
                && (covering.Contains(lease.KeyNorm) || lease.KeyNorm.StartsWith(descendantPrefix)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// Releases the conflicting leases whose run is no longer live, and returns how many it released.
    /// Asked only on conflict, so the common uncontended acquire never touches the run lifecycle.
    /// Leases held by the host (no run) are never reclaimed here.
    /// </summary>
    private async Task<int> ReclaimDeadAsync(IReadOnlyList<ResourceLeaseInfo> conflicts, CancellationToken cancellationToken)
    {
        if (liveness is null) return 0;
        var dead = new List<Guid>();
        foreach (var runId in conflicts.Where(lease => lease.RunId is not null).Select(lease => lease.RunId!.Value).Distinct())
        {
            bool live;
            try
            {
                live = await liveness.IsLiveAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unable to tell is not dead: keep the holder's claim rather than risk a double writer.
                logger?.LogWarning(ex, "Could not determine whether run {RunId} is live; its leases stay.", runId);
                live = true;
            }
            if (!live) dead.Add(runId);
        }
        var released = 0;
        foreach (var runId in dead)
        {
            released += await ReleaseRunAsync(runId, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Reclaimed the leases of run {RunId}, which is no longer live.", runId);
        }
        return released;
    }

    private async Task<int> ReleaseWhereAsync(System.Linq.Expressions.Expression<Func<ResourceLeaseRecord, bool>> predicate, CancellationToken cancellationToken)
    {
        var scope = tenant.Current;
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ResourceLeases
            .Where(lease => lease.TenantId == scope.TenantId && lease.WorkspaceId == scope.WorkspaceId
                && lease.Status == ResourceLeaseStatuses.Active)
            .Where(predicate)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(lease => lease.Status, ResourceLeaseStatuses.Released)
                .SetProperty(lease => lease.ReleasedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];

    private static ResourceLeaseInfo ToInfo(ResourceLeaseRecord record) => new(
        record.Id, record.Kind, record.ResourceKey, record.Exclusive,
        record.SessionId, record.RunId, record.TaskId, record.AgentInstanceId, record.Status,
        string.IsNullOrWhiteSpace(record.Purpose) ? ResourceLeasePurposes.Tool : record.Purpose,
        record.Reason);
}
