using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.TinaChat;

/// <summary>
/// The organization tools (architecture §9.1): one implementation, two callers. An in-run agent calls
/// them through the tool gateway and acts as the member its instance was enrolled as; a standing
/// member calls them from its own turn through <see cref="MemberToolbox"/>. Either way every read goes
/// through the organization's visibility rule and every write through the one message path, so a
/// tool can never reach something the member is not entitled to.
///
/// Errors are sentences a model can act on: who to address instead, what to ask for, which tool to
/// read first. A bare refusal only makes a model repeat the call.
/// </summary>
public sealed partial class TinaChatService
{
    private const int OrganizationPageLimit = 20;
    private const int OrganizationDirectoryLimit = 100;
    private const int OrganizationRoomLimit = 50;

    private static readonly string[] ReportKinds = ["conflict", "risk", "drift", "budget", "progress"];
    private static readonly string[] ReportSeverities = ["info", "warning", "blocking"];
    private static readonly string[] SubjectKinds = ["run", "task", "lease", "approval", "member", "path"];
    private static readonly string[] ProposedVerbs = ["wait", "serialize", "reassign", "separate_worktree", "narrow_scope", "pause_run", "resume_run", "stop_run", "assign_environment", "ask_user"];

    /// <summary>Gateway entry: the calling instance's own member acts.</summary>
    private async Task<object> OrganizationToolAsync(TenantContext scope, TinaChatToolCall call, CancellationToken ct)
    {
        if (call.AgentInstanceId is not { } instanceId || instanceId == Guid.Empty)
            throw Invalid("Organization tools act as the calling agent instance, and this call carries none.");
        Guid organizationId;
        Guid actorId;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var binding = await db.InstanceBindings.AsNoTracking().SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct)
                ?? throw Invalid("This agent is not a member of the session's organization yet, so it has nobody to speak as. Members are enrolled when their task starts; carry on with your task and report through your result instead.");
            var org = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == binding.OrganizationId, ct);
            if (org.TenantId != scope.TenantId || org.SessionId != call.SessionId)
                throw Forbidden("The calling instance belongs to another session's organization.");
            if (org.OwnerPrincipalId != scope.PrincipalId)
                throw Forbidden("This run's principal does not own the session's organization.");
            organizationId = org.Id;
            actorId = binding.ParticipantId;
        }
        return await RunOrganizationToolAsync(scope, organizationId, actorId, call.ToolId, call.Arguments, ToolKey(call), ct);
    }

    private async Task<object> RunOrganizationToolAsync(TenantContext scope, Guid organizationId, Guid actorId, string toolId,
        JsonElement? args, string key, CancellationToken ct) => toolId.Trim().ToLowerInvariant() switch
    {
        "org_directory" => await DirectoryToolAsync(organizationId, actorId, args, ct),
        "org_read" => await ReadToolAsync(organizationId, actorId, args, ct),
        "org_send" => await SendOrganizationToolAsync(scope, organizationId, actorId, args, key, ct),
        "org_report" => await ReportToolAsync(scope, organizationId, actorId, args, key, ct),
        "org_decide_report" => await DecideReportToolAsync(scope, organizationId, actorId, args, ct),
        "org_execute_report" => await ExecuteReportToolAsync(scope, organizationId, actorId, args, ct),
        "org_contact" => await ContactToolAsync(scope, organizationId, actorId, args, ct),
        "org_room" => await RoomToolAsync(scope, organizationId, actorId, args, key, ct),
        _ => throw Invalid($"Unknown organization tool '{toolId}'."),
    };

    // ── reads ────────────────────────────────────────────────────────────────

    private async Task<object> DirectoryToolAsync(Guid organizationId, Guid actorId, JsonElement? args, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (org, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
        var role = Text(args, "role", required: false, max: 32).ToLowerInvariant();
        var query = Text(args, "query", required: false, max: 128).ToLowerInvariant();
        var members = db.Participants.AsNoTracking().Where(x => x.OrganizationId == org.Id && x.OrgRole != OrganizationRoles.Host);
        if (role.Length > 0) members = members.Where(x => x.OrgRole == role);
        if (query.Length > 0) members = members.Where(x => x.DisplayName.ToLower().Contains(query) || (x.AgentSlug != null && x.AgentSlug.ToLower().Contains(query)));
        var rows = await members.OrderBy(x => x.Presence == Online ? 0 : 1).ThenBy(x => x.OrgRole).ThenBy(x => x.DisplayName)
            .Take(OrganizationDirectoryLimit + 1).ToArrayAsync(ct);
        var contacts = await db.Contacts.AsNoTracking().Where(x => x.OrganizationId == org.Id && (x.OwnerId == actor.Id || x.ContactId == actor.Id))
            .Take(500).ToArrayAsync(ct);
        var names = await NamesAsync(db, org.Id, rows.Select(x => x.ParentParticipantId ?? Guid.Empty)
            .Concat(contacts.Select(x => x.OwnerId)).Concat(contacts.Select(x => x.ContactId)), ct);
        bool Explicit(Guid other) => contacts.Any(x => x.Status == "active" && (x.OwnerId == other || x.ContactId == other));
        var rooms = await ReadableRoomsAsync(db, org, actor, ct);
        return new
        {
            you = new { address = actor.DisplayName, role = actor.OrgRole },
            members = rows.Take(OrganizationDirectoryLimit).Select(x => new
            {
                address = x.DisplayName,
                role = x.OrgRole,
                presence = x.Presence ?? Offline,
                agent = x.AgentSlug,
                dispatched_by = x.ParentParticipantId is { } parent ? names.GetValueOrDefault(parent) : null,
                is_you = x.Id == actor.Id,
                is_contact = x.Id != actor.Id && (DerivedContact(actor, x) || Explicit(x.Id)),
            }).ToArray(),
            members_truncated = rows.Length > OrganizationDirectoryLimit,
            rooms = rooms.Select(x => new
            {
                room_id = x.Room.Id.ToString("N"), kind = x.Room.Kind, title = x.Room.Title,
                last_sequence = x.Room.LastSequence, you_are_member = x.IsMember,
                you_may_post = x.IsMember && (x.Room.Kind != "board" || OrganizationRoles.MayPostOnBoard(actor.OrgRole)),
            }).ToArray(),
            contact_requests = new
            {
                waiting_for_you = contacts.Where(x => x.Status == "requested" && x.ContactId == actor.Id).Select(x => names.GetValueOrDefault(x.OwnerId)).ToArray(),
                you_asked = contacts.Where(x => x.Status == "requested" && x.OwnerId == actor.Id).Select(x => names.GetValueOrDefault(x.ContactId)).ToArray(),
            },
            note = org.Status == "active" ? null : "This session is archived: everything is readable, nothing can be posted.",
        };
    }

    private async Task<object> ReadToolAsync(Guid organizationId, Guid actorId, JsonElement? args, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (org, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
        var roomArg = Text(args, "room", required: false, max: 64);
        var with = Text(args, "with", required: false, max: 160);
        var after = OptionalLong(args, "after_sequence") ?? 0;
        if (after < 0) throw Invalid("after_sequence must not be negative.");
        var kinds = List(args, "kinds", max: 3).Select(x => x.ToLowerInvariant()).ToArray();
        if (string.Equals(roomArg, "inbox", StringComparison.OrdinalIgnoreCase))
            return await InboxToolAsync(db, org, actor, after, kinds, ct);

        ChatConversation conversation;
        if (with.Length > 0)
        {
            var other = await ResolveAddressAsync(db, org, actor, with, ct);
            var existing = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == org.TenantId
                && x.CreatorId == org.HostParticipantId && x.ClientRequestId == DirectKey(org, actor, other), ct);
            if (existing is null)
                return new { room = (string?)null, messages = Array.Empty<object>(), next_cursor = after, note = $"You have no direct chat with '{other.DisplayName}' yet." };
            conversation = existing;
        }
        else
        {
            conversation = await ResolveRoomAsync(db, org, actor, roomArg.Length == 0 ? await DefaultRoomAsync(db, org, actor, ct) : roomArg, ct);
        }
        var isMember = await IsActiveMemberAsync(db, conversation.Id, actor.Id, ct);
        if (!CanSeeRoom(conversation, actor, isMember))
            throw Forbidden($"'{conversation.Title}' is not a room you can read. Your rooms are listed by org_directory; ask one of its members if you need what is said there.");
        var query = db.Messages.AsNoTracking().Where(x => x.ConversationId == conversation.Id && x.Sequence > after);
        if (kinds.Length > 0) query = query.Where(x => kinds.Contains(x.Kind));
        var rows = await query.OrderBy(x => x.Sequence).Take(OrganizationPageLimit * 2).ToArrayAsync(ct);
        var page = new List<ChatMessage>();
        var cursor = after;
        foreach (var row in rows)
        {
            cursor = row.Sequence;
            if (await CanReadOrganizationMessageAsync(db, actor, row, ct)) page.Add(row);
            if (page.Count == OrganizationPageLimit) break;
        }
        return new
        {
            room = new { room_id = conversation.Id.ToString("N"), kind = conversation.Kind, title = conversation.Title },
            messages = await ProjectMessagesAsync(db, org, page, ct),
            next_cursor = cursor,
            more = rows.Length > page.Count && cursor < conversation.LastSequence,
        };
    }

    private async Task<object> InboxToolAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor, long after, string[] kinds, CancellationToken ct)
    {
        var query = from audience in db.Audiences.AsNoTracking()
                    join message in db.Messages.AsNoTracking() on audience.MessageId equals message.Id
                    join conversation in db.Conversations.AsNoTracking() on message.ConversationId equals conversation.Id
                    where audience.ParticipantId == actor.Id && audience.CanReadOriginal && audience.Id > after
                        && conversation.OrganizationId == org.Id && message.SenderId != actor.Id
                    orderby audience.Id
                    select new { AudienceId = audience.Id, Message = message };
        var rows = await query.Take(OrganizationPageLimit).ToArrayAsync(ct);
        var messages = rows.Select(x => x.Message).Where(x => kinds.Length == 0 || kinds.Contains(x.Kind)).ToList();
        return new
        {
            room = "inbox",
            messages = await ProjectMessagesAsync(db, org, messages, ct),
            next_cursor = rows.Length == 0 ? after : rows[^1].AudienceId,
            note = "The inbox holds what was addressed to you: direct messages, notices, mentions, and warning or blocking reports. Rooms are read with org_read room=...",
        };
    }

    private async Task<object[]> ProjectMessagesAsync(TinaChatDbContext db, ChatOrganization org, IReadOnlyList<ChatMessage> rows, CancellationToken ct)
    {
        var names = await NamesAsync(db, org.Id, rows.Select(x => x.SenderId), ct);
        var roles = await db.Participants.AsNoTracking().Where(x => x.OrganizationId == org.Id && rows.Select(r => r.SenderId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.OrgRole, ct);
        var reportIds = rows.Where(x => x.Kind == "report").Select(x => x.Id).ToArray();
        var reports = reportIds.Length == 0
            ? new Dictionary<Guid, ChatReport>()
            : await db.Reports.AsNoTracking().Where(x => reportIds.Contains(x.MessageId)).ToDictionaryAsync(x => x.MessageId, ct);
        var result = new List<object>();
        foreach (var row in rows)
        {
            reports.TryGetValue(row.Id, out var report);
            result.Add(new
            {
                message_id = row.Id.ToString("N"),
                room_id = row.ConversationId.ToString("N"),
                sequence = row.Sequence,
                from = names.GetValueOrDefault(row.SenderId) ?? "unknown",
                from_role = roles.GetValueOrDefault(row.SenderId),
                kind = row.Kind,
                content = Clip(await ReadBodyAsync(row, ct)),
                reply_to_message_id = row.ReplyToMessageId?.ToString("N"),
                report = report is null ? null : new
                {
                    report_id = report.MessageId.ToString("N"), kind = report.ReportKind, severity = report.Severity, status = report.Status,
                    subject_kind = report.SubjectKind, subject_id = report.SubjectId, proposed_verb = report.ProposedVerb,
                    revision = report.Revision, decision_note = report.DecisionNote,
                },
                at = row.CreatedAt,
            });
        }
        return result.ToArray();
    }

    // ── writes ───────────────────────────────────────────────────────────────

    private Task<object> SendOrganizationToolAsync(TenantContext scope, Guid organizationId, Guid actorId, JsonElement? args, string key, CancellationToken ct)
    {
        var content = Text(args, "content", max: 16384);
        var to = Text(args, "to", required: false, max: 160);
        var roomArg = Text(args, "room", required: false, max: 64);
        var mentions = List(args, "mention", max: 16);
        var replyTo = OptionalIdentifier(args, "reply_to_message_id");
        if (to.Length > 0 && roomArg.Length > 0) throw Invalid("Pass either room or to, not both.");
        return WriteAsync(scope, async (db, _) =>
        {
            var (org, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
            RequireWritable(org);
            ChatConversation conversation;
            var recipients = new List<ChatParticipant> { actor };
            if (to.Length > 0)
            {
                var target = await ResolveAddressAsync(db, org, actor, to, ct);
                await RequireDirectReachableAsync(db, actor, target, ct);
                conversation = await EnsureDirectAsync(db, org, actor, target, ct);
                recipients.Add(target);
            }
            else
            {
                conversation = await ResolveRoomAsync(db, org, actor, roomArg.Length == 0 ? await DefaultRoomAsync(db, org, actor, ct) : roomArg, ct);
                await RequirePostableAsync(db, conversation, actor, ct);
                recipients.AddRange(await MentionedAsync(db, org, actor, conversation, mentions, ct));
            }
            var messageKey = "org:" + key;
            var existing = await db.Messages.SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.SenderId == actor.Id && x.ClientMessageId == messageKey, ct);
            if (existing is not null)
                return (object)new { status = "sent", message_id = existing.Id.ToString("N"), room_id = conversation.Id.ToString("N"), sequence = existing.Sequence, replayed = true };
            if (replyTo is { } reply && !await db.Messages.AnyAsync(x => x.Id == reply && x.ConversationId == conversation.Id, ct))
                throw Invalid("reply_to_message_id must be a message in the same room or chat.");
            var hash = Hash(new { content, conversation.Id, recipients = recipients.Select(x => x.Id).Order().ToArray(), replyTo });
            var row = await AppendMessageAsync(db, conversation, actor, content, messageKey, hash, "message", "normal",
                allowDerivedSharing: false, replyTo, sources: [], recipients: recipients.DistinctBy(x => x.Id).ToArray(), ct);
            return new
            {
                status = "sent", message_id = row.Id.ToString("N"), room_id = conversation.Id.ToString("N"), sequence = row.Sequence,
                note = "Delivered. Any reply is somebody else's later turn; read it with org_read before relying on it.",
            };
        }, ct);
    }

    private async Task<object> ReportToolAsync(TenantContext scope, Guid organizationId, Guid actorId, JsonElement? args, string key, CancellationToken ct)
    {
        EvidenceEntry? archived = null;
        var outcome = await FileReportAsync(scope, organizationId, actorId, args, key, entry => archived = entry, ct);
        // Kept verbatim in the session's evidence archive once committed, so it can be recalled after the
        // room has scrolled on. Best-effort: a report that could not be archived is still filed.
        if (archived is not null && services?.GetService(typeof(IEvidenceArchive)) is IEvidenceArchive archive)
        {
            try { await archive.AppendAsync(archived, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        return outcome;
    }

    private Task<object> FileReportAsync(TenantContext scope, Guid organizationId, Guid actorId, JsonElement? args, string key,
        Action<EvidenceEntry> archive, CancellationToken ct)
    {
        var kind = OneOf(args, "kind", ReportKinds, required: true)!;
        var severity = OneOf(args, "severity", ReportSeverities, required: true)!;
        var subjectKind = OneOf(args, "subject_kind", SubjectKinds, required: false);
        var subjectId = Text(args, "subject_id", required: false, max: 512);
        if ((subjectKind is null) != (subjectId.Length == 0))
            throw Invalid("subject_kind and subject_id go together: name what the report is about (ids come from graph_view), or omit both.");
        var finding = Text(args, "finding", max: 4000);
        var evidence = List(args, "evidence", max: 20).Select(x => x.Length > 500 ? x[..500] : x).ToArray();
        var verb = Text(args, "proposed_verb", required: false, max: 32).ToLowerInvariant();
        if (verb is "none") verb = "";
        if (verb.Length > 0 && !ProposedVerbs.Contains(verb))
            throw Invalid($"proposed_verb must be one of: none, {string.Join(", ", ProposedVerbs)}.");
        var proposedArgs = Text(args, "proposed_args", required: false, max: 1000);
        var roomArg = Text(args, "room", required: false, max: 64);
        var supersedes = OptionalIdentifier(args, "supersedes_report_id");
        return WriteAsync(scope, async (db, _) =>
        {
            var (org, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
            RequireWritable(org);
            var conversation = await ResolveRoomAsync(db, org, actor, roomArg.Length == 0 ? await DefaultReportRoomAsync(db, org, actor, ct) : roomArg, ct);
            if (conversation.Kind == "direct") throw Invalid("Reports go into a room (lobby, plan or an ad-hoc room), where the people who act on them read.");
            await RequirePostableAsync(db, conversation, actor, ct);
            var messageKey = "org-report:" + key;
            var existing = await db.Messages.SingleOrDefaultAsync(x => x.ConversationId == conversation.Id && x.SenderId == actor.Id && x.ClientMessageId == messageKey, ct);
            if (existing is not null)
            {
                var filed = await db.Reports.SingleAsync(x => x.MessageId == existing.Id, ct);
                return (object)new { status = "filed", report_id = filed.MessageId.ToString("N"), room_id = conversation.Id.ToString("N"), revision = filed.Revision, replayed = true };
            }
            // Warning and blocking reports reach the people who can act on them through their inbox;
            // an informational one stays in the room for whoever reads it.
            var recipients = new List<ChatParticipant> { actor };
            if (severity is "warning" or "blocking")
                recipients.AddRange(await db.Participants.Where(x => x.OrganizationId == org.Id && x.Status == "active"
                    && (x.OrgRole == OrganizationRoles.Human || x.OrgRole == OrganizationRoles.Conversation)).ToArrayAsync(ct));
            var body = RenderReport(kind, severity, subjectKind, subjectId, finding, evidence, verb, proposedArgs);
            var hash = Hash(new { kind, severity, subjectKind, subjectId, finding, evidence, verb, proposedArgs, conversation.Id });
            var row = await AppendMessageAsync(db, conversation, actor, body, messageKey, hash, "report", "normal",
                allowDerivedSharing: false, replyTo: null, sources: [], recipients: recipients.DistinctBy(x => x.Id).ToArray(), ct);
            var now = DateTimeOffset.UtcNow;
            db.Reports.Add(new ChatReport
            {
                MessageId = row.Id, OrganizationId = org.Id, ConversationId = conversation.Id, AuthorId = actor.Id,
                ReportKind = kind, Severity = severity, Status = "open", SubjectKind = subjectKind,
                SubjectId = subjectId.Length == 0 ? null : subjectId, ProposedVerb = verb.Length == 0 ? null : verb,
                ProposedArgs = proposedArgs.Length == 0 ? null : proposedArgs, Finding = finding,
                EvidenceJson = JsonSerializer.Serialize(evidence, Json), SupersedesMessageId = supersedes, CreatedAt = now
            });
            if (supersedes is { } priorId)
            {
                var prior = await db.Reports.SingleOrDefaultAsync(x => x.MessageId == priorId && x.OrganizationId == org.Id, ct)
                    ?? throw Invalid("supersedes_report_id names no report in this organization.");
                if (prior.AuthorId != actor.Id && !OrganizationRoles.SeesWholeOrganization(actor.OrgRole))
                    throw Forbidden("Only the author of a report, or a role that sees the whole organization, may supersede it.");
                if (prior.Status == "open")
                {
                    prior.Status = "superseded"; prior.DecidedById = actor.Id; prior.DecidedAt = now; prior.Revision++;
                    prior.DecisionNote = $"Superseded by report {row.Id:N}.";
                }
            }
            Audit(db, new TenantContext(org.TenantId, org.WorkspaceId, org.OwnerPrincipalId, "tina-chat-organization"), actor.Id, "report.filed", row.Id, conversation.Id);
            archive(new EvidenceEntry(org.TenantId, org.WorkspaceId, org.SessionId, actor.CurrentRunId, null, EvidenceKinds.Report,
                $"[{severity}] {kind}: {(finding.Length > 160 ? finding[..160] + "…" : finding)}", actor.DisplayName, body, $"report:{row.Id:N}"));
            return new
            {
                status = "filed", report_id = row.Id.ToString("N"), room_id = conversation.Id.ToString("N"), sequence = row.Sequence, revision = 1,
                note = "Filed. It changes nothing by itself: whoever holds the proposed verb acts on it and records the decision with org_decide_report.",
            };
        }, ct);
    }

    private Task<object> DecideReportToolAsync(TenantContext scope, Guid organizationId, Guid actorId, JsonElement? args, CancellationToken ct)
    {
        var reportId = Identifier(args, "report_id");
        var decision = OneOf(args, "decision", ["acted", "dismissed"], required: true)!;
        var note = Text(args, "note", required: false, max: 2000);
        var expected = Long(args, "expected_revision");
        return WriteAsync(scope, async (db, _) =>
        {
            var (org, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
            RequireWritable(org);
            var report = await DecideReportCoreAsync(db, org, actor, reportId, decision, note, expected, ct);
            return (object)new { status = report.Status, report_id = report.MessageId.ToString("N"), revision = report.Revision };
        }, ct);
    }

    /// <summary>
    /// Executes a report proposal through the runtime's one action port. The
    /// action runs before the report is marked acted; a rejected or unsupported
    /// action leaves the report open so another governance node can continue it.
    /// </summary>
    private async Task<object> ExecuteReportToolAsync(TenantContext scope, Guid organizationId, Guid actorId, JsonElement? args, CancellationToken ct)
    {
        var reportId = Identifier(args, "report_id");
        var expected = Long(args, "expected_revision");
        string verb;
        string? subjectKind;
        string? subjectId;
        string? proposedArgs;
        ChatOrganization organization;
        ChatParticipant actor;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            (organization, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
            RequireWritable(organization);
            var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == reportId && x.OrganizationId == organization.Id, ct)
                ?? throw new TinaChatException(404, "tina_chat_not_found", "No report with that id in this organization. Take report ids from org_read.");
            if (!MayActOnReport(actor, report))
                throw Forbidden("Only the user, the conversation identity and the report's own author may execute a report proposal.");
            if (report.Status != "open")
                throw Conflict("report_closed", $"The report is already {report.Status}; a closed report is not reopened.");
            Expect(report.Revision, expected);
            verb = report.ProposedVerb ?? "";
            subjectKind = report.SubjectKind;
            subjectId = report.SubjectId;
            proposedArgs = report.ProposedArgs;
        }

        if (verb.Length == 0)
            throw Invalid("This report has no proposed action. Use org_decide_report to record a decision instead.");

        if (services?.GetService(typeof(IOrganizationReportActionExecutor)) is not IOrganizationReportActionExecutor executor)
            throw new TinaChatException(503, "organization_action_unavailable", "The runtime has no governance action executor; the report remains open.");

        var action = await executor.ExecuteAsync(new OrganizationReportActionRequest(
            scope, organization.SessionId, organization.Id, reportId, actor.Id, verb, proposedArgs,
            subjectKind, subjectId, $"report:{reportId:N}:revision:{expected}"), ct).ConfigureAwait(false);

        if (!action.Completed)
        {
            await RecordReportActionAuditAsync(scope, organizationId, actorId, reportId, "report.action." + verb + "." + action.Status, ct).ConfigureAwait(false);
            return new
            {
                status = action.Status,
                executed = false,
                code = action.Code,
                report_id = reportId.ToString("N"),
                revision = expected,
                target_run_id = action.TargetRunId?.ToString("N"),
                note = action.Message,
            };
        }

        try
        {
            var decided = await WriteAsync(scope, async (db, _) =>
            {
                var org = await db.Organizations.SingleAsync(x => x.Id == organizationId, ct);
                RequireWritable(org);
                var currentActor = await db.Participants.SingleAsync(x => x.Id == actorId && x.OrganizationId == org.Id, ct);
                var report = await db.Reports.SingleOrDefaultAsync(x => x.MessageId == reportId && x.OrganizationId == org.Id, ct)
                    ?? throw new TinaChatException(404, "tina_chat_not_found", "The report disappeared before its action could be recorded.");
                if (!MayActOnReport(currentActor, report))
                    throw Forbidden("The acting member no longer has authority over this report.");
                if (report.Status != "open")
                    throw Conflict("report_closed", $"The report is already {report.Status}; the action may already have been recorded.");
                Expect(report.Revision, expected);
                var result = await DecideReportCoreAsync(db, org, currentActor, reportId, "acted",
                    $"Executed {verb}: {action.Message}", expected, ct);
                Audit(db, scope, currentActor.Id, "report.action." + verb, reportId, result.ConversationId);
                return result;
            }, ct).ConfigureAwait(false);

            return new
            {
                status = decided.Status,
                executed = true,
                code = action.Code,
                action = verb,
                report_id = reportId.ToString("N"),
                revision = decided.Revision,
                target_run_id = action.TargetRunId?.ToString("N"),
                target_status = action.TargetStatus,
                note = action.Message,
            };
        }
        catch (TinaChatException ex) when (ex.Code == "report_closed")
        {
            // The run-control idempotency key makes the external operation safe
            // when two governance members race; report closure is then a normal
            // replay outcome rather than a second execution.
            return new
            {
                status = "already_decided",
                executed = true,
                code = "report_closed_after_action",
                action = verb,
                report_id = reportId.ToString("N"),
                revision = expected,
                target_run_id = action.TargetRunId?.ToString("N"),
                target_status = action.TargetStatus,
                note = "The action was accepted, but another member recorded the report first. Read the report again before continuing.",
            };
        }
    }

    private async Task RecordReportActionAuditAsync(TenantContext scope, Guid organizationId, Guid actorId, Guid reportId, string action, CancellationToken ct)
    {
        await WriteAsync(scope, async (db, _) =>
        {
            var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == reportId && x.OrganizationId == organizationId, ct);
            if (report is not null) Audit(db, scope, actorId, action, reportId, report.ConversationId);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Shared by the tool and the owner's HTTP view: one decision rule, one CAS.</summary>
    private static async Task<ChatReport> DecideReportCoreAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor, Guid reportId,
        string decision, string note, long expectedRevision, CancellationToken ct)
    {
        var report = await db.Reports.SingleOrDefaultAsync(x => x.MessageId == reportId && x.OrganizationId == org.Id, ct)
            ?? throw new TinaChatException(404, "tina_chat_not_found", "No report with that id in this organization. Take report ids from org_read.");
        var permitted = MayActOnReport(actor, report);
        if (!permitted)
            throw Forbidden("Only the user, the conversation identity and the report's author record a decision. If you acted on it, say so in the room; they will close it.");
        if (report.Status != "open")
            throw Conflict("report_closed", $"The report is already {report.Status}; a closed report is not reopened. File a new one if the situation changed.");
        Expect(report.Revision, expectedRevision);
        report.Status = decision;
        report.DecidedById = actor.Id;
        report.DecisionNote = note.Length == 0 ? null : note;
        report.DecidedAt = DateTimeOffset.UtcNow;
        report.Revision++;
        Audit(db, new TenantContext(org.TenantId, org.WorkspaceId, org.OwnerPrincipalId, "tina-chat-organization"), actor.Id, "report." + decision, report.MessageId, report.ConversationId);
        return report;
    }

    private static bool MayActOnReport(ChatParticipant actor, ChatReport report) =>
        actor.OrgRole is OrganizationRoles.Human or OrganizationRoles.Conversation || report.AuthorId == actor.Id;

    private Task<object> ContactToolAsync(TenantContext scope, Guid organizationId, Guid actorId, JsonElement? args, CancellationToken ct)
    {
        var action = OneOf(args, "action", ["request", "accept", "decline"], required: true)!;
        var address = Text(args, "address", max: 160);
        return WriteAsync(scope, async (db, _) =>
        {
            var (org, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
            RequireWritable(org);
            var other = await ResolveAddressAsync(db, org, actor, address, ct);
            if (other.Id == actor.Id) throw Invalid("That address is you.");
            if (other.OrgRole == OrganizationRoles.Host) throw Invalid("The host only posts facts; it is nobody's contact.");
            var now = DateTimeOffset.UtcNow;
            if (action == "request")
            {
                if (DerivedContact(actor, other)) return (object)new { status = "already_contacts", note = $"'{other.DisplayName}' is reachable already; message it with org_send to=..." };
                var reverse = await db.Contacts.SingleOrDefaultAsync(x => x.OwnerId == other.Id && x.ContactId == actor.Id, ct);
                if (reverse is { Status: "requested" })
                {
                    reverse.Status = "active"; reverse.DecidedAt = now;
                    return new { status = "active", note = $"'{other.DisplayName}' had asked you; you are contacts now." };
                }
                if (reverse is { Status: "active" }) return new { status = "already_contacts" };
                var own = await db.Contacts.SingleOrDefaultAsync(x => x.OwnerId == actor.Id && x.ContactId == other.Id, ct);
                if (own is not null)
                {
                    if (own.Status == "declined")
                        return new { status = "declined", note = $"'{other.DisplayName}' declined. Reach it through someone you share, or post in a room it reads." };
                    return new { status = own.Status };
                }
                db.Contacts.Add(new ChatContact { OwnerId = actor.Id, ContactId = other.Id, OrganizationId = org.Id, Status = "requested", CreatedAt = now });
                return new { status = "requested", note = $"'{other.DisplayName}' must accept (org_contact action=accept address={actor.DisplayName}) before you can message it directly." };
            }
            var pending = await db.Contacts.SingleOrDefaultAsync(x => x.OwnerId == other.Id && x.ContactId == actor.Id && x.Status == "requested", ct)
                ?? throw new TinaChatException(404, "tina_chat_not_found", $"'{other.DisplayName}' has not asked to be your contact. Pending requests are listed by org_directory.");
            pending.Status = action == "accept" ? "active" : "declined";
            pending.DecidedAt = now;
            return new { status = pending.Status };
        }, ct);
    }

    private Task<object> RoomToolAsync(TenantContext scope, Guid organizationId, Guid actorId, JsonElement? args, string key, CancellationToken ct)
    {
        var action = OneOf(args, "action", ["create", "invite", "leave"], required: true)!;
        var title = Text(args, "title", required: false, max: 120);
        var addresses = List(args, "members", max: MaxMembers - 1);
        var roomArg = Text(args, "room_id", required: false, max: 64);
        return WriteAsync(scope, async (db, _) =>
        {
            var (org, actor) = await LoadMemberAsync(db, organizationId, actorId, ct);
            RequireWritable(org);
            switch (action)
            {
                case "create":
                {
                    if (title.Length == 0) throw Invalid("A new room needs a title.");
                    var members = new List<ChatParticipant>();
                    foreach (var address in addresses)
                    {
                        var member = await ResolveAddressAsync(db, org, actor, address, ct);
                        if (member.Id == actor.Id) continue;
                        if (!await AreContactsAsync(db, actor, member, ct))
                            throw Forbidden($"'{member.DisplayName}' is not your contact, so you cannot pull it into a room. Ask with org_contact first.");
                        members.Add(member);
                    }
                    var roomKey = $"org:{org.Id:N}:adhoc:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor.Id.ToString("N") + key)))[..24].ToLowerInvariant();
                    var existing = await db.Conversations.SingleOrDefaultAsync(x => x.TenantId == org.TenantId && x.CreatorId == org.HostParticipantId && x.ClientRequestId == roomKey, ct);
                    if (existing is not null) return (object)new { status = "created", room_id = existing.Id.ToString("N"), replayed = true };
                    var room = NewRoom(new TenantContext(org.TenantId, org.WorkspaceId, org.OwnerPrincipalId, "tina-chat-organization"),
                        org.Id, org.HostParticipantId, "adhoc", title, roomKey);
                    db.Conversations.Add(room);
                    db.Members.Add(new ChatMember { ConversationId = room.Id, ParticipantId = actor.Id, Role = "owner", Status = "active" });
                    foreach (var member in members.DistinctBy(x => x.Id))
                        db.Members.Add(new ChatMember { ConversationId = room.Id, ParticipantId = member.Id, Role = "member", Status = "active" });
                    return new { status = "created", room_id = room.Id.ToString("N"), members = members.Select(x => x.DisplayName).Distinct().ToArray() };
                }
                case "invite":
                {
                    var room = await ResolveRoomAsync(db, org, actor, roomArg, ct);
                    if (room.Kind != "adhoc") throw Invalid("Only ad-hoc rooms take invitations; the lobby, the board and plan rooms follow the organization's structure.");
                    var own = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == room.Id && x.ParticipantId == actor.Id, ct);
                    if (own is null) throw Forbidden("You are not a member of that room.");
                    RequireAdmin(own);
                    var invited = new List<string>();
                    foreach (var address in addresses)
                    {
                        var member = await ResolveAddressAsync(db, org, actor, address, ct);
                        if (!await AreContactsAsync(db, actor, member, ct))
                            throw Forbidden($"'{member.DisplayName}' is not your contact. Ask with org_contact first.");
                        if (await db.Members.CountAsync(x => x.ConversationId == room.Id && x.Status == "active", ct) >= MaxMembers)
                            throw Conflict("member_limit", "The room already has 64 members. Open another room, or address people directly.");
                        await JoinAsync(db, room.Id, member.Id, "member", ct);
                        invited.Add(member.DisplayName);
                    }
                    room.Revision++;
                    return new { status = "invited", room_id = room.Id.ToString("N"), members = invited.ToArray() };
                }
                default:
                {
                    var room = await ResolveRoomAsync(db, org, actor, roomArg, ct);
                    if (room.Kind is not ("adhoc" or "plan")) throw Invalid("The lobby and the board are part of the organization; only ad-hoc and plan rooms can be left.");
                    var own = await db.Members.SingleOrDefaultAsync(x => x.ConversationId == room.Id && x.ParticipantId == actor.Id && x.Status == "active", ct)
                        ?? throw Forbidden("You are not a member of that room.");
                    own.Status = "left";
                    return new { status = "left", room_id = room.Id.ToString("N") };
                }
            }
        }, ct);
    }

    // ── rules ────────────────────────────────────────────────────────────────

    private static async Task<(ChatOrganization Org, ChatParticipant Actor)> LoadMemberAsync(TinaChatDbContext db, Guid organizationId, Guid actorId, CancellationToken ct)
    {
        var org = await db.Organizations.SingleOrDefaultAsync(x => x.Id == organizationId, ct) ?? throw Missing();
        var actor = await db.Participants.SingleOrDefaultAsync(x => x.Id == actorId && x.OrganizationId == organizationId, ct) ?? throw Missing();
        if (actor.Status != "active") throw Forbidden("This member is archived and cannot act.");
        return (org, actor);
    }

    /// <summary>Read visibility: members read their rooms, everybody reads the board, the whole-organization roles read every room.</summary>
    private static bool CanSeeRoom(ChatConversation conversation, ChatParticipant actor, bool isMember) =>
        conversation.OrganizationId == actor.OrganizationId
        && (isMember || conversation.Kind == "board" || OrganizationRoles.SeesWholeOrganization(actor.OrgRole));

    /// <summary>Room messages are readable to whoever can see the room; a confidential one still needs the reader in its audience.</summary>
    private static async Task<bool> CanReadOrganizationMessageAsync(TinaChatDbContext db, ChatParticipant actor, ChatMessage message, CancellationToken ct)
    {
        if (message.Sensitivity != "confidential") return true;
        return await db.Audiences.AnyAsync(x => x.MessageId == message.Id && x.ParticipantId == actor.Id && x.CanReadOriginal, ct);
    }

    private static async Task<bool> IsActiveMemberAsync(TinaChatDbContext db, Guid conversationId, Guid participantId, CancellationToken ct) =>
        await db.Members.AnyAsync(x => x.ConversationId == conversationId && x.ParticipantId == participantId && x.Status == "active", ct);

    private static async Task RequirePostableAsync(TinaChatDbContext db, ChatConversation conversation, ChatParticipant actor, CancellationToken ct)
    {
        if (conversation.Kind == "board" && !OrganizationRoles.MayPostOnBoard(actor.OrgRole))
            throw Forbidden("Only the user, the conversation identity and governance post on the board; everyone reads it. Post in your plan room, or message a governance member directly.");
        if (!await IsActiveMemberAsync(db, conversation.Id, actor.Id, ct))
            throw Forbidden($"You are not a member of '{conversation.Title}', so you cannot post there. Post in a room you belong to, or message one of its members directly.");
    }

    private static async Task RequireDirectReachableAsync(TinaChatDbContext db, ChatParticipant actor, ChatParticipant target, CancellationToken ct)
    {
        if (target.Id == actor.Id) throw Invalid("You cannot message yourself.");
        if (target.OrgRole == OrganizationRoles.Host) throw Invalid("The host only posts facts; it reads nothing.");
        if (!await AreContactsAsync(db, actor, target, ct))
            throw Forbidden($"'{target.DisplayName}' is not your contact. Ask with org_contact action=request; it must accept before you can message it directly. "
                + "Whoever dispatched you, your siblings, governance and the user are always reachable.");
        if (target.OrgRole == OrganizationRoles.Executor && target.Presence != Online)
            throw Conflict("member_offline", $"'{target.DisplayName}' has finished its task and is offline, so it would never read this. Address whoever dispatched it, or post in its plan room.");
    }

    private static async Task<IReadOnlyList<ChatParticipant>> MentionedAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor,
        ChatConversation conversation, string[] mentions, CancellationToken ct)
    {
        var result = new List<ChatParticipant>();
        foreach (var mention in mentions)
        {
            var member = await ResolveAddressAsync(db, org, actor, mention, ct);
            if (member.Id == actor.Id) continue;
            if (!await IsActiveMemberAsync(db, conversation.Id, member.Id, ct))
                throw Forbidden($"'{member.DisplayName}' is not in '{conversation.Title}', so a mention would never reach it here. Message it directly with to=, or post where it is.");
            result.Add(member);
        }
        return result;
    }

    /// <summary>
    /// A member by the name others use. Display names repeat across a session's runs (every run has a
    /// search#1), so a tie is broken by who is online, then by who shares the caller's run; anything
    /// still ambiguous is refused with the unique handles to use instead.
    /// </summary>
    private static async Task<ChatParticipant> ResolveAddressAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor, string address, CancellationToken ct)
    {
        var term = address.Trim().TrimStart('@');
        if (term.Length == 0) throw Invalid("address must name a member; addresses are listed by org_directory.");
        var lowered = term.ToLowerInvariant();
        var candidates = await db.Participants.Where(x => x.OrganizationId == org.Id && x.Status == "active"
            && (x.DisplayName.ToLower() == lowered || x.Handle == lowered)).Take(20).ToListAsync(ct);
        if (candidates.Count == 0)
            throw new TinaChatException(404, "tina_chat_not_found", $"Nobody in this organization is called '{term}'. Look up addresses with org_directory.");
        if (candidates.Count == 1) return candidates[0];
        var online = candidates.Where(x => x.Presence == Online).ToList();
        if (online.Count == 1) return online[0];
        var pool = online.Count > 0 ? online : candidates;
        var sameRun = pool.Where(x => actor.CurrentRunId is not null && x.CurrentRunId == actor.CurrentRunId).ToList();
        if (sameRun.Count == 1) return sameRun[0];
        throw Conflict("ambiguous_address", $"'{term}' names {candidates.Count} members (several runs used that name). Use one of these handles instead: {string.Join(", ", candidates.Select(x => x.Handle))}.");
    }

    private static async Task<ChatConversation> ResolveRoomAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor, string room, CancellationToken ct)
    {
        switch (room.Trim().ToLowerInvariant())
        {
            case "":
                throw Invalid("room is required: a room_id from org_directory, or lobby, board, plan.");
            case "lobby":
                return await db.Conversations.SingleAsync(x => x.Id == org.LobbyConversationId, ct);
            case "board":
                return await db.Conversations.SingleAsync(x => x.Id == org.BoardConversationId, ct);
            case "plan":
                // "plan" is your team: the room you were dispatched into (your dispatcher and your
                // siblings). A member nobody dispatched (the conversation identity) has only its own.
                // A dispatcher that was itself dispatched reaches its own plan room by room_id.
                var joined = await (from member in db.Members
                                    join conversation in db.Conversations on member.ConversationId equals conversation.Id
                                    where member.ParticipantId == actor.Id && member.Status == "active"
                                        && conversation.OrganizationId == org.Id && conversation.Kind == "plan"
                                        && conversation.PlanOwnerId != actor.Id
                                    orderby conversation.PlanOwnerId == actor.ParentParticipantId ? 0 : 1, conversation.Title
                                    select conversation).FirstOrDefaultAsync(ct);
                if (joined is not null) return joined;
                var own = await db.Conversations.Where(x => x.OrganizationId == org.Id && x.Kind == "plan" && x.PlanOwnerId == actor.Id)
                    .OrderBy(x => x.Title).FirstOrDefaultAsync(ct);
                return own ?? throw new TinaChatException(404, "tina_chat_not_found",
                    "You have no plan room: you neither dispatched executors nor were dispatched into one. Use lobby, or a room_id from org_directory.");
            default:
                var id = Parse(room);
                return await db.Conversations.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org.Id, ct)
                    ?? throw new TinaChatException(404, "tina_chat_not_found", "No room with that id in this organization. Take room ids from org_directory.");
        }
    }

    /// <summary>Where a member speaks when it names no room: the lobby for those in it, else its plan room.</summary>
    private static async Task<string> DefaultRoomAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor, CancellationToken ct) =>
        await IsActiveMemberAsync(db, org.LobbyConversationId, actor.Id, ct) ? "lobby" : "plan";

    private static Task<string> DefaultReportRoomAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor, CancellationToken ct) =>
        DefaultRoomAsync(db, org, actor, ct);

    private sealed record ReadableRoom(ChatConversation Room, bool IsMember);

    private static async Task<IReadOnlyList<ReadableRoom>> ReadableRoomsAsync(TinaChatDbContext db, ChatOrganization org, ChatParticipant actor, CancellationToken ct)
    {
        var memberOf = await (from member in db.Members.AsNoTracking()
                              join conversation in db.Conversations.AsNoTracking() on member.ConversationId equals conversation.Id
                              where member.ParticipantId == actor.Id && member.Status == "active"
                                  && conversation.OrganizationId == org.Id && conversation.Kind != "direct"
                              select conversation.Id).Take(OrganizationRoomLimit * 2).ToArrayAsync(ct);
        var rooms = OrganizationRoles.SeesWholeOrganization(actor.OrgRole)
            ? await db.Conversations.AsNoTracking().Where(x => x.OrganizationId == org.Id && x.Kind != "direct")
                .OrderBy(x => x.Kind).ThenBy(x => x.Title).Take(OrganizationRoomLimit).ToArrayAsync(ct)
            : await db.Conversations.AsNoTracking().Where(x => x.OrganizationId == org.Id
                    && (memberOf.Contains(x.Id) || x.Id == org.BoardConversationId))
                .OrderBy(x => x.Kind).ThenBy(x => x.Title).Take(OrganizationRoomLimit).ToArrayAsync(ct);
        return rooms.Select(x => new ReadableRoom(x, memberOf.Contains(x.Id))).ToArray();
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(TinaChatDbContext db, Guid organizationId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (wanted.Length == 0) return [];
        return await db.Participants.AsNoTracking().Where(x => x.OrganizationId == organizationId && wanted.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
    }

    private static string DirectKey(ChatOrganization org, ChatParticipant a, ChatParticipant b)
    {
        var (low, high) = a.Id.CompareTo(b.Id) < 0 ? (a, b) : (b, a);
        var key = $"org:{org.Id:N}:dm:{low.Id:N}:{high.Id:N}";
        return key.Length > 128
            ? $"org:{org.Id:N}:dm:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40].ToLowerInvariant()
            : key;
    }

    private static string RenderReport(string kind, string severity, string? subjectKind, string subjectId, string finding,
        IReadOnlyList<string> evidence, string verb, string proposedArgs)
    {
        var text = new StringBuilder();
        text.Append("[report · ").Append(kind).Append(" · ").Append(severity).Append("] ").Append(finding);
        if (subjectKind is not null) text.Append("\nabout: ").Append(subjectKind).Append(' ').Append(subjectId);
        if (evidence.Count > 0) text.Append("\nevidence:\n").Append(string.Join("\n", evidence.Select(x => "- " + x)));
        if (verb.Length > 0) text.Append("\nproposes: ").Append(verb).Append(proposedArgs.Length > 0 ? " — " + proposedArgs : "");
        return text.ToString();
    }

    private static string? OneOf(JsonElement? args, string field, string[] values, bool required)
    {
        var value = Text(args, field, required, max: 64).ToLowerInvariant();
        if (value.Length == 0) return null;
        return values.Contains(value) ? value : throw Invalid($"{field} must be one of: {string.Join(", ", values)}.");
    }

    private static long? OptionalLong(JsonElement? args, string field)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw Invalid($"{field} must be a whole number."),
        };
    }

    /// <summary>The tool surface of one standing member's turn, bound to that member.</summary>
    private sealed class MemberToolbox(TinaChatService service, TenantContext scope, Guid organizationId, Guid participantId) : ITinaChatMemberToolbox
    {
        public IReadOnlyList<TinaChatToolDeclaration> Tools { get; } = CoreVirtualToolPolicy.OrganizationToolIds
            .Select(OrganizationToolCatalog.Find).Where(x => x is not null)
            .Select(x => new TinaChatToolDeclaration(x!.Id, x.Description, JsonDocument.Parse(x.SchemaJson).RootElement.Clone()))
            .ToArray();

        public async Task<TinaChatToolOutcome> ExecuteAsync(string toolId, JsonElement? arguments, string callKey, CancellationToken cancellationToken = default)
        {
            if (!CoreVirtualToolPolicy.IsOrganization(toolId))
                return TinaChatToolOutcome.Failed($"'{toolId}' is not an organization tool.");
            try
            {
                var payload = await service.RunOrganizationToolAsync(scope, organizationId, participantId, toolId, arguments, "turn:" + callKey, cancellationToken);
                return new TinaChatToolOutcome(true, JsonSerializer.Serialize(payload, Json));
            }
            catch (TinaChatException ex)
            {
                return TinaChatToolOutcome.Failed(ex.Message);
            }
        }
    }
}
