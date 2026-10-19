using Microsoft.EntityFrameworkCore;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.Runtime;

/// <summary>
/// The user scope's AgentConfiguration database is the application-level source for
/// agent packs. Project runtimes receive this instance from the host so pack modes,
/// agents and prompt pipelines remain global while sessions and runs stay project-local.
/// </summary>
public sealed class GlobalAgentConfiguration
{
    private readonly IDbContextFactory<AgentConfigurationDbContext> _factory;

    public GlobalAgentConfiguration(IDbContextFactory<AgentConfigurationDbContext> factory) => _factory = factory;

    public IDbContextFactory<AgentConfigurationDbContext> Factory => _factory;

    internal async Task<IReadOnlyList<(string PackId, string Status, string? ActiveVersion)>> ListPacksAsync(
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var installations = await db.AgentPackInstallations.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.WorkspaceId == workspaceId)
            .OrderBy(item => item.PackId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var versionIds = installations.Where(item => item.ActiveVersionId.HasValue)
            .Select(item => item.ActiveVersionId!.Value).Distinct().ToArray();
        var versions = await db.AgentPackVersions.AsNoTracking()
            .Where(item => versionIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
        return installations.Select(item =>
        {
            var active = item.ActiveVersionId is { } id && versions.TryGetValue(id, out var version) ? version.PackVersion : null;
            return (item.PackId, item.Status, active);
        }).ToArray();
    }
}

/// <summary>
/// Pack-managed configuration is global; legacy project-local modes remain readable so
/// existing sessions keep their frozen identity after a global upgrade.
/// </summary>
public static class AgentConfigurationLookup
{
    public static async Task<bool> HasPublishedModeAsync(
        IDbContextFactory<AgentConfigurationDbContext> localFactory,
        GlobalAgentConfiguration global,
        Guid tenantId,
        Guid workspaceId,
        Guid modeVersionId,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(localFactory, global.Factory))
        {
            await using var user = await global.Factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (await user.ModeVersions.AsNoTracking().AnyAsync(item => item.Id == modeVersionId
                && item.TenantId == tenantId && item.WorkspaceId == workspaceId
                && item.Status == "published", cancellationToken).ConfigureAwait(false))
                return true;
        }

        await using var local = await localFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await local.ModeVersions.AsNoTracking().AnyAsync(item => item.Id == modeVersionId
            && item.TenantId == tenantId && item.WorkspaceId == workspaceId
            && item.Status == "published", cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Guid?> GetDefaultModeVersionIdAsync(
        IDbContextFactory<AgentConfigurationDbContext> localFactory,
        GlobalAgentConfiguration global,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(localFactory, global.Factory))
        {
            await using var user = await global.Factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var userDefault = await ReadDefaultAsync(user, tenantId, workspaceId, cancellationToken).ConfigureAwait(false);
            if (userDefault is not null) return userDefault;
        }

        await using var local = await localFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDefaultAsync(local, tenantId, workspaceId, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IDbContextFactory<AgentConfigurationDbContext>> SelectModeFactoryAsync(
        IDbContextFactory<AgentConfigurationDbContext> localFactory,
        GlobalAgentConfiguration global,
        Guid tenantId,
        Guid workspaceId,
        Guid modeVersionId,
        CancellationToken cancellationToken)
    {
        if (ReferenceEquals(localFactory, global.Factory)) return localFactory;
        await using var user = await global.Factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await user.ModeVersions.AsNoTracking().AnyAsync(item => item.Id == modeVersionId
            && item.TenantId == tenantId && item.WorkspaceId == workspaceId)
            ? global.Factory
            : localFactory;
    }

    public static async Task<IDbContextFactory<AgentConfigurationDbContext>> SelectCatalogFactoryAsync(
        IDbContextFactory<AgentConfigurationDbContext> localFactory,
        GlobalAgentConfiguration global,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        if (ReferenceEquals(localFactory, global.Factory)) return localFactory;
        await using var user = await global.Factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var hasGlobalModes = await (from mode in user.AgentModes.AsNoTracking()
                                    join version in user.ModeVersions.AsNoTracking() on mode.Id equals version.AgentModeId
                                    where mode.TenantId == tenantId && mode.WorkspaceId == workspaceId
                                        && mode.ArchivedAt == null && version.Status == "published"
                                    select mode.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
        return hasGlobalModes ? global.Factory : localFactory;
    }

    public static async Task<(string PackId, string Status)?> FindPackAsync(
        IDbContextFactory<AgentConfigurationDbContext> localFactory,
        GlobalAgentConfiguration global,
        Guid tenantId,
        Guid workspaceId,
        Guid modeVersionId,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(localFactory, global.Factory))
        {
            await using var user = await global.Factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var found = await ReadPackAsync(user, tenantId, workspaceId, modeVersionId, cancellationToken).ConfigureAwait(false);
            if (found is not null) return found;
        }

        await using var local = await localFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPackAsync(local, tenantId, workspaceId, modeVersionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid?> ReadDefaultAsync(
        AgentConfigurationDbContext db,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken cancellationToken) =>
        await (from defaults in db.WorkspaceDefaults.AsNoTracking()
               join version in db.ModeVersions.AsNoTracking() on defaults.DefaultModeVersionId equals version.Id
               where defaults.TenantId == tenantId && defaults.WorkspaceId == workspaceId
                   && defaults.Status == "active" && defaults.ArchivedAt == null
                   && version.Status == "published"
               select (Guid?)version.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private static async Task<(string PackId, string Status)?> ReadPackAsync(
        AgentConfigurationDbContext db,
        Guid tenantId,
        Guid workspaceId,
        Guid modeVersionId,
        CancellationToken cancellationToken)
    {
        var row = await (from mode in db.AgentModes.AsNoTracking()
                         join version in db.ModeVersions.AsNoTracking() on mode.Id equals version.AgentModeId
                         join resource in db.AgentPackManagedResources.AsNoTracking() on mode.Id equals resource.LogicalEntityId
                         join installation in db.AgentPackInstallations.AsNoTracking() on resource.InstallationId equals installation.Id
                         where version.Id == modeVersionId && version.Status == "published"
                             && mode.TenantId == tenantId && mode.WorkspaceId == workspaceId
                         select new { installation.PackId, installation.Status })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : (row.PackId, row.Status);
    }
}
