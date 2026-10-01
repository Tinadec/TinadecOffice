using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.TinaChat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace TinadecCore.Api.Tests;

/// <summary>
/// A session as a TinaChat organization (architecture §9.1): who is a member and where they meet,
/// who may talk to whom without asking, what the board and reports allow, and how a standing
/// governance member is woken, takes its own turn and is held to an hourly budget without ever
/// losing a notice. Driven through the same ports the engine and the tools use.
/// </summary>
public sealed class OrganizationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-org-tests", Guid.NewGuid().ToString("N"));
    private readonly MemberScript _script = new();
    private readonly RecordingRunController _governanceController = new();
    private OrganizationFactory _factory = null!;
    private readonly Guid _session = Guid.NewGuid();
    private readonly Guid _run = Guid.NewGuid();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new OrganizationFactory(_root, _script, _governanceController);
        _ = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private ISessionOrganization Organization => _factory.Services.GetRequiredService<ISessionOrganization>();
    private ISessionOrganizationView View => _factory.Services.GetRequiredService<ISessionOrganizationView>();

    private OrganizationScope Scope()
    {
        var current = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        return new OrganizationScope(current.TenantId, current.WorkspaceId, current.PrincipalId, _session);
    }

    private Task<OrganizationMemberRef> EnrolAsync(string role, string slug, string name, Guid? instance = null, Guid? dispatcher = null) =>
        Organization.EnrolAsync(new OrganizationMemberEnrolment(Scope(), role, slug, name, _run, instance, Guid.NewGuid(), dispatcher));

    private long _call;

    private async Task<(bool Ok, JsonElement Result, string? Error)> ToolAsync(Guid instance, string toolId, object arguments)
    {
        var scope = Scope();
        var outcome = await _factory.Services.GetRequiredService<ITinaChatToolGateway>().ExecuteAsync(new TinaChatToolCall(
            scope.TenantId, scope.WorkspaceId, scope.PrincipalId, _session, _run, Interlocked.Increment(ref _call), toolId,
            JsonSerializer.SerializeToElement(arguments))
        { AgentInstanceId = instance });
        return (outcome.IsSuccess, JsonDocument.Parse(outcome.ResultJson).RootElement.Clone(), outcome.Error);
    }

    /// <summary>The standard cast: the conversation identity (instance A), a governance role, two executors A dispatched, one executor B dispatched.</summary>
    private async Task<(Guid A, Guid B, Guid C, Guid D)> CastAsync()
    {
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await EnrolAsync(OrganizationRoles.Conversation, "meeting", "meeting", a);
        await EnrolAsync(OrganizationRoles.Governance, "governance_reviewer", "governance_reviewer");
        await EnrolAsync(OrganizationRoles.Executor, "search", "search#1", b, dispatcher: a);
        await EnrolAsync(OrganizationRoles.Executor, "global_engineering", "global_engineering#1", c, dispatcher: a);
        await EnrolAsync(OrganizationRoles.Executor, "search", "search#2", d, dispatcher: b);
        return (a, b, c, d);
    }

    [Fact]
    public async Task EnrolmentBuildsOneOrganization_AndPutsEachMemberWhereItsRoleMeets()
    {
        var (a, b, _, d) = await CastAsync();
        // Re-enrolling an instance (a resumed task) is the same member, not a second one.
        var again = await EnrolAsync(OrganizationRoles.Executor, "search", "search#1", b, dispatcher: a);

        var organization = await View.GetAsync(_session);
        Assert.NotNull(organization);
        Assert.Equal(["global_engineering#1", "governance_reviewer", "meeting", "search#1", "search#2", "user"],
            organization!.Members.Select(member => member.DisplayName).Order().ToArray());
        Assert.Equal(again.ParticipantId, organization.Members.Single(member => member.DisplayName == "search#1").Id);

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        var org = await db.Organizations.SingleAsync(x => x.SessionId == _session);
        async Task<string[]> MembersOf(Guid room) => await (from member in db.Members
                                                            join participant in db.Participants on member.ParticipantId equals participant.Id
                                                            where member.ConversationId == room && member.Status == "active"
                                                            orderby participant.DisplayName
                                                            select participant.DisplayName).ToArrayAsync();
        // The lobby holds only the roles that see everything; executors never fan out there.
        Assert.Equal(["governance_reviewer", "host", "meeting", "user"], await MembersOf(org.LobbyConversationId));
        // Each dispatcher gets a plan room with the executors it dispatched.
        var plans = await db.Conversations.Where(x => x.OrganizationId == org.Id && x.Kind == "plan").ToListAsync();
        Assert.Equal(2, plans.Count);
        var meetingPlan = plans.Single(x => x.Title.StartsWith("meeting", StringComparison.Ordinal));
        Assert.Equal(["global_engineering#1", "meeting", "search#1"], await MembersOf(meetingPlan.Id));
        var searchPlan = plans.Single(x => x.Title.StartsWith("search#1", StringComparison.Ordinal));
        Assert.Equal(["search#1", "search#2"], await MembersOf(searchPlan.Id));
        // Nothing of the organization leaks into the workspace directory.
        Assert.DoesNotContain(await _factory.Services.GetRequiredService<ITinaChatService>().DiscoverAsync(), person => person.DisplayName == "search#1");
        _ = d;
    }

    [Fact]
    public async Task DefaultContactsFollowTheGraph_AndAnyoneElseNeedsConsent()
    {
        var (_, b, c, d) = await CastAsync();
        // Siblings under one dispatcher reach each other directly.
        var sibling = await ToolAsync(b, "org_send", new { to = "global_engineering#1", content = "I will touch src/a.ts" });
        Assert.True(sibling.Ok, sibling.Error);
        // search#2 (dispatched by search#1) and global_engineering#1 share no edge: refused, told how to ask.
        var stranger = await ToolAsync(d, "org_send", new { to = "global_engineering#1", content = "hello" });
        Assert.False(stranger.Ok);
        Assert.Contains("org_contact", stranger.Error);
        // Everybody reaches governance and the user without asking.
        Assert.True((await ToolAsync(d, "org_send", new { to = "governance_reviewer", content = "is src/a.ts free?" })).Ok);
        Assert.True((await ToolAsync(d, "org_send", new { to = "user", content = "fyi" })).Ok);

        var request = await ToolAsync(d, "org_contact", new { action = "request", address = "global_engineering#1" });
        Assert.Equal("requested", request.Result.GetProperty("status").GetString());
        var directory = await ToolAsync(c, "org_directory", new { });
        Assert.Contains("search#2", directory.Result.GetProperty("contact_requests").GetProperty("waiting_for_you").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("active", (await ToolAsync(c, "org_contact", new { action = "accept", address = "search#2" })).Result.GetProperty("status").GetString());
        Assert.True((await ToolAsync(d, "org_send", new { to = "global_engineering#1", content = "hello again" })).Ok);
    }

    [Fact]
    public async Task TheBoardIsReadByEveryone_ButOnlyTheRolesThatSeeTheWholeOrganizationPostThere()
    {
        var (a, b, _, _) = await CastAsync();
        var refused = await ToolAsync(b, "org_send", new { room = "board", content = "announcement from an executor" });
        Assert.False(refused.Ok);
        Assert.Contains("board", refused.Error);
        Assert.True((await ToolAsync(a, "org_send", new { room = "board", content = "Freeze: nobody touches migrations today." })).Ok);

        var read = await ToolAsync(b, "org_read", new { room = "board" });
        Assert.True(read.Ok, read.Error);
        var message = Assert.Single(read.Result.GetProperty("messages").EnumerateArray());
        Assert.Equal("meeting", message.GetProperty("from").GetString());
        // A board post writes one audience row (the sender): hundreds of readers cost nothing.
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        var id = Guid.Parse(message.GetProperty("message_id").GetString()!);
        Assert.Equal(1, await db.Audiences.CountAsync(x => x.MessageId == id));
    }

    [Fact]
    public async Task ARoomPostReachesTheInboxOfWhoWasMentioned_AndEveryoneElseReadsItByCursor()
    {
        var (a, b, c, _) = await CastAsync();
        var post = await ToolAsync(b, "org_send", new { room = "plan", content = "Taking src/a.ts; global_engineering#1 please wait.", mention = new[] { "global_engineering#1" } });
        Assert.True(post.Ok, post.Error);

        var mentioned = await ToolAsync(c, "org_read", new { room = "inbox" });
        Assert.Single(mentioned.Result.GetProperty("messages").EnumerateArray());
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync())
        {
            // An execution member gets a durable wake as well as an inbox item; Runtime consumes it
            // into that member's run-scoped context at the next safe boundary.
            var binding = await db.InstanceBindings.SingleAsync(row => row.InstanceId == c);
            Assert.Contains(await db.Wakes.ToArrayAsync(), wake => wake.ParticipantId == binding.ParticipantId && wake.Status == "pending");
        }
        // The dispatcher was not mentioned: nothing in its inbox, but it sees its plan room (it owns it).
        Assert.Empty((await ToolAsync(a, "org_read", new { room = "inbox" })).Result.GetProperty("messages").EnumerateArray());
        var room = await ToolAsync(a, "org_read", new { room = "plan" });
        Assert.Single(room.Result.GetProperty("messages").EnumerateArray());
        // Mentioning somebody who is not in the room is refused rather than silently dropped.
        var stray = await ToolAsync(b, "org_send", new { room = "plan", content = "x", mention = new[] { "governance_reviewer" } });
        Assert.False(stray.Ok);
    }

    [Fact]
    public async Task AReportChangesNothing_ReachesWhoCanActOnIt_AndIsDecidedOnceAgainstItsRevision()
    {
        var (a, b, c, _) = await CastAsync();
        // An executor files a report into its plan room.
        var filed = await ToolAsync(b, "org_report", new
        {
            kind = "conflict", severity = "blocking", subject_kind = "path", subject_id = "C:/ws/src/a.ts",
            finding = "search#1 and global_engineering#1 both plan to edit src/a.ts.", evidence = new[] { "plan room message 1" },
            proposed_verb = "serialize", proposed_args = "global_engineering#1 waits for search#1"
        });
        Assert.True(filed.Ok, filed.Error);
        var reportId = Guid.Parse(filed.Result.GetProperty("report_id").GetString()!);

        // Blocking: it reaches the conversation identity's inbox (and the user's), with its structure.
        var inbox = await ToolAsync(a, "org_read", new { room = "inbox", kinds = new[] { "report" } });
        var item = Assert.Single(inbox.Result.GetProperty("messages").EnumerateArray());
        Assert.Equal("blocking", item.GetProperty("report").GetProperty("severity").GetString());
        var page = await View.ListReportsAsync(_session);
        Assert.Equal("open", Assert.Single(page.Items).Status);

        // An executor that did not write it cannot close it; a stale revision is refused; then it is decided once.
        var bystander = await ToolAsync(c, "org_decide_report", new { report_id = reportId.ToString("N"), decision = "acted", expected_revision = 1 });
        Assert.False(bystander.Ok);
        Assert.Contains("author", bystander.Error);
        var stale = await ToolAsync(a, "org_decide_report", new { report_id = reportId.ToString("N"), decision = "acted", expected_revision = 7 });
        Assert.False(stale.Ok);
        Assert.Contains("revision", stale.Error);
        var decided = await ToolAsync(a, "org_decide_report", new { report_id = reportId.ToString("N"), decision = "acted", note = "serialized", expected_revision = 1 });
        Assert.True(decided.Ok, decided.Error);
        Assert.Equal(2, decided.Result.GetProperty("revision").GetInt64());
        var again = await ToolAsync(a, "org_decide_report", new { report_id = reportId.ToString("N"), decision = "dismissed", expected_revision = 2 });
        Assert.False(again.Ok);
        Assert.Contains("already acted", again.Error);
        // The owner reads the same record through the session view.
        var owner = await View.ListReportsAsync(_session, "acted");
        Assert.Equal("serialized", Assert.Single(owner.Items).DecisionNote);
    }

    [Fact]
    public async Task AnOpenReportActionUsesTheRuntimeController_BeforeItMarksTheReportActed()
    {
        var (meeting, worker, _, _) = await CastAsync();
        var filed = await ToolAsync(worker, "org_report", new
        {
            kind = "risk", severity = "blocking", subject_kind = "run", subject_id = _run.ToString("N"),
            finding = "The run must stop before the conflicting write continues.", evidence = new[] { "run graph" },
            proposed_verb = "stop_run"
        });
        Assert.True(filed.Ok, filed.Error);
        var reportId = Guid.Parse(filed.Result.GetProperty("report_id").GetString()!);

        var executed = await ToolAsync(meeting, "org_execute_report", new
        {
            report_id = reportId.ToString("N"), expected_revision = 1
        });

        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.Result.GetProperty("executed").GetBoolean());
        Assert.Equal("acted", executed.Result.GetProperty("status").GetString());
        var call = Assert.Single(_governanceController.Calls);
        Assert.Equal("cancel", call.Action);
        Assert.Equal(_run, call.RunId);
        Assert.Equal($"report:{reportId:N}:revision:1", call.ClientControlId);

        var report = Assert.Single((await View.ListReportsAsync(_session, "acted")).Items);
        Assert.Equal(reportId, report.Id);
        Assert.Contains("Executed stop_run", report.DecisionNote);
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        Assert.Contains(await db.Audit.AsNoTracking().ToArrayAsync(), item => item.TargetId == reportId && item.Action == "report.action.stop_run");
    }

    [Fact]
    public async Task AnOpenReportActionCanAssignAnEnvironment_AndTheLeaseIsVisibleToTheRun()
    {
        var (meeting, worker, _, _) = await CastAsync();
        var environmentRoot = Path.Combine(_root, "governed-environment");
        Directory.CreateDirectory(environmentRoot);
        await _factory.Services.GetRequiredService<IEnvironmentRegistry>().RegisterAsync(new EnvironmentRegistration(
            "governed-test", EnvironmentKinds.Test, "Governed test", null,
            JsonSerializer.Serialize(new { workspace_root = environmentRoot })));

        var filed = await ToolAsync(worker, "org_report", new
        {
            kind = "budget", severity = "warning", subject_kind = "run", subject_id = _run.ToString("N"),
            finding = "The task needs the governed test environment before continuing.", evidence = new[] { "plan room" },
            proposed_verb = "assign_environment",
            proposed_args = JsonSerializer.Serialize(new { key = "governed-test", reason = "governance requested test execution" })
        });
        Assert.True(filed.Ok, filed.Error);
        var reportId = Guid.Parse(filed.Result.GetProperty("report_id").GetString()!);

        var executed = await ToolAsync(meeting, "org_execute_report", new
        {
            report_id = reportId.ToString("N"), expected_revision = 1
        });

        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.Result.GetProperty("executed").GetBoolean());
        var environment = Assert.Single(await _factory.Services.GetRequiredService<IEnvironmentRegistry>().ListAsync());
        Assert.Equal("governed-test", environment.Key);
        var holder = Assert.Single(environment.Holders);
        Assert.Equal(_run, holder.RunId);
        Assert.Contains("governed-test", executed.Result.GetProperty("note").GetString());
    }

    [Fact]
    public async Task ANoticeWakesTheStandingMember_WhichActsInItsOwnTurn_AndTheBudgetPostponesInsteadOfDropping()
    {
        await CastAsync();
        _script.Enqueue(
            [new FunctionCallContent("c1", "org_report", new Dictionary<string, object?>
            {
                ["kind"] = "conflict", ["severity"] = "warning", ["finding"] = "Two tasks hold overlapping scopes.",
                ["subject_kind"] = "task", ["subject_id"] = "task-1", ["evidence"] = new[] { "lease L1" }, ["proposed_verb"] = "wait"
            })],
            [new TextContent("Reported.")]);

        Assert.True(await Organization.NotifyAsync(new OrganizationNotice(Scope(), _run, GovernanceTopics.LeaseConflict,
            "governance_reviewer", "Task 'b' could not start: its write scope overlaps task 'a'.", "task", "task-1")));
        // Idempotent within its window: the same fact twice is one notice.
        Assert.True(await Organization.NotifyAsync(new OrganizationNotice(Scope(), _run, GovernanceTopics.LeaseConflict,
            "governance_reviewer", "Task 'b' could not start: its write scope overlaps task 'a'.", "task", "task-1")));

        var drain = _factory.Services.GetRequiredService<TinaChatWakeService>();
        Assert.Equal(1, await drain.RunPassAsync(5));

        // The turn ran in the member's own framing, saw what woke it, and could only use its tools.
        var turn = Assert.Single(_script.Turns);
        Assert.Contains(TinaChatMemberTurnRunner.TurnMarker, turn.Instructions);
        Assert.Contains("could not start", turn.Briefing);
        Assert.Contains("org_report", turn.Tools);
        Assert.DoesNotContain("write_file", turn.Tools);
        var report = Assert.Single((await View.ListReportsAsync(_session)).Items);
        Assert.Equal("governance_reviewer", report.AuthorDisplayName);
        Assert.Equal("task-1", report.SubjectId);

        // Over budget (MemberTurnsPerHour = 1): the next notice is kept and its turn postponed, not run and not failed.
        Assert.True(await Organization.NotifyAsync(new OrganizationNotice(Scope(), _run, GovernanceTopics.TaskClosed,
            "governance_reviewer", "Task 'a' failed.")));
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync())
        {
            // Skip the per-recipient cooldown so the pass reaches the budget gate itself.
            await db.Wakes.Where(x => x.Status == "pending").ExecuteUpdateAsync(s => s.SetProperty(x => x.DueAtUnixMs, 0L));
        }
        Assert.Equal(0, await drain.RunPassAsync(5));
        Assert.Single(_script.Turns);
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync())
        {
            var wake = await db.Wakes.SingleAsync(x => x.Status == "pending");
            Assert.StartsWith("budget", wake.LastError);
            Assert.True(wake.DueAtUnixMs > DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds());
            Assert.NotEqual("[]", wake.SourceMessageIdsJson);
        }
    }

    [Fact]
    public async Task AnArchivedSessionKeepsItsOrganizationReadable_AndRefusesEveryWrite()
    {
        var (a, b, _, _) = await CastAsync();
        Assert.True((await ToolAsync(a, "org_send", new { room = "lobby", content = "before archive" })).Ok);
        await Organization.SetArchivedAsync(_session, archived: true);

        var write = await ToolAsync(a, "org_send", new { room = "lobby", content = "after archive" });
        Assert.False(write.Ok);
        Assert.Contains("read-only", write.Error);
        Assert.True((await ToolAsync(a, "org_read", new { room = "lobby" })).Ok);
        Assert.False(await Organization.NotifyAsync(new OrganizationNotice(Scope(), _run, GovernanceTopics.LeaseConflict, "governance_reviewer", "late")));

        await Organization.SetArchivedAsync(_session, archived: false);
        Assert.True((await ToolAsync(a, "org_send", new { room = "lobby", content = "restored" })).Ok);
        _ = b;
    }

    [Fact]
    public async Task AFinishedExecutorIsOffline_KeepsItsHistory_AndIsNotSentMessagesItWouldNeverRead()
    {
        var (a, b, _, _) = await CastAsync();
        Assert.True((await ToolAsync(b, "org_send", new { room = "plan", content = "done with src/a.ts" })).Ok);
        Assert.True(await Organization.SetRunOfflineAsync(_run) >= 3);

        var dm = await ToolAsync(a, "org_send", new { to = "search#1", content = "one more thing" });
        Assert.False(dm.Ok);
        Assert.Contains("offline", dm.Error);
        var history = await ToolAsync(a, "org_read", new { room = "plan" });
        Assert.Equal("search#1", Assert.Single(history.Result.GetProperty("messages").EnumerateArray()).GetProperty("from").GetString());
        var directory = await ToolAsync(a, "org_directory", new { role = "executor" });
        Assert.All(directory.Result.GetProperty("members").EnumerateArray(), member => Assert.Equal("offline", member.GetProperty("presence").GetString()));
    }

    [Fact]
    public async Task AnOfflineExecutorKeepsAPlanRoomMentionInItsInbox_WithoutRetryingADeadRunWake()
    {
        var (meeting, worker, _, _) = await CastAsync();
        Assert.True(await Organization.SetRunOfflineAsync(_run) >= 3);
        var post = await ToolAsync(meeting, "org_send", new
        {
            room = "plan", content = "search#1 please review the finished artifact.", mention = new[] { "search#1" }
        });
        Assert.True(post.Ok, post.Error);

        var drain = _factory.Services.GetRequiredService<TinaChatWakeService>();
        await using (var dueDb = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync())
            await dueDb.Wakes.Where(wake => wake.Status == "pending").ExecuteUpdateAsync(setters => setters.SetProperty(wake => wake.DueAtUnixMs, 0L));
        _ = await drain.RunPassAsync(5);
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        var workerParticipantId = await db.InstanceBindings.AsNoTracking()
            .Where(binding => binding.InstanceId == worker).Select(binding => binding.ParticipantId).SingleAsync();
        var wakes = await db.Wakes.AsNoTracking().ToArrayAsync();
        Assert.DoesNotContain(wakes, wake => wake.ParticipantId == workerParticipantId && (wake.Status is "pending" or "running"));
        var inbox = await ToolAsync(worker, "org_read", new { room = "inbox" });
        Assert.True(inbox.Ok, inbox.Error);
        Assert.Contains(inbox.Result.GetProperty("messages").EnumerateArray(), message =>
            message.GetProperty("content").GetString()!.Contains("finished artifact", StringComparison.Ordinal));
    }

    /// <summary>
    /// Per-member visibility (todo E5): the user restricts a member to its own run. A restricted
    /// standing governance member has no run of its own, so it is muted entirely — the notice is
    /// accepted but queues no wake and sets no current run; an executor reads as "own run only".
    /// Clearing restores the default flow. The owner's and the host's visibility cannot be changed.
    /// </summary>
    [Fact]
    public async Task VisibilityScope_RestrictsTheMember_NotifiesNoMore_UntilCleared()
    {
        var (_, b, _, _) = await CastAsync();
        var organization = (await View.GetAsync(_session))!;
        var reviewer = organization.Members.Single(member => member.DisplayName == "governance_reviewer");
        var worker = organization.Members.Single(member => member.DisplayName == "search#1");
        var user = organization.Members.Single(member => member.Role == "human");
        Assert.Null(reviewer.VisibilityScope);

        // Restrict the reviewer before any fact: a notice is accepted but muted — no wake, no turn,
        // and the muted notice never becomes the member's "current run" either.
        var set = await View.SetMemberVisibilityAsync(_session, reviewer.Id, new OrganizationMemberVisibilityRequest("own"));
        Assert.Equal("own", set.VisibilityScope);
        var visibility = await Organization.VisibilityForParticipantAsync(_session, reviewer.Id);
        Assert.NotNull(visibility);
        Assert.True(visibility!.Restricted);
        Assert.Null(visibility.CurrentRunId);
        Assert.True(await Organization.NotifyAsync(new OrganizationNotice(Scope(), _run, GovernanceTopics.LeaseConflict,
            "governance_reviewer", "muted fact")));
        var drain = _factory.Services.GetRequiredService<TinaChatWakeService>();
        Assert.Equal(0, await drain.RunPassAsync(5));
        Assert.Empty(_script.Turns);
        Assert.Null((await Organization.VisibilityForParticipantAsync(_session, reviewer.Id))!.CurrentRunId);

        // An executor is restricted to exactly its own run.
        await View.SetMemberVisibilityAsync(_session, worker.Id, new OrganizationMemberVisibilityRequest("own"));
        var workerVisibility = await Organization.VisibilityForInstanceAsync(_session, b);
        Assert.NotNull(workerVisibility);
        Assert.True(workerVisibility!.Restricted);
        Assert.Equal(_run, workerVisibility.CurrentRunId);

        // The owner (and the host) can never be blinded, and unknown scopes are refused.
        await Assert.ThrowsAsync<TinaChatException>(() => View.SetMemberVisibilityAsync(_session, user.Id, new OrganizationMemberVisibilityRequest("own")));
        await Assert.ThrowsAsync<TinaChatException>(() => View.SetMemberVisibilityAsync(_session, reviewer.Id, new OrganizationMemberVisibilityRequest("sideways")));

        // Cleared ("down" is the default, stored as null again), the same fact flows once more.
        var cleared = await View.SetMemberVisibilityAsync(_session, reviewer.Id, new OrganizationMemberVisibilityRequest(null));
        Assert.Null(cleared.VisibilityScope);
        Assert.True(await Organization.NotifyAsync(new OrganizationNotice(Scope(), _run, GovernanceTopics.LeaseConflict,
            "governance_reviewer", "unmuted fact")));
        Assert.Equal(1, await drain.RunPassAsync(5));
        Assert.Single(_script.Turns);
    }

    private sealed class RecordingRunController : IGovernanceRunController
    {
        public List<Call> Calls { get; } = [];

        public Task<GovernanceRunControlResult> ControlAsync(TenantContext scope, Guid sessionId, Guid runId,
            string action, string clientControlId, CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(scope, sessionId, runId, action, clientControlId));
            return Task.FromResult(new GovernanceRunControlResult(true, action == "cancel" ? "cancelled" : "paused", action, RunId: runId));
        }
    }

    private sealed record Call(TenantContext Scope, Guid SessionId, Guid RunId, string Action, string ClientControlId);

    private sealed class OrganizationFactory(string root, MemberScript script, RecordingRunController governanceController) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning",
                // Tests drive the drain one pass at a time.
                ["TinadecTinaChat:WakeDrainEnabled"] = "false",
                ["TinadecTinaChat:MemberTurnsPerHour"] = "1",
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ISecretStore>(new TestModelSecretStore());
                services.AddSingleton<IAgentChatClientFactory>(new MemberScriptFactory(script));
                services.AddSingleton<IGovernanceRunController>(governanceController);
            });
    }

    }

    /// <summary>A scripted model for standing-member turns: queued responses in order, then silence.</summary>
    private sealed class MemberScript : IChatClient
    {
        private readonly Queue<AIContent[]> _responses = new();
        private readonly object _gate = new();
        public List<(string Instructions, string Briefing, string[] Tools)> Turns { get; } = [];

        public void Enqueue(params AIContent[][] responses)
        {
            lock (_gate) foreach (var response in responses) _responses.Enqueue(response);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            lock (_gate)
            {
                // One record per turn (its first round), with the briefing the member was handed.
                if (list.Count == 1)
                    Turns.Add((options?.Instructions ?? "", list[0].Text ?? "", options?.Tools?.Select(tool => tool.Name).ToArray() ?? []));
                var next = _responses.Count > 0 ? _responses.Dequeue() : [new TextContent("Nothing to report.")];
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, next)));
            }
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MemberScriptFactory(MemberScript script) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, Model = "scripted", ModelId = "scripted/member" });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(new NonDisposing(script));

        /// <summary>The runner disposes its client after a turn; the script must survive that.</summary>
        private sealed class NonDisposing(IChatClient inner) : DelegatingChatClient(inner)
        {
            protected override void Dispose(bool disposing) { }
        }
    }
}
