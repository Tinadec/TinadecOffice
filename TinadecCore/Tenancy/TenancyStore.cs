using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TinadecCore.Persistence;

namespace TinadecCore.Tenancy;

public sealed class TenancyStore : IStorageMigrationParticipant
{
    private readonly IDbContextFactory<TenancyDbContext> _dbFactory;
    private readonly TenancyOptions _options;

    public TenancyStore(IDbContextFactory<TenancyDbContext> dbFactory, IOptions<TenancyOptions> options)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        if (!_options.EnableDevelopmentIdentity) return;

        var now = DateTimeOffset.UtcNow;
        if (!await db.Tenants.AnyAsync(x => x.Id == DevelopmentTenantContextAccessor.TenantId, cancellationToken).ConfigureAwait(false))
        {
            db.Tenants.Add(new TenantRecord { Id = DevelopmentTenantContextAccessor.TenantId, Slug = _options.DevelopmentTenantSlug, Name = "Local development", CreatedAt = now, UpdatedAt = now });
        }
        if (!await db.Principals.AnyAsync(x => x.Id == DevelopmentTenantContextAccessor.PrincipalId, cancellationToken).ConfigureAwait(false))
        {
            db.Principals.Add(new PrincipalRecord { Id = DevelopmentTenantContextAccessor.PrincipalId, Issuer = _options.DevelopmentIssuer, Subject = _options.DevelopmentSubject, DisplayName = "Local developer", CreatedAt = now, UpdatedAt = now });
        }
        if (!await db.Workspaces.AnyAsync(x => x.Id == DevelopmentTenantContextAccessor.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            db.Workspaces.Add(new WorkspaceRecord { Id = DevelopmentTenantContextAccessor.WorkspaceId, TenantId = DevelopmentTenantContextAccessor.TenantId, Slug = _options.DevelopmentWorkspaceSlug, Name = "Default workspace", CreatedAt = now, UpdatedAt = now });
        }
        if (!await db.TenantMemberships.AnyAsync(x => x.TenantId == DevelopmentTenantContextAccessor.TenantId && x.PrincipalId == DevelopmentTenantContextAccessor.PrincipalId, cancellationToken).ConfigureAwait(false))
        {
            db.TenantMemberships.Add(new TenantMembershipRecord { TenantId = DevelopmentTenantContextAccessor.TenantId, PrincipalId = DevelopmentTenantContextAccessor.PrincipalId, Role = "owner", CreatedAt = now, UpdatedAt = now });
        }
        if (!await db.WorkspaceMemberships.AnyAsync(x => x.WorkspaceId == DevelopmentTenantContextAccessor.WorkspaceId && x.PrincipalId == DevelopmentTenantContextAccessor.PrincipalId, cancellationToken).ConfigureAwait(false))
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembershipRecord { WorkspaceId = DevelopmentTenantContextAccessor.WorkspaceId, PrincipalId = DevelopmentTenantContextAccessor.PrincipalId, Role = "owner", CreatedAt = now, UpdatedAt = now });
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
