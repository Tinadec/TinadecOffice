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
            // The deterministic handle belongs to this companion. The row may be archived (archiving
            // keeps the handle reserved), in which case the companion was restored and its
            // participant has to come back rather than stay silently unbridged.
            await using var db = await participants.CreateDbContextAsync(ct);
            var row = await db.Participants.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                    && x.Handle == handle, ct);
            if (row is null || row.CompanionId != companionId) throw;
            if (row.Status != "active")
            {
                return await chat.UpdateParticipantAsync(row.Id, new TinaChatUpdateParticipantRequest(
                    row.Revision, companion.Name, row.JobTitle, row.Description,
                    ReceiveHumanMessages: true, CanInterpretIntent: true, row.Discoverable, "active"), ct);
            }
            return await chat.GetParticipantAsync(row.Id, ct);
        }
    }

    /// <summary>
    /// Retires the participants of an archived companion. It goes through the chat service rather
    /// than the table so the change keeps its audit row and revision, and it is idempotent because
    /// only active rows are selected.
    /// </summary>
    public async Task<int> ArchiveParticipantsAsync(Guid companionId, CancellationToken ct = default)
    {
        var scope = tenant.Current;
        await using var participantDb = await participants.CreateDbContextAsync(ct);
        var rows = await participantDb.Participants.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId
                && x.CompanionId == companionId && x.Status == "active")
            .ToListAsync(ct);
        var archived = 0;
        foreach (var row in rows)
        {
            await chat.UpdateParticipantAsync(row.Id, new TinaChatUpdateParticipantRequest(
                row.Revision, row.DisplayName, row.JobTitle, row.Description,
                row.ReceiveHumanMessages, row.CanInterpretIntent, row.Discoverable, "archived"), ct);
            archived++;
        }
        return archived;
    }
}