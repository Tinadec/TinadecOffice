using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA.LiveExplainer;

/// <summary>
/// Durable persistence store for workspace Live Digest settings (<c>off | auto | always</c>)
/// and per-run <see cref="LiveDigestSnapshotDto"/> projections so completed runs can be
/// replayed faithfully after completion or host restart.
/// </summary>
public sealed class LiveExplainerStore(
    IDbContextFactory<AgentControlDbContext> dbFactory,
    ITenantContextAccessor tenantAccessor)
{
    public async Task<(string Mode, long Revision, DateTimeOffset UpdatedAt)> GetSettingsAsync(
        Guid? tenantId = null,
        Guid? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var current = SafeCurrentTenant();
        var tId = tenantId ?? current.TenantId;
        var wId = workspaceId ?? current.WorkspaceId;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.LiveDigestSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tId && x.WorkspaceId == wId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return (LiveExplainerModes.Auto, 0L, DateTimeOffset.UtcNow);
        }

        return (LiveExplainerModes.NormalizeOrDefault(row.Mode), row.Revision, row.UpdatedAt);
    }

    public async Task<(string Mode, long Revision, DateTimeOffset UpdatedAt)> SaveModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (!LiveExplainerModes.TryNormalize(mode, out var normalized))
        {
            throw new ArgumentException("mode must be one of: off, auto, always.", nameof(mode));
        }

        var scope = SafeCurrentTenant();
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.LiveDigestSettings
            .SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new LiveDigestSettingsRecord
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                Mode = normalized,
                Revision = 1,
                UpdatedByPrincipalId = scope.PrincipalId,
                UpdatedAt = now
            };
            db.LiveDigestSettings.Add(row);
        }
        else
        {
            row.Mode = normalized;
            row.Revision++;
            row.UpdatedByPrincipalId = scope.PrincipalId;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (row.Mode, row.Revision, row.UpdatedAt);
    }

    public async Task<LiveDigestSnapshotDto?> GetSnapshotAsync(
        Guid runId,
        Guid? tenantId = null,
        Guid? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var current = SafeCurrentTenant();
        var tId = tenantId ?? current.TenantId;
        var wId = workspaceId ?? current.WorkspaceId;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.LiveDigestSnapshots.AsNoTracking().Where(x => x.RunId == runId);
        if (tId != Guid.Empty && wId != Guid.Empty)
        {
            query = query.Where(x => x.TenantId == tId && x.WorkspaceId == wId);
        }

        var row = await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null || string.IsNullOrWhiteSpace(row.SnapshotJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<LiveDigestSnapshotDto>(row.SnapshotJson, LiveDigestJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveSnapshotAsync(
        Guid tenantId,
        Guid workspaceId,
        LiveDigestSnapshotDto snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = SafeCurrentTenant();
        var effectiveTenant = tenantId == Guid.Empty ? current.TenantId : tenantId;
        var effectiveWorkspace = workspaceId == Guid.Empty ? current.WorkspaceId : workspaceId;

        var serialized = JsonSerializer.Serialize(snapshot, LiveDigestJson.Options);
        var now = snapshot.UpdatedAt == default ? DateTimeOffset.UtcNow : snapshot.UpdatedAt;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.LiveDigestSnapshots
            .SingleOrDefaultAsync(
                x => x.TenantId == effectiveTenant && x.WorkspaceId == effectiveWorkspace && x.RunId == snapshot.RunId,
                cancellationToken)
            .ConfigureAwait(false);

        var headline = Truncate(snapshot.Headline, 1024);
        var phase = Truncate(LiveDigestPhases.Normalize(snapshot.CurrentPhase), 32);
        var status = Truncate(string.IsNullOrWhiteSpace(snapshot.Status) ? "running" : snapshot.Status, 32);
        var mode = Truncate(LiveExplainerModes.NormalizeOrDefault(snapshot.Mode), 16);

        if (row is null)
        {
            row = new LiveDigestSnapshotRecord
            {
                Id = Guid.NewGuid(),
                TenantId = effectiveTenant,
                WorkspaceId = effectiveWorkspace,
                SessionId = snapshot.SessionId,
                RunId = snapshot.RunId,
                Status = status,
                Mode = mode,
                Seq = snapshot.Seq,
                Headline = headline,
                CurrentPhase = phase,
                SnapshotJson = serialized,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.LiveDigestSnapshots.Add(row);
        }
        else if (snapshot.Seq >= row.Seq || IsTerminal(status))
        {
            row.SessionId = snapshot.SessionId != Guid.Empty ? snapshot.SessionId : row.SessionId;
            row.Status = status;
            row.Mode = mode;
            row.Seq = Math.Max(row.Seq, snapshot.Seq);
            row.Headline = headline;
            row.CurrentPhase = phase;
            row.SnapshotJson = serialized;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private TenantContext SafeCurrentTenant()
    {
        try
        {
            return tenantAccessor.Current;
        }
        catch
        {
            return new TenantContext(Guid.Empty, Guid.Empty, Guid.Empty, "system");
        }
    }

    private static bool IsTerminal(string status) =>
        status is "completed" or "failed" or "cancelled";

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? (value ?? string.Empty) : value[..maxLength];
}
