using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.TinaChat;

public sealed partial class TinaChatService
{
    public Task<TinaChatMessageDto> SendAsync(Guid conversationId, TinaChatSendMessageRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        var (actor, conversation, _) = await AccessAsync(db, scope, conversationId, request.ActorId, ct);
        Required(request.Content, "content", 65536);
        var key = "message:" + Hash(Required(request.ClientMessageId, "client_message_id", 128));
        var sources = Ids(request.SourceMessageIds ?? [], "source_message_ids")
            .Concat(request.ReplyToMessageId is { } reply ? new[] { reply } : []).Distinct().Order().ToArray();
        var audience = request.AudienceParticipantIds is null ? null : Ids(request.AudienceParticipantIds, "audience_participant_ids");
        if (request.Sensitivity is not ("normal" or "confidential")) throw Invalid("Sensitivity must be normal or confidential.");
        var hash = Hash(new { request.Content, sources, audience, request.ReplyToMessageId, request.Sensitivity, request.AllowDerivedSharing });
        var existing = await db.Messages.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.SenderId == actor.Id && x.ClientMessageId == key, ct);
        if (existing is not null)
        {
            SameHash(existing.RequestHash, hash);
            if (!await CanReadAsync(db, conversation, actor, existing, ct)) throw Missing();
            return await ToMessageAsync(db, conversation, actor, existing, ct);
        }
        var sourceMessages = await SourcesAsync(db, conversation, actor, sources, ct);
        var recipients = await RecipientsAsync(db, scope, conversation, actor, audience, sourceMessages, derived: false, ct);
        var sensitivity = sourceMessages.Any(x => x.Sensitivity == "confidential") ? "confidential" : request.Sensitivity;
        if (sensitivity == "confidential" && recipients.Any(x => x.WorkspaceId != actor.WorkspaceId))
            throw Forbidden("Confidential content cannot cross workspace boundaries.");
        var row = await AppendMessageAsync(db, conversation, actor, request.Content, key, hash, "message", sensitivity,
            request.AllowDerivedSharing, request.ReplyToMessageId, sources, recipients, ct);
        return await ToMessageAsync(db, conversation, actor, row, ct, sources);
    }, ct);

    public async Task<TinaChatMessagePage> ReadMessagesAsync(Guid conversationId, Guid actorId, long afterSequence = 0, int limit = 50, CancellationToken ct = default)
    {
        ValidatePage(afterSequence, limit);
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var (actor, conversation, member) = await AccessAsync(db, scope, conversationId, actorId, ct);
        var rows = await (from message in db.Messages
                          join audience in db.Audiences on message.Id equals audience.MessageId
                          where message.ConversationId == conversationId && message.Sequence > afterSequence
                              && message.Sequence > member.JoinedAfterSequence
                              && audience.ParticipantId == actorId && audience.CanReadOriginal
                          orderby message.Sequence
                          select message).Take(limit * 4).ToArrayAsync(ct);
        var result = new List<TinaChatMessageDto>();
        var cursor = afterSequence;
        foreach (var row in rows)
        {
            cursor = row.Sequence;
            if (await CanReadAsync(db, conversation, actor, row, ct)) result.Add(await ToMessageAsync(db, conversation, actor, row, ct));
            if (result.Count == limit) break;
        }
        return new TinaChatMessagePage(result.ToArray(), cursor);
    }

    public async Task<TinaChatInboxPage> ReadInboxAsync(Guid actorId, long afterSequence = 0, int limit = 50, CancellationToken ct = default)
    {
        ValidatePage(afterSequence, limit);
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await OwnedAsync(db, scope, actorId, ct);
        var rows = await (from audience in db.Audiences
                          join message in db.Messages on audience.MessageId equals message.Id
                          where audience.ParticipantId == actorId && audience.CanReadOriginal
                              && audience.Id > afterSequence && message.TenantId == scope.TenantId
                          orderby audience.Id
                          select new { Audience = audience, Message = message }).Take(limit * 4).ToArrayAsync(ct);
        var result = new List<TinaChatInboxItem>();
        var cursor = afterSequence;
        foreach (var row in rows)
        {
            cursor = row.Audience.Id;
            var conversation = await db.Conversations.SingleAsync(x => x.Id == row.Message.ConversationId && x.TenantId == scope.TenantId, ct);
            if (await CanReadAsync(db, conversation, actor, row.Message, ct))
                result.Add(new TinaChatInboxItem(await ToMessageAsync(db, conversation, actor, row.Message, ct), row.Audience.Acknowledged));
            if (result.Count == limit) break;
        }
        return new TinaChatInboxPage(result.ToArray(), cursor);
    }

    public async Task AcknowledgeAsync(Guid actorId, Guid messageId, CancellationToken ct = default) => await WriteAsync(async (db, scope) =>
    {
        var actor = await OwnedAsync(db, scope, actorId, ct);
        var message = await db.Messages.SingleOrDefaultAsync(x => x.Id == messageId && x.TenantId == scope.TenantId, ct) ?? throw Missing();
        var conversation = await db.Conversations.SingleAsync(x => x.Id == message.ConversationId, ct);
        if (!await CanReadAsync(db, conversation, actor, message, ct)) throw Missing();
        var audience = await db.Audiences.SingleAsync(x => x.MessageId == messageId && x.ParticipantId == actorId, ct);
        audience.Acknowledged = true;
        return true;
    }, ct);

    private async Task<ChatMessage[]> SourcesAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant actor, Guid[] sourceIds, CancellationToken ct)
    {
        var result = new List<ChatMessage>();
        foreach (var id in sourceIds)
        {
            var source = await db.Messages.SingleOrDefaultAsync(x => x.Id == id && x.ConversationId == conversation.Id && x.TenantId == conversation.TenantId, ct) ?? throw Missing();
            if (!await CanReadAsync(db, conversation, actor, source, ct)) throw Forbidden("A source message is not readable by the acting participant.");
            result.Add(source);
        }
        return result.ToArray();
    }

    private async Task<ChatParticipant[]> RecipientsAsync(TinaChatDbContext db, TenantContext scope, ChatConversation conversation,
        ChatParticipant actor, Guid[]? requestedIds, ChatMessage[] sources, bool derived, CancellationToken ct)
    {
        var ids = requestedIds?.Append(actor.Id).Distinct().ToArray()
            ?? await db.Members.Where(x => x.ConversationId == conversation.Id && x.Status == "active").Select(x => x.ParticipantId).ToArrayAsync(ct);
        if (ids.Length > MaxMembers) throw Invalid("Too many audience members.");
        var recipients = new List<ChatParticipant>();
        foreach (var id in ids)
        {
            var recipient = await ParticipantAsync(db, scope.TenantId, id, ct);
            var member = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ParticipantId == id && x.Status == "active", ct);
            var permitted = member is not null && recipient.Status == "active"
                && await identity.IsWorkspaceMemberAsync(scope.TenantId, recipient.WorkspaceId, recipient.OwnerPrincipalId, ct)
                && await CanCommunicateAsync(db, conversation, actor.WorkspaceId, recipient.WorkspaceId, ct);
            if (permitted)
                foreach (var source in sources)
                    if (!await CanReadAsync(db, conversation, recipient, source, ct, derived)) { permitted = false; break; }
            if (permitted) recipients.Add(recipient);
            else if (requestedIds is not null) throw Forbidden("The selected audience is not permitted to receive this content or its sources.");
        }
        if (!recipients.Any(x => x.Id == actor.Id)) throw Forbidden("The sender must remain an authorized audience member.");
        return recipients.ToArray();
    }

    private async Task<ChatMessage> AppendMessageAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant actor,
        string body, string key, string requestHash, string kind, string sensitivity, bool allowDerivedSharing,
        Guid? replyTo, Guid[] sources, ChatParticipant[] recipients, CancellationToken ct)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var stored = await content.PutAsync(new ContentWriteRequest(conversation.TenantId, conversation.WorkspaceId, "tina-chat-message", "text/plain; charset=utf-8", stream), ct);
        conversation.LastSequence++;
        conversation.Revision++;
        var row = new ChatMessage
        {
            Id = Guid.NewGuid(), TenantId = conversation.TenantId, ConversationId = conversation.Id,
            SenderId = actor.Id, SenderKind = actor.Kind, Sequence = conversation.LastSequence,
            Kind = kind, ClientMessageId = key, RequestHash = requestHash,
            ContentReference = stored.Value, ContentHash = stored.Sha256, ContentLength = stored.Length,
            Sensitivity = sensitivity, AllowDerivedSharing = allowDerivedSharing, ReplyToMessageId = replyTo,
            SourceMessageIdsJson = JsonSerializer.Serialize(sources, Json), CreatedAt = DateTimeOffset.UtcNow
        };
        db.Messages.Add(row);
        foreach (var recipient in recipients)
        {
            var original = actor.Kind != "human" || recipient.ReceiveHumanMessages || recipient.Id == actor.Id;
            db.Audiences.Add(new ChatAudience
            {
                MessageId = row.Id, ParticipantId = recipient.Id, CanReadOriginal = original,
                CanReceiveDerived = allowDerivedSharing, Acknowledged = recipient.Id == actor.Id
            });
        }
        return row;
    }

    private async Task<bool> CanReadAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant actor, ChatMessage message,
        CancellationToken ct, bool allowDerived = false, Dictionary<(Guid, bool), bool>? evaluated = null)
    {
        evaluated ??= [];
        var key = (message.Id, allowDerived);
        if (evaluated.TryGetValue(key, out var known)) return known;
        // Fail closed on a damaged/cyclic or excessively deep provenance graph.
        if (evaluated.Count >= 256) return false;
        evaluated[key] = false;
        if (actor.Status != "active" || message.TenantId != actor.TenantId || message.ConversationId != conversation.Id) return false;
        var membership = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.ParticipantId == actor.Id, ct);
        if (membership?.Status != "active" || message.Sequence <= membership.JoinedAfterSequence) return false;
        if (!await identity.IsWorkspaceMemberAsync(actor.TenantId, actor.WorkspaceId, actor.OwnerPrincipalId, ct)) return false;
        var audience = await db.Audiences.SingleOrDefaultAsync(x => x.MessageId == message.Id && x.ParticipantId == actor.Id, ct);
        if (audience is null) return false;
        var original = audience.CanReadOriginal && (message.SenderKind != "human" || actor.ReceiveHumanMessages || message.SenderId == actor.Id);
        if (!original && !(allowDerived && message.AllowDerivedSharing && audience.CanReceiveDerived)) return false;
        var sender = await ParticipantAsync(db, actor.TenantId, message.SenderId, ct);
        if (!await CanCommunicateAsync(db, conversation, sender.WorkspaceId, actor.WorkspaceId, ct)) return false;
        if (message.Sensitivity == "confidential" && sender.WorkspaceId != actor.WorkspaceId) return false;
        foreach (var sourceId in JsonSerializer.Deserialize<Guid[]>(message.SourceMessageIdsJson, Json) ?? [])
        {
            var source = await db.Messages.SingleOrDefaultAsync(x => x.Id == sourceId && x.ConversationId == conversation.Id && x.TenantId == actor.TenantId, ct);
            if (source is null || !await CanReadAsync(db, conversation, actor, source, ct,
                message.Kind == "intent_brief" || allowDerived, evaluated)) return false;
        }
        evaluated[key] = true;
        return true;
    }

    private async Task<string> ReadBodyAsync(ChatMessage row, CancellationToken ct)
    {
        await using var stream = await content.OpenReadAsync(new ContentReference(row.ContentReference, row.ContentHash, row.ContentLength, "text/plain; charset=utf-8"), ct);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    private async Task<TinaChatMessageDto> ToMessageAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant actor,
        ChatMessage row, CancellationToken ct, Guid[]? alreadyAuthorizedSources = null)
    {
        var visibleSources = alreadyAuthorizedSources?.ToList() ?? [];
        if (alreadyAuthorizedSources is null)
            foreach (var sourceId in JsonSerializer.Deserialize<Guid[]>(row.SourceMessageIdsJson, Json) ?? [])
            {
                var source = await db.Messages.SingleOrDefaultAsync(x => x.Id == sourceId && x.ConversationId == conversation.Id, ct);
                if (source is not null && await CanReadAsync(db, conversation, actor, source, ct)) visibleSources.Add(sourceId);
            }
        return new TinaChatMessageDto(row.Id, row.ConversationId, row.SenderId, row.SenderKind, row.Sequence, row.Kind,
            await ReadBodyAsync(row, ct), row.Sensitivity, row.AllowDerivedSharing,
            row.ReplyToMessageId is { } reply && visibleSources.Contains(reply) ? reply : null,
            visibleSources.ToArray(), row.CreatedAt);
    }

    private static void ValidatePage(long cursor, int limit)
    {
        if (cursor < 0 || limit is < 1 or > 100) throw Invalid("Cursor must be nonnegative and limit must be between 1 and 100.");
    }
}
