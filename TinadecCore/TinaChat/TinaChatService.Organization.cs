using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.TinaChat;

/// <summary>
/// A session as an organization (architecture §9.1). Everything here is additive over the chat
/// model: an organization is a set of participants, conversations and edges tagged with the session,
/// and every message still goes through the one write path with its frozen audience.
///
/// Scale shape, because a session can hold hundreds of instances:
/// <list type="bullet">
/// <item>The lobby and the board hold only the few roles that see everything (user, host,
/// conversation identity, governance). Executors meet in their dispatcher's plan room (at most 64 per
/// room; a busy dispatcher overflows into another), and read the board by cursor.</item>
/// <item>Room posts freeze an audience of the sender and whoever was mentioned, not the whole room;
/// the rest read by cursor. A post is O(mentions) rows, not O(members).</item>
/// <item>Default contacts are derived from the graph when checked; only consented extra contacts are
/// stored.</item>
/// <item>Presence, notices and turn budgets are single-row updates.</item>
/// </list>
/// </summary>
public sealed partial class TinaChatService : ISessionOrganization
{
    private const string HumanKey = "user";
    private const string HostKey = "host";
    private const string ConversationKey = "conversation";
    private const string Online = "online";
    private const string Offline = "offline";
    private static readonly TimeSpan NoticeBucket = TimeSpan.FromSeconds(30);

    private static string GovernanceKey(string slug) => "governance:" + slug.Trim().ToLowerInvariant();
    private static string InstanceKey(Guid instanceId) => "instance:" + instanceId.ToString("N");

