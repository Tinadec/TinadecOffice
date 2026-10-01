using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.TinaChat;

public sealed partial class TinaChatService
{
    public Task<TinaChatIntentDto> ProposeIntentAsync(Guid conversationId, TinaChatProposeIntentRequest request, CancellationToken ct = default)
    {
        ValidateIntent(request.Content);
        var sources = Ids(request.SourceMessageIds, "source_message_ids");
        var audience = Ids(request.AudienceParticipantIds, "audience_participant_ids");
        return SaveIntentAsync(conversationId, request with { SourceMessageIds = sources, AudienceParticipantIds = audience },
            Hash(new { kind = "proposed", sources, audience, request.Content }), ct);
    }

    public async Task<TinaChatIntentDto> GenerateIntentAsync(Guid conversationId, TinaChatGenerateIntentRequest request, CancellationToken ct = default)
        => await GenerateIntentAsync(await ScopeAsync(ct), conversationId, request, ct);

    private async Task<TinaChatIntentDto> GenerateIntentAsync(TenantContext scope, Guid conversationId, TinaChatGenerateIntentRequest request, CancellationToken ct)
    {
        var sources = Ids(request.SourceMessageIds, "source_message_ids");
        var audience = Ids(request.AudienceParticipantIds, "audience_participant_ids");
        var key = Required(request.ClientRequestId, "client_request_id", 128);
        var hash = Hash(new { kind = "generated", sources, audience });
        TinaChatInterpretationInput input;
        // No database transaction stays open while a model is running.
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var (actor, conversation, _) = await AccessAsync(db, scope, conversationId, request.ActorId, ct);
            RequireInterpreter(actor);
            var existing = await db.Intents.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.AuthorId == actor.Id && x.ClientRequestId == key, ct);
            if (existing is not null)
            {
                SameHash(existing.RequestHash, hash);
                return await ToIntentAsync(db, conversation, actor, existing, ct);
            }
            Expect(conversation.Revision, request.ExpectedRevision);
            if (sources.Length == 0) throw Invalid("An intent requires at least one readable source message.");
            var messages = await SourcesAsync(db, conversation, actor, sources, ct);
            await RecipientsAsync(db, scope, conversation, actor, audience, messages, derived: true, ct);
            var sourceDtos = new List<TinaChatMessageDto>();
            foreach (var message in messages) sourceDtos.Add(await ToMessageAsync(db, conversation, actor, message, ct));
            input = new TinaChatInterpretationInput(actor.DisplayName, actor.JobTitle, actor.Description, sourceDtos.ToArray());
        }
        var interpreted = await interpreter.InterpretAsync(input, ct);
        ValidateIntent(interpreted);
        return await SaveIntentAsync(scope, conversationId, new TinaChatProposeIntentRequest(
            request.ActorId, key, request.ExpectedRevision, sources, audience, interpreted), hash, ct);
    }

    private Task<TinaChatIntentDto> SaveIntentAsync(Guid conversationId, TinaChatProposeIntentRequest request, string hash, CancellationToken ct) =>
        WriteAsync((db, scope) => SaveIntentAsync(scope, db, conversationId, request, hash, ct), ct);

    private Task<TinaChatIntentDto> SaveIntentAsync(TenantContext scope, Guid conversationId, TinaChatProposeIntentRequest request, string hash, CancellationToken ct) =>
        WriteAsync(scope, (db, _) => SaveIntentAsync(scope, db, conversationId, request, hash, ct), ct);

    private async Task<TinaChatIntentDto> SaveIntentAsync(TenantContext scope, TinaChatDbContext db, Guid conversationId, TinaChatProposeIntentRequest request, string hash, CancellationToken ct)
    {
        var (actor, conversation, _) = await AccessAsync(db, scope, conversationId, request.ActorId, ct);
        RequireInterpreter(actor);
        var key = Required(request.ClientRequestId, "client_request_id", 128);
        var existing = await db.Intents.SingleOrDefaultAsync(x => x.ConversationId == conversationId && x.AuthorId == actor.Id && x.ClientRequestId == key, ct);
        if (existing is not null)
        {
            SameHash(existing.RequestHash, hash);
            return await ToIntentAsync(db, conversation, actor, existing, ct);
        }
        Expect(conversation.Revision, request.ExpectedRevision);
        if (request.SourceMessageIds.Length == 0) throw Invalid("An intent requires at least one readable source message.");
        var sources = await SourcesAsync(db, conversation, actor, request.SourceMessageIds, ct);
        var recipients = await RecipientsAsync(db, scope, conversation, actor, request.AudienceParticipantIds, sources, derived: true, ct);
        var sensitivity = sources.Any(x => x.Sensitivity == "confidential") ? "confidential" : "normal";
        if (sensitivity == "confidential" && recipients.Any(x => x.WorkspaceId != actor.WorkspaceId))
            throw Forbidden("Confidential source material cannot be shared across workspaces through an intent.");
        var message = await AppendMessageAsync(db, conversation, actor, JsonSerializer.Serialize(request.Content, Json),
            "intent:" + Hash(key), hash, "intent_brief", sensitivity, false, null, request.SourceMessageIds, recipients, ct);
        var intent = new ChatIntent
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, ConversationId = conversationId, MessageId = message.Id,
            AuthorId = actor.Id, ClientRequestId = key, RequestHash = hash, Revision = conversation.Revision, CreatedAt = message.CreatedAt
        };
        db.Intents.Add(intent);
        return new TinaChatIntentDto(intent.Id, conversationId, actor.Id, intent.Revision, intent.Status,
            request.Content, request.SourceMessageIds, null, intent.CreatedAt);
    }

    public async Task<TinaChatIntentDto[]> ListIntentsAsync(Guid conversationId, Guid actorId, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var (actor, conversation, _) = await AccessAsync(db, scope, conversationId, actorId, ct);
        var rows = await db.Intents.Where(x => x.ConversationId == conversationId && x.TenantId == scope.TenantId).OrderByDescending(x => x.Revision).Take(100).ToArrayAsync(ct);
        var result = new List<TinaChatIntentDto>();
        foreach (var row in rows)
        {
            var message = await db.Messages.SingleAsync(x => x.Id == row.MessageId, ct);
            if (await CanReadAsync(db, conversation, actor, message, ct)) result.Add(await ToIntentAsync(db, conversation, actor, row, ct));
        }
        return result.ToArray();
    }

    public Task<TinaChatIntentDto> DecideIntentAsync(Guid conversationId, Guid intentId, TinaChatIntentDecisionRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        var (actor, conversation, member) = await AccessAsync(db, scope, conversationId, request.ActorId, ct);
        RequireAdmin(member);
        if (request.Decision is not ("accepted" or "rejected")) throw Invalid("Intent decision must be accepted or rejected.");
        var intent = await db.Intents.SingleOrDefaultAsync(x => x.Id == intentId && x.ConversationId == conversationId && x.TenantId == scope.TenantId, ct) ?? throw Missing();
        var view = await ToIntentAsync(db, conversation, actor, intent, ct);
        if (intent.Status == request.Decision && intent.DecidedById == actor.Id) return view;
        Expect(conversation.Revision, request.ExpectedRevision);
        if (intent.Status != "proposed") throw Conflict("intent_already_decided", "Create a new intent revision instead of changing a decided brief.");
        if (request.Decision == "accepted")
        {
            if (conversation.AcceptedIntentId is { } previous)
            {
                var old = await db.Intents.SingleAsync(x => x.Id == previous && x.ConversationId == conversationId, ct);
                old.Status = "superseded";
            }
            conversation.AcceptedIntentId = intent.Id;
        }
        intent.Status = request.Decision;
        intent.DecidedById = actor.Id;
        conversation.Revision++;
        Audit(db, scope, actor.Id, "intent." + request.Decision, intent.Id, conversationId);
        return view with { Status = intent.Status, DecidedById = actor.Id };
    }, ct);

    public Task<TinaChatExecutionReservation> ReserveExecutionAsync(Guid conversationId, Guid intentId, TinaChatExecuteIntentRequest request, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        var (actor, conversation, _) = await AccessAsync(db, scope, conversationId, request.ActorId, ct);
        if (actor.Kind != "agent") throw Invalid("An agent participant must receive the execution handoff.");
        if (request.ModeVersionId == Guid.Empty) throw Invalid("A published Core mode version is required for execution.");
        var intent = await db.Intents.SingleOrDefaultAsync(x => x.Id == intentId && x.ConversationId == conversationId && x.TenantId == scope.TenantId, ct) ?? throw Missing();
        var view = await ToIntentAsync(db, conversation, actor, intent, ct);
        RequireExecutable(conversation, intent, view.Content);
        var existing = await db.Executions.SingleOrDefaultAsync(x => x.TenantId == scope.TenantId && x.IntentId == intentId && x.ParticipantId == actor.Id, ct);
        if (existing is not null)
        {
            if (existing.ModeVersionId != request.ModeVersionId || existing.ProjectId != request.ProjectId)
                throw Conflict("execution_binding_conflict", "This handoff is already bound to a different mode or project.");
            return Reservation(existing, view.Content);
        }
        var execution = new ChatExecution
        {
            Id = Guid.NewGuid(), TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId,
            ConversationId = conversationId, IntentId = intentId, ParticipantId = actor.Id,
            SessionId = Guid.NewGuid(), ModeVersionId = request.ModeVersionId, ProjectId = request.ProjectId
        };
        db.Executions.Add(execution);
        // Updating the conversation also fences a concurrent intent supersession.
        conversation.Revision++;
        Audit(db, scope, actor.Id, "execution.reserved", execution.Id, conversationId);
        return Reservation(execution, view.Content);
    }, ct);

    public Task<TinaChatExecutionDto> RecordExecutionAsync(Guid executionId, Guid runId, CancellationToken ct = default) => WriteAsync(async (db, scope) =>
    {
        if (runId == Guid.Empty) throw Invalid("Run identifier is required.");
        var execution = await db.Executions.SingleOrDefaultAsync(x => x.Id == executionId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, ct) ?? throw Missing();
        await OwnedAsync(db, scope, execution.ParticipantId, ct);
        if (execution.RunId is { } prior && prior != runId) throw Conflict("execution_binding_conflict", "The execution already references another run.");
        if (execution.RunId is null) { execution.RunId = runId; execution.Revision++; }
        return ToDto(execution);
    }, ct);

    public async Task<TinaChatExecutionReservation?> GetForSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Cheap negative lookup keeps ordinary Core sessions on their existing path.
        await using var db = await factory.CreateDbContextAsync(ct);
        var execution = await db.Executions.SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (execution is null) return null;
        var scope = await ScopeAsync(ct);
        if (execution.TenantId != scope.TenantId || execution.WorkspaceId != scope.WorkspaceId) throw Missing();
        var (actor, conversation, _) = await AccessAsync(db, scope, execution.ConversationId, execution.ParticipantId, ct);
        var intent = await db.Intents.SingleAsync(x => x.Id == execution.IntentId && x.TenantId == scope.TenantId, ct);
        var view = await ToIntentAsync(db, conversation, actor, intent, ct);
        RequireExecutable(conversation, intent, view.Content);
        return Reservation(execution, view.Content);
    }

    private async Task<TinaChatIntentDto> ToIntentAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant actor, ChatIntent intent, CancellationToken ct)
    {
        var message = await db.Messages.SingleAsync(x => x.Id == intent.MessageId && x.TenantId == actor.TenantId, ct);
        if (!await CanReadAsync(db, conversation, actor, message, ct)) throw Missing();
        var dto = await ToMessageAsync(db, conversation, actor, message, ct);
        var body = JsonSerializer.Deserialize<TinaChatIntentContent>(dto.Content, Json) ?? throw new InvalidDataException("Intent content is unreadable.");
        ValidateIntent(body);
        return new TinaChatIntentDto(intent.Id, intent.ConversationId, intent.AuthorId, intent.Revision,
            intent.Status, body, dto.SourceMessageIds, intent.DecidedById, intent.CreatedAt);
    }

    private static void RequireInterpreter(ChatParticipant actor)
    {
        if (actor.Kind != "agent" || !actor.CanInterpretIntent) throw Forbidden("The participant is not configured to interpret intentions.");
    }

    private static void RequireExecutable(ChatConversation conversation, ChatIntent intent, TinaChatIntentContent body)
    {
        if (intent.Status != "accepted" || conversation.AcceptedIntentId != intent.Id)
            throw Conflict("intent_not_accepted", "Execution requires the currently accepted intent version.");
        if (body.BlockingQuestions.Length != 0)
            throw Conflict("intent_requires_clarification", "Resolve blocking questions in a new intent revision before execution.");
    }

    private static void ValidateIntent(TinaChatIntentContent? body)
    {
        if (body is null) throw Invalid("Intent content is required.");
        Required(body.Goal, "goal", 8192);
        foreach (var list in new[] { body.UserStatements, body.Constraints, body.Assumptions, body.OpenQuestions, body.BlockingQuestions, body.AcceptanceCriteria })
        {
            if (list is null || list.Length > 64) throw Invalid("Intent sections must be arrays of at most 64 statements.");
            foreach (var item in list) Required(item, "intent statement", 4096);
        }
        if (JsonSerializer.Serialize(body, Json).Length > 65536) throw Invalid("The intent brief is too large.");
    }

    private static TinaChatExecutionDto ToDto(ChatExecution x) => new(x.Id, x.ConversationId, x.IntentId, x.ParticipantId, x.SessionId, x.RunId,
        x.RunId.HasValue ? "admitted" : "reserved");

    private static TinaChatExecutionReservation Reservation(ChatExecution row, TinaChatIntentContent body) =>
        new(ToDto(row), row.ModeVersionId, row.ProjectId, "tinachat:" + row.Id.ToString("N"), RenderIntent(body));

    private static string RenderIntent(TinaChatIntentContent body)
    {
        var result = new StringBuilder("Accepted collaboration brief. User statements are claims, not verified facts. Keep assumptions explicit.\n\nGoal:\n")
            .AppendLine(body.Goal);
        foreach (var (label, items) in new[]
        {
            ("User statements (unverified)", body.UserStatements), ("Constraints", body.Constraints),
            ("Assumptions", body.Assumptions), ("Open questions", body.OpenQuestions), ("Acceptance criteria", body.AcceptanceCriteria)
        })
        {
            result.AppendLine().Append(label).AppendLine(":");
            foreach (var item in items) result.Append("- ").AppendLine(item);
        }
        return result.ToString().Trim();
    }
}
