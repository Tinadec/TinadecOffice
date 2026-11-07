using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Companion;
using TinadecCore.Contracts.Dtos;
using TinadecCore.TinaChat;

namespace TinadecCore.Runtime;

/// <summary>
/// Bridges the Companion identity domain into TinaChat: a companion becomes a named companion-kind
/// participant so the three roles (buddy, autonomous partner, session partner) communicate over the
/// durable inbox/wake/handoff machinery instead of a second channel. Registration is idempotent per
/// (workspace, companion); the companion domain remains the authority for its own profile. The bridge
/// lives in the composition root because business modules may not reference each other.
/// </summary>
public sealed class CompanionChatBridge(
    ITinaChatService chat,
    ITenantContextAccessor tenant,
    IDbContextFactory<CompanionDbContext> companions,
    IDbContextFactory<TinaChatDbContext> participants)
{
    public async Task<TinaChatParticipantDto> EnsureParticipantAsync(Guid companionId, CancellationToken ct = default)
    {
        var scope = tenant.Current;
        await using var companionDb = await companions.CreateDbContextAsync(ct);
        var companion = await companionDb.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == scope.TenantId && x.Status != "archived", ct)
            ?? throw new TinaChatException(404, "companion_not_found", "The companion does not exist in this tenant.");

        await using var participantDb = await participants.CreateDbContextAsync(ct);
        var existing = await participantDb.Participants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.CompanionId == companionId && x.Status == "active", ct);
        if (existing is not null) return await chat.GetParticipantAsync(existing.Id, ct);

        var handle = "companion-" + companionId.ToString("N")[..12];
        var request = new TinaChatRegisterParticipantRequest(
            handle, companion.Name, Kind: "companion", CompanionId: companionId,
            ReceiveHumanMessages: true, CanInterpretIntent: true);
        try
        {
            return await chat.RegisterAsync(request, ct);
        }
        catch (TinaChatException ex) when (ex.Code == "participant_handle_exists")
        {
            // A concurrent registration won the race; the deterministic handle means it is ours.
            await using var db = await participants.CreateDbContextAsync(ct);
            var row = await db.Participants.AsNoTracking()
                .SingleAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                    && x.CompanionId == companionId && x.Status == "active", ct);
            return await chat.GetParticipantAsync(row.Id, ct);
        }
    }
}