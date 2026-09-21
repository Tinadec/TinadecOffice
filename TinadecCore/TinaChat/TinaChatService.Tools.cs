using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.TinaChat;

/// <summary>
/// The tools a governed run uses to act as a named member of a conversation. They are a thin
/// surface over the same service the HTTP layer serves, so every audience, provenance,
/// confidentiality and membership rule is enforced exactly once — a tool call cannot reach a
/// message a participant is not entitled to send or hear. Accepting a brief is therefore an
/// agent-side capability too: the authority is the conversation role, and no human has to be
/// present for a room to reach a decision and start work.
///
/// Identity: a session speaks only as the participant bound through <c>tina_chat_bind</c> by a
/// principal that already controls it, and every later call re-verifies that the participant is
/// still active, still owned by this run's principal, and still a member where it wants to speak.
///
/// <c>tina_chat_execute_intent</c> is deliberately NOT handled here: it needs the mode catalog and
/// the run coordinator, both of which live above this module, so a Runtime decorator over this same
/// port serves that one tool and forwards the rest. Injecting it here would close a DI cycle.
/// </summary>
public sealed partial class TinaChatService : ITinaChatToolGateway
{
    private const int ToolPageLimit = 20;
    private const int ToolContentCeiling = 2000;

    public async Task<TinaChatToolOutcome> ExecuteAsync(TinaChatToolCall call, CancellationToken ct = default)
    {
        var scope = new TenantContext(call.TenantId, call.WorkspaceId, call.PrincipalId, "tina-chat-tool");
        if (!await identity.IsWorkspaceMemberAsync(scope.TenantId, scope.WorkspaceId, scope.PrincipalId, ct))
            return TinaChatToolOutcome.Failed("This run's principal is not an active member of the workspace, so it has no chat identity to act through.");
        try
        {
            var payload = call.ToolId switch
            {
                "tina_chat_bind" => await BindAsync(scope, call, ct),
                "tina_chat_search_people" => await SearchPeopleAsync(scope, call, ct),
                "tina_chat_list_rooms" => await ListRoomsAsync(scope, call, ct),
                "tina_chat_read_inbox" => await ReadInboxToolAsync(scope, call, ct),
                "tina_chat_send" => await SendToolAsync(scope, call, ct),
                "tina_chat_propose_intent" => await ProposeIntentToolAsync(scope, call, ct),
                "tina_chat_list_intents" => await ListIntentsToolAsync(scope, call, ct),
                "tina_chat_decide_intent" => await DecideIntentToolAsync(scope, call, ct),
                _ => throw Invalid($"Unknown tool '{call.ToolId}'."),
            };
            return new TinaChatToolOutcome(true, JsonSerializer.Serialize(payload, Json));
        }
        catch (TinaChatException ex)
        {
            // Hand the model the service's own sentence rather than a bare 403, so it can change
            // what it addresses instead of replaying the same call.
            return TinaChatToolOutcome.Failed(ex.Message);
        }
    }

