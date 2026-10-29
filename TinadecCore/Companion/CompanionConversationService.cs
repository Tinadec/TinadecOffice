using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Companion;

/// <summary>
/// Companion conversation domain: private chats, invite-created groups, membership, messages and
/// the runs a user message owes. Every predicate starts from the verified tenant id, so an id
/// from another tenant is the same "not found" answer as an unknown one.
///
/// Waking is deliberately conservative: only a user message creates runs, only explicit
/// addressees (or an exact <c>@name</c> mention) are woken, and a companion reply never cascades.
/// A message always lands first; when a wake is not allowed the response still succeeds and says
/// why in <c>skipped_runs</c> instead of failing the send.
/// </summary>
public sealed class CompanionConversationService(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ITenantContextAccessor tenant,
    ICompanionRuntime runtime)
{
    private const int MaxDepth = 3;
    private const int DefaultTurnBudget = 6;

    private Guid TenantId => tenant.Current.TenantId;

    // ── conversations ──

    /// <summary>The user's private conversation with one companion; repeated calls reuse the same row.</summary>
    public async Task<object> EnsurePrivateAsync(Guid companionId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var companion = await db.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == tenantId && x.Status != "archived", cancellationToken)
            .ConfigureAwait(false) ?? throw CompanionNotFound();

        var existing = await (
            from conversation in db.Conversations.AsNoTracking()
            join member in db.ConversationMembers.AsNoTracking() on conversation.Id equals member.ConversationId
            where conversation.TenantId == tenantId && conversation.Kind == "private"
                && member.TenantId == tenantId && member.MemberKind == "companion"
                && member.CompanionId == companionId && member.Status == "active"
            select conversation).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null) return await BuildConversationAsync(db, tenantId, existing, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var row = new CompanionConversationRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "private",
            Origin = "user",
            Title = null,
            ParentConversationId = null,
            AutonomyPaused = false,
            TurnBudget = DefaultTurnBudget,
            Revision = 1,
            CreatedAt = now,
            LastMessageAt = null
        };
        db.Conversations.Add(row);
        db.ConversationMembers.Add(NewMember(tenantId, row.Id, "user", null, now));
        db.ConversationMembers.Add(NewMember(tenantId, row.Id, "companion", companionId, now));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await BuildConversationAsync(db, tenantId, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<object> CreateGroupAsync(string? title, IReadOnlyList<Guid>? companionIds, CancellationToken cancellationToken = default) =>
        (await CreateGroupCoreAsync(title, companionIds, cancellationToken).ConfigureAwait(false)).Json;

    /// <summary>
    /// Same validation and persistence as the endpoint path, but also reports the row it created
    /// so the autonomy service can name the group in its own activity without re-reading it.
    /// <paramref name="origin"/> records who opened it: "user" from the endpoint, "companion" from
    /// an autonomy decision. Both add a user member, which is exactly why origin is its own column.
    /// </summary>
    internal async Task<CompanionConversationOutcome> CreateGroupCoreAsync(
        string? title, IReadOnlyList<Guid>? companionIds, CancellationToken cancellationToken = default, string origin = "user")
    {
        var tenantId = TenantId;
        var ids = (companionIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw Invalid("companion_ids must contain at least one companion.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var companions = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && x.Status == "active")
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (companions.Count != ids.Count) throw CompanionNotFound();

        var now = DateTimeOffset.UtcNow;
        var membersById = ids.ToDictionary(id => id, id => companions.Single(x => x.Id == id).Name);
        var resolvedTitle = ComposeTitle(title, ids.Select(id => membersById[id]));
        var row = new CompanionConversationRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "group",
            Origin = origin,
            Title = resolvedTitle,
            ParentConversationId = null,
            AutonomyPaused = false,
            TurnBudget = DefaultTurnBudget,
            Revision = 1,
            CreatedAt = now,
            LastMessageAt = null
        };
        db.Conversations.Add(row);
        db.ConversationMembers.Add(NewMember(tenantId, row.Id, "user", null, now));
        foreach (var id in ids) db.ConversationMembers.Add(NewMember(tenantId, row.Id, "companion", id, now));
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "group_created",
            CompanionId = null,
            ConversationId = row.Id,
            Summary = $"建立群聊「{resolvedTitle}」",
            // The payload is what a client renders. `summary` is a server-rendered Chinese string
            // kept for compatibility; a five-language UI cannot use it.
            PayloadJson = JsonSerializer.Serialize(new { title = resolvedTitle, companion_ids = ids }),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CompanionConversationOutcome(
            await BuildConversationAsync(db, tenantId, row, cancellationToken).ConfigureAwait(false), row.Id, resolvedTitle);
    }

    public async Task<object> ListAsync(Guid companionId, bool includeGroups, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var query =
            from conversation in db.Conversations.AsNoTracking()
            join member in db.ConversationMembers.AsNoTracking() on conversation.Id equals member.ConversationId
            where conversation.TenantId == tenantId && member.TenantId == tenantId
                && member.MemberKind == "companion" && member.CompanionId == companionId && member.Status == "active"
            select conversation;
        if (!includeGroups) query = query.Where(x => x.Kind == "private");
        var rows = (await query.ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(x => x.Id).Select(group => group.First())
            .OrderByDescending(x => x.LastMessageAt.HasValue)
            .ThenByDescending(x => x.LastMessageAt ?? x.CreatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ToList();
        return await BuildConversationsAsync(db, tenantId, rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every conversation this tenant can see, across companions, with one fixed number of queries.
    /// The centre needs this: asking once per companion made "how many groups are there" cost one
    /// request per companion on every poll, and still let two panels disagree within one refresh.
    /// </summary>
    public async Task<object> ListAllAsync(
        Guid? companionId, string? kind, string? origin, int limit, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var wantedKind = TrimToNull(kind)?.ToLowerInvariant();
        if (wantedKind is not null && wantedKind is not ("private" or "group"))
            throw Invalid("kind must be 'private' or 'group'.");
        var wantedOrigin = TrimToNull(origin)?.ToLowerInvariant();
        if (wantedOrigin is not null && wantedOrigin is not ("user" or "companion"))
            throw Invalid("origin must be 'user' or 'companion'.");
        var pageSize = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (companionId is { } memberId) await RequireCompanionAsync(db, tenantId, memberId, cancellationToken).ConfigureAwait(false);

        var query = db.Conversations.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (wantedKind is not null) query = query.Where(x => x.Kind == wantedKind);
        if (wantedOrigin == "companion")
        {
            query = query.Where(x => x.Origin == "companion"
                || (x.Origin == null && db.Activities.Any(activity => activity.TenantId == tenantId
                    && activity.ConversationId == x.Id && activity.Kind == "autonomy_group_created")));
        }
        else if (wantedOrigin == "user")
        {
            query = query.Where(x => x.Origin == "user"
                || (x.Origin == null && !db.Activities.Any(activity => activity.TenantId == tenantId
                    && activity.ConversationId == x.Id && activity.Kind == "autonomy_group_created")));
        }
        if (companionId is { } wantedCompanion)
        {
            query = query.Where(x => db.ConversationMembers.Any(member => member.TenantId == tenantId
                && member.ConversationId == x.Id && member.MemberKind == "companion"
                && member.CompanionId == wantedCompanion && member.Status == "active"));
        }

        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        // SQLite cannot ORDER BY a DateTimeOffset server-side, so the page is ordered here.
        var page = rows
            .OrderByDescending(x => x.LastMessageAt.HasValue)
            .ThenByDescending(x => x.LastMessageAt ?? x.CreatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Take(pageSize)
            .ToList();
        return await BuildConversationsAsync(db, tenantId, page, cancellationToken).ConfigureAwait(false);
    }

    public async Task<object?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await BuildConversationAsync(db, tenantId, row, cancellationToken).ConfigureAwait(false);
    }

    // ── membership ──

    /// <summary>
    /// Inviting into a private chat creates a linked group and keeps the private row untouched.
    /// Existing history is copied only when the caller asks for it.
    /// </summary>
    public async Task<object> InviteAsync(Guid conversationId, Guid companionId, bool shareHistory, CancellationToken cancellationToken = default) =>
        (await InviteCoreAsync(conversationId, companionId, shareHistory, cancellationToken).ConfigureAwait(false)).Json;

    internal async Task<CompanionConversationOutcome> InviteCoreAsync(
        Guid conversationId, Guid companionId, bool shareHistory, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw ConversationNotFound();
        var invited = await db.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == tenantId && x.Status == "active", cancellationToken)
            .ConfigureAwait(false) ?? throw CompanionNotFound();

        var members = await db.ConversationMembers
            .Where(x => x.TenantId == tenantId && x.ConversationId == conversation.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var existing = members.FirstOrDefault(x => x.MemberKind == "companion" && x.CompanionId == companionId);
        if (existing?.Status == "active")
            return new CompanionConversationOutcome(
                await BuildConversationAsync(db, tenantId, conversation, cancellationToken).ConfigureAwait(false),
                conversation.Id, conversation.Title);

        var now = DateTimeOffset.UtcNow;
        if (conversation.Kind == "private")
        {
            var originalIds = members
                .Where(x => x.MemberKind == "companion" && x.CompanionId is not null)
                .Select(x => x.CompanionId!.Value)
                .Distinct()
                .ToList();
            var allIds = originalIds.Append(companionId).Distinct().ToList();
            var names = await db.Companions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && allIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var title = ComposeTitle(null, allIds.Select(id => names.FirstOrDefault(x => x.Id == id)?.Name ?? "伙伴"));

            var group = new CompanionConversationRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = "group",
                // An invite starts from the user's private chat, so the linked group is the user's.
                Origin = "user",
                Title = title,
                ParentConversationId = conversation.Id,
                AutonomyPaused = false,
                TurnBudget = conversation.TurnBudget,
                Revision = 1,
                CreatedAt = now,
                LastMessageAt = null
            };
            db.Conversations.Add(group);
            db.ConversationMembers.Add(NewMember(tenantId, group.Id, "user", null, now));
            foreach (var id in allIds) db.ConversationMembers.Add(NewMember(tenantId, group.Id, "companion", id, now));

            if (shareHistory)
            {
                var source = await db.Messages.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConversationId == conversation.Id)
                    .OrderBy(x => x.Seq)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                long seq = 0;
                foreach (var message in source)
                {
                    seq++;
                    db.Messages.Add(new CompanionMessageRecord
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        ConversationId = group.Id,
                        Seq = seq,
                        AuthorKind = message.AuthorKind,
                        AuthorCompanionId = message.AuthorCompanionId,
                        Body = message.Body,
                        AddressedToJson = message.AddressedToJson,
                        // The key is scoped to one conversation; a copy is a new message, not a replay.
                        IdempotencyKey = null,
                        CreatedAt = message.CreatedAt
                    });
                }
                if (source.Count > 0) group.LastMessageAt = source[^1].CreatedAt;
            }

            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = "group_created",
                CompanionId = companionId,
                ConversationId = group.Id,
                Summary = $"邀请伙伴 {invited.Name} 建立群聊「{title}」",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    title,
                    parent_conversation_id = conversation.Id,
                    companion_ids = allIds,
                    invited_companion_id = companionId,
                    share_history = shareHistory
                }),
                CreatedAt = now
            });
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = "member_invited",
                CompanionId = companionId,
                ConversationId = group.Id,
                Summary = $"伙伴 {invited.Name} 加入会话「{title}」",
                PayloadJson = JsonSerializer.Serialize(new { companion_id = companionId, companion_name = invited.Name, title }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new CompanionConversationOutcome(
                await BuildConversationAsync(db, tenantId, group, cancellationToken).ConfigureAwait(false), group.Id, title);
        }

        if (existing is not null)
        {
            existing.Status = "active";
            existing.JoinedAt = now;
        }
        else
        {
            db.ConversationMembers.Add(NewMember(tenantId, conversation.Id, "companion", companionId, now));
        }
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "member_invited",
            CompanionId = companionId,
            ConversationId = conversation.Id,
            Summary = $"伙伴 {invited.Name} 加入会话「{conversation.Title ?? "群聊"}」",
            PayloadJson = JsonSerializer.Serialize(new { companion_id = companionId, companion_name = invited.Name, title = conversation.Title }),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CompanionConversationOutcome(
            await BuildConversationAsync(db, tenantId, conversation, cancellationToken).ConfigureAwait(false),
            conversation.Id, conversation.Title);
    }

    public async Task<object> LeaveAsync(Guid conversationId, Guid companionId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw ConversationNotFound();
        var member = await db.ConversationMembers.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.ConversationId == conversationId
                && x.MemberKind == "companion" && x.CompanionId == companionId,
            cancellationToken).ConfigureAwait(false);
        if (member is null) throw CompanionNotFound();
        if (member.Status != "left")
        {
            var now = DateTimeOffset.UtcNow;
            member.Status = "left";
            var name = await db.Companions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == companionId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "伙伴";
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = "member_left",
                CompanionId = companionId,
                ConversationId = conversationId,
                Summary = $"伙伴 {name} 离开会话「{conversation.Title ?? "私聊"}」",
                PayloadJson = JsonSerializer.Serialize(new { companion_id = companionId, companion_name = name, title = conversation.Title }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return await BuildConversationAsync(db, tenantId, conversation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<object> SetPausedAsync(Guid conversationId, bool paused, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw ConversationNotFound();
        if (conversation.AutonomyPaused != paused)
        {
            var now = DateTimeOffset.UtcNow;
            conversation.AutonomyPaused = paused;
            conversation.Revision++;
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = "conversation_paused",
                CompanionId = null,
                ConversationId = conversationId,
                Summary = paused ? $"暂停会话「{conversation.Title ?? "私聊"}」" : $"恢复会话「{conversation.Title ?? "私聊"}」",
                PayloadJson = JsonSerializer.Serialize(new { paused, title = conversation.Title }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return await BuildConversationAsync(db, tenantId, conversation, cancellationToken).ConfigureAwait(false);
    }

    // ── messages ──

    public async Task<object> ListMessagesAsync(Guid conversationId, long? afterSeq, int? limit, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var take = Math.Clamp(limit ?? 50, 1, 200);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireConversationAsync(db, tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        var rows = await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == conversationId && x.Seq > (afterSeq ?? 0))
            .OrderBy(x => x.Seq)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await LoadCompanionNamesAsync(db, tenantId, rows.Select(x => x.AuthorCompanionId), cancellationToken).ConfigureAwait(false);
        return rows.Select(row => CompanionContracts.MessageJson(row, AuthorName(row, names), CompanionMessages.DeserializeAddressed(row.AddressedToJson))).ToList();
    }

    /// <summary>
    /// Persists the user message and the runs it owes. A message is never rejected because a wake
    /// was skipped (paused, over budget, too deep); the skip is reported alongside the message.
    /// </summary>
    public async Task<object> PostMessageAsync(
        Guid conversationId, CompanionMessagePostRequest input, string? headerIdempotencyKey, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var body = input.Body?.Trim();
        if (string.IsNullOrEmpty(body)) throw Invalid("body is required.");
        var idempotencyKey = TrimToNull(input.IdempotencyKey) ?? TrimToNull(headerIdempotencyKey);
        if (idempotencyKey is { Length: > 256 }) throw Invalid("idempotency_key must be at most 256 characters.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw ConversationNotFound();

        if (idempotencyKey is not null)
        {
            var replayed = await db.Messages.AsNoTracking().SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.ConversationId == conversationId && x.IdempotencyKey == idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (replayed is not null)
                return await DuplicateResponseAsync(db, tenantId, conversationId, replayed, cancellationToken).ConfigureAwait(false);
        }

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == conversationId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var activeCompanionIds = members
            .Where(x => x.MemberKind == "companion" && x.Status == "active" && x.CompanionId is not null)
            .Select(x => x.CompanionId!.Value)
            .Distinct()
            .ToList();
        var companions = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && activeCompanionIds.Contains(x.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var requested = (input.AddressedTo ?? []).Distinct().ToList();
        var addressed = requested.Count > 0
            ? requested
            : ResolveMentions(body, companions);
        if (addressed.Any(id => !activeCompanionIds.Contains(id)))
            throw new CompanionException("companion_not_member", "addressed_to contains a companion that is not an active member of this conversation.", 400);
        if (addressed.Count == 0 && conversation.Kind == "private") addressed = activeCompanionIds;

        var previousUserSeq = await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == conversationId && x.AuthorKind == "user")
            .Select(x => (long?)x.Seq)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;

        var now = DateTimeOffset.UtcNow;
        var message = await CompanionMessages.AppendAsync(
            db, tenantId, conversationId, "user", null, body, addressed, idempotencyKey, now, cancellationToken).ConfigureAwait(false);

        var skipped = new List<object>();
        var runs = new List<CompanionRunRecord>();
        var turnBudget = conversation.TurnBudget <= 0 ? DefaultTurnBudget : conversation.TurnBudget;
        foreach (var companionId in addressed.Distinct())
        {
            var companion = companions.FirstOrDefault(x => x.Id == companionId);
            if (companion is null)
            {
                skipped.Add(Skip(companionId, "companion_not_found"));
                continue;
            }
            if (conversation.AutonomyPaused)
            {
                skipped.Add(Skip(companionId, "conversation_paused"));
                continue;
            }
            if (companion.Status != "active" || companion.AutonomyPaused)
            {
                skipped.Add(Skip(companionId, "companion_paused"));
                continue;
            }
            var used = await (
                from run in db.Runs.AsNoTracking()
                join trigger in db.Messages.AsNoTracking() on run.TriggerMessageId equals trigger.Id
                where run.TenantId == tenantId && run.ConversationId == conversationId
                    && run.CompanionId == companionId && trigger.Seq >= previousUserSeq
                select run.Id).CountAsync(cancellationToken).ConfigureAwait(false);
            if (used >= turnBudget)
            {
                skipped.Add(Skip(companionId, "turn_budget"));
                continue;
            }
            // Only a companion-authored trigger advances depth. This endpoint accepts user messages
            // only, so a posted message starts at 0; the rule is explicit because Task 3 reuses the
            // same wake path with companion-authored triggers.
            var depth = 0;
            if (message.AuthorKind == "companion")
            {
                var previous = await db.Runs.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.ConversationId == conversationId && x.CompanionId == companionId)
                    .MaxAsync(x => (int?)x.Depth, cancellationToken).ConfigureAwait(false);
                depth = (previous ?? -1) + 1;
            }
            if (depth > MaxDepth)
            {
                skipped.Add(Skip(companionId, "depth_limit"));
                continue;
            }
            runs.Add(new CompanionRunRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CompanionId = companionId,
                ConversationId = conversationId,
                TriggerMessageId = message.Id,
                Status = "queued",
                Depth = depth,
                ConfigRevision = companion.Revision,
                ConfigSnapshotJson = "{}",
                LoadedMemoryIdsJson = "[]",
                LoadedSkillBindingIdsJson = "[]",
                Rounds = 0,
                CreatedAt = now
            });
        }
        foreach (var run in runs) db.Runs.Add(run);
        conversation.LastMessageAt = now;
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "message_sent",
            CompanionId = null,
            ConversationId = conversationId,
            Summary = $"用户发言：{Preview(body, 40)}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                message_id = message.Id,
                author_kind = "user",
                addressed_to = addressed,
                preview = Preview(body, 80),
                run_ids = runs.Select(x => x.Id).ToList()
            }),
            CreatedAt = now
        });

        try
        {
            await CompanionMessages.SaveAsync(db, message, tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw new CompanionException("companion_duplicate_message", "The message could not be stored without colliding with a concurrent write.", 409);
        }

        var messageJson = CompanionContracts.MessageJson(message, "用户", addressed);
        foreach (var run in runs)
            await runtime.EnqueueAsync(run.Id, cancellationToken).ConfigureAwait(false);

        return new
        {
            message = messageJson,
            runs = runs.Select(CompanionContracts.RunJson).ToList(),
            skipped_runs = skipped,
            duplicate = false
        };
    }

    // ── runs & activity ──

    public async Task<object?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Runs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == runId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CompanionContracts.RunJson(row);
    }

    public async Task<object> ListActivityAsync(int? limit, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var take = Math.Clamp(limit ?? 50, 1, 200);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await db.Activities.AsNoTracking()
                .Where(x => x.TenantId == tenantId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToList();
        var companionIds = rows.Where(x => x.CompanionId is not null).Select(x => x.CompanionId!.Value).Distinct().ToList();
        var conversationIds = rows.Where(x => x.ConversationId is not null).Select(x => x.ConversationId!.Value).Distinct().ToList();
        var names = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && companionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken).ConfigureAwait(false);
        var titles = await db.Conversations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && conversationIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Title })
            .ToDictionaryAsync(x => x.Id, x => x.Title, cancellationToken).ConfigureAwait(false);
        return rows.Select(row => CompanionContracts.ActivityJson(
            row,
            row.CompanionId is { } id ? names.GetValueOrDefault(id) : null,
            row.ConversationId is { } conversationId ? titles.GetValueOrDefault(conversationId) : null)).ToList();
    }

    public async Task<object> ListConnectionsOverviewAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var companions = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ConnectionBindings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = companions.ToDictionary(x => x.Id, x => x.Name);
        return rows
            .OrderBy(x => byId.GetValueOrDefault(x.CompanionId, string.Empty), StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(row => CompanionContracts.ConnectionOverviewJson(row, byId.GetValueOrDefault(row.CompanionId)))
            .ToList();
    }

    // ── helpers ──

    private async Task<object> BuildConversationAsync(
        CompanionDbContext db, Guid tenantId, CompanionConversationRecord row, CancellationToken cancellationToken)
    {
        var built = await BuildConversationsAsync(db, tenantId, [row], cancellationToken).ConfigureAwait(false);
        return built[0];
    }

    /// <summary>
    /// Projects a page of conversations with a fixed number of queries. The single-row builder this
    /// replaces cost four round trips per conversation, which is why the companion centre polled
    /// one request per companion and still could not afford a cross-companion list.
    /// </summary>
    private async Task<List<object>> BuildConversationsAsync(
        CompanionDbContext db, Guid tenantId, IReadOnlyList<CompanionConversationRecord> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(x => x.Id).ToList();
        var members = (await db.ConversationMembers.AsNoTracking()
                .Where(x => x.TenantId == tenantId && ids.Contains(x.ConversationId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.MemberKind == "user" ? 0 : 1)
            .ThenBy(x => x.JoinedAt)
            .ToList();
        var names = await LoadCompanionNamesAsync(db, tenantId, members.Select(x => x.CompanionId), cancellationToken).ConfigureAwait(false);
        var counts = await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConversationId))
            .GroupBy(x => x.ConversationId)
            .Select(group => new { ConversationId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.ConversationId, x => x.Count, cancellationToken).ConfigureAwait(false);
        var lastPairs = await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.ConversationId))
            .GroupBy(x => x.ConversationId)
            .Select(group => new { ConversationId = group.Key, Seq = group.Max(x => x.Seq) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lastSeqs = lastPairs.Select(x => x.Seq).Distinct().ToList();
        Dictionary<(Guid ConversationId, long Seq), string> lastBodies = lastSeqs.Count == 0
            ? []
            : (await db.Messages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && ids.Contains(x.ConversationId) && lastSeqs.Contains(x.Seq))
                .Select(x => new { x.ConversationId, x.Seq, x.Body })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(x => (x.ConversationId, x.Seq), x => x.Body);
        var autonomousIds = await AutonomousGroupIdsAsync(db, tenantId, ids, cancellationToken).ConfigureAwait(false);

        var membersByConversation = members.GroupBy(x => x.ConversationId).ToDictionary(group => group.Key, group => group.ToList());
        var lastSeqByConversation = lastPairs.ToDictionary(x => x.ConversationId, x => x.Seq);
        var result = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var conversationMembers = membersByConversation.GetValueOrDefault(row.Id) ?? [];
            var lastBody = lastSeqByConversation.TryGetValue(row.Id, out var lastSeq)
                ? lastBodies.GetValueOrDefault((row.Id, lastSeq))
                : null;
            result.Add(CompanionContracts.ConversationJson(
                row,
                counts.GetValueOrDefault(row.Id),
                lastBody is null ? null : Preview(lastBody, 80),
                conversationMembers.Select(member => CompanionContracts.MemberJson(member, MemberName(member, names))).ToList(),
                OriginOf(row, autonomousIds),
                conversationMembers.Any(x => x.MemberKind == "user" && x.Status == "active")));
        }
        return result;
    }

    /// <summary>
    /// Rows written before <c>origin</c> existed answer from the autonomy activity trail: a group a
    /// companion opened always recorded <c>autonomy_group_created</c> for it. Never inferred from
    /// membership, because every group carries the user as a member either way.
    /// </summary>
    private static async Task<HashSet<Guid>> AutonomousGroupIdsAsync(
        CompanionDbContext db, Guid tenantId, IReadOnlyList<Guid> conversationIds, CancellationToken cancellationToken)
    {
        var found = await db.Activities.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.ConversationId != null && conversationIds.Contains(x.ConversationId.Value)
                && x.Kind == "autonomy_group_created")
            .Select(x => x.ConversationId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. found];
    }

    private static string OriginOf(CompanionConversationRecord row, HashSet<Guid> autonomousIds) =>
        row.Origin is { Length: > 0 } origin ? origin : autonomousIds.Contains(row.Id) ? "companion" : "user";

    private async Task<object> DuplicateResponseAsync(
        CompanionDbContext db, Guid tenantId, Guid conversationId, CompanionMessageRecord message, CancellationToken cancellationToken)
    {
        var runs = await db.Runs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == conversationId && x.TriggerMessageId == message.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await LoadCompanionNamesAsync(db, tenantId, [message.AuthorCompanionId], cancellationToken).ConfigureAwait(false);
        return new
        {
            message = CompanionContracts.MessageJson(message, AuthorName(message, names), CompanionMessages.DeserializeAddressed(message.AddressedToJson)),
            runs = runs.OrderBy(x => x.CreatedAt).Select(CompanionContracts.RunJson).ToList(),
            skipped_runs = Array.Empty<object>(),
            duplicate = true
        };
    }

    private static async Task<Dictionary<Guid, string>> LoadCompanionNamesAsync(
        CompanionDbContext db, Guid tenantId, IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && wanted.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken).ConfigureAwait(false);
    }

    private static string AuthorName(CompanionMessageRecord row, Dictionary<Guid, string> names) => row.AuthorKind switch
    {
        "companion" => row.AuthorCompanionId is { } id ? names.GetValueOrDefault(id, "伙伴") : "伙伴",
        "system" => "系统",
        _ => "用户"
    };

    private static string MemberName(CompanionConversationMemberRecord member, Dictionary<Guid, string> names) =>
        member.MemberKind == "companion" && member.CompanionId is { } id ? names.GetValueOrDefault(id, "伙伴") : "用户";

    /// <summary>Exact <c>@name</c> mentions, longest name first so "AB" is not split into "A".</summary>
    private static List<Guid> ResolveMentions(string body, IReadOnlyList<CompanionAgentRecord> companions)
    {
        var result = new List<Guid>();
        var remaining = body;
        foreach (var companion in companions.OrderByDescending(x => x.Name.Length).ThenBy(x => x.Id))
        {
            var token = "@" + companion.Name;
            if (!remaining.Contains(token, StringComparison.Ordinal)) continue;
            result.Add(companion.Id);
            remaining = remaining.Replace(token, string.Empty, StringComparison.Ordinal);
        }
        return result;
    }

    private static object Skip(Guid companionId, string reason) => new { companion_id = companionId, reason };

    private static CompanionConversationMemberRecord NewMember(Guid tenantId, Guid conversationId, string kind, Guid? companionId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ConversationId = conversationId,
        MemberKind = kind,
        CompanionId = companionId,
        JoinedAt = now,
        Status = "active"
    };

    private static async Task RequireCompanionAsync(CompanionDbContext db, Guid tenantId, Guid companionId, CancellationToken cancellationToken)
    {
        var exists = await db.Companions.AsNoTracking()
            .AnyAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (!exists) throw CompanionNotFound();
    }

    private static async Task RequireConversationAsync(CompanionDbContext db, Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var exists = await db.Conversations.AsNoTracking()
            .AnyAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (!exists) throw ConversationNotFound();
    }

    private static string Preview(string body, int max)
    {
        var oneLine = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    /// <summary>Default title is the member names joined by 、; the row column is 256 characters.</summary>
    private static string ComposeTitle(string? title, IEnumerable<string> names)
    {
        var value = TrimToNull(title) ?? string.Join("、", names);
        if (value.Length > 256) throw Invalid("title must be at most 256 characters.");
        return value;
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CompanionException Invalid(string message) => new("invalid_request", message, 400);
    private static CompanionException CompanionNotFound() => new("companion_not_found", "Companion not found.", 404);
    private static CompanionException ConversationNotFound() => new("companion_conversation_not_found", "Conversation not found.", 404);
}

/// <summary>
/// The conversation row a create/invite produced, paired with its HTTP shape. Callers that only
/// answer a request use <see cref="Json"/>; the autonomy service also needs the row id and title
/// to record its own activity without querying the row back.
/// </summary>
internal sealed record CompanionConversationOutcome(object Json, Guid ConversationId, string? Title);

/// <summary>
/// Sequence allocation for one conversation. A user post and a companion reply can be written by
/// different contexts, so the unique (tenant, conversation, seq) index is the real guard and the
/// caller retries with the next free number when it loses the race.
/// </summary>
internal static class CompanionMessages
{
    internal static async Task<CompanionMessageRecord> AppendAsync(
        CompanionDbContext db,
        Guid tenantId,
        Guid conversationId,
        string authorKind,
        Guid? authorCompanionId,
        string body,
        IReadOnlyList<Guid> addressedTo,
        string? idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = new CompanionMessageRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Seq = await NextSeqAsync(db, tenantId, conversationId, cancellationToken).ConfigureAwait(false),
            AuthorKind = authorKind,
            AuthorCompanionId = authorCompanionId,
            Body = body,
            AddressedToJson = SerializeAddressed(addressedTo),
            IdempotencyKey = idempotencyKey,
            CreatedAt = now
        };
        db.Messages.Add(row);
        return row;
    }

    internal static async Task<long> NextSeqAsync(CompanionDbContext db, Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        (await db.Messages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConversationId == conversationId)
            .Select(x => (long?)x.Seq)
            .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0) + 1;

    internal static async Task SaveAsync(
        CompanionDbContext db, CompanionMessageRecord message, Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                message.Seq = await NextSeqAsync(db, tenantId, conversationId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static string SerializeAddressed(IReadOnlyList<Guid> addressedTo) =>
        JsonSerializer.Serialize(addressedTo.Distinct().ToArray());

    internal static IReadOnlyList<Guid> DeserializeAddressed(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
