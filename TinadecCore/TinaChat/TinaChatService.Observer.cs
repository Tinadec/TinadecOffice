using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.TinaChat;

public sealed partial class TinaChatService
{
    public async Task<TinaChatObserverAccessDto> GetObserverAccessAsync(CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        return await ObserverAccessAsync(scope, ct);
    }

    private async Task<TinaChatObserverAccessDto> ObserverAccessAsync(TenantContext scope, CancellationToken ct) =>
        await observerAuthority.ResolveObserverAccessAsync(scope.TenantId, scope.PrincipalId, ct)
        ?? throw new TinaChatException(403, "tina_chat_observer_forbidden", "Administrator permission is required to observe conversations.");

    public async Task<TinaChatObservedConversationPage> ObserveConversationsAsync(string? query = null, string? kind = null,
        Guid? workspaceId = null, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var access = await ObserverAccessAsync(scope, ct);
        ValidatePage(offset, limit);
        if (kind is not (null or "group" or "direct")) throw Invalid("Kind must be group or direct.");
        var term = Optional(query, "query", 256);
        var workspaceIds = access.Workspaces.Select(x => x.Id).ToArray();
        if (workspaceId.HasValue && !workspaceIds.Contains(workspaceId.Value)) throw Missing();
        await using var db = await factory.CreateDbContextAsync(ct);
        var conversations = db.Conversations.AsNoTracking().Where(x => x.TenantId == scope.TenantId
            && workspaceIds.Contains(x.WorkspaceId) && (!workspaceId.HasValue || x.WorkspaceId == workspaceId.Value));
        if (kind is not null) conversations = conversations.Where(x => x.Kind == kind);
        if (term is not null) conversations = conversations.Where(x => x.Title.Contains(term)
            || db.Members.Any(m => m.ConversationId == x.Id && db.Participants.Any(p => p.Id == m.ParticipantId
                && p.TenantId == scope.TenantId && (p.DisplayName.Contains(term) || p.Handle.Contains(term)))));
        var total = await conversations.CountAsync(ct);
        var rows = await conversations.OrderBy(x => x.Title).ThenBy(x => x.Id).Skip(offset).Take(limit).ToArrayAsync(ct);
        var result = new List<TinaChatObservedConversationDto>();
        foreach (var row in rows) result.Add(await ObservedConversationAsync(db, row, access, ct));
        return new(result.ToArray(), total, offset + rows.Length, offset + rows.Length < total);
    }

