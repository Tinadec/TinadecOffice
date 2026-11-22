using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Memory;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// High-signal Phase 11 tests covering TOML policy configuration, 3-tier context strategies
/// + always-on micro-compaction, 4-layer DB &lt;-&gt; token-capped Markdown projection + 1-click
/// revert, and conscious conversation history recall + HTTP surface.
/// </summary>
public sealed class ContextMemoryArchitectureTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _root = ApiTestStorage.CreateRoot("context-memory-arch");
    private ManagedFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, recursive: true); break; }
            catch (IOException) { await Task.Delay(250); }
        }
    }

    [Fact]
    public void RuntimeTomlBaseline_LoadsRichContextAndMemoryPolicies_AndValidatesBounds()
    {
        var baselinePath = Path.Combine(AppContext.BaseDirectory, "Configuration", "default-agent-runtime.toml");
        var snapshot = AgentRuntimeConfigurationStore.LoadSnapshot(baselinePath, 1);

        Assert.Equal("managed", snapshot.Context.Strategy);
        Assert.Equal("balanced", snapshot.Context.RecallFidelity);
        Assert.Equal(0.75, snapshot.Context.CompactionTriggerRatio, precision: 3);
        Assert.Equal(128, snapshot.Context.RecentMessageLimit);
        Assert.True(snapshot.Context.AllowCrossSessionRecall);
        Assert.True(snapshot.Context.MicroCompactStaleToolOutputs);
        Assert.Equal(16_384, snapshot.Context.OffloadLargeToolPayloadBytes);

        Assert.Equal("auto_evolve", snapshot.Memory.EvolutionMode);
        Assert.False(snapshot.Memory.EffectiveCandidateOnly);
        Assert.Equal(1500, snapshot.Memory.CoreMarkdownMaxTokens);
        Assert.True(snapshot.Memory.ProjectMarkdownProjection);
        Assert.Equal(".tinadec/memory/project-core.md", snapshot.Memory.ProjectMemoryPath);
        Assert.False(snapshot.Memory.CompanionCanWriteProjectMemory);
        Assert.Contains("companion", snapshot.Memory.AllowedScopes);
        Assert.Contains("bond", snapshot.Memory.AllowedKinds);
        Assert.Contains("episode", snapshot.Memory.AllowedKinds);

        var raw = File.ReadAllText(baselinePath);
        Assert.Throws<InvalidDataException>(() => AgentRuntimeConfigurationStore.LoadSnapshotText(
            raw.Replace("strategy = \"managed\"", "strategy = \"unknown_tier\"", StringComparison.Ordinal),
            "runtime.toml", 1));
        Assert.Throws<InvalidDataException>(() => AgentRuntimeConfigurationStore.LoadSnapshotText(
            raw.Replace("compaction_trigger_ratio = 0.75", "compaction_trigger_ratio = 0.99", StringComparison.Ordinal),
            "runtime.toml", 1));
        Assert.Throws<InvalidDataException>(() => AgentRuntimeConfigurationStore.LoadSnapshotText(
            raw.Replace("core_markdown_max_tokens = 1500", "core_markdown_max_tokens = 128", StringComparison.Ordinal),
            "runtime.toml", 1));
    }

    [Fact]
    public void ContextStrategyService_ResolvesThreeTiers_AndAppliesAlwaysOnMicroCompaction()
    {
        var service = new ContextStrategyService();

        var direct = service.ResolveProfile("direct");
        Assert.True(direct.AlwaysOnKvCacheAppendWindow);
        Assert.True(direct.AlwaysOnMicroCompaction);
        Assert.False(direct.AsyncCompactionEnabled);
        Assert.False(direct.HistoryRecallEnabled);

        var managed = service.ResolveProfile("managed");
        Assert.True(managed.AsyncCompactionEnabled);
        Assert.True(managed.HistoryRecallEnabled);
        Assert.False(managed.CrossSessionRecallEnabled);

        var deep = service.ResolveProfile("deep_curator");
        Assert.True(deep.CrossSessionRecallEnabled);
        Assert.True(deep.ExplorationDigestEnabled);
        Assert.True(deep.ActiveMemoryEvolutionEnabled);

        Assert.False(service.ShouldTriggerAsyncCompaction(90_000, 100_000, "direct"));
        Assert.False(service.ShouldTriggerAsyncCompaction(70_000, 100_000, "managed", 0.75));
        Assert.True(service.ShouldTriggerAsyncCompaction(75_000, 100_000, "managed", 0.75));

        var messages = new List<ContextWindowMessage>
        {
            new(1, "tool", new string('A', 20_000)),
            new(2, "tool", "[tool_result] " + new string('B', 600)),
            new(3, "user", "recent user question"),
            new(4, "assistant", "recent assistant reply")
        };

        var compacted = service.MicroCompactMessages(messages, hotTailCount: 2, offloadThresholdBytes: 16_384);
        Assert.Equal(1, compacted.OffloadedCount);
        Assert.Equal(1, compacted.FoldedCount);
        Assert.True(compacted.SavedBytes > 19_000);
        Assert.Contains("artifact://tool-output/", compacted.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("[micro-compacted stale tool output", compacted.Messages[1].Content, StringComparison.Ordinal);
        Assert.Equal("recent user question", compacted.Messages[2].Content);
        Assert.Equal("recent assistant reply", compacted.Messages[3].Content);
    }

    [Fact]
    public async Task EndToEnd_FourLayerMemory_DualSync_Revert_AndConsciousRecall()
    {
        var token = Guid.NewGuid().ToString("N");
        var previousToken = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN");
        Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", token);
        try
        {
            _factory = new ManagedFactory(_root, token);
            using var client = _factory.CreateClient();

            // 1. Policy GET & PUT preserves runtime.toml comments and persists changes
            var initialPolicy = await client.GetFromJsonAsync<ContextMemoryPolicyDto>("/api/v1/context-memory/policy", SnakeJson);
            Assert.NotNull(initialPolicy);
            Assert.Equal("managed", initialPolicy.Strategy);
            Assert.Equal(1500, initialPolicy.CoreMarkdownMaxTokens);

            var updatePolicyResp = await client.PutAsJsonAsync("/api/v1/context-memory/policy", new UpdateContextMemoryPolicyRequest
            {
                Strategy = "deep_curator",
                RecallFidelity = "balanced",
                CoreMarkdownMaxTokens = 256,
                CompanionCanWriteProjectMemory = false
            }, SnakeJson);
            updatePolicyResp.EnsureSuccessStatusCode();
            var updatedPolicy = await updatePolicyResp.Content.ReadFromJsonAsync<ContextMemoryPolicyDto>(SnakeJson);
            Assert.NotNull(updatedPolicy);
            Assert.Equal("deep_curator", updatedPolicy.Strategy);
            Assert.Equal(256, updatedPolicy.CoreMarkdownMaxTokens);

            var runtimeTomlText = await File.ReadAllTextAsync(Path.Combine(_root, "config", "runtime.toml"));
            Assert.Contains("上下文管理策略", runtimeTomlText, StringComparison.Ordinal);
            Assert.Contains("strategy = \"deep_curator\"", runtimeTomlText, StringComparison.Ordinal);

            // 2. Seed a workspace project + two sessions in the same project
            var workspaceDir = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(workspaceDir);

            Guid projectId;
            Guid session1Id;
            Guid session2Id;
            using (var scope = _factory.Services.CreateScope())
            {
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current;
                var sessionStore = scope.ServiceProvider.GetRequiredService<ProjectSessionStore>();
                var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();

                projectId = Guid.NewGuid();
                session1Id = Guid.NewGuid();
                session2Id = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;

                await sessionStore.CreateProjectAsync("Phase11TestProject", workspaceDir, stableProjectId: projectId);

                await using var db = await dbFactory.CreateDbContextAsync();
                db.Sessions.Add(new SessionRecord
                {
                    Id = session1Id,
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    ProjectId = projectId,
                    Title = "Primary Debug Session",
                    Summary = "Investigated NullReferenceException in TokenRefresher",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                db.Sessions.Add(new SessionRecord
                {
                    Id = session2Id,
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    ProjectId = projectId,
                    Title = "Sibling Migration Session",
                    Summary = "Discussed PostgreSQL retry policy",
                    CreatedAt = now.AddMinutes(-5),
                    UpdatedAt = now.AddMinutes(-5)
                });
                await db.SaveChangesAsync();

                await conversations.AppendMessageAsync(session1Id, "user", "We hit NullReferenceException in TokenRefresher.cs line 88 when refreshing expired OAuth tokens.");
                await conversations.AppendMessageAsync(session1Id, "assistant", "Root cause: RefreshTokenCache returned null when clock skew exceeded 30s.");
                await conversations.AppendMessageAsync(session2Id, "user", "Sibling session note: PostgreSQL retry policy uses exponential backoff with TokenRefresher lock.");
            }

            // 3. Companion write-guard on project scope (403) + Companion 4-section document
            var forbiddenWrite = await client.PostAsJsonAsync("/api/v1/memories/items", new UpsertMemoryItemRequest
            {
                Scope = "project",
                ProjectId = projectId,
                Content = "Companion attempted overwrite",
                IsCompanionWriter = true
            }, SnakeJson);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenWrite.StatusCode);

            var companionId = Guid.NewGuid();
            var companionItemResp = await client.PostAsJsonAsync("/api/v1/memories/items", new UpsertMemoryItemRequest
            {
                Scope = "companion",
                CompanionId = companionId,
                Kind = "bond",
                Section = "Persona & Bond",
                Content = "Prefers concise technical summaries in Chinese and English."
            }, SnakeJson);
            companionItemResp.EnsureSuccessStatusCode();

            var companionDoc = await client.GetFromJsonAsync<MemoryDocumentDto>(
                $"/api/v1/memories/documents/companion?companion_id={companionId}", SnakeJson);
            Assert.NotNull(companionDoc);
            Assert.Contains("## Persona & Bond", companionDoc.Markdown, StringComparison.Ordinal);
            Assert.Contains("## Episodic Highlights", companionDoc.Markdown, StringComparison.Ordinal);
            Assert.Contains("## Operational Focus", companionDoc.Markdown, StringComparison.Ordinal);
            Assert.Contains("## Cross-Project Notes", companionDoc.Markdown, StringComparison.Ordinal);

            // 4. Token-capped project Markdown + cold archive search + Markdown diff -> supersede + 1-click revert
            for (var i = 0; i < 6; i++)
            {
                var addResp = await client.PostAsJsonAsync("/api/v1/memories/items", new UpsertMemoryItemRequest
                {
                    Scope = "project",
                    ProjectId = projectId,
                    Kind = "decision",
                    Content = $"Architecture rule #{i}: Always keep immutable ledger rows and avoid silent deletion ({new string('x', 120)}).",
                    SourceSessionId = session1Id,
                    SourceMessageSeq = i + 1
                }, SnakeJson);
                addResp.EnsureSuccessStatusCode();
            }

            var projectDoc = await client.GetFromJsonAsync<MemoryDocumentDto>(
                $"/api/v1/memories/documents/project?project_id={projectId}", SnakeJson);
            Assert.NotNull(projectDoc);
            Assert.True(projectDoc.EstimatedTokens <= 300);
            Assert.True(projectDoc.ColdArchiveCount > 0);
            Assert.Contains("<!-- overflow:", projectDoc.Markdown, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(workspaceDir, ".tinadec", "memory", "project-core.md")));

            // Cold items remain searchable via SQLite item query
            var coldSearch = await client.GetFromJsonAsync<MemoryItemListResultDto>(
                $"/api/v1/memories/items?scope=project&project_id={projectId}&query=Architecture%20rule%20%230", SnakeJson);
            Assert.NotNull(coldSearch);
            Assert.NotEmpty(coldSearch.Items);

            // Edit hot Markdown directly to supersede first hot item + add a brand new bullet
            var hotItem = projectDoc.Items.First(x => x.InHotMarkdown);
            var editedMarkdown = projectDoc.Markdown.Replace(
                hotItem.Content,
                "Architecture rule updated via Markdown edit",
                StringComparison.Ordinal)
                + "\n- Brand new rule added straight from Markdown editor\n";

            var putDocResp = await client.PutAsJsonAsync("/api/v1/memories/documents/project", new UpdateMemoryDocumentRequest
            {
                ProjectId = projectId,
                Markdown = editedMarkdown,
                SourceSessionId = session1Id,
                SourceMessageSeq = 99
            }, SnakeJson);
            putDocResp.EnsureSuccessStatusCode();
            var syncedDoc = await putDocResp.Content.ReadFromJsonAsync<MemoryDocumentDto>(SnakeJson);
            Assert.NotNull(syncedDoc);

            var revisedHotItem = syncedDoc.Items.Single(x => x.Id == hotItem.Id);
            Assert.Equal(2, revisedHotItem.CurrentVersion);
            Assert.Equal("supersede", revisedHotItem.Action);
            Assert.Equal("Architecture rule updated via Markdown edit", revisedHotItem.Content);
            Assert.Equal(hotItem.Content, revisedHotItem.PreviousContent);

            // 1-click Revert restores v1 content cleanly as immutable v3
            var revertResp = await client.PostAsync($"/api/v1/memories/items/{hotItem.Id}/revert", content: null);
            revertResp.EnsureSuccessStatusCode();
            var revertedItem = await revertResp.Content.ReadFromJsonAsync<MemoryItemLedgerDto>(SnakeJson);
            Assert.NotNull(revertedItem);
            Assert.Equal(3, revertedItem.CurrentVersion);
            Assert.Equal(hotItem.Content, revertedItem.Content);
            Assert.Equal(3, revertedItem.Versions.Count);

            // 5. Conscious recall over HTTP + CoreRecallHistoryTool (fidelity & cross-session)
            var summaryOnlyResp = await client.PostAsJsonAsync($"/api/v1/sessions/{session1Id}/recall", new ConversationRecallRequest
            {
                Query = "TokenRefresher",
                CrossSession = true,
                Fidelity = "summary_only"
            }, SnakeJson);
            summaryOnlyResp.EnsureSuccessStatusCode();
            var summaryOnly = await summaryOnlyResp.Content.ReadFromJsonAsync<ConversationRecallResultDto>(SnakeJson);
            Assert.NotNull(summaryOnly);
            Assert.True(summaryOnly.CrossSessionUsed);
            Assert.Equal(2, summaryOnly.SearchedSessions);
            Assert.Empty(summaryOnly.VerbatimSnippets);
            Assert.Contains("TokenRefresher", summaryOnly.Summary, StringComparison.Ordinal);

            var memoryService = _factory.Services.GetRequiredService<IContextMemoryService>();
            var tenantCtx = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
            var toolResult = await CoreRecallHistoryTool.ExecuteAsync(
                memoryService,
                new ToolInvocationScope(
                    tenantCtx.TenantId,
                    tenantCtx.WorkspaceId,
                    tenantCtx.PrincipalId,
                    projectId,
                    session1Id,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    workspaceDir,
                    ["recall_conversation_history"],
                    [],
                    "hash",
                    60,
                    2,
                    false),
                new ToolWireRequestDto
                {
                    ToolCallId = 1,
                    ToolId = CoreVirtualToolPolicy.RecallHistoryToolId,
                    SessionId = session1Id.ToString(),
                    Params = JsonSerializer.SerializeToElement(new
                    {
                        query = "TokenRefresher",
                        cross_session = true,
                        fidelity = "verbatim_first",
                        limit = 5
                    })
                });
            Assert.True(toolResult.IsSuccess);
            Assert.NotNull(toolResult.Result);
            Assert.True(toolResult.Result.Value.GetProperty("verbatim_snippets").GetArrayLength() >= 2);

            // 7. Phase 11.5 steady L3 principal + L2 project Markdown prefix injection & cold-item deduplication
            await memoryService.UpdatePolicyAsync(new UpdateContextMemoryPolicyRequest
            {
                CoreMarkdownMaxTokens = 1500
            });
            var steadyPrincipal = await memoryService.UpsertItemAsync(new UpsertMemoryItemRequest
            {
                Scope = "principal",
                Kind = "preference",
                Content = "STEADY-L3-PREF-771: prefer concise bullet summaries without filler."
            });
            var steadyProject = await memoryService.UpsertItemAsync(new UpsertMemoryItemRequest
            {
                Scope = "project",
                Kind = "fact",
                ProjectId = projectId,
                WorkspaceRoot = workspaceDir,
                Content = "STEADY-L2-FACT-882: sharedkeyword architecture enforces strict module boundaries."
            });

            // Also promote a duplicate workspace memory item with the exact same statement as steadyProject plus a distinct cold/workspace entry,
            // verifying that hot Markdown items are deduplicated out of reviewed_memory while non-hot matching items still surface.
            using (var scope = _factory.Services.CreateScope())
            {
                var longTermMemory = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
                var dupCandidate = await longTermMemory.CreateCandidateAsync(new MemoryCandidateProposal(
                    Guid.NewGuid(), Guid.NewGuid(), "workspace", "fact", steadyProject.Content, 0.95));
                await longTermMemory.DecideCandidateAsync(dupCandidate.Id, "promoted", "test duplicate");
                var coldCandidate = await longTermMemory.CreateCandidateAsync(new MemoryCandidateProposal(
                    Guid.NewGuid(), Guid.NewGuid(), "workspace", "fact", "COLD-ONLY-FACT-993: sharedkeyword legacy migration note kept in cold recall.", 0.95));
                await longTermMemory.DecideCandidateAsync(coldCandidate.Id, "promoted", "test cold item");

                var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
                await conversations.AppendMessageAsync(session2Id, "user", "unrelated greeting with sharedkeyword");

                var contextProvider = scope.ServiceProvider.GetRequiredService<IContextProvider>();
                var pack = await contextProvider.BuildContextAsync(new ContextBuildRequest(session2Id.ToString(), Guid.NewGuid().ToString())
                {
                    Workspace = new FrozenWorkspaceBinding(projectId, workspaceDir, [], false, null, [], false, WorkspacePathContracts.AbsoluteInRoot),
                    TokenBudget = 8192
                });

                var sources = pack.Evidence.Select(e => e.Source).ToList();
                var l3Idx = sources.IndexOf("user_profile_memory");
                var l2Idx = sources.IndexOf("project_core_memory");
                var histIdx = sources.IndexOf("session_history");
                Assert.True(l3Idx >= 0, $"Expected user_profile_memory in sources: {string.Join(", ", sources)}");
                Assert.True(l2Idx >= 0, $"Expected project_core_memory in sources: {string.Join(", ", sources)}");
                Assert.True(l3Idx < histIdx && l2Idx < histIdx, "Steady L3/L2 core memory blocks must sit ahead of session_history.");
                Assert.Contains("STEADY-L3-PREF-771", pack.Evidence[l3Idx].Content, StringComparison.Ordinal);
                Assert.Contains("STEADY-L2-FACT-882", pack.Evidence[l2Idx].Content, StringComparison.Ordinal);

                var reviewedMemories = pack.Evidence.Where(e => e.Source == "reviewed_memory").Select(e => e.Content).ToArray();
                Assert.NotEmpty(reviewedMemories);
                Assert.All(reviewedMemories, content => Assert.DoesNotContain("STEADY-L2-FACT-882", content, StringComparison.Ordinal));
                Assert.Contains(reviewedMemories, content => content.Contains("COLD-ONLY-FACT-993", StringComparison.Ordinal));
                _ = steadyPrincipal;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", previousToken);
        }
    }

    private sealed class ManagedFactory(string root, string token) : IsolatedApiFactory
    {
        protected override bool UsesManagedStorage => true;
        protected override string? ManagedUserRoot => root;

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", token);
        }

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Warning",
                ["TinadecStorage:Enabled"] = "true",
                ["TinadecStorage:UserRoot"] = root,
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(root, "workspace")
            }));
    }
}