    public async Task<OrganizationMemberRef> EnrolAsync(OrganizationMemberEnrolment enrolment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(enrolment);
        if (enrolment.Role is not (OrganizationRoles.Conversation or OrganizationRoles.Governance or OrganizationRoles.Executor))
            throw Invalid("An enrolled member is the conversation identity, a governance role or an executor.");
        if (enrolment.Role == OrganizationRoles.Executor && enrolment.AgentInstanceId is null)
            throw Invalid("An executor is enrolled for the instance that works its task.");
        var scope = await OrganizationScopeAsync(enrolment.Scope, ct);
        return await WriteAsync(scope, async (db, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            if (enrolment.AgentInstanceId is { } instanceId
                && await db.InstanceBindings.SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct) is { } bound)
            {
                // Idempotent re-entry: a resumed task, a second tick. Presence is the only thing that moves.
                var existing = await db.Participants.SingleAsync(x => x.Id == bound.ParticipantId, ct);
                if (existing.Presence != Online) existing.Presence = Online;
                if (enrolment.RunId is { } rerun && existing.OrgRole == OrganizationRoles.Executor) existing.CurrentRunId = rerun;
                return Ref(existing);
            }

            var org = await EnsureOrganizationAsync(db, scope, enrolment.Scope.SessionId, ct);
            RequireWritable(org);
            ChatParticipant member;
            switch (enrolment.Role)
            {
                case OrganizationRoles.Conversation:
                    member = await EnsureMemberAsync(db, org, ConversationKey, OrganizationRoles.Conversation, enrolment.AgentSlug,
                        enrolment.DisplayName, enrolment.AgentDefinitionId, enrolment.Description, parent: null, runId: null, ct);
                    await JoinAsync(db, org.LobbyConversationId, member.Id, "member", ct);
                    await JoinAsync(db, org.BoardConversationId, member.Id, "member", ct);
                    break;
                case OrganizationRoles.Governance:
                    member = await EnsureMemberAsync(db, org, GovernanceKey(enrolment.AgentSlug), OrganizationRoles.Governance, enrolment.AgentSlug,
                        enrolment.DisplayName, enrolment.AgentDefinitionId, enrolment.Description, parent: null, runId: null, ct);
                    await JoinAsync(db, org.LobbyConversationId, member.Id, "member", ct);
                    await JoinAsync(db, org.BoardConversationId, member.Id, "member", ct);
                    await EnsureAttentionAsync(db, org, member, ct);
                    break;
                default:
                    var dispatcher = await DispatcherAsync(db, org, enrolment.DispatcherInstanceId, ct);
                    member = await EnsureMemberAsync(db, org, InstanceKey(enrolment.AgentInstanceId!.Value), OrganizationRoles.Executor, enrolment.AgentSlug,
                        enrolment.DisplayName, enrolment.AgentDefinitionId, enrolment.Description, dispatcher.Id, enrolment.RunId, ct);
                    var plan = await EnsurePlanRoomAsync(db, org, dispatcher, ct);
                    await JoinAsync(db, plan.Id, member.Id, "member", ct);
                    break;
            }
            if (enrolment.AgentInstanceId is { } actingInstance)
            {
                db.InstanceBindings.Add(new ChatInstanceBinding
                {
                    InstanceId = actingInstance, OrganizationId = org.Id, ParticipantId = member.Id,
                    RunId = enrolment.RunId, TaskId = enrolment.TaskId, CreatedAt = now
                });
            }
            return Ref(member);
        }, ct);
    }

    public async Task<int> SetRunOfflineAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Participants
            .Where(x => x.CurrentRunId == runId && x.OrgRole == OrganizationRoles.Executor && x.Presence == Online)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Presence, Offline), ct);
    }

    public async Task SetInstanceOfflineAsync(Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var binding = await db.InstanceBindings.AsNoTracking().SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct);
        if (binding is null) return;
        await db.Participants
            .Where(x => x.Id == binding.ParticipantId && x.OrgRole == OrganizationRoles.Executor && x.Presence == Online)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Presence, Offline), ct);
    }

    public async Task<bool> NotifyAsync(OrganizationNotice notice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        await using (var probe = await factory.CreateDbContextAsync(ct))
        {
            // An archived session's organization is read-only: facts about it are dropped at the
            // door rather than failing the run that raised them.
            var status = await probe.Organizations.AsNoTracking().Where(x => x.TenantId == notice.Scope.TenantId && x.SessionId == notice.Scope.SessionId)
                .Select(x => x.Status).SingleOrDefaultAsync(ct);
            if (status is not null && status != "active") return false;
        }
        var member = await EnrolAsync(new OrganizationMemberEnrolment(
            notice.Scope, OrganizationRoles.Governance, notice.AgentSlug, notice.AgentSlug, RunId: notice.RunId), ct);
        var scope = await OrganizationScopeAsync(notice.Scope, ct);
        return await WriteAsync(scope, async (db, _) =>
        {
            var org = await db.Organizations.SingleAsync(x => x.Id == member.OrganizationId, ct);
            if (org.Status != "active") return false;
            var target = await db.Participants.SingleAsync(x => x.Id == member.ParticipantId, ct);
            // A member whose visibility the user restricted hears no more facts (todo E5): a standing
            // member has no run of its own (CurrentRunId is only the last fact's run), so restricting
            // a governance role mutes it entirely; any other member hears only its own run. True, not
            // false: the member exists and was deliberately muted, not unreachable.
            if (string.Equals(target.VisibilityScope, OrganizationMemberVisibility.Own, StringComparison.OrdinalIgnoreCase)
                && (target.OrgRole == OrganizationRoles.Governance || target.CurrentRunId != notice.RunId))
                return true;
            // The run whose fact this is: its frozen configuration is the role's definition for the
            // turn the notice will cause (prompt, model plan, tool scope).
            target.CurrentRunId = notice.RunId;
            var host = await db.Participants.SingleAsync(x => x.Id == org.HostParticipantId, ct);
            var attention = await EnsureAttentionAsync(db, org, target, ct);
            // One notice per topic, run and 30-second window: a burst of the same fact is one nudge
            // (the member reads the graph for the details), and the idempotency index enforces it.
            var bucket = DateTimeOffset.UtcNow.UtcTicks / NoticeBucket.Ticks;
            var key = $"notice:{notice.Topic}:{notice.RunId:N}:{bucket}";
            if (await db.Messages.AnyAsync(x => x.ConversationId == attention.Id && x.SenderId == host.Id && x.ClientMessageId == key, ct))
                return true;
            var subject = notice.SubjectKind is null ? "" : $" (subject: {notice.SubjectKind} {notice.SubjectId})";
            var body = Clip($"[{notice.Topic}] run {notice.RunId:N}: {notice.Text}{subject}");
            await AppendMessageAsync(db, attention, host, body, key, Hash(new { notice.Topic, notice.RunId, bucket }), "notice", "normal",
                allowDerivedSharing: false, replyTo: null, sources: [], recipients: [host, target], ct);
            return true;
        }, ct);
    }

    public async Task SetArchivedAsync(Guid sessionId, bool archived, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await db.Organizations.SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (org is null) return;
        var target = archived ? "archived" : "active";
        if (org.Status == target) return;
        org.Status = target;
        org.ArchivedAt = archived ? DateTimeOffset.UtcNow : null;
        org.Revision++;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OrganizationReportDigest>> ListOpenReportsAsync(Guid sessionId, int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (org is null) return [];
        // SQLite cannot order by DateTimeOffset; open reports are bounded before the in-memory sort.
        var open = await db.Reports.AsNoTracking().Where(x => x.OrganizationId == org.Id && x.Status == "open").Take(500).ToArrayAsync(ct);
        var authors = await db.Participants.AsNoTracking().Where(x => x.OrganizationId == org.Id && open.Select(r => r.AuthorId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        return open.OrderBy(x => SeverityRank(x.Severity)).ThenByDescending(x => x.CreatedAt).Take(Math.Clamp(max, 1, 100))
            .Select(x => new OrganizationReportDigest(x.MessageId, authors.GetValueOrDefault(x.AuthorId, "unknown"), x.ReportKind, x.Severity,
                x.Finding, x.SubjectKind, x.SubjectId, x.ProposedVerb, x.CreatedAt))
            .ToArray();
    }

    public async Task<IReadOnlyList<OrganizationMemberSummary>> ListMembersAsync(Guid sessionId, int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (org is null) return [];
        var rows = await db.Participants.AsNoTracking().Where(x => x.OrganizationId == org.Id)
            .OrderBy(x => x.OrgRole).ThenBy(x => x.DisplayName).Take(Math.Clamp(max, 1, 1000)).ToArrayAsync(ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var bindings = await db.InstanceBindings.AsNoTracking().Where(x => x.OrganizationId == org.Id && ids.Contains(x.ParticipantId))
            .ToArrayAsync(ct);
        return rows.Select(x => new OrganizationMemberSummary(x.Id, x.Handle, x.DisplayName, x.OrgRole ?? "", x.Presence ?? Offline,
                x.AgentSlug, x.ParentParticipantId, x.CurrentRunId,
                x.OrgRole == OrganizationRoles.Executor ? bindings.FirstOrDefault(b => b.ParticipantId == x.Id)?.InstanceId : null))
            .ToArray();
    }

    public async Task<OrganizationMemberVisibility?> VisibilityForInstanceAsync(Guid sessionId, Guid agentInstanceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (org is null) return null;
#pragma warning disable CA1862
        var member = await (
            from binding in db.InstanceBindings.AsNoTracking()
            join participant in db.Participants.AsNoTracking() on binding.ParticipantId equals participant.Id
            where binding.OrganizationId == org.Id && binding.InstanceId == agentInstanceId
            select new { participant.VisibilityScope, participant.CurrentRunId }).FirstOrDefaultAsync(ct);
#pragma warning restore CA1862
        return member is null ? null : OrganizationMemberVisibility.Of(member.VisibilityScope, member.CurrentRunId);
    }

    public async Task<OrganizationMemberVisibility?> VisibilityForParticipantAsync(Guid sessionId, Guid participantId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var org = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (org is null) return null;
        var member = await db.Participants.AsNoTracking()
            .Where(x => x.OrganizationId == org.Id && x.Id == participantId)
            .Select(x => new { x.VisibilityScope, x.CurrentRunId }).FirstOrDefaultAsync(ct);
        return member is null ? null : OrganizationMemberVisibility.Of(member.VisibilityScope, member.CurrentRunId);
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// <summary>The organization's acting scope: its owner, verified to still belong to the workspace.</summary>
    private async Task<TenantContext> OrganizationScopeAsync(OrganizationScope scope, CancellationToken ct)
    {
        if (scope.TenantId == Guid.Empty || scope.WorkspaceId == Guid.Empty || scope.PrincipalId == Guid.Empty || scope.SessionId == Guid.Empty)
            throw Invalid("An organization is scoped by tenant, workspace, principal and session.");
        if (!await identity.IsWorkspaceMemberAsync(scope.TenantId, scope.WorkspaceId, scope.PrincipalId, ct))
            throw Forbidden("The session's principal is not an active member of the workspace, so it has no organization to act in.");
        return new TenantContext(scope.TenantId, scope.WorkspaceId, scope.PrincipalId, "tina-chat-organization");
    }

    private static void RequireWritable(ChatOrganization org)
    {
        if (org.Status != "active")
            throw new TinaChatException(409, "organization_archived", "This session is archived, so its organization is read-only.");
    }

    private async Task<ChatOrganization> EnsureOrganizationAsync(TinaChatDbContext db, TenantContext scope, Guid sessionId, CancellationToken ct)
    {
        var org = await db.Organizations.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.SessionId == sessionId, ct);
        if (org is not null)
        {
            if (org.OwnerPrincipalId != scope.PrincipalId || org.WorkspaceId != scope.WorkspaceId)
                throw Forbidden("This session's organization belongs to another principal.");
            return org;
        }
        var now = DateTimeOffset.UtcNow;
        var orgId = Guid.NewGuid();
        var host = NewMember(scope, orgId, HostKey, OrganizationRoles.Host, "host", "host", kind: "agent", agentDefinitionId: null, description: "Posts engine facts.", parent: null, runId: null);
        var human = NewMember(scope, orgId, HumanKey, OrganizationRoles.Human, "user", "user", kind: "human", agentDefinitionId: null, description: null, parent: null, runId: null);
        db.Participants.Add(host);
        db.Participants.Add(human);
        var lobby = NewRoom(scope, orgId, host.Id, "lobby", "大厅", $"org:{orgId:N}:lobby");
        var board = NewRoom(scope, orgId, host.Id, "board", "公告板", $"org:{orgId:N}:board");
        db.Conversations.Add(lobby);
        db.Conversations.Add(board);
        foreach (var room in new[] { lobby, board })
        {
            db.Members.Add(new ChatMember { ConversationId = room.Id, ParticipantId = host.Id, Role = "owner", Status = "active" });
            db.Members.Add(new ChatMember { ConversationId = room.Id, ParticipantId = human.Id, Role = "admin", Status = "active" });
        }
        org = new ChatOrganization
        {
            Id = orgId, TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, SessionId = sessionId,
            OwnerPrincipalId = scope.PrincipalId, Status = "active", HostParticipantId = host.Id, HumanParticipantId = human.Id,
            LobbyConversationId = lobby.Id, BoardConversationId = board.Id, CreatedAt = now
        };
        db.Organizations.Add(org);
        Audit(db, scope, host.Id, "organization.created", orgId);
        // The organization's own rows must exist before members join its rooms in this transaction.
        await db.SaveChangesAsync(ct);
        return org;
    }

    private static async Task<ChatParticipant> EnsureMemberAsync(TinaChatDbContext db, ChatOrganization org, string key, string role, string slug,
        string displayName, Guid? agentDefinitionId, string? description, Guid? parent, Guid? runId, CancellationToken ct)
    {
        var member = await db.Participants.SingleOrDefaultAsync(x => x.OrganizationId == org.Id && x.MemberKey == key, ct);
        if (member is null)
        {
            var scope = new TenantContext(org.TenantId, org.WorkspaceId, org.OwnerPrincipalId, "tina-chat-organization");
            member = NewMember(scope, org.Id, key, role, slug, displayName, "agent", agentDefinitionId, description, parent, runId);
            db.Participants.Add(member);
            await db.SaveChangesAsync(ct);
            return member;
        }
        member.Presence = Online;
        if (role == OrganizationRoles.Executor && runId is not null) member.CurrentRunId = runId;
        if (!string.IsNullOrWhiteSpace(displayName) && member.OrgRole != OrganizationRoles.Executor) member.DisplayName = Limit(displayName, 160);
        return member;
    }

    private static ChatParticipant NewMember(TenantContext scope, Guid orgId, string key, string role, string slug, string displayName,
        string kind, Guid? agentDefinitionId, string? description, Guid? parent, Guid? runId) => new()
    {
        Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, OwnerPrincipalId = scope.PrincipalId,
        Handle = OrganizationHandle(key, orgId), DisplayName = Limit(string.IsNullOrWhiteSpace(displayName) ? slug : displayName, 160),
        Kind = kind, Description = description is null ? null : Limit(description, 16384),
        AgentDefinitionId = kind == "agent" ? agentDefinitionId : null,
        // Members work for the session's owner; hearing the user is the point, not an opt-in.
        ReceiveHumanMessages = true, CanInterpretIntent = false, Discoverable = false, Status = "active",
        OrganizationId = orgId, MemberKey = key, OrgRole = role, AgentSlug = Limit(slug, 128),
        ParentParticipantId = parent, Presence = Online, CurrentRunId = role == OrganizationRoles.Executor ? runId : null
    };

    /// <summary>
    /// A workspace-unique handle derived from the member key and the organization: members are
    /// addressed by display name inside their organization, so the handle only has to be unique,
    /// stable and within the handle alphabet.
    /// </summary>
    private static string OrganizationHandle(string key, Guid orgId)
    {
        var base36 = new StringBuilder(key.Length);
        foreach (var c in key.ToLowerInvariant())
            base36.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var local = base36.ToString();
        if (local.Length > 40)
            local = local[..33] + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..6].ToLowerInvariant();
        return $"{local}.{orgId:N}";
    }

    private static ChatConversation NewRoom(TenantContext scope, Guid orgId, Guid creatorId, string kind, string title, string key) => new()
    {
        Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, CreatorId = creatorId,
        Title = Limit(title, 256), Kind = kind, ClientRequestId = key, RequestHash = Hash(new { orgId, kind, key }),
        OrganizationId = orgId
    };

    private static async Task JoinAsync(TinaChatDbContext db, Guid conversationId, Guid participantId, string role, CancellationToken ct)
    {
        var member = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.ParticipantId == participantId, ct);
        if (member is null)
        {
            db.Members.Add(new ChatMember { ConversationId = conversationId, ParticipantId = participantId, Role = role, Status = "active" });
            return;
        }
        if (member.Status != "active") member.Status = "active";
    }

    /// <summary>The member that dispatched an executor: the dispatcher instance's member, else the conversation identity.</summary>
    private async Task<ChatParticipant> DispatcherAsync(TinaChatDbContext db, ChatOrganization org, Guid? dispatcherInstanceId, CancellationToken ct)
    {
        if (dispatcherInstanceId is { } instanceId
            && await db.InstanceBindings.AsNoTracking().SingleOrDefaultAsync(x => x.InstanceId == instanceId && x.OrganizationId == org.Id, ct) is { } binding)
            return await db.Participants.SingleAsync(x => x.Id == binding.ParticipantId, ct);
        return await db.Participants.SingleOrDefaultAsync(x => x.OrganizationId == org.Id && x.MemberKey == ConversationKey, ct)
            ?? await EnsureMemberAsync(db, org, ConversationKey, OrganizationRoles.Conversation, "conversation", "conversation", null, null, null, null, ct);
    }

    /// <summary>
    /// The dispatcher's plan room: where it and the executors it dispatched coordinate. A room holds at
    /// most 64 members, so a dispatcher with more executors than that gets a second room rather than
    /// a refusal — the cap bounds fan-out per post, not the size of a team.
    /// </summary>
    private static async Task<ChatConversation> EnsurePlanRoomAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant owner, CancellationToken ct)
    {
        var rooms = await db.Conversations.Where(x => x.OrganizationId == org.Id && x.Kind == "plan" && x.PlanOwnerId == owner.Id)
            .OrderBy(x => x.Title).ToListAsync(ct);
        foreach (var room in rooms)
        {
            var count = await db.Members.CountAsync(x => x.ConversationId == room.Id && x.Status == "active", ct);
            if (count < MaxMembers) return room;
        }
        var index = rooms.Count + 1;
        var scope = new TenantContext(org.TenantId, org.WorkspaceId, org.OwnerPrincipalId, "tina-chat-organization");
        var plan = NewRoom(scope, org.Id, org.HostParticipantId, "plan",
            index == 1 ? $"{owner.DisplayName} 的计划室" : $"{owner.DisplayName} 的计划室 {index}",
            $"org:{org.Id:N}:plan:{owner.Id:N}:{index}");
        plan.PlanOwnerId = owner.Id;
        db.Conversations.Add(plan);
        db.Members.Add(new ChatMember { ConversationId = plan.Id, ParticipantId = owner.Id, Role = "owner", Status = "active" });
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>A standing member's attention channel: a direct chat with the host, where notices land and wake it.</summary>
    private static async Task<ChatConversation> EnsureAttentionAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant member, CancellationToken ct)
    {
        var key = $"org:{org.Id:N}:attention:{member.Id:N}";
        var existing = await db.Conversations.SingleOrDefaultAsync(x => x.TenantId == org.TenantId && x.CreatorId == org.HostParticipantId && x.ClientRequestId == key, ct);
        if (existing is not null) return existing;
        var scope = new TenantContext(org.TenantId, org.WorkspaceId, org.OwnerPrincipalId, "tina-chat-organization");
        var attention = NewRoom(scope, org.Id, org.HostParticipantId, "direct", $"提醒 · {member.DisplayName}", key);
        db.Conversations.Add(attention);
        db.Members.Add(new ChatMember { ConversationId = attention.Id, ParticipantId = org.HostParticipantId, Role = "owner", Status = "active" });
        db.Members.Add(new ChatMember { ConversationId = attention.Id, ParticipantId = member.Id, Role = "member", Status = "active" });
        await db.SaveChangesAsync(ct);
        return attention;
    }

    /// <summary>The direct chat between two members, created on first use. The host creates it so the pair has one chat whichever side spoke first.</summary>
    private static async Task<ChatConversation> EnsureDirectAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant a, ChatParticipant b, CancellationToken ct)
    {
        var (low, high) = a.Id.CompareTo(b.Id) < 0 ? (a, b) : (b, a);
        var key = DirectKey(org, a, b);
        var existing = await db.Conversations.SingleOrDefaultAsync(x => x.TenantId == org.TenantId && x.CreatorId == org.HostParticipantId && x.ClientRequestId == key, ct);
        if (existing is not null) return existing;
        var scope = new TenantContext(org.TenantId, org.WorkspaceId, org.OwnerPrincipalId, "tina-chat-organization");
        var direct = NewRoom(scope, org.Id, org.HostParticipantId, "direct", $"{low.DisplayName} · {high.DisplayName}", key);
        db.Conversations.Add(direct);
        db.Members.Add(new ChatMember { ConversationId = direct.Id, ParticipantId = low.Id, Role = "member", Status = "active" });
        db.Members.Add(new ChatMember { ConversationId = direct.Id, ParticipantId = high.Id, Role = "member", Status = "active" });
        await db.SaveChangesAsync(ct);
        return direct;
    }

    /// <summary>
    /// Whether two members may talk directly. Derived edges first (they cost nothing to store): the
    /// universally reachable roles, dispatcher and dispatched, siblings under one dispatcher. Anything
    /// else needs a contact both sides agreed to.
    /// </summary>
    private static async Task<bool> AreContactsAsync(TinaChatDbContext db, ChatParticipant a, ChatParticipant b, CancellationToken ct)
    {
        if (a.Id == b.Id || a.OrganizationId is null || a.OrganizationId != b.OrganizationId) return false;
        if (DerivedContact(a, b)) return true;
        return await db.Contacts.AnyAsync(x => x.Status == "active"
            && ((x.OwnerId == a.Id && x.ContactId == b.Id) || (x.OwnerId == b.Id && x.ContactId == a.Id)), ct);
    }

    private static bool DerivedContact(ChatParticipant a, ChatParticipant b) =>
        OrganizationRoles.IsUniversalContact(a.OrgRole) || OrganizationRoles.IsUniversalContact(b.OrgRole)
        || a.ParentParticipantId == b.Id || b.ParentParticipantId == a.Id
        || (a.ParentParticipantId is { } parent && parent == b.ParentParticipantId);

    private static OrganizationMemberRef Ref(ChatParticipant member) =>
        new(member.OrganizationId!.Value, member.Id, member.Handle, member.DisplayName, member.OrgRole ?? "");

    private static int SeverityRank(string severity) => severity switch { "blocking" => 0, "warning" => 1, _ => 2 };

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