    /// <summary>The identity check every tool runs, offered to the handoff that lives above this module.</summary>
    public async Task<Guid> RequireActorAsync(TinaChatToolCall call, CancellationToken ct = default)
    {
        var scope = new TenantContext(call.TenantId, call.WorkspaceId, call.PrincipalId, "tina-chat-tool");
        if (!await identity.IsWorkspaceMemberAsync(scope.TenantId, scope.WorkspaceId, scope.PrincipalId, ct))
            throw Forbidden("This run's principal is not an active member of the workspace, so it has no chat identity to act through.");
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await ActorAsync(db, scope, call, ct)).Id;
    }

    private async Task<object> BindAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        var handle = Text(call.Arguments, "handle", max: 80).ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        var participant = await db.Participants.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Handle == handle, ct)
            ?? throw new TinaChatException(404, "tina_chat_not_found", $"No participant '@{handle}' exists in this workspace. Check the spelling with tina_chat_search_people, or have an administrator register it.");
        if (participant.OwnerPrincipalId != scope.PrincipalId)
            throw Forbidden($"'@{handle}' is not yours to speak as; only the principal that registered it may bind it.");
        var existing = await BindingAsync(db, call.SessionId, ct);
        if (existing is not null && existing.ParticipantId != participant.Id)
            throw Conflict("session_identity_bound", "This session already speaks as another participant. Rebinding mid-conversation would make its earlier statements unattributable.");
        if (existing is not null) participant = await ParticipantAsync(db, scope.TenantId, existing.ParticipantId, ct);
        else
        {
            db.SessionIdentities.Add(new ChatSessionIdentity
            {
                SessionId = call.SessionId, TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
                ParticipantId = participant.Id, OwnerPrincipalId = scope.PrincipalId, CreatedAt = DateTimeOffset.UtcNow
            });
            Audit(db, scope, participant.Id, "session.identity_bound", participant.Id);
            await db.SaveChangesAsync(ct);
        }
        if (participant.Status != "active") throw Forbidden("The participant to bind is archived.");
        return new
        {
            status = "bound", handle = participant.Handle, display_name = participant.DisplayName, kind = participant.Kind,
            receives_human_messages = participant.ReceiveHumanMessages, can_interpret_intent = participant.CanInterpretIntent,
        };
    }

    private async Task<object> SearchPeopleAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        var query = Text(call.Arguments, "query", required: false, max: 256);
        var people = await DiscoverAsync(scope, query.Length == 0 ? null : query, ct);
        return new
        {
            people = people.Take(ToolPageLimit).Select(x => new
            {
                handle = x.Handle, display_name = x.DisplayName, kind = x.Kind, job_title = x.JobTitle,
                receives_human_messages = x.ReceiveHumanMessages, can_interpret_intent = x.CanInterpretIntent,
            }).ToArray(),
            truncated = people.Length > ToolPageLimit,
        };
    }

    private async Task<object> ListRoomsAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await ActorAsync(db, scope, call, ct);
        var rooms = await ListConversationsAsync(scope, actor.Id, ct);
        return new
        {
            speaking_as = actor.Handle,
            rooms = rooms.Take(ToolPageLimit).Select(x => new
            {
                conversation_id = x.Id.ToString("N"), title = x.Title, kind = x.Kind, membership_status = x.MembershipStatus,
                last_sequence = x.LastSequence, accepted_intent_id = x.AcceptedIntentId?.ToString("N"),
            }).ToArray(),
        };
    }

    private async Task<object> ReadInboxToolAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await ActorAsync(db, scope, call, ct);
        var after = long.TryParse(Text(call.Arguments, "after_sequence", required: false), out var cursor) && cursor >= 0 ? cursor : 0;
        var page = await ReadInboxAsync(scope, actor.Id, after, ToolPageLimit, ct);
        return new
        {
            speaking_as = actor.Handle,
            messages = page.Items.Select(x => new
            {
                message_id = x.Message.Id.ToString("N"), conversation_id = x.Message.ConversationId.ToString("N"),
                sequence = x.Message.Sequence, sender_kind = x.Message.SenderKind, kind = x.Message.Kind,
                confidentiality = x.Message.Sensitivity, may_share_as_derived = x.Message.AllowDerivedSharing,
                acknowledged = x.Acknowledged, content = Clip(x.Message.Content),
            }).ToArray(),
            next_cursor = page.NextCursor,
        };
    }

    private async Task<object> SendToolAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await ActorAsync(db, scope, call, ct);
        var conversationId = Identifier(call.Arguments, "conversation_id");
        var requested = List(call.Arguments, "audience_handles");
        var audience = await ResolveAudienceAsync(db, scope, actor, conversationId, requested, ct);
        var message = await SendAsync(scope, conversationId, new TinaChatSendMessageRequest(
            actor.Id, Text(call.Arguments, "content", max: 16384), ToolKey(call),
            audience, OptionalIdentifier(call.Arguments, "reply_to_message_id"),
            Identifiers(call.Arguments, "source_message_ids"),
            AllowDerivedSharing: Bool(call.Arguments, "allow_derived_sharing"), Sensitivity: Sensitivity(call.Arguments)), ct);
        return new
        {
            status = "sent", message_id = message.Id.ToString("N"), sequence = message.Sequence,
            // Interpreter members are queued a turn by the same durable wake the HTTP path uses;
            // the model must not expect an answer inside this call.
            note = "Durable. Any entitled interpreter in the audience is queued a turn; no reply arrives in this call.",
        };
    }

    private async Task<object> ProposeIntentToolAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await ActorAsync(db, scope, call, ct);
        if (!actor.CanInterpretIntent)
            throw Forbidden("This session's participant is not configured to interpret intent. Send an ordinary message instead, or hand the material to an interpreter.");
        var conversationId = Identifier(call.Arguments, "conversation_id");
        var sources = Identifiers(call.Arguments, "source_message_ids");
        if (sources.Length == 0) throw Invalid("source_message_ids must name the messages this brief is built from. Read the inbox first.");
        var requested = List(call.Arguments, "audience_handles");
        var audience = await ResolveAudienceAsync(db, scope, actor, conversationId, requested, ct)
            ?? await InterpretableAudienceAsync(db, scope, actor, conversationId, sources, ct);
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == scope.TenantId, ct) ?? throw Missing();
        var content = new TinaChatIntentContent(
            Text(call.Arguments, "goal", max: 8192),
            List(call.Arguments, "user_statements"), List(call.Arguments, "constraints"), List(call.Arguments, "assumptions"),
            List(call.Arguments, "open_questions"), List(call.Arguments, "blocking_questions"), List(call.Arguments, "acceptance_criteria"));
        ValidateIntent(content);
        var hash = Hash(new { kind = "proposed", sources, audience, content });
        var intent = await SaveIntentAsync(scope, conversationId, new TinaChatProposeIntentRequest(
            actor.Id, ToolKey(call), conversation.Revision, sources, audience, content), hash, ct);
        return new { status = intent.Status, intent_id = intent.Id.ToString("N"), revision = intent.Revision, source_count = sources.Length };
    }

    /// <summary>
    /// The briefs this participant may see, with the conversation revision needed to decide one.
    /// Reading and deciding are separate calls on purpose: a brief is only authoritative against the
    /// revision it was read at, and a stale decision must fail as 412 rather than overwrite a newer one.
    /// </summary>
    private async Task<object> ListIntentsToolAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await ActorAsync(db, scope, call, ct);
        var conversationId = Identifier(call.Arguments, "conversation_id");
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == scope.TenantId, ct) ?? throw Missing();
        var intents = await ListIntentsAsync(conversationId, actor.Id, ct);
        return new
        {
            conversation_revision = conversation.Revision,
            accepted_intent_id = conversation.AcceptedIntentId?.ToString("N"),
            intents = intents.Take(ToolPageLimit).Select(x => new
            {
                intent_id = x.Id.ToString("N"),
                status = x.Status,
                x.Revision,
                goal = x.Content.Goal,
                user_statements = x.Content.UserStatements,
                constraints = x.Content.Constraints,
                assumptions = x.Content.Assumptions,
                open_questions = x.Content.OpenQuestions,
                blocking_questions = x.Content.BlockingQuestions,
                acceptance_criteria = x.Content.AcceptanceCriteria,
                source_message_ids = x.SourceMessageIds.Select(id => id.ToString("N")).ToArray(),
                authored_by_you = x.AuthorId == actor.Id,
                created_at = x.CreatedAt,
            }).ToArray(),
        };
    }

    /// <summary>
    /// Accept or reject a brief as the bound participant. Authority is the conversation role, not the
    /// species: <see cref="RequireAdmin"/> answers for agents exactly as for humans, and a participant
    /// that is not an owner/admin gets the service's own refusal back as the tool result.
    /// </summary>
    private async Task<object> DecideIntentToolAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await ActorAsync(db, scope, call, ct);
        var conversationId = Identifier(call.Arguments, "conversation_id");
        var intentId = Identifier(call.Arguments, "intent_id");
        var decision = Text(call.Arguments, "decision", max: 16).ToLowerInvariant();
        if (decision is not ("accepted" or "rejected")) throw Invalid("decision must be 'accepted' or 'rejected'.");
        var result = await DecideIntentAsync(conversationId, intentId,
            new TinaChatIntentDecisionRequest(actor.Id, decision, Long(call.Arguments, "expected_revision")), ct);
        var conversation = await db.Conversations.SingleAsync(x => x.Id == conversationId, ct);
        return new
        {
            status = result.Status,
            intent_id = result.Id.ToString("N"),
            conversation_revision = conversation.Revision,
            note = decision == "accepted"
                ? "This brief is now the conversation's accepted intent; earlier versions are superseded. Execution still needs tina_chat_execute_intent."
                : "The brief is rejected. Raise the objection in the room with tina_chat_send and propose a revision that resolves it.",
        };
    }


    /// <summary>
    /// Handle names to audience ids, refusing loudly on anybody the room cannot hear. Returns null
    /// when nothing was named so the caller keeps its own default (the room's active members, still
    /// narrowed per recipient downstream).
    /// </summary>
    private static async Task<Guid[]?> ResolveAudienceAsync(TinaChatDbContext db, TenantContext scope, ChatParticipant actor,
        Guid conversationId, string[] handles, CancellationToken ct)
    {
        if (handles.Length == 0) return null;
        var ids = new List<Guid>();
        foreach (var raw in handles)
        {
            var handle = raw.Trim().TrimStart('@').ToLowerInvariant();
            if (handle.Length == 0) continue;
            var participant = await db.Participants.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.Handle == handle, ct)
                ?? throw new TinaChatException(404, "tina_chat_not_found", $"No participant '@{handle}' exists in this tenant. Find real handles with tina_chat_search_people.");
            var member = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.ParticipantId == participant.Id && x.Status == "active", ct);
            if (member is null) throw Forbidden($"'@{handle}' is not an active member of this conversation, so it cannot be addressed here.");
            if (participant.Status != "active") throw Forbidden($"'@{handle}' is archived and cannot receive messages.");
            ids.Add(participant.Id);
        }
        if (ids.Count == 0) return null;
        return ids.Append(actor.Id).Distinct().ToArray();
    }

    /// <summary>The members entitled to the brief's sources, computed by the same rule the save applies.</summary>
    private async Task<Guid[]> InterpretableAudienceAsync(TinaChatDbContext db, TenantContext scope, ChatParticipant actor,
        Guid conversationId, Guid[] sources, CancellationToken ct)
    {
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == scope.TenantId, ct) ?? throw Missing();
        var messages = await SourcesAsync(db, conversation, actor, sources, ct);
        return (await RecipientsAsync(db, scope, conversation, actor, null, messages, derived: true, ct)).Select(x => x.Id).ToArray();
    }

    /// <summary>The participant this session speaks as, re-checked on every call.</summary>
    private static async Task<ChatParticipant> ActorAsync(TinaChatDbContext db, TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        var participantId = (await BindingAsync(db, call.SessionId, ct))?.ParticipantId
            ?? await db.Executions.Where(x => x.SessionId == call.SessionId).Select(x => (Guid?)x.ParticipantId).FirstOrDefaultAsync(ct);
        if (participantId is null)
            throw Invalid("This session has no chat identity yet. Call tina_chat_bind with a handle you own before reading or speaking in a conversation.");
        var participant = await ParticipantAsync(db, scope.TenantId, participantId.Value, ct);
        if (participant.Status != "active") throw Forbidden("The bound participant is archived and cannot speak.");
        if (participant.OwnerPrincipalId != scope.PrincipalId) throw Forbidden("The bound participant is no longer controlled by this run's principal.");
        return participant;
    }

    private static async Task<ChatSessionIdentity?> BindingAsync(TinaChatDbContext db, Guid sessionId, CancellationToken ct) =>
        await db.SessionIdentities.SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);

    private static string ToolKey(TinaChatToolCall call) => "tool:" + (call.ToolCallId == 0 ? Guid.NewGuid().ToString("N") : call.ToolCallId.ToString());
    private static Guid[] Identifiers(JsonElement? args, string field) => List(args, field).Select(Parse).ToArray();

    private static Guid? OptionalIdentifier(JsonElement? args, string field)
    {
        var raw = Text(args, field, required: false);
        return raw.Length == 0 ? null : Parse(raw);
    }

    private static Guid Identifier(JsonElement? args, string field) => Parse(Text(args, field));

    /// <summary>Accepts either spelling of an identifier the model was handed: dashed, or the 32-character form a tool result returns.</summary>
    private static Guid Parse(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 32 && value.All(c => Uri.IsHexDigit(c)))
            value = value.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id : throw Invalid($"'{raw}' is not an identifier from an earlier tool result.");
    }

    private static string Text(JsonElement? args, string field, bool required = true, int max = 8192)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw Invalid($"{field} is required.");
            return "";
        }
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"{field} must be a string.");
        var text = (value.GetString() ?? "").Trim();
        if (required && text.Length == 0) throw Invalid($"{field} must not be empty.");
        if (text.Length > max) throw Invalid($"{field} must be at most {max} characters.");
        return text;
    }

    private static string[] List(JsonElement? args, string field, int max = 64)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array) throw Invalid($"{field} must be an array of strings.");
        var items = value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "").Trim() : "")
            .Where(x => x.Length != 0).Distinct().ToArray();
        if (items.Length > max) throw Invalid($"{field} accepts at most {max} entries.");
        return items;
    }

    private static bool Bool(JsonElement? args, string field) =>
        args is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A required counter the model was handed earlier, accepted as a JSON number or its string form.</summary>
    private static long Long(JsonElement? args, string field)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(field, out var value))
            throw Invalid($"{field} is required. Read it from tina_chat_list_intents before deciding.");
        var raw = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? "",
            _ => throw Invalid($"{field} must be the conversation revision you read, as a number."),
        };
        return long.TryParse(raw, out var parsed)
            ? parsed : throw Invalid($"'{raw}' is not a conversation revision.");
    }

    private static string Sensitivity(JsonElement? args)
    {
        var value = Text(args, "sensitivity", required: false);
        return value is "confidential" or "normal" ? value : "normal";
    }

    private static string Clip(string content) => content.Length <= ToolContentCeiling ? content : content[..ToolContentCeiling] + "…";
}
