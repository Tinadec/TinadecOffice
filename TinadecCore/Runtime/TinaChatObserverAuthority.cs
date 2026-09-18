using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tenancy;

namespace TinadecCore.Runtime;

internal sealed class TinaChatObserverAuthority(IDbContextFactory<TenancyDbContext> factory) : ITinaChatObserverAuthority
{
    public async Task<TinaChatObserverAccessDto?> ResolveObserverAccessAsync(Guid tenantId, Guid principalId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var membership = await (from member in db.TenantMemberships.AsNoTracking()
                                join organization in db.Tenants on member.TenantId equals organization.Id
                                join principal in db.Principals on member.PrincipalId equals principal.Id
                                where member.TenantId == tenantId && member.PrincipalId == principalId
                                    && member.Status == "active" && organization.Status == "active"
                                    && organization.DeletedAt == null && principal.Status == "active"
                                select member).SingleOrDefaultAsync(ct);
        if (membership is null) return null;
        var tenantAdmin = membership.Role is "owner" or "admin";
        var workspaces = await db.Workspaces.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DeletedAt == null && x.Status == "active"
                && (tenantAdmin || db.WorkspaceMemberships.Any(m => m.WorkspaceId == x.Id
                    && m.PrincipalId == principalId && m.Status == "active" && (m.Role == "owner" || m.Role == "admin"))))
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new TinaChatObserverWorkspaceDto(x.Id, x.Name)).ToArrayAsync(ct);
        return workspaces.Length == 0 ? null : new TinaChatObserverAccessDto(tenantAdmin ? "tenant" : "workspace", workspaces);
    }
}
