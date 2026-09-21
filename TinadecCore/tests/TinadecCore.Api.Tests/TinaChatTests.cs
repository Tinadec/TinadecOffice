using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Tenancy;
using TinadecCore.TinaChat;
using ModelMessage = Microsoft.Extensions.AI.ChatMessage;

namespace TinadecCore.Api.Tests;

public sealed class TinaChatTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinachat-tests", Guid.NewGuid().ToString("N"));
    private readonly MutableIdentity _identity = new();
    private readonly RecordingModel _model = new();
    private readonly RecordingLoggerProvider _logs = new();
    private ChatFactory _factory = null!;
    private HttpClient _http = null!;
    private ITinaChatService Chat => _factory.Services.GetRequiredService<ITinaChatService>();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        StartHost();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _http.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private void StartHost()
    {
        _factory = new ChatFactory(_root, _identity, _model, _logs);
        _http = _factory.CreateClient();
    }

    [Fact]
    public async Task NamedAgents_ReceiveDerivedIntentWithoutHumanOriginals_AndKeepIndependentIdentity()
    {
        var team = await TeamAsync();
        var source = await Chat.SendAsync(team.Conversation, new(team.Human, "PRIVATE_RAW: perhaps the login feels slow?", "raw-1", AllowDerivedSharing: true));
        Assert.Empty((await Chat.ReadMessagesAsync(team.Conversation, team.Worker)).Items);
        Assert.Empty((await Chat.ReadInboxAsync(team.Worker)).Items);
        Assert.Single((await Chat.ReadMessagesAsync(team.Conversation, team.Interpreter)).Items);

        var intent = await ProposeAsync(team, source.Id);
        var workerMessage = Assert.Single((await Chat.ReadMessagesAsync(team.Conversation, team.Worker)).Items);
        Assert.Equal("intent_brief", workerMessage.Kind);
        Assert.DoesNotContain("PRIVATE_RAW", workerMessage.Content);
        Assert.Empty(workerMessage.SourceMessageIds);
        var workerIntent = Assert.Single(await Chat.ListIntentsAsync(team.Conversation, team.Worker));
        Assert.Equal(intent.Id, workerIntent.Id);
        Assert.Equal("proposed", workerIntent.Status);
        Assert.NotEmpty(workerIntent.Content.Assumptions);

        var before = await Chat.GetParticipantAsync(team.Worker);
        var renamed = await Chat.UpdateParticipantAsync(team.Worker, new(before.Revision, "New independent name"));
        Assert.Equal(before.Id, renamed.Id);
        Assert.Single((await Chat.ReadInboxAsync(team.Worker)).Items);
        var second = await Chat.RegisterAsync(new("another-interpreter", "A different dialogue specialist", ReceiveHumanMessages: true, CanInterpretIntent: true));
        var revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        await Chat.ChangeMemberAsync(team.Conversation, new(team.Human, second.Id, "invite", revision));
        await JoinAsync(team.Conversation, second.Id);
        var later = await Chat.SendAsync(team.Conversation, new(team.Human, "Investigate the page before changing it.", "raw-2", AllowDerivedSharing: true));
        var current = await Chat.GetConversationAsync(team.Conversation, second.Id);
        var otherBrief = await Chat.ProposeIntentAsync(team.Conversation, new(second.Id, "other-brief", current.Revision,
            [later.Id], [team.Human, team.Worker, second.Id], Brief()));
        Assert.Equal(second.Id, otherBrief.AuthorId);
    }

    [Fact]
    public async Task PrivateSources_CannotBeLaunderedThroughQuotesOrIntentGeneration()
    {
        var team = await TeamAsync();
        var secret = await Chat.SendAsync(team.Conversation, new(team.Human, "Private material", "private",
            [team.Human, team.Interpreter], AllowDerivedSharing: true));
        var revision = (await Chat.GetConversationAsync(team.Conversation, team.Interpreter)).Revision;
        await DeniedAsync(() => Chat.ProposeIntentAsync(team.Conversation, new(team.Interpreter, "leak", revision,
            [secret.Id], [team.Worker], Brief())));
        await DeniedAsync(() => Chat.SendAsync(team.Conversation, new(team.Interpreter, "Quoted material", "quote",
            [team.Worker], SourceMessageIds: [secret.Id])));
        Assert.Empty((await Chat.ReadInboxAsync(team.Worker)).Items);

        var unshareable = await Chat.SendAsync(team.Conversation, new(team.Human, "No derivative grant", "no-derive"));
        revision = (await Chat.GetConversationAsync(team.Conversation, team.Interpreter)).Revision;
        await DeniedAsync(() => Chat.GenerateIntentAsync(team.Conversation, new(team.Interpreter, "no-derived-leak", revision,
            [unshareable.Id], [team.Worker])));
        Assert.Empty(_model.Calls);
    }

    [Fact]
    public async Task ActorSpoofing_AndCrossTenantLookup_AreRejectedByTheService()
    {
        var team = await TeamAsync();
        var original = _identity.Current;
        var stranger = original with { PrincipalId = Guid.NewGuid(), Role = "viewer" };
        await SeedScopeAsync(stranger);
        _identity.Current = stranger;
        await DeniedAsync(() => Chat.SendAsync(team.Conversation, new(team.Human, "spoofed", "spoof")));
        await DeniedAsync(() => Chat.ReadInboxAsync(team.Interpreter));
        await DeniedAsync(() => Chat.SetPolicyAsync(new(0, true, true)));

        var foreign = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner", true);
        await SeedScopeAsync(foreign);
        _identity.Current = foreign;
        var notFound = await Assert.ThrowsAsync<TinaChatException>(() => Chat.GetParticipantAsync(team.Human));
        Assert.Equal(404, notFound.StatusCode);
        _identity.Current = original;
    }

    [Fact]
    public async Task CrossWorkspaceDiscoveryAndMessaging_RequireBothPolicies_AndRevocationAppliesToInbox()
    {
        var firstScope = _identity.Current;
        var a = await Chat.RegisterAsync(new("local", "Local", ReceiveHumanMessages: true));
        var secondScope = firstScope with { WorkspaceId = Guid.NewGuid(), PrincipalId = Guid.NewGuid() };
        await SeedScopeAsync(secondScope);
        _identity.Current = secondScope;
        var b = await Chat.RegisterAsync(new("remote", "Remote", ReceiveHumanMessages: true));
        await Chat.SetPolicyAsync(new(0, true, true));
        _identity.Current = firstScope;
        Assert.DoesNotContain(await Chat.DiscoverAsync(), x => x.Id == b.Id);
        await Chat.SetPolicyAsync(new(0, true, true));
        Assert.Contains(await Chat.DiscoverAsync(), x => x.Id == b.Id);
        await DeniedAsync(() => Chat.CreateConversationAsync(new(a.Id, "Closed", [b.Id], "direct", false)));
        var conversation = await Chat.CreateConversationAsync(new(a.Id, "Joint", [b.Id], "direct", true));
        _identity.Current = secondScope;
        await JoinAsync(conversation.Id, b.Id);
        _identity.Current = firstScope;
        await Chat.SendAsync(conversation.Id, new(a.Id, "Allowed cross-workspace message", "cross"));
        await DeniedAsync(() => Chat.SendAsync(conversation.Id, new(a.Id, "Confidential", "confidential", [b.Id], Sensitivity: "confidential")));
        _identity.Current = secondScope;
        Assert.Single((await Chat.ReadInboxAsync(b.Id)).Items);
        await Chat.SetPolicyAsync(new(1, true, false));
        Assert.Empty((await Chat.ReadInboxAsync(b.Id)).Items);
        await DeniedAsync(() => Chat.ReadMessagesAsync(conversation.Id, b.Id));
        _identity.Current = firstScope;
    }

    [Fact]
    public async Task InvitationConsent_LateJoin_AndRejoin_DoNotExposePriorHistory()
    {
        var human = await Chat.RegisterAsync(new("owner", "Owner", "human"));
        var agent = await Chat.RegisterAsync(new("invitee", "Invitee", ReceiveHumanMessages: true));
        var group = await Chat.CreateConversationAsync(new(human.Id, "Invitation", [agent.Id]));
        await DeniedAsync(() => Chat.ReadMessagesAsync(group.Id, agent.Id));
        await DeniedAsync(() => Chat.ChangeMemberAsync(group.Id, new(human.Id, agent.Id, "accept", group.Revision)));
        await Chat.SendAsync(group.Id, new(human.Id, "Before join", "before"));
        await JoinAsync(group.Id, agent.Id);
        Assert.Empty((await Chat.ReadMessagesAsync(group.Id, agent.Id)).Items);
        await Chat.SendAsync(group.Id, new(human.Id, "After join", "after"));
        Assert.Single((await Chat.ReadMessagesAsync(group.Id, agent.Id)).Items);
        var revision = (await Chat.GetConversationAsync(group.Id, human.Id)).Revision;
        await DeniedAsync(() => Chat.ChangeMemberAsync(group.Id, new(agent.Id, human.Id, "remove", revision)));
        await Chat.ChangeMemberAsync(group.Id, new(human.Id, agent.Id, "remove", revision));
        Assert.Empty((await Chat.ReadInboxAsync(agent.Id)).Items);
        revision = (await Chat.GetConversationAsync(group.Id, human.Id)).Revision;
        await Chat.ChangeMemberAsync(group.Id, new(human.Id, agent.Id, "invite", revision));
        await JoinAsync(group.Id, agent.Id);
        Assert.Empty((await Chat.ReadMessagesAsync(group.Id, agent.Id)).Items);
    }

    [Fact]
    public async Task DuplicateAndConcurrentSends_PersistOneMessageAndOneInboxEntryAcrossRestart()
    {
        var team = await TeamAsync();
        var request = new TinaChatSendMessageRequest(team.Interpreter, "Durable response", "same-key");
        var sends = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Chat.SendAsync(team.Conversation, request)));
        Assert.Single(sends.Select(x => x.Id).Distinct());
        var conflict = await Assert.ThrowsAsync<TinaChatException>(() => Chat.SendAsync(team.Conversation, request with { Content = "Different" }));
        Assert.Equal("idempotency_key_reuse", conflict.Code);
        await Chat.AcknowledgeAsync(team.Worker, sends[0].Id);
        await Chat.AcknowledgeAsync(team.Worker, sends[0].Id);
        _http.Dispose(); _factory.Dispose();
        StartHost();
        var inbox = Assert.Single((await Chat.ReadInboxAsync(team.Worker)).Items);
        Assert.Equal(sends[0].Id, inbox.Message.Id);
        Assert.True(inbox.Acknowledged);
        Assert.Equal(sends[0].Id, (await Chat.SendAsync(team.Conversation, request)).Id);
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.Messages.CountAsync(x => x.ConversationId == team.Conversation));
        Assert.Equal(3, await db.Audiences.CountAsync(x => x.MessageId == sends[0].Id));
    }

    [Fact]
    public async Task InterpretationUsesOnlySelectedSources_AndUnresolvedBriefCannotExecute()
    {
        var team = await TeamAsync();
        await Chat.SendAsync(team.Conversation, new(team.Human, "HIDDEN_SOURCE", "hidden", [team.Human]));
        var source = await Chat.SendAsync(team.Conversation, new(team.Human, "Could we make the page faster?", "visible", AllowDerivedSharing: true));
        var revision = (await Chat.GetConversationAsync(team.Conversation, team.Interpreter)).Revision;
        var generated = await Chat.GenerateIntentAsync(team.Conversation, new(team.Interpreter, "generate", revision, [source.Id], [team.Human, team.Worker]));
        var call = Assert.Single(_model.Calls);
        Assert.Contains("Could we make", call.Prompt);
        Assert.DoesNotContain("HIDDEN_SOURCE", call.Prompt);
        Assert.Contains("ambiguous", call.Instructions);
        Assert.Empty(call.ToolNames);
        Assert.Equal("proposed", generated.Status);
        Assert.Equal(generated.Id, (await Chat.GenerateIntentAsync(team.Conversation, new(team.Interpreter, "generate", revision, [source.Id], [team.Human, team.Worker]))).Id);
        Assert.Single(_model.Calls);

        revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        await Chat.DecideIntentAsync(team.Conversation, generated.Id, new(team.Human, "accepted", revision));
        var blocked = await Assert.ThrowsAsync<TinaChatException>(() => Chat.ReserveExecutionAsync(team.Conversation, generated.Id, new(team.Worker, Guid.NewGuid())));
        Assert.Equal("intent_requires_clarification", blocked.Code);
    }

    [Fact]
    public async Task AcceptedHandoff_UsesIsolatedContextAndRealCoreModelPath_RejectsRawInsertion()
    {
        var team = await TeamAsync();
        var original = await Chat.SendAsync(team.Conversation, new(team.Human, "PRIVATE_ORIGINAL_73 Do a bounded investigation.", "source", AllowDerivedSharing: true));
        var intent = await ProposeAsync(team, original.Id);
        var revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        await Chat.DecideIntentAsync(team.Conversation, intent.Id, new(team.Human, "accepted", revision));
        await using var cfg = await _factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        var modeId = (await cfg.WorkspaceDefaults.SingleAsync(x => x.TenantId == _identity.Current.TenantId && x.WorkspaceId == _identity.Current.WorkspaceId)).DefaultModeVersionId!.Value;
        var request = new TinaChatExecuteIntentRequest(team.Worker, modeId);
        var reservation = await Chat.ReserveExecutionAsync(team.Conversation, intent.Id, request);
        var mode = await cfg.ModeVersions.SingleAsync(x => x.Id == modeId);
        var definitions = await cfg.AgentDefinitions.Select(x => new ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson)).ToArrayAsync();
        var root = ConversationIdentityResolver.Resolve(mode.SnapshotJson, definitions)!;
        await _factory.Services.GetRequiredService<ProjectSessionStore>().CreateSessionAsync(null, "Isolated", modeId,
            conversationNodeKey: root.NodeKey, conversationTemplateSlug: root.TemplateSlug, stableSessionId: reservation.Execution.SessionId);
        // Canary models a caller appending unrelated history through a legacy API.
        await _factory.Services.GetRequiredService<IConversationStore>().AppendMessageAsync(reservation.Execution.SessionId, "user", "UNAUTHORIZED_SESSION_CANARY");
        var context = await _factory.Services.GetRequiredService<IContextProvider>().BuildContextAsync(new ContextBuildRequest(
            reservation.Execution.SessionId.ToString(), null, AgentId: "independent"));
        Assert.DoesNotContain(context.Evidence, x => x.Content.Contains("PRIVATE_ORIGINAL_73") || x.Content.Contains("UNAUTHORIZED_SESSION_CANARY"));
        Assert.Contains(context.Evidence, x => x.Source == "accepted_intent");
        _model.PlannerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _model.PlannerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execute = await _http.PostAsJsonAsync($"/api/v1/tina-chat/conversations/{team.Conversation}/intents/{intent.Id}/execute", request, Wire);
        Assert.True(execute.IsSuccessStatusCode, await execute.Content.ReadAsStringAsync());
        var receipt = (await execute.Content.ReadFromJsonAsync<TinaChatExecutionDto>(Wire))!;
        Assert.NotNull(receipt.RunId);
        try
        {
            await _model.PlannerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var store = _factory.Services.GetRequiredService<IConversationStore>();
            var baseRevision = await store.GetContextRevisionAsync(receipt.SessionId);
            var patch = await store.ApplyContextPatchAsync(new ContextPatchRequest(receipt.SessionId, baseRevision,
                "UNAUTHORIZED_PATCH_CANARY", "Legacy patch", receipt.RunId, Kind: "goal_adjustment"));
            Assert.Equal("applied", patch.Status);
        }
        finally { _model.PlannerGate.TrySetResult(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stream = _factory.Services.GetRequiredService<IFullDuplexRunCoordinator>().FollowAsync(receipt.RunId!.Value, cancellationToken: timeout.Token);
        var chunks = new List<RunStreamChunk>();
        try
        {
            await foreach (var chunk in stream) chunks.Add(chunk);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
            var state = await lifecycle.GetRunStateAsync(receipt.RunId.Value.ToString());
            var checkpoint = await lifecycle.GetCurrentRunCheckpointAsync(receipt.RunId.Value.ToString());
            Assert.Fail($"Handoff did not terminate. Status={state.Status}; checkpoint={checkpoint?.Content}; model calls={_model.Calls.Count}.");
        }
        Assert.Contains(chunks, x => x.Kind == "done" && x.FinishReason == "completed");
        Assert.Contains(_model.Calls, x => x.Instructions.Contains("执行以下任务"));
        Assert.All(_model.Calls, x =>
        {
            Assert.DoesNotContain("PRIVATE_ORIGINAL_73", x.Prompt + x.Instructions);
            Assert.DoesNotContain("UNAUTHORIZED_SESSION_CANARY", x.Prompt + x.Instructions);
            Assert.DoesNotContain("UNAUTHORIZED_PATCH_CANARY", x.Prompt + x.Instructions);
        });
        var replay = await _factory.Services.GetRequiredService<ITinaChatRunService>().ExecuteAsync(team.Conversation, intent.Id, request);
        Assert.Equal(receipt.RunId, replay.RunId);
        Assert.Equal(receipt.SessionId, replay.SessionId);
        var injected = await _http.PostAsJsonAsync($"/api/v1/sessions/{receipt.SessionId}/interactions", new
        {
            content = "RAW_INSERT_ATTEMPT", client_message_id = "insert-raw", dispatch_mode = "insert", target_run_id = receipt.RunId
        });
        Assert.Equal(HttpStatusCode.Forbidden, injected.StatusCode);
        using var problem = JsonDocument.Parse(await injected.Content.ReadAsStringAsync());
        Assert.Equal("https://tinadec.dev/errors/tina_chat_input_locked", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("tina_chat_input_locked", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("tina_chat_input_locked", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(403, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, _logs.ExceptionHandlerErrors);
    }

    [Fact]
    public async Task HttpContracts_UseSnakeCase_RejectUnknownSenderFields_AndEnforceRevision()
    {
        var response = await _http.PostAsJsonAsync("/api/v1/tina-chat/participants", new TinaChatRegisterParticipantRequest("named", "Named", "human"), Wire);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("display_name", text);
        Assert.DoesNotContain("owner_principal_id", text);
        var participant = (await response.Content.ReadFromJsonAsync<TinaChatParticipantDto>(Wire))!;
        var invalid = await _http.PostAsJsonAsync("/api/v1/tina-chat/participants", new { handle = "forged", display_name = "Forged", principal_id = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var stale = await _http.PatchAsJsonAsync($"/api/v1/tina-chat/participants/{participant.Id}", new TinaChatUpdateParticipantRequest(0, "Changed"), Wire);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var collision = await Assert.ThrowsAsync<TinaChatException>(() => Chat.RegisterAsync(new("NAMED", "Same normalized handle")));
        Assert.Equal("participant_handle_exists", collision.Code);
    }

    [Fact]
    public async Task NewIntentAndMembershipRevocation_InvalidateBoundInputsWithoutRawFallback()
    {
        var team = await TeamAsync();
        var source = await Chat.SendAsync(team.Conversation, new(team.Human, "Original context", "source", AllowDerivedSharing: true));
        var first = await ProposeAsync(team, source.Id);
        var revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        await Chat.DecideIntentAsync(team.Conversation, first.Id, new(team.Human, "accepted", revision));
        var reservation = await Chat.ReserveExecutionAsync(team.Conversation, first.Id, new(team.Worker, Guid.NewGuid()));
        var second = await ProposeAsync(team, source.Id);
        revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        var stale = await Assert.ThrowsAsync<TinaChatException>(() => Chat.DecideIntentAsync(team.Conversation, second.Id, new(team.Human, "accepted", revision - 1)));
        Assert.Equal(412, stale.StatusCode);
        await Chat.DecideIntentAsync(team.Conversation, second.Id, new(team.Human, "accepted", revision));
        var reader = _factory.Services.GetRequiredService<ITinaChatRunInput>();
        var superseded = await Assert.ThrowsAsync<TinaChatException>(() => reader.GetForSessionAsync(reservation.Execution.SessionId));
        Assert.Equal("intent_not_accepted", superseded.Code);
        revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        await Chat.ChangeMemberAsync(team.Conversation, new(team.Human, team.Worker, "remove", revision));
        await DeniedAsync(() => _factory.Services.GetRequiredService<IContextProvider>().BuildContextAsync(
            new ContextBuildRequest(reservation.Execution.SessionId.ToString(), null)));
    }

    [Fact]
    public async Task DisabledPack_CannotBeExecutedThroughTheChatAdapter()
    {
        var team = await TeamAsync();
        var source = await Chat.SendAsync(team.Conversation, new(team.Human, "Agreed work", "source", AllowDerivedSharing: true));
        var brief = await ProposeAsync(team, source.Id);
        var revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        await Chat.DecideIntentAsync(team.Conversation, brief.Id, new(team.Human, "accepted", revision));
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        var modeId = (await db.WorkspaceDefaults.SingleAsync()).DefaultModeVersionId!.Value;
        var installation = await db.AgentPackInstallations.SingleAsync();
        installation.Status = "disabled";
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<TinaChatException>(() => _factory.Services.GetRequiredService<ITinaChatRunService>()
            .ExecuteAsync(team.Conversation, brief.Id, new(team.Worker, modeId)));
        Assert.Equal("pack_disabled", error.Code);
        await using var chatDb = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        Assert.False(await chatDb.Executions.AnyAsync());
    }

    [Fact]
    public async Task Observer_ReadsPrivateGroupsAndDirectMessages_WithoutJoiningOrAcknowledging()
    {
        var team = await TeamAsync();
        var source = await Chat.SendAsync(team.Conversation, new(team.Human, "Private original for the interpreter", "observer-source",
            [team.Human, team.Interpreter], Sensitivity: "confidential"));
        var direct = await Chat.CreateConversationAsync(new(team.Interpreter, "Private agent discussion", [team.Worker], "direct"));
        await JoinAsync(direct.Id, team.Worker);
        var dm = await Chat.SendAsync(direct.Id, new(team.Interpreter, "Independent agent reply", "observer-dm", [team.Worker]));
        var originalScope = _identity.Current;
        var observerScope = originalScope with { PrincipalId = Guid.NewGuid() };
        await SeedScopeAsync(observerScope);
        _identity.Current = observerScope;
        var observer = _factory.Services.GetRequiredService<ITinaChatObserver>();
        var list = await observer.ObserveConversationsAsync();
        Assert.Equal(2, list.Total);
        Assert.Contains(list.Items, x => x.Kind == "group");
        Assert.Contains(list.Items, x => x.Kind == "direct");
        var detail = await observer.ObserveConversationAsync(team.Conversation);
        Assert.Equal(3, detail.Members.Length);
        var http = await _http.GetAsync($"/api/v1/tina-chat/observer/conversations/{team.Conversation}/messages");
        Assert.True(http.IsSuccessStatusCode, await http.Content.ReadAsStringAsync());
        Assert.True(http.Headers.CacheControl?.NoStore);
        var page = (await http.Content.ReadFromJsonAsync<TinaChatObservedMessagePage>(Wire))!;
        var message = Assert.Single(page.Items);
        Assert.Equal(source.Content, message.Message.Content);
        Assert.Equal("confidential", message.Message.Sensitivity);
        Assert.Equal(team.Human, message.Sender.Id);
        Assert.Equal(new[] { team.Human, team.Interpreter }.Order(), message.Audience.Select(x => x.Participant.Id).Order());
        Assert.Equal(dm.Id, Assert.Single((await observer.ObserveMessagesAsync(direct.Id)).Items).Message.Id);
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();
        Assert.Equal(3, await db.Participants.CountAsync());
        Assert.False(await db.Audiences.Where(x => x.MessageId == dm.Id && x.ParticipantId == team.Worker).Select(x => x.Acknowledged).SingleAsync());
        Assert.False(await db.Executions.AnyAsync());
        Assert.Contains(await db.Audit.ToArrayAsync(), x => x.Action == "observer.conversation_opened" && x.PrincipalId == observerScope.PrincipalId && x.ActorId == null);
        _identity.Current = originalScope;
        Assert.Empty((await Chat.ReadMessagesAsync(team.Conversation, team.Worker)).Items);
        Assert.Empty(_model.Calls);
    }

    [Fact]
    public async Task Observer_UsesVerifiedAdministratorScope_RejectsSpoofingAndCrossTenantReads()
    {
        var team = await TeamAsync();
        var owner = _identity.Current;
        var member = owner with { PrincipalId = Guid.NewGuid(), Role = "member" };
        await SeedScopeAsync(member);
        _identity.Current = member with { Role = "owner" }; // Untrusted role text must not grant access.
        var observer = _factory.Services.GetRequiredService<ITinaChatObserver>();
        foreach (var path in new[] { "/access", "/conversations", $"/conversations/{team.Conversation}", $"/conversations/{team.Conversation}/messages" })
        {
            var response = await _http.GetAsync("/api/v1/tina-chat/observer" + path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        await using var tenancy = await _factory.Services.GetRequiredService<IDbContextFactory<TenancyDbContext>>().CreateDbContextAsync();
        var workspaceMembership = await tenancy.WorkspaceMemberships.SingleAsync(x => x.WorkspaceId == owner.WorkspaceId && x.PrincipalId == member.PrincipalId);
        workspaceMembership.Role = "admin";
        await tenancy.SaveChangesAsync();
        Assert.Equal("workspace", (await observer.GetObserverAccessAsync()).Level);
        Assert.Single((await observer.ObserveConversationsAsync()).Items);

        var remote = owner with { WorkspaceId = Guid.NewGuid(), PrincipalId = Guid.NewGuid() };
        await SeedScopeAsync(remote);
        _identity.Current = remote;
        var remoteTeam = await TeamAsync();
        _identity.Current = member;
        Assert.Single((await observer.ObserveConversationsAsync()).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<TinaChatException>(() => observer.ObserveMessagesAsync(remoteTeam.Conversation))).StatusCode);
        _identity.Current = owner;
        Assert.Equal("tenant", (await observer.GetObserverAccessAsync()).Level);
        Assert.Equal(2, (await observer.ObserveConversationsAsync()).Total); // Tenant owner observes both, despite closed communication policies.

        var otherTenant = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner", true);
        await SeedScopeAsync(otherTenant);
        _identity.Current = otherTenant;
        Assert.Empty((await observer.ObserveConversationsAsync()).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<TinaChatException>(() => observer.ObserveConversationAsync(team.Conversation))).StatusCode);
        workspaceMembership.Role = "member";
        await tenancy.SaveChangesAsync();
        _identity.Current = member;
        await DeniedAsync(() => observer.ObserveMessagesAsync(team.Conversation));
        _identity.Current = owner;
    }

    [Fact]
    public async Task Observer_PagesHistoryAndFollowsMessages_WithFullSourceAndArchivedAuthor()
    {
        var team = await TeamAsync();
        var first = await Chat.SendAsync(team.Conversation, new(team.Interpreter, "First", "observer-first"));
        var second = await Chat.SendAsync(team.Conversation, new(team.Interpreter, "Second", "observer-second", ReplyToMessageId: first.Id));
        var third = await Chat.SendAsync(team.Conversation, new(team.Interpreter, "Third", "observer-third"));
        var author = await Chat.GetParticipantAsync(team.Interpreter);
        await Chat.UpdateParticipantAsync(author.Id, new(author.Revision, "Archived specialist", Status: "archived", Discoverable: false));
        var observer = _factory.Services.GetRequiredService<ITinaChatObserver>();
        var latest = await observer.ObserveMessagesAsync(team.Conversation, limit: 2);
        Assert.Equal(new[] { second.Id, third.Id }, latest.Items.Select(x => x.Message.Id));
        Assert.True(latest.HasMore);
        Assert.Equal(first.Id, latest.Items[0].Message.ReplyToMessageId);
        Assert.Contains(first.Id, latest.Items[0].Message.SourceMessageIds);
        Assert.All(latest.Items, x => Assert.Equal("archived", x.Sender.Status));
        var older = await observer.ObserveMessagesAsync(team.Conversation, beforeSequence: latest.OldestSequence, limit: 2);
        Assert.Equal(first.Id, Assert.Single(older.Items).Message.Id);
        Assert.False(older.HasMore);
        var follow = await observer.ObserveMessagesAsync(team.Conversation, afterSequence: first.Sequence, limit: 1);
        Assert.Equal(second.Id, Assert.Single(follow.Items).Message.Id);
        Assert.True(follow.HasMore);
        Assert.Empty((await observer.ObserveMessagesAsync(team.Conversation, afterSequence: third.Sequence)).Items);
        Assert.Single((await observer.ObserveConversationsAsync(query: "Archived specialist")).Items);
        var invalid = await _http.GetAsync($"/api/v1/tina-chat/observer/conversations/{team.Conversation}/messages?after_sequence=1&before_sequence=3");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task CommittedMessage_QueuesADurableTurn_ThatSurvivesRestartAndFilesOneBrief()
    {
        var team = await TeamAsync();
        var source = await Chat.SendAsync(team.Conversation, new(team.Human, "Could we make the page faster?", "wake-source", AllowDerivedSharing: true));
        await using (var db = await DbAsync())
        {
            var queued = Assert.Single(await db.Wakes.Where(x => x.ConversationId == team.Conversation && x.ParticipantId == team.Interpreter).ToArrayAsync());
            Assert.Equal("pending", queued.Status);
            Assert.Equal(new[] { source.Id }, JsonSerializer.Deserialize<Guid[]>(queued.SourceMessageIdsJson, Wire));
        }

        // The owed turn belongs to the database, not to the process that wrote the message.
        _http.Dispose(); _factory.Dispose();
        StartHost();
        Assert.Equal(1, await WakesAsync().RunPassAsync(5));

        var brief = Assert.Single(await Chat.ListIntentsAsync(team.Conversation, team.Interpreter));
        Assert.Equal("proposed", brief.Status);
        Assert.Equal(new[] { source.Id }, brief.SourceMessageIds.OrderBy(x => x).ToArray());
        var call = Assert.Single(_model.Calls);
        Assert.Contains("Could we make", call.Prompt);
        Assert.Empty(call.ToolNames);
        Assert.Contains((await Chat.ReadInboxAsync(team.Human)).Items, x => x.Message.Kind == "intent_brief");

        await using (var db = await DbAsync())
        {
            var settled = Assert.Single(await db.Wakes.ToArrayAsync());
            Assert.Equal("done", settled.Status);
        }
        // A brief is the interpreter's own output: the next move belongs to a human decision, so
        // draining again must not chain another turn.
        Assert.Equal(0, await WakesAsync().RunPassAsync(5));
        Assert.Single(_model.Calls);
    }

    [Fact]
    public async Task ArrivalsDuringAPendingTurn_CoalesceIntoOneBrief_WithoutWakingTheSender()
    {
        var team = await TeamAsync();
        var first = await Chat.SendAsync(team.Conversation, new(team.Human, "First observation request", "coalesce-1", AllowDerivedSharing: true));
        var second = await Chat.SendAsync(team.Conversation, new(team.Human, "Second observation request", "coalesce-2", AllowDerivedSharing: true));
        await using var db = await DbAsync();
        var queued = Assert.Single(await db.Wakes.ToArrayAsync());
        Assert.Equal(new[] { first.Id, second.Id }.OrderBy(x => x).ToArray(), JsonSerializer.Deserialize<Guid[]>(queued.SourceMessageIdsJson, Wire));

        // The only interpreter already holds the floor, and it never answers itself.
        await Chat.SendAsync(team.Conversation, new(team.Interpreter, "Noting the shape of the problem", "self-note"));
        Assert.Single(await db.Wakes.ToArrayAsync());

        Assert.Equal(1, await WakesAsync().RunPassAsync(5));
        var call = Assert.Single(_model.Calls);
        Assert.Contains("First observation request", call.Prompt);
        Assert.Contains("Second observation request", call.Prompt);
        var brief = Assert.Single(await Chat.ListIntentsAsync(team.Conversation, team.Interpreter));
        Assert.Equal(2, brief.SourceMessageIds.Length);
    }

    [Fact]
    public async Task AdmittedHandoff_CompletingItsRun_AnnouncesTheOutcomeBackIntoTheConversation()
    {
        var team = await TeamAsync();
        var source = await Chat.SendAsync(team.Conversation, new(team.Human, "Make the settings page load faster.", "outcome-source", AllowDerivedSharing: true));
        // Let the interpreter's own turn land first, so the pass under test carries only the outcome.
        await WakesAsync().RunPassAsync(5);
        var intent = await ProposeAsync(team, source.Id);
        var revision = (await Chat.GetConversationAsync(team.Conversation, team.Human)).Revision;
        await Chat.DecideIntentAsync(team.Conversation, intent.Id, new(team.Human, "accepted", revision));
        await using var cfg = await _factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        var modeId = (await cfg.WorkspaceDefaults.SingleAsync(x => x.TenantId == _identity.Current.TenantId && x.WorkspaceId == _identity.Current.WorkspaceId)).DefaultModeVersionId!.Value;

        var execute = await _http.PostAsJsonAsync($"/api/v1/tina-chat/conversations/{team.Conversation}/intents/{intent.Id}/execute",
            new TinaChatExecuteIntentRequest(team.Worker, modeId), Wire);
        Assert.True(execute.IsSuccessStatusCode, await execute.Content.ReadAsStringAsync());
        var receipt = (await execute.Content.ReadFromJsonAsync<TinaChatExecutionDto>(Wire))!;
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        RunState state = new();
        for (var attempt = 0; attempt < 200 && !RunStatusMachine.IsTerminal(state.Status.ToString()); attempt++)
        {
            await Task.Delay(50);
            state = await lifecycle.GetRunStateAsync(receipt.RunId!.Value.ToString("N"));
        }
        Assert.Equal(RunStatus.Completed, state.Status);

        Assert.Equal(1, await WakesAsync().RunPassAsync(5));
        var history = await Chat.ReadMessagesAsync(team.Conversation, team.Human);
        var outcome = Assert.Single(history.Items, x => x.Kind == "result");
        Assert.Equal(team.Worker, outcome.SenderId);
        Assert.Equal("agent", outcome.SenderKind);
        Assert.Contains("completed", outcome.Content);
        // The outcome speaks about a brief, so it carries exactly one cited brief as its source.
        var briefIds = history.Items.Where(x => x.Kind == "intent_brief").Select(x => x.Id).ToArray();
        Assert.Contains(Assert.Single(outcome.SourceMessageIds), briefIds);
        // The group now has something new for the interpreter to react to, so the loop is still open.
        await using (var db = await DbAsync())
            Assert.Contains(await db.Wakes.ToArrayAsync(), x => x.Status == "pending" && x.ParticipantId == team.Interpreter);
        await using (var chat = await DbAsync())
            Assert.NotNull((await chat.Executions.SingleAsync(x => x.Id == receipt.Id)).ResultRunStatus);
    }

    [Fact]
    public async Task BoundSessionSpeaksInTheRoom_AndTheOtherInterpreterIsQueuedATurn()
    {
        var team = await TeamAsync();
        // A participant can only accept an invitation it was actually given, so invite first.
        var oracle = await Chat.RegisterAsync(new("oracle", "Second interpreter", ReceiveHumanMessages: true, CanInterpretIntent: true));
        var host = await Chat.GetConversationAsync(team.Conversation, team.Human);
        await Chat.ChangeMemberAsync(team.Conversation, new(team.Human, oracle.Id, "invite", host.Revision));
        await JoinAsync(team.Conversation, oracle.Id);
        var session = await NewSessionAsync();
        var tools = ToolsAsync();

        var bound = await tools.ExecuteAsync(ToolCall(session, "tina_chat_bind", Args(("handle", "thought-partner"))));
        Assert.True(bound.IsSuccess, bound.Error);
        Assert.Contains("bound", bound.ResultJson);

        var rooms = await tools.ExecuteAsync(ToolCall(session, "tina_chat_list_rooms", Args()));
        Assert.True(rooms.IsSuccess, rooms.Error);
        var room = JsonDocument.Parse(rooms.ResultJson).RootElement.GetProperty("rooms")[0];
        Assert.Equal(team.Conversation.ToString("N"), room.GetProperty("conversation_id").GetString());

        var sent = await tools.ExecuteAsync(ToolCall(session, "tina_chat_send", Args(
            ("conversation_id", team.Conversation.ToString("N")),
            ("content", "I checked the two slow paths; the settings page rebuilds the model list on every keystroke."))));
        Assert.True(sent.IsSuccess, sent.Error);

        var heard = await Chat.ReadMessagesAsync(team.Conversation, team.Human);
        var posted = Assert.Single(heard.Items, x => x.Kind == "message" && x.SenderId == team.Interpreter);
        Assert.Contains("settings page rebuilds", posted.Content);

        // Speaking in the room owes the same durable turn as a human message would.
        await using var db = await DbAsync();
        var queued = Assert.Single(await db.Wakes.Where(x => x.ParticipantId == oracle.Id).ToArrayAsync());
        Assert.Equal("pending", queued.Status);
        Assert.Contains(posted.Id, JsonSerializer.Deserialize<Guid[]>(queued.SourceMessageIdsJson, Wire) ?? []);
    }

    [Fact]
    public async Task Tools_RefuseAnUnboundSession_AnUnentitledAudienceAndReplayTheSameCallOnce()
    {
        var team = await TeamAsync();
        var session = await NewSessionAsync();
        var tools = ToolsAsync();

        var voiceless = await tools.ExecuteAsync(ToolCall(session, "tina_chat_list_rooms", Args()));
        Assert.False(voiceless.IsSuccess);
        Assert.Contains("tina_chat_bind", voiceless.Error);

        Assert.True((await tools.ExecuteAsync(ToolCall(session, "tina_chat_bind", Args(("handle", "thought-partner"))))).IsSuccess);

        var outsider = await Chat.RegisterAsync(new("outsider", "Outsider", ReceiveHumanMessages: true, CanInterpretIntent: true));
        var badAudience = await tools.ExecuteAsync(ToolCall(session, "tina_chat_send", Args(
            ("conversation_id", team.Conversation.ToString("N")),
            ("content", "addressing someone who is not here"),
            ("audience_handles", new[] { "outsider" })), callId: 3));
        Assert.False(badAudience.IsSuccess);
        Assert.Contains("not an active member", badAudience.Error);

        var first = await tools.ExecuteAsync(ToolCall(session, "tina_chat_send", Args(
            ("conversation_id", team.Conversation.ToString("N")),
            ("content", "one message, keyed by its call id")), callId: 11));
        var replay = await tools.ExecuteAsync(ToolCall(session, "tina_chat_send", Args(
            ("conversation_id", team.Conversation.ToString("N")),
            ("content", "one message, keyed by its call id")), callId: 11));
        Assert.True(first.IsSuccess, first.Error);
        Assert.Equal(first.ResultJson, replay.ResultJson);

        await using var db = await DbAsync();
        Assert.Equal(1, await db.Messages.CountAsync(x => x.ConversationId == team.Conversation && x.Kind == "message"));
        Assert.Equal(1, await db.SessionIdentities.CountAsync(x => x.SessionId == session));
        Assert.NotNull(outsider);
    }

    [Fact]
    public async Task Tools_StayInsideWhatTheParticipantMayHear_AndWhatItIsConfiguredToDo()
    {
        var team = await TeamAsync();
        var session = await NewSessionAsync();
        var tools = ToolsAsync();
        Assert.True((await tools.ExecuteAsync(ToolCall(session, "tina_chat_bind", Args(("handle", "independent"))))).IsSuccess);

        // The worker receives no human originals, so a human-only note never reaches its page.
        await Chat.SendAsync(team.Conversation, new(team.Human, "HUMAN_ONLY_NOTE", "tool-private", [team.Human]));
        var agentNote = await Chat.SendAsync(team.Conversation, new(team.Interpreter, "An agent-authored note the worker may read", "tool-visible"));
        var inbox = await tools.ExecuteAsync(ToolCall(session, "tina_chat_read_inbox", Args()));
        Assert.True(inbox.IsSuccess, inbox.Error);
        Assert.DoesNotContain("HUMAN_ONLY_NOTE", inbox.ResultJson);
        Assert.Contains("agent-authored note", inbox.ResultJson);

        // The worker holds no interpretation capability: it may speak, not file a brief.
        var proposed = await tools.ExecuteAsync(ToolCall(session, "tina_chat_propose_intent", Args(
            ("conversation_id", team.Conversation.ToString("N")),
            ("source_message_ids", new[] { agentNote.Id.ToString("N") }),
            ("goal", "Do the thing"),
            ("user_statements", new[] { "the user asked" }),
            ("constraints", Array.Empty<string>()), ("assumptions", Array.Empty<string>()),
            ("open_questions", Array.Empty<string>()), ("blocking_questions", Array.Empty<string>()),
            ("acceptance_criteria", new[] { "it works" }))));
        Assert.False(proposed.IsSuccess);
        Assert.Contains("not configured to interpret intent", proposed.Error);
    }

    /// <summary>
    /// A missing required query parameter is the caller's mistake, not the host's. Minimal APIs throw
    /// BadHttpRequestException before the handler runs, so the catch-all must not turn "you forgot
    /// actor_id" into an opaque 500 that no client can act on.
    /// </summary>
    [Fact]
    public async Task Http_MissingRequiredActor_ReturnsActionableBadRequest_NotInternalError()
    {
        var response = await _http.GetAsync("/api/v1/tina-chat/conversations");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains("actor_id", problem.RootElement.GetProperty("detail").GetString());
    }

    /// <summary>
    /// A room reaches an authorised goal without any human click: the brief is read, then accepted by
    /// whoever holds the conversation role. Authorship does not confer the right to decide, and a
    /// decision carries the revision it was read at so a newer one cannot be silently overwritten.
    /// </summary>
    [Fact]
    public async Task Tools_DecideBriefsAsTheConversationRole_NotAsAHuman()
    {
        var team = await TeamAsync();
        var session = await NewSessionAsync();
        var tools = ToolsAsync();
        Assert.True((await tools.ExecuteAsync(ToolCall(session, "tina_chat_bind", Args(("handle", "thought-partner"))))).IsSuccess);
        var humanNote = await Chat.SendAsync(team.Conversation, new(team.Human, "please make greet reject empty input", "decide-1"));

        var filed = await tools.ExecuteAsync(ToolCall(session, "tina_chat_propose_intent", Args(
            ("conversation_id", team.Conversation.ToString("N")),
            ("source_message_ids", new[] { humanNote.Id.ToString("N") }),
            ("goal", "Reject empty input in greet"),
            ("user_statements", new[] { "please make greet reject empty input" }),
            ("constraints", Array.Empty<string>()), ("assumptions", Array.Empty<string>()),
            ("open_questions", Array.Empty<string>()), ("blocking_questions", Array.Empty<string>()),
            ("acceptance_criteria", new[] { "an empty argument is refused" }))));
        Assert.True(filed.IsSuccess, filed.Error);
        var intentId = JsonDocument.Parse(filed.ResultJson).RootElement.GetProperty("intent_id").GetString()!;

        var listed = await tools.ExecuteAsync(ToolCall(session, "tina_chat_list_intents", Args(("conversation_id", team.Conversation.ToString("N")))));
        Assert.True(listed.IsSuccess, listed.Error);
        var page = JsonDocument.Parse(listed.ResultJson).RootElement;
        var revision = page.GetProperty("conversation_revision").GetInt64();
        Assert.Equal("proposed", page.GetProperty("intents")[0].GetProperty("status").GetString());
        Assert.True(page.GetProperty("intents")[0].GetProperty("authored_by_you").GetBoolean());

        var refused = await tools.ExecuteAsync(ToolCall(session, "tina_chat_decide_intent", Args(
            ("conversation_id", team.Conversation.ToString("N")), ("intent_id", intentId),
            ("decision", "accepted"), ("expected_revision", revision))));
        Assert.False(refused.IsSuccess);
        Assert.Contains("Conversation administration permission is required", refused.Error);

        var owned = await Chat.GetConversationAsync(team.Conversation, team.Human);
        await Chat.ChangeMemberAsync(team.Conversation, new(team.Human, team.Interpreter, "role", owned.Revision, "admin"));
        var afterRole = await Chat.GetConversationAsync(team.Conversation, team.Human);
        var accepted = await tools.ExecuteAsync(ToolCall(session, "tina_chat_decide_intent", Args(
            ("conversation_id", team.Conversation.ToString("N")), ("intent_id", intentId),
            ("decision", "accepted"), ("expected_revision", afterRole.Revision))));
        Assert.True(accepted.IsSuccess, accepted.Error);
        Assert.Equal("accepted", JsonDocument.Parse(accepted.ResultJson).RootElement.GetProperty("status").GetString());

        await using var db = await DbAsync();
        var conversation = await db.Conversations.SingleAsync(x => x.Id == team.Conversation);
        Assert.Equal(Guid.Parse(intentId), conversation.AcceptedIntentId);

        // The revision it read is now stale, and the brief is decided: neither may be rewritten.
        var stale = await tools.ExecuteAsync(ToolCall(session, "tina_chat_decide_intent", Args(
            ("conversation_id", team.Conversation.ToString("N")), ("intent_id", intentId),
            ("decision", "rejected"), ("expected_revision", revision))));
        Assert.False(stale.IsSuccess);
    }

    private static JsonElement Args(params (string Name, object Value)[] fields) =>
        JsonSerializer.SerializeToElement(fields.ToDictionary(x => x.Name, x => x.Value), Wire);

    private TinaChatToolCall ToolCall(Guid sessionId, string toolId, JsonElement args, long callId = 0)
    {
        var scope = _identity.Current;
        return new TinaChatToolCall(scope.TenantId, scope.WorkspaceId, scope.PrincipalId, sessionId, Guid.NewGuid(), callId, toolId, args);
    }

    private ITinaChatToolGateway ToolsAsync() => _factory.Services.GetRequiredService<ITinaChatToolGateway>();

    /// <summary>A real session row, so a tool binding points at something that exists.</summary>
    private async Task<Guid> NewSessionAsync()
    {
        var id = Guid.NewGuid();
        await using var cfg = await _factory.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync();
        var modeId = (await cfg.WorkspaceDefaults.SingleAsync(x => x.TenantId == _identity.Current.TenantId && x.WorkspaceId == _identity.Current.WorkspaceId)).DefaultModeVersionId!.Value;
        await _factory.Services.GetRequiredService<ProjectSessionStore>().CreateSessionAsync(null, "Tool session", modeId, stableSessionId: id);
        return id;
    }

    private async Task<TinaChatDbContext> DbAsync() =>
        await _factory.Services.GetRequiredService<IDbContextFactory<TinaChatDbContext>>().CreateDbContextAsync();

    private TinaChatWakeService WakesAsync() => _factory.Services.GetRequiredService<TinaChatWakeService>();

    private async Task<Team> TeamAsync()
    {
        var human = await Chat.RegisterAsync(new("human", "Human", "human"));
        var dialogue = await Chat.RegisterAsync(new("thought-partner", "Dialogue partner", ReceiveHumanMessages: true, CanInterpretIntent: true));
        var worker = await Chat.RegisterAsync(new("independent", "Independent engineer"));
        var conversation = await Chat.CreateConversationAsync(new(human.Id, "Focused collaboration", [dialogue.Id, worker.Id]));
        await JoinAsync(conversation.Id, dialogue.Id);
        await JoinAsync(conversation.Id, worker.Id);
        return new Team(human.Id, dialogue.Id, worker.Id, conversation.Id);
    }

    private async Task JoinAsync(Guid conversation, Guid participant)
    {
        var view = await Chat.GetConversationAsync(conversation, participant);
        await Chat.ChangeMemberAsync(conversation, new(participant, participant, "accept", view.Revision));
    }

    private async Task<TinaChatIntentDto> ProposeAsync(Team team, Guid source)
    {
        var view = await Chat.GetConversationAsync(team.Conversation, team.Interpreter);
        return await Chat.ProposeIntentAsync(team.Conversation, new(team.Interpreter, Guid.NewGuid().ToString("N"), view.Revision,
            [source], [team.Human, team.Worker], Brief()));
    }

    private static TinaChatIntentContent Brief() => new("Investigate the agreed page behavior", ["The user reports a slow page."],
        ["Produce evidence before changes."], ["The cause remains unverified."], ["Which path dominates latency?"], [], ["Report observed behavior."]);

    private static async Task DeniedAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TinaChatException>(action);
        Assert.Equal(403, error.StatusCode);
    }

    private async Task SeedScopeAsync(TenantContext scope)
    {
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<TenancyDbContext>>().CreateDbContextAsync();
        if (!await db.Tenants.AnyAsync(x => x.Id == scope.TenantId)) db.Tenants.Add(new TenantRecord { Id = scope.TenantId, Slug = scope.TenantId.ToString("N"), Name = "Test tenant" });
        if (!await db.Principals.AnyAsync(x => x.Id == scope.PrincipalId)) db.Principals.Add(new PrincipalRecord { Id = scope.PrincipalId, Issuer = "test", Subject = scope.PrincipalId.ToString("N"), DisplayName = "Test principal" });
        if (!await db.Workspaces.AnyAsync(x => x.Id == scope.WorkspaceId)) db.Workspaces.Add(new WorkspaceRecord { Id = scope.WorkspaceId, TenantId = scope.TenantId, Slug = scope.WorkspaceId.ToString("N"), Name = "Test workspace" });
        if (!await db.TenantMemberships.AnyAsync(x => x.TenantId == scope.TenantId && x.PrincipalId == scope.PrincipalId)) db.TenantMemberships.Add(new TenantMembershipRecord { TenantId = scope.TenantId, PrincipalId = scope.PrincipalId, Role = scope.Role });
        if (!await db.WorkspaceMemberships.AnyAsync(x => x.WorkspaceId == scope.WorkspaceId && x.PrincipalId == scope.PrincipalId)) db.WorkspaceMemberships.Add(new WorkspaceMembershipRecord { WorkspaceId = scope.WorkspaceId, PrincipalId = scope.PrincipalId, Role = scope.Role });
        await db.SaveChangesAsync();
    }

    private sealed record Team(Guid Human, Guid Interpreter, Guid Worker, Guid Conversation);
    private sealed class MutableIdentity : ITenantContextAccessor
    {
        public TenantContext Current { get; set; } = new(Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d001"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d002"), Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d003"), "owner", true);
    }

    private sealed class ChatFactory(string root, MutableIdentity identity, RecordingModel model, RecordingLoggerProvider logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logs);
            });
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                // The drain is a scheduler; tests drive one pass at a time so a background tick
                // cannot add model calls between an action and its assertion.
                ["TinadecTinaChat:WakeDrainEnabled"] = "false"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ITenantContextAccessor>(identity);
                services.AddSingleton<ISecretStore>(new TestModelSecretStore());
                services.AddSingleton<IAgentChatClientFactory>(new ModelFactory(model));
                services.AddSingleton<IToolManifestSnapshotResolver>(new EmptyManifest());
            });
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private int _exceptionHandlerErrors;
        public int ExceptionHandlerErrors => Volatile.Read(ref _exceptionHandlerErrors);

        public ILogger CreateLogger(string categoryName) =>
            categoryName.Contains("ExceptionHandlerMiddleware", StringComparison.Ordinal)
                ? new RecordingLogger(this)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private void Record(LogLevel level)
        {
            if (level >= LogLevel.Error) Interlocked.Increment(ref _exceptionHandlerErrors);
        }

        private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Record(logLevel);
        }
    }

    private sealed class EmptyManifest : IToolManifestSnapshotResolver
    {
        public Task<ToolManifestSnapshot> ResolveAsync(ToolManifestSnapshotRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolManifestSnapshot(2, ToolManifestHasher.Compute(Array.Empty<FrozenToolManifestEntry>()), []));
    }

    private sealed class ModelFactory(RecordingModel model) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "test", ApiKey = "test", ModelId = "test/model" });
        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) => Task.FromResult<IChatClient>(model);
    }

    private sealed record ModelCall(string Instructions, string Prompt, string[] ToolNames);
    private sealed class RecordingModel : IChatClient
    {
        private readonly FullDuplexEndpointTests.ScriptedChatClient _inner = new FullDuplexEndpointTests.ScriptedChatClient()
            .WhenPlanner("""[{"task_key":"inspect","title":"Inspect the agreed behavior","description":"Report the bounded observation","success_criteria":["Report observed behavior."],"dependencies":[],"required_capabilities":[],"required_tools":[],"priority":1,"risk":"low"}]""")
            .WhenWorker("The agreed observation is complete.\nTASK_OUTCOME: completed")
            .WhenSupervisor("""{"decision":"pass","reasons":[],"revise_task_indexes":[]}""")
            .WhenMeeting("The scoped investigation completed.");
        public ConcurrentQueue<ModelCall> Calls { get; } = new();
        public TaskCompletionSource? PlannerGate { get; set; }
        public TaskCompletionSource? PlannerStarted { get; set; }
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ModelMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToArray();
            Calls.Enqueue(new ModelCall(options?.Instructions ?? "", string.Join("\n", list.Select(x => x.Text)), options?.Tools?.Select(x => x.Name).ToArray() ?? []));
            if (options?.Instructions?.Contains("understanding a user's intent", StringComparison.Ordinal) == true)
            {
                var brief = Brief() with { BlockingQuestions = ["Clarify which page and permitted scope."] };
                return new ChatResponse(new ModelMessage(ChatRole.Assistant, JsonSerializer.Serialize(brief, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            }
            if (options?.Instructions?.Contains("任务规划智能体", StringComparison.Ordinal) == true && PlannerGate is { } gate)
            {
                PlannerStarted?.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
            }
            return await _inner.GetResponseAsync(list, options, cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ModelMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}
