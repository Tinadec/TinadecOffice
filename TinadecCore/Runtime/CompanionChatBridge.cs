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
    /// Sends one message from <paramref name="fromCompanionId"/> to <paramref name="toCompanionId"/>
    /// over TinaChat: the durable channel the three roles already share.
    ///
    /// The send is made **as the sender's own participant**, not as the host: TinaChat's ownership
    /// rule says a participant may only act for the principal that owns it, and every companion
    /// participant is registered to the host principal, so this is a real identity rather than a
    /// borrowed one.
    ///
    /// The conversation is a direct one between the two companions and is ensured idempotently —
    /// a second message between the same pair reuses the existing thread instead of opening a new
    /// one per message. <paramref name="idempotencyKey"/> makes a retried turn land once.
    /// </summary>
    public async Task<TinaChatMessageDto> RelayAsync(
        Guid fromCompanionId,
        Guid toCompanionId,
        string content,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (fromCompanionId == toCompanionId)
            throw new TinaChatException(400, "companion_self_message", "A companion cannot relay a message to itself.");
        if (string.IsNullOrWhiteSpace(content))
            throw new TinaChatException(400, "companion_message_empty", "A relayed message needs content.");

        var sender = await EnsureParticipantAsync(fromCompanionId, ct);
        var recipient = await EnsureParticipantAsync(toCompanionId, ct);

        var conversation = await EnsureDirectConversationAsync(sender.Id, recipient.Id, ct);
        return await chat.SendAsync(conversation.Id, new TinaChatSendMessageRequest(
            sender.Id, content.Trim(), idempotencyKey.Trim()), ct);
    }

    /// <summary>
    /// The direct conversation between two participants, created once. TinaChat's create path is
    /// itself idempotent on the client request id, and listing the sender's conversations finds an
    /// existing thread here without a second write in the common case.
    /// </summary>
    private async Task<TinaChatConversationDto> EnsureDirectConversationAsync(
        Guid senderId, Guid recipientId, CancellationToken ct)
    {
        var ordered = new[] { senderId, recipientId }.OrderBy(id => id).ToArray();
        foreach (var conversation in await chat.ListConversationsAsync(senderId, ct))
        {
            if (conversation.Kind != "direct") continue;
            var members = await chat.ListMembersAsync(conversation.Id, senderId, ct);
            if (members.Length != 2) continue;
            if (!members.Select(member => member.ParticipantId).OrderBy(id => id).SequenceEqual(ordered)) continue;

            // TinaChat's membership model leaves a newly added participant "invited" until it
            // accepts for itself. Both companions here are system identities owned by the same
            // principal, and the accept has to happen BEFORE the first message: a member that
            // joins at sequence N can never read messages at or before N, so accepting after a
            // relay would hide that relay from the recipient forever.
            await AcceptIfInvitedAsync(conversation, members, recipientId, ct);
            return conversation;
        }

        var created = await chat.CreateConversationAsync(new TinaChatCreateConversationRequest(
            senderId, "companion relay", [recipientId], "direct", AllowCrossWorkspace: false,
            ClientRequestId: $"companion-relay-{ordered[0]:N}-{ordered[1]:N}"), ct);
        await AcceptIfInvitedAsync(created, await chat.ListMembersAsync(created.Id, senderId, ct), recipientId, ct);
        return created;
    }

    /// <summary>
    /// Accepts the recipient's own invitation. Only the invited participant may accept for itself;
    /// the bridge may act as that participant because TinaChat registered it under the host
    /// principal, which is the same principal this bridge already acts for everywhere else.
    /// </summary>
    private async Task AcceptIfInvitedAsync(
        TinaChatConversationDto conversation, TinaChatMemberDto[] members, Guid recipientId, CancellationToken ct)
    {
        var own = members.SingleOrDefault(member => member.ParticipantId == recipientId);
        if (own is null || own.Status != "invited") return;
        await chat.ChangeMemberAsync(conversation.Id, new TinaChatMemberRequest(
            recipientId, recipientId, "accept", conversation.Revision), ct);
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