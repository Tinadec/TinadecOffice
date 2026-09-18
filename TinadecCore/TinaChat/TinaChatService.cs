using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.TinaChat;

public sealed partial class TinaChatService(
    IDbContextFactory<TinaChatDbContext> factory,
    ITenantContextAccessor tenant,
    ITinaChatIdentityBoundary identity,
    IContentStore content,
    ITinaChatIntentInterpreter interpreter,
    ITinaChatObserverAuthority observerAuthority) : ITinaChatService, ITinaChatRunInput, ITinaChatObserver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxMembers = 64;

    public Task<TinaChatParticipantDto> RegisterAsync(TinaChatRegisterParticipantRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        var handle = Required(request.Handle, "handle", 80).ToLowerInvariant();
        if (handle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw Invalid("Handle must use ASCII letters, digits, '.', '-' or '_'. Display names may use any language.");
        if (request.Kind is not ("human" or "agent")) throw Invalid("Participant kind must be human or agent.");
        if (request.Kind == "human" && (request.AgentDefinitionId.HasValue || request.CanInterpretIntent))
            throw Invalid("Human participants cannot bind an agent definition or an agent interpretation capability.");
        if (request.AgentDefinitionId is { } definitionId
            && !await identity.IsAgentDefinitionAvailableAsync(scope.TenantId, scope.WorkspaceId, definitionId, ct))
            throw Invalid("Agent definition must be active in the current workspace.");
        if (await db.Participants.AnyAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Handle == handle, ct))
            throw Conflict("participant_handle_exists", "This handle is already registered in the workspace.");
        var participant = new ChatParticipant
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            OwnerPrincipalId = scope.PrincipalId, Handle = handle,
            DisplayName = Required(request.DisplayName, "display_name", 160), Kind = request.Kind,
            JobTitle = Optional(request.JobTitle, "job_title", 256), Description = Optional(request.Description, "description", 16384),
            AgentDefinitionId = request.AgentDefinitionId,
            ReceiveHumanMessages = request.Kind == "human" || request.ReceiveHumanMessages,
            CanInterpretIntent = request.CanInterpretIntent, Discoverable = request.Discoverable
        };
        db.Participants.Add(participant);
        Audit(db, scope, participant.Id, "participant.registered", participant.Id);
        return ToDto(participant);
    }, ct);

    public Task<TinaChatParticipantDto> UpdateParticipantAsync(Guid id, TinaChatUpdateParticipantRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        var actor = await OwnedAsync(db, scope, id, ct, includeInactive: true);
        Expect(actor.Revision, request.ExpectedRevision);
        if (request.Status is not ("active" or "archived")) throw Invalid("Status must be active or archived.");
        if (actor.Kind == "human" && request.CanInterpretIntent) throw Invalid("Only agents can hold the interpretation capability.");
        actor.DisplayName = Required(request.DisplayName, "display_name", 160);
        actor.JobTitle = Optional(request.JobTitle, "job_title", 256);
        actor.Description = Optional(request.Description, "description", 16384);
        actor.ReceiveHumanMessages = actor.Kind == "human" || request.ReceiveHumanMessages;
        actor.CanInterpretIntent = request.CanInterpretIntent;
        actor.Discoverable = request.Discoverable;
        actor.Status = request.Status;
        actor.Revision++;
        Audit(db, scope, actor.Id, "participant.updated", actor.Id);
        return ToDto(actor);
    }, ct);

    public async Task<TinaChatParticipantDto[]> DiscoverAsync(string? query = null, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var term = Optional(query, "query", 256);
        var candidates = db.Participants.Where(x => x.TenantId == scope.TenantId && x.Status == "active"
            && (x.Discoverable || (x.OwnerPrincipalId == scope.PrincipalId && x.WorkspaceId == scope.WorkspaceId)));
        if (term is not null) candidates = candidates.Where(x => x.Handle.Contains(term) || x.DisplayName.Contains(term)
            || (x.JobTitle != null && x.JobTitle.Contains(term)));
        var rows = await candidates.OrderBy(x => x.Handle).Take(200).ToArrayAsync(ct);
        var result = new List<TinaChatParticipantDto>();
        foreach (var row in rows)
            if (await CanDiscoverAsync(db, scope, row, ct)) result.Add(ToDto(row));
        return result.ToArray();
    }

    public async Task<TinaChatParticipantDto> GetParticipantAsync(Guid id, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await ParticipantAsync(db, scope.TenantId, id, ct);
        if (!await CanDiscoverAsync(db, scope, row, ct)) throw Missing();
        return ToDto(row);
    }

    public Task<TinaChatConversationDto> CreateConversationAsync(TinaChatCreateConversationRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        var actor = await OwnedAsync(db, scope, request.ActorId, ct);
        if (request.Kind is not ("direct" or "group")) throw Invalid("Conversation kind must be direct or group.");
        var ids = Ids(request.ParticipantIds, "participant_ids").Append(actor.Id).Distinct().Order().ToArray();
        if (ids.Length > MaxMembers || (request.Kind == "direct" && ids.Length != 2))
            throw Invalid("A direct conversation has two participants; a group has at most 64.");
        var title = Required(request.Title, "title", 256);
        var key = request.ClientRequestId is null ? Guid.NewGuid().ToString("N") : Required(request.ClientRequestId, "client_request_id", 128);
        var hash = Hash(new { actor.Id, title, request.Kind, request.AllowCrossWorkspace, ids });
        var existing = await db.Conversations.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.CreatorId == actor.Id && x.ClientRequestId == key, ct);
        if (existing is not null)
        {
            SameHash(existing.RequestHash, hash);
            var membership = await MemberAsync(db, existing.Id, actor.Id, ct);
            return ToDto(existing, membership.Status);
        }
        var conversation = new ChatConversation
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            CreatorId = actor.Id, Title = title, Kind = request.Kind, AllowCrossWorkspace = request.AllowCrossWorkspace,
            ClientRequestId = key, RequestHash = hash
        };
        db.Conversations.Add(conversation);
        foreach (var id in ids)
        {
            var participant = await ParticipantAsync(db, scope.TenantId, id, ct);
            if (participant.Status != "active" || !await CanDiscoverAsync(db, scope, participant, ct)) throw Missing();
            await RequireCommunicationAsync(db, conversation, actor.WorkspaceId, participant.WorkspaceId, ct);
            db.Members.Add(new ChatMember
            {
                ConversationId = conversation.Id, ParticipantId = id,
                Role = id == actor.Id ? "owner" : "member", Status = id == actor.Id ? "active" : "invited"
            });
        }
        Audit(db, scope, actor.Id, "conversation.created", conversation.Id, conversation.Id);
        return ToDto(conversation, "active");
    }, ct);

    public async Task<TinaChatConversationDto[]> ListConversationsAsync(Guid actorId, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await OwnedAsync(db, scope, actorId, ct);
        var rows = await (from member in db.Members
                          join conversation in db.Conversations on member.ConversationId equals conversation.Id
                          where member.ParticipantId == actorId && conversation.TenantId == scope.TenantId
                              && (member.Status == "active" || member.Status == "invited")
                          orderby conversation.Title, conversation.Id
                          select new { Conversation = conversation, member.Status }).Take(200).ToArrayAsync(ct);
        var result = new List<TinaChatConversationDto>();
        foreach (var row in rows)
            if (await CanCommunicateAsync(db, row.Conversation, row.Conversation.WorkspaceId, actor.WorkspaceId, ct))
                result.Add(ToDto(row.Conversation, row.Status));
        return result.ToArray();
    }

    public async Task<TinaChatConversationDto> GetConversationAsync(Guid id, Guid actorId, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var (_, conversation, member) = await AccessAsync(db, scope, id, actorId, ct, allowInvitation: true);
        return ToDto(conversation, member.Status);
    }

    public async Task<TinaChatMemberDto[]> ListMembersAsync(Guid conversationId, Guid actorId, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        await AccessAsync(db, scope, conversationId, actorId, ct);
        return await db.Members.Where(x => x.ConversationId == conversationId && (x.Status == "active" || x.Status == "invited"))
            .OrderBy(x => x.ParticipantId).Select(x => new TinaChatMemberDto(x.ParticipantId, x.Role, x.Status, x.JoinedAfterSequence)).ToArrayAsync(ct);
    }

    public Task<TinaChatConversationDto> ChangeMemberAsync(Guid conversationId, TinaChatMemberRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        var (actor, conversation, own) = await AccessAsync(db, scope, conversationId, request.ActorId, ct, allowInvitation: true);
        Expect(conversation.Revision, request.ExpectedRevision);
        var target = await ParticipantAsync(db, scope.TenantId, request.ParticipantId, ct);
        var member = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.ParticipantId == target.Id, ct);
        switch (request.Action)
        {
            case "accept":
                if (target.Id != actor.Id || member?.Status != "invited") throw Forbidden("Only the invited participant may accept.");
                await RequireCommunicationAsync(db, conversation, conversation.WorkspaceId, actor.WorkspaceId, ct);
                member.Status = "active";
                member.JoinedAfterSequence = conversation.LastSequence;
                break;
            case "leave":
                if (target.Id != actor.Id || member is null) throw Forbidden("Only the participant may leave on their own behalf.");
                if (member.Role == "owner") throw Conflict("ownership_transfer_required", "Transfer ownership before leaving.");
                member.Status = "left";
                break;
            case "invite":
                RequireAdmin(own);
                if (conversation.Kind == "direct") throw Invalid("Direct conversation membership is fixed.");
                if (target.Status != "active" || !await CanDiscoverAsync(db, scope, target, ct)) throw Missing();
                await RequireCommunicationAsync(db, conversation, actor.WorkspaceId, target.WorkspaceId, ct);
                if (member?.Status is "active" or "invited") throw Conflict("already_a_member", "Participant is already a member or invited.");
                if (await db.Members.CountAsync(x => x.ConversationId == conversationId && (x.Status == "active" || x.Status == "invited"), ct) >= MaxMembers)
                    throw Conflict("member_limit", "The conversation has reached its member limit.");
                if (member is null)
                {
                    member = new ChatMember { ConversationId = conversationId, ParticipantId = target.Id };
                    db.Members.Add(member);
                }
                member.Status = "invited"; member.Role = "member";
                member.JoinedAfterSequence = conversation.LastSequence;
                break;
            case "remove":
                RequireAdmin(own);
                if (member is null || member.Role == "owner" || (member.Role == "admin" && own.Role != "owner"))
                    throw Forbidden("This participant cannot be removed by the acting member.");
                member.Status = "removed";
                break;
            case "role":
                if (own.Status != "active" || own.Role != "owner" || member?.Status != "active" || member.Role == "owner")
                    throw Forbidden("Only the owner can change another active member's role.");
                if (request.Role is not ("admin" or "member")) throw Invalid("Role must be admin or member.");
                member.Role = request.Role;
                break;
            case "transfer":
                if (own.Status != "active" || own.Role != "owner" || member?.Status != "active" || member.ParticipantId == actor.Id)
                    throw Forbidden("Ownership can only be transferred to another active member.");
                own.Role = "admin"; member.Role = "owner";
                break;
            default: throw Invalid("Unknown membership action.");
        }
        conversation.Revision++;
        Audit(db, scope, actor.Id, "member." + request.Action, target.Id, conversationId);
        return ToDto(conversation, own.Status);
    }, ct);

    public async Task<TinaChatWorkspacePolicyDto> GetPolicyAsync(CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        return ToDto(await PolicyAsync(db, scope.TenantId, scope.WorkspaceId, ct));
    }

    public Task<TinaChatWorkspacePolicyDto> SetPolicyAsync(TinaChatWorkspacePolicyRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        if (!await identity.IsWorkspaceAdministratorAsync(scope.TenantId, scope.WorkspaceId, scope.PrincipalId, ct))
            throw Forbidden("Workspace administration permission is required.");
        var policy = await db.Policies.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct);
        Expect(policy?.Revision ?? 0, request.ExpectedRevision);
        if (policy is null)
        {
            policy = new ChatWorkspacePolicy { TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId };
            db.Policies.Add(policy);
        }
        policy.AllowCrossWorkspaceDiscovery = request.AllowCrossWorkspaceDiscovery;
        policy.AllowCrossWorkspaceMessaging = request.AllowCrossWorkspaceMessaging;
        policy.Revision++;
        Audit(db, scope, null, "workspace.policy_updated", scope.WorkspaceId);
        return ToDto(policy);
    }, ct);

    private async Task<TenantContext> ScopeAsync(CancellationToken ct)
    {
        var scope = tenant.Current;
        if (scope.TenantId == Guid.Empty || scope.WorkspaceId == Guid.Empty || scope.PrincipalId == Guid.Empty
            || !await identity.IsWorkspaceMemberAsync(scope.TenantId, scope.WorkspaceId, scope.PrincipalId, ct))
            throw Forbidden("An active, verified workspace membership is required.");
        return scope;
    }

    private async Task<T> WriteAsync<T>(Func<TinaChatDbContext, TenantContext, Task<T>> action, CancellationToken ct)
    {
        // The database, not a process-local lock, serializes audience/policy/sequence updates.
        // A retry rereads the entire authorization decision and the idempotency record.
        for (var attempt = 0; ; attempt++)
        {
            var scope = await ScopeAsync(ct);
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var result = await action(db, scope);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (attempt < 3 && ex is DbUpdateException or DbException)
            {
                await Task.Delay(20 * (attempt + 1), ct);
            }
        }
    }

    private static async Task<ChatParticipant> ParticipantAsync(TinaChatDbContext db, Guid tenantId, Guid id, CancellationToken ct) =>
        await db.Participants.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct) ?? throw Missing();

    private static async Task<ChatParticipant> OwnedAsync(TinaChatDbContext db, TenantContext scope, Guid id, CancellationToken ct, bool includeInactive = false)
    {
        var actor = await ParticipantAsync(db, scope.TenantId, id, ct);
        if (actor.WorkspaceId != scope.WorkspaceId || actor.OwnerPrincipalId != scope.PrincipalId)
            throw Forbidden("The authenticated principal does not control this participant.");
        if (!includeInactive && actor.Status != "active") throw Forbidden("The participant is archived.");
        return actor;
    }

    private static async Task<ChatMember> MemberAsync(TinaChatDbContext db, Guid conversationId, Guid actorId, CancellationToken ct) =>
        await db.Members.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.ParticipantId == actorId, ct) ?? throw Missing();

    private async Task<(ChatParticipant Actor, ChatConversation Conversation, ChatMember Member)> AccessAsync(
        TinaChatDbContext db, TenantContext scope, Guid conversationId, Guid actorId, CancellationToken ct, bool allowInvitation = false)
    {
        var actor = await OwnedAsync(db, scope, actorId, ct);
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == scope.TenantId, ct) ?? throw Missing();
        var member = await MemberAsync(db, conversationId, actorId, ct);
        if (member.Status != "active" && !(allowInvitation && member.Status == "invited")) throw Forbidden("Active conversation membership is required.");
        await RequireCommunicationAsync(db, conversation, conversation.WorkspaceId, actor.WorkspaceId, ct);
        return (actor, conversation, member);
    }

    private static async Task<ChatWorkspacePolicy> PolicyAsync(TinaChatDbContext db, Guid tenantId, Guid workspaceId, CancellationToken ct) =>
        await db.Policies.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.WorkspaceId == workspaceId, ct)
        ?? new ChatWorkspacePolicy { TenantId = tenantId, WorkspaceId = workspaceId };

    private static async Task<bool> CanDiscoverAsync(TinaChatDbContext db, TenantContext scope, ChatParticipant participant, CancellationToken ct)
    {
        if (participant.WorkspaceId == scope.WorkspaceId && participant.OwnerPrincipalId == scope.PrincipalId) return true;
        if (participant.Status != "active" || !participant.Discoverable) return false;
        if (participant.WorkspaceId == scope.WorkspaceId) return true;
        return (await PolicyAsync(db, scope.TenantId, scope.WorkspaceId, ct)).AllowCrossWorkspaceDiscovery
            && (await PolicyAsync(db, scope.TenantId, participant.WorkspaceId, ct)).AllowCrossWorkspaceDiscovery;
    }

    private static async Task<bool> CanCommunicateAsync(TinaChatDbContext db, ChatConversation conversation, Guid sourceWorkspaceId, Guid targetWorkspaceId, CancellationToken ct)
    {
        var workspaces = new[] { conversation.WorkspaceId, sourceWorkspaceId, targetWorkspaceId }.Distinct().ToArray();
        if (workspaces.Length == 1) return true;
        if (!conversation.AllowCrossWorkspace) return false;
        foreach (var workspace in workspaces)
            if (!(await PolicyAsync(db, conversation.TenantId, workspace, ct)).AllowCrossWorkspaceMessaging) return false;
        return true;
    }

    private static async Task RequireCommunicationAsync(TinaChatDbContext db, ChatConversation conversation, Guid source, Guid target, CancellationToken ct)
    {
        if (!await CanCommunicateAsync(db, conversation, source, target, ct))
            throw Forbidden("Cross-workspace communication must be enabled by the conversation and both workspaces.");
    }

    private static void RequireAdmin(ChatMember member)
    {
        if (member.Status != "active" || member.Role is not ("owner" or "admin")) throw Forbidden("Conversation administration permission is required.");
    }

    private static string Required(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) throw Invalid($"{field} must contain 1 to {maxLength} characters.");
        return value.Trim();
    }
    private static string? Optional(string? value, string field, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : Required(value, field, maxLength);
    private static Guid[] Ids(Guid[]? ids, string field)
    {
        if (ids is null || ids.Length > MaxMembers || ids.Any(x => x == Guid.Empty)) throw Invalid($"{field} must contain at most {MaxMembers} nonempty identifiers.");
        return ids.Distinct().Order().ToArray();
    }
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
    private static void SameHash(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw Conflict("idempotency_key_reuse", "The request identifier was already used for different content.");
    }
    private static void Expect(long actual, long expected)
    {
        if (actual != expected) throw new TinaChatException(412, "tina_chat_revision_conflict", $"Expected revision {expected}; current revision is {actual}.");
    }
    private static TinaChatException Invalid(string message) => new(400, "invalid_tina_chat_request", message);
    private static TinaChatException Forbidden(string message) => new(403, "tina_chat_forbidden", message);
    private static TinaChatException Missing() => new(404, "tina_chat_not_found", "The requested resource is not available in this scope.");
    private static TinaChatException Conflict(string code, string message) => new(409, code, message);

    private static TinaChatParticipantDto ToDto(ChatParticipant x) => new(x.Id, x.WorkspaceId, x.Handle, x.DisplayName, x.Kind, x.JobTitle, x.Description,
        x.AgentDefinitionId, x.ReceiveHumanMessages, x.CanInterpretIntent, x.Discoverable, x.Status, x.Revision);
    private static TinaChatConversationDto ToDto(ChatConversation x, string status) => new(x.Id, x.WorkspaceId, x.Title, x.Kind, x.AllowCrossWorkspace, x.Revision, x.LastSequence, x.AcceptedIntentId, status);
    private static TinaChatWorkspacePolicyDto ToDto(ChatWorkspacePolicy x) => new(x.WorkspaceId, x.Revision, x.AllowCrossWorkspaceDiscovery, x.AllowCrossWorkspaceMessaging);
    private static void Audit(TinaChatDbContext db, TenantContext scope, Guid? actorId, string action, Guid targetId, Guid? conversationId = null) => db.Audit.Add(new ChatAudit
    {
        Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, PrincipalId = scope.PrincipalId,
        ActorId = actorId, Action = action, TargetId = targetId, ConversationId = conversationId, CreatedAt = DateTimeOffset.UtcNow
    });
}