    public async Task<TinaChatObservedConversationDetail> ObserveConversationAsync(Guid id, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var access = await ObserverAccessAsync(scope, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conversation = await ObservableConversationAsync(db, scope, access, id, ct);
        var members = await (from m in db.Members.AsNoTracking()
                             join p in db.Participants.AsNoTracking() on m.ParticipantId equals p.Id
                             where m.ConversationId == id && p.TenantId == scope.TenantId
                             orderby p.DisplayName, p.Id
                             select new { Member = m, Participant = p }).ToArrayAsync(ct);
        var result = new TinaChatObservedConversationDetail(await ObservedConversationAsync(db, conversation, access, ct),
            members.Select(x => new TinaChatObservedMemberDto(ToDto(x.Participant), x.Member.Role, x.Member.Status, x.Member.JoinedAfterSequence)).ToArray());
        // Opening a conversation leaves an administrator audit entry, never a participant read receipt.
        Audit(db, scope, null, "observer.conversation_opened", id, id);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<TinaChatObservedMessagePage> ObserveMessagesAsync(Guid id, long? beforeSequence = null,
        long? afterSequence = null, int limit = 50, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        var access = await ObserverAccessAsync(scope, ct);
        ValidatePage(beforeSequence ?? afterSequence ?? 0, limit);
        if (beforeSequence.HasValue && afterSequence.HasValue) throw Invalid("Use before_sequence or after_sequence, not both.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await ObservableConversationAsync(db, scope, access, id, ct);
        var query = db.Messages.AsNoTracking().Where(x => x.TenantId == scope.TenantId && x.ConversationId == id);
        if (beforeSequence.HasValue) query = query.Where(x => x.Sequence < beforeSequence.Value);
        if (afterSequence.HasValue) query = query.Where(x => x.Sequence > afterSequence.Value);
        var rows = await (afterSequence.HasValue ? query.OrderBy(x => x.Sequence) : query.OrderByDescending(x => x.Sequence))
            .Take(limit + 1).ToArrayAsync(ct);
        var hasMore = rows.Length > limit;
        rows = rows.Take(limit).OrderBy(x => x.Sequence).ToArray();
        var ids = rows.Select(x => x.Id).ToArray();
        var audiences = await db.Audiences.AsNoTracking().Where(x => ids.Contains(x.MessageId)).ToArrayAsync(ct);
        var participantIds = rows.Select(x => x.SenderId).Concat(audiences.Select(x => x.ParticipantId)).Distinct().ToArray();
        var participants = await db.Participants.AsNoTracking().Where(x => x.TenantId == scope.TenantId && participantIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var intents = await db.Intents.AsNoTracking().Where(x => x.TenantId == scope.TenantId && ids.Contains(x.MessageId)).ToDictionaryAsync(x => x.MessageId, ct);
        var result = new List<TinaChatObservedMessageDto>();
        foreach (var row in rows)
        {
            var message = new TinaChatMessageDto(row.Id, row.ConversationId, row.SenderId, row.SenderKind, row.Sequence,
                row.Kind, await ReadBodyAsync(row, ct), row.Sensitivity, row.AllowDerivedSharing, row.ReplyToMessageId,
                JsonSerializer.Deserialize<Guid[]>(row.SourceMessageIdsJson, Json) ?? [], row.CreatedAt);
            intents.TryGetValue(row.Id, out var intent);
            result.Add(new(message, ToDto(participants[row.SenderId]), audiences.Where(x => x.MessageId == row.Id)
                .OrderBy(x => x.Id).Select(x => new TinaChatObservedAudienceDto(ToDto(participants[x.ParticipantId]),
                    x.CanReadOriginal, x.CanReceiveDerived, x.Acknowledged)).ToArray(), intent?.Id, intent?.Status));
        }
        return new(result.ToArray(), rows.FirstOrDefault()?.Sequence ?? 0, rows.LastOrDefault()?.Sequence ?? afterSequence ?? 0, hasMore);
    }

    private static async Task<ChatConversation> ObservableConversationAsync(TinaChatDbContext db, TenantContext scope,
        TinaChatObserverAccessDto access, Guid id, CancellationToken ct)
    {
        var ids = access.Workspaces.Select(x => x.Id).ToArray();
        return await db.Conversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.TenantId == scope.TenantId && ids.Contains(x.WorkspaceId), ct) ?? throw Missing();
    }

    private async Task<TinaChatObservedConversationDto> ObservedConversationAsync(TinaChatDbContext db,
        ChatConversation row, TinaChatObserverAccessDto access, CancellationToken ct)
    {
        var people = await (from m in db.Members.AsNoTracking()
                            join p in db.Participants.AsNoTracking() on m.ParticipantId equals p.Id
                            where m.ConversationId == row.Id && p.TenantId == row.TenantId
                            orderby p.DisplayName, p.Id
                            select new { p.DisplayName, m.Status }).ToArrayAsync(ct);
        var latest = await db.Messages.AsNoTracking().Where(x => x.ConversationId == row.Id && x.TenantId == row.TenantId)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        var preview = latest is null ? null : latest.Kind == "intent_brief" ? null : await ReadBodyAsync(latest, ct);
        if (preview?.Length > 160) preview = preview[..160];
        return new(row.Id, row.WorkspaceId, access.Workspaces.Single(x => x.Id == row.WorkspaceId).Name,
            row.Title, row.Kind, row.Revision, row.LastSequence, people.Count(x => x.Status == "active"),
            people.Select(x => x.DisplayName).Take(4).ToArray(), preview, latest?.CreatedAt);
    }
}
