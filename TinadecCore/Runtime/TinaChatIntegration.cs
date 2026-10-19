using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Memory;
using TinadecCore.Tenancy;

namespace TinadecCore.Runtime;

internal sealed class TinaChatIdentityBoundary(
    IDbContextFactory<TenancyDbContext> tenants,
    IDbContextFactory<AgentConfigurationDbContext> agents,
    GlobalAgentConfiguration globalConfiguration) : ITinaChatIdentityBoundary
{
    public Task<bool> IsWorkspaceMemberAsync(Guid tenantId, Guid workspaceId, Guid principalId, CancellationToken ct) =>
        MembershipAsync(tenantId, workspaceId, principalId, administrator: false, ct);

    public Task<bool> IsWorkspaceAdministratorAsync(Guid tenantId, Guid workspaceId, Guid principalId, CancellationToken ct) =>
        MembershipAsync(tenantId, workspaceId, principalId, administrator: true, ct);

    private async Task<bool> MembershipAsync(Guid tenantId, Guid workspaceId, Guid principalId, bool administrator, CancellationToken ct)
    {
        await using var db = await tenants.CreateDbContextAsync(ct);
        return await (from membership in db.WorkspaceMemberships
                      join workspace in db.Workspaces on membership.WorkspaceId equals workspace.Id
                      join principal in db.Principals on membership.PrincipalId equals principal.Id
                      join tenantMember in db.TenantMemberships on new { workspace.TenantId, membership.PrincipalId } equals new { tenantMember.TenantId, tenantMember.PrincipalId }
                      join organization in db.Tenants on workspace.TenantId equals organization.Id
                      where workspace.Id == workspaceId && workspace.TenantId == tenantId && membership.PrincipalId == principalId
                          && membership.Status == "active" && workspace.Status == "active" && workspace.DeletedAt == null
                          && principal.Status == "active" && tenantMember.Status == "active" && organization.Status == "active" && organization.DeletedAt == null
                          && (!administrator || membership.Role == "owner" || membership.Role == "admin")
                      select membership).AnyAsync(ct);
    }

    public async Task<bool> IsAgentDefinitionAvailableAsync(Guid tenantId, Guid workspaceId, Guid definitionId, CancellationToken ct)
    {
        if (!ReferenceEquals(agents, globalConfiguration.Factory))
        {
            await using var global = await globalConfiguration.Factory.CreateDbContextAsync(ct);
            if (await global.AgentDefinitions.AnyAsync(x => x.Id == definitionId && x.TenantId == tenantId && x.WorkspaceId == workspaceId
                && x.Enabled && x.ArchivedAt == null, ct)) return true;
        }
        await using var db = await agents.CreateDbContextAsync(ct);
        return await db.AgentDefinitions.AnyAsync(x => x.Id == definitionId && x.TenantId == tenantId && x.WorkspaceId == workspaceId
            && x.Enabled && x.ArchivedAt == null, ct);
    }
}

/// <summary>Uses ordinary Core admission/approval/execution, with a stable isolated session for each accepted handoff.</summary>
internal sealed class TinaChatRunService(
    ITinaChatService chat,
    ITenantContextAccessor tenant,
    IDbContextFactory<AgentConfigurationDbContext> configuration,
    GlobalAgentConfiguration globalConfiguration,
    ProjectSessionStore sessions,
    IFullDuplexRunCoordinator coordinator) : ITinaChatRunService
{
    public async Task<TinaChatExecutionDto> ExecuteAsync(Guid conversationId, Guid intentId, TinaChatExecuteIntentRequest request, CancellationToken ct = default)
    {
        var scope = tenant.Current;
        ConversationIdentityResolver.ConversationIdentity conversationIdentity;
        var modeFactory = await AgentConfigurationLookup.SelectModeFactoryAsync(
            configuration, globalConfiguration, scope.TenantId, scope.WorkspaceId, request.ModeVersionId, ct);
        await using (var db = await modeFactory.CreateDbContextAsync(ct))
        {
            var mode = await db.ModeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ModeVersionId
                && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status == "published", ct)
                ?? throw new TinaChatException(400, "invalid_mode_version", "Execution requires a published mode in the receiving workspace.");
            var ownerPack = await AgentConfigurationLookup.FindPackAsync(configuration, globalConfiguration,
                scope.TenantId, scope.WorkspaceId, request.ModeVersionId, ct);
            if (ownerPack is { } owner && owner.Status != "active")
                throw new TinaChatException(409, "pack_disabled", $"Agent pack '{owner.PackId}' is disabled. Enable it before admitting a handoff.");
            var definitions = await db.AgentDefinitions.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
                .Select(x => new ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson)).ToListAsync(ct);
            conversationIdentity = ConversationIdentityResolver.Resolve(mode.SnapshotJson, definitions)
                ?? throw new TinaChatException(409, "conversation_identity_missing", "The selected mode does not define a conversation-capable agent.");
        }
        var reservation = await chat.ReserveExecutionAsync(conversationId, intentId, request, ct);
        var binding = reservation.Execution;
        if (binding.RunId.HasValue) return binding;
        await sessions.CreateSessionAsync(reservation.ProjectId, "TinaChat handoff " + intentId.ToString("N")[..8],
            reservation.ModeVersionId, conversationNodeKey: conversationIdentity.NodeKey,
            conversationTemplateSlug: conversationIdentity.TemplateSlug, cancellationToken: ct, stableSessionId: binding.SessionId);
        var submission = await coordinator.SubmitAsync(new FullDuplexInvocation(binding.SessionId, reservation.Content,
            reservation.ClientMessageId, "ask", null, null, ModeVersionId: reservation.ModeVersionId), ct);
        return await chat.RecordExecutionAsync(binding.Id, submission.RunId, ct);
    }
}
