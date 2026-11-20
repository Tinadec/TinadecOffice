using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

public sealed class FormalModeSnapshotTests
{
    [Fact]
    public async Task ResolveRoster_UsesExactAgentAndPromptVersionsAfterNewerVersionsExist()
    {
        await using var fixture = await FormalModeFixture.CreateAsync();

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        var meeting = Assert.Single(roster.Operation, agent => agent.Id == "meeting");
        Assert.Equal(fixture.MeetingVersionV1, meeting.AgentVersionId);
        Assert.Equal("meeting-system-v1", meeting.SystemPrompt);
        Assert.Equal(fixture.PromptVersionV1, meeting.PromptVersionId);
        Assert.Contains("prompt-template-v1", meeting.PromptGraphJson, StringComparison.Ordinal);
        Assert.DoesNotContain("v2", meeting.PromptGraphJson, StringComparison.Ordinal);

        var frozen = await fixture.Services.GetRequiredService<IAgentRuntimeConfigurationResolver>()
            .ResolveAsync(fixture.SessionId, "ask");
        Assert.Contains(frozen.Bindings, binding =>
            binding.ConfigurationKind == "agent_mode_version"
            && binding.ConfigurationVersionId == fixture.ModeVersionId);
        Assert.Contains(frozen.Bindings, binding =>
            binding.ConfigurationKind == "agent_version"
            && binding.ConfigurationVersionId == fixture.MeetingVersionV1);
        Assert.Contains(frozen.Bindings, binding =>
            binding.ConfigurationKind == "prompt_version"
            && binding.ConfigurationVersionId == fixture.PromptVersionV1);
    }

    /// <summary>
    /// The last hop of the explicit-tier feature: a tier declared on the published mode snapshot
    /// must arrive on the roster, because that is the value the run freeze reads. The derivation
    /// (`DeriveGraphTier`/`MapDeclaredTier`) and the snapshot reader are both unit-tested, but
    /// nothing drove a snapshot through the resolver into <see cref="FormalModeRoster"/>. Without
    /// this, the feature can be fully "tested" and still be inert — which is exactly what happened
    /// before this phase: the 4th argument was honoured and nothing ever populated it.
    /// </summary>
    [Fact]
    public async Task ResolveRoster_CarriesTheDeclaredOrchestrationTierFromTheSnapshot()
    {
        await using var fixture = await FormalModeFixture.CreateAsync(orchestrationTier: "self_dispatch");

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        Assert.Equal("self_dispatch", roster.OrchestrationTier);
        Assert.True(roster.HasExplicitOrchestrationTier);
    }

    /// <summary>
    /// The complement, and the one that matters for legacy data: a mode published before the field
    /// existed must read back as "no declared tier" so inference still runs and the run does not
    /// fail admission over a field it never declared.
    ///
    /// The fixture now OMITS the key by default, which is the shape both publish paths produce
    /// (<c>AgentTopologyService.cs:656</c>, <c>AgentPackService.cs:1155</c>). That makes this a
    /// test of the production shape rather than of a JSON <c>null</c>, which production never
    /// emits. <see cref="ResolveRoster_TreatsAnExplicitNullTierAsUndeclared"/> covers the null
    /// shape separately, because a hand-edited or externally-authored snapshot can contain one.
    /// </summary>
    [Fact]
    public async Task ResolveRoster_WithoutADeclaredTier_LeavesItNullSoInferenceRuns()
    {
        await using var fixture = await FormalModeFixture.CreateAsync();

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        Assert.Null(roster.OrchestrationTier);
        Assert.False(roster.HasExplicitOrchestrationTier);
    }

    /// <summary>
    /// A snapshot that carries <c>"orchestration_tier": null</c> explicitly must read the same as
    /// one that omits the key. Production never writes this shape, but a snapshot authored by
    /// hand, imported from a pack, or produced by an older writer can, and the reader must not
    /// throw or invent a tier for it.
    /// </summary>
    [Fact]
    public async Task ResolveRoster_TreatsAnExplicitNullTierAsUndeclared()
    {
        await using var fixture = await FormalModeFixture.CreateAsync(orchestrationTier: null, writeNullTier: true);

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        Assert.Null(roster.OrchestrationTier);
        Assert.False(roster.HasExplicitOrchestrationTier);
    }

    [Fact]
    public async Task ResolveRoster_InjectsModeInstructionsIntoConversationIdentityOnly()
    {
        const string instructions = "Always answer with the evidence gathered by the workers.";
        await using var fixture = await FormalModeFixture.CreateAsync(instructions: instructions);

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        Assert.Equal(instructions, roster.ModeInstructions);
        var meeting = Assert.Single(roster.Operation, agent => agent.Id == "meeting");
        Assert.Equal($"meeting-system-v1\n\n{instructions}", meeting.SystemPrompt);
        var planner = Assert.Single(roster.Execution);
        Assert.Equal("planner-system-v1", planner.SystemPrompt);
        Assert.DoesNotContain(instructions, planner.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRoster_WithoutModeInstructions_LeavesSystemPromptUnchanged()
    {
        await using var fixture = await FormalModeFixture.CreateAsync();

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        Assert.Null(roster.ModeInstructions);
        Assert.Equal("meeting-system-v1", Assert.Single(roster.Operation, agent => agent.Id == "meeting").SystemPrompt);
    }

    [Fact]
    public async Task ResolveRoster_RejectsAgentVersionHashMismatch()
    {
        await using var fixture = await FormalModeFixture.CreateAsync(tamperAgentHash: true);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Services.GetRequiredService<IFormalModeResolver>().ResolveRosterAsync(fixture.SessionId));

        Assert.Contains("could not be verified", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains("content hash verification failed", error.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRoster_StructuredInvestigation_ExposesTraceProposalToEveryExecutableIdentity()
    {
        await using var fixture = await FormalModeFixture.CreateAsync(structuredInvestigation: true);

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        Assert.All(roster.Operation.Concat(roster.Execution), agent =>
            Assert.Contains(CoreVirtualToolPolicy.CorrectionTraceToolId, agent.AllowedTools));
        var spawnable = Assert.Single(roster.SpawnableTemplates, agent => agent.Slug == "task_planner");
        Assert.Contains(CoreVirtualToolPolicy.CorrectionTraceToolId, spawnable.ToolScope);
    }

    [Fact]
    public async Task ResolveRoster_OrdinaryMode_DoesNotExposeCorrectionTraceProposal()
    {
        await using var fixture = await FormalModeFixture.CreateAsync();

        var roster = await fixture.Services.GetRequiredService<IFormalModeResolver>()
            .ResolveRosterAsync(fixture.SessionId);

        Assert.NotNull(roster);
        Assert.All(roster.Operation.Concat(roster.Execution), agent =>
            Assert.DoesNotContain(CoreVirtualToolPolicy.CorrectionTraceToolId, agent.AllowedTools));
        Assert.All(roster.SpawnableTemplates, agent =>
            Assert.DoesNotContain(CoreVirtualToolPolicy.CorrectionTraceToolId, agent.ToolScope));
    }

    private sealed class FormalModeFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private FormalModeFixture(
            string root,
            ServiceProvider provider,
            Guid sessionId,
            Guid modeVersionId,
            Guid meetingVersionV1,
            Guid promptVersionV1)
        {
            _root = root;
            _provider = provider;
            SessionId = sessionId;
            ModeVersionId = modeVersionId;
            MeetingVersionV1 = meetingVersionV1;
            PromptVersionV1 = promptVersionV1;
        }

        public IServiceProvider Services => _provider;
        public Guid SessionId { get; }
        public Guid ModeVersionId { get; }
        public Guid MeetingVersionV1 { get; }
        public Guid PromptVersionV1 { get; }

        public static async Task<FormalModeFixture> CreateAsync(
            bool tamperAgentHash = false,
            bool structuredInvestigation = false,
            string? instructions = null,
            string? orchestrationTier = null,
            bool writeNullTier = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "tinadec-formal-mode-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "data"));
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddTinadecPersistence(configuration, root);
            services.AddTinadecCore();
            services.AddSingleton<IAgentModelResolver, FrozenPlanOnlyModelResolver>();
            var provider = services.BuildServiceProvider();

            try
            {
                await provider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
                var scope = provider.GetRequiredService<ITenantContextAccessor>().Current;
                var sessions = provider.GetRequiredService<ProjectSessionStore>();
                var project = await sessions.CreateProjectAsync("Formal mode project", Path.Combine(root, "workspace"));
                var now = DateTimeOffset.UtcNow;

                var meetingDefinitionId = Guid.NewGuid();
                var plannerDefinitionId = Guid.NewGuid();
                var modeId = Guid.NewGuid();
                var modeVersionId = Guid.NewGuid();
                var promptPipelineId = Guid.NewGuid();
                var promptVersionV1 = Guid.NewGuid();
                var promptVersionV2 = Guid.NewGuid();
                var meetingVersionV1 = Guid.NewGuid();
                var meetingVersionV2 = Guid.NewGuid();
                var plannerVersionV1 = Guid.NewGuid();

                var meetingV1Body = AgentSnapshot(meetingDefinitionId, "meeting", "operation", "session_coordinator", "meeting-system-v1", []);
                var meetingV2Body = AgentSnapshot(meetingDefinitionId, "meeting", "operation", "session_coordinator", "meeting-system-v2", []);
                var plannerV1Body = AgentSnapshot(plannerDefinitionId, "task_planner", "execution", "execution_coordinator", "planner-system-v1", ["task.plan", "agent.create_temporary"]);
                var meetingV1Hash = Hash(meetingV1Body);
                var plannerV1Hash = Hash(plannerV1Body);
                const string promptV1Body = "{\"nodes\":[{\"id\":\"template\",\"type\":\"template\",\"config\":{\"content\":\"prompt-template-v1\"}},{\"id\":\"assemble\",\"type\":\"assemble\"}],\"edges\":[{\"source\":\"template\",\"target\":\"assemble\"}]}";
                const string promptV2Body = "{\"nodes\":[{\"id\":\"template\",\"type\":\"template\",\"config\":{\"content\":\"prompt-template-v2\"}},{\"id\":\"assemble\",\"type\":\"assemble\"}],\"edges\":[{\"source\":\"template\",\"target\":\"assemble\"}]}";
                var promptV1Hash = Hash(promptV1Body);

                await using (var db = await provider.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync())
                {
                    db.AgentDefinitions.AddRange(
                        Definition(scope, meetingDefinitionId, "meeting", "operation", "session_coordinator", 2, now),
                        Definition(scope, plannerDefinitionId, "task_planner", "execution", "execution_coordinator", 1, now));
                    db.AgentVersions.AddRange(
                        Version(scope, meetingVersionV1, meetingDefinitionId, 1, "operation", "session_coordinator", meetingV1Body, meetingV1Hash, now),
                        Version(scope, meetingVersionV2, meetingDefinitionId, 2, "operation", "session_coordinator", meetingV2Body, Hash(meetingV2Body), now.AddSeconds(1)),
                        Version(scope, plannerVersionV1, plannerDefinitionId, 1, "execution", "execution_coordinator", plannerV1Body, plannerV1Hash, now));
                    db.PromptPipelines.Add(new PromptPipelineRecord
                    {
                        Id = promptPipelineId,
                        TenantId = scope.TenantId,
                        WorkspaceId = scope.WorkspaceId,
                        Slug = "prompt",
                        DisplayName = "Prompt",
                        GraphJson = promptV2Body,
                        Status = "published",
                        Revision = 2,
                        Version = 2,
                        CreatedAt = now,
                        UpdatedAt = now.AddSeconds(1),
                        CreatedByPrincipalId = scope.PrincipalId,
                        UpdatedByPrincipalId = scope.PrincipalId
                    });
                    db.PromptVersions.AddRange(
                        PromptVersion(scope, promptVersionV1, promptPipelineId, 1, promptV1Body, promptV1Hash, now),
                        PromptVersion(scope, promptVersionV2, promptPipelineId, 2, promptV2Body, Hash(promptV2Body), now.AddSeconds(1)));
                    db.AgentModes.Add(new AgentModeRecord
                    {
                        Id = modeId,
                        TenantId = scope.TenantId,
                        WorkspaceId = scope.WorkspaceId,
                        Slug = "mode",
                        DisplayName = "Mode",
                        Status = "published",
                        Revision = 1,
                        Version = 1,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CreatedByPrincipalId = scope.PrincipalId,
                        UpdatedByPrincipalId = scope.PrincipalId
                    });
                    string modeBody = JsonSerializer.Serialize(new
                    {
                        schema = "tinadec.mode_version/v1",
                        mode = new { id = modeId, slug = "mode", display_name = "Mode" },
                        nodes = new object[]
                        {
                            new
                            {
                                node_key = "executor-1",
                                agent_definition_id = plannerDefinitionId,
                                agent_version_id = plannerVersionV1,
                                agent_version_hash = plannerV1Hash,
                                layer = "execution",
                                effective_tools = new[] { "read_file" },
                                prompt_pipeline_id = promptPipelineId,
                                prompt_version_id = promptVersionV1,
                                prompt_version_hash = promptV1Hash,
                                relationship = new
                                {
                                    agent_types = Array.Empty<string>(),
                                    allowed_dispatch_targets = Array.Empty<string>()
                                }
                            },
                            new
                            {
                                node_key = "meeting-1",
                                agent_definition_id = meetingDefinitionId,
                                agent_version_id = meetingVersionV1,
                                agent_version_hash = tamperAgentHash ? new string('0', 64) : meetingV1Hash,
                                layer = "operation",
                                effective_tools = Array.Empty<string>(),
                                config = new { conversation = true },
                                prompt_pipeline_id = promptPipelineId,
                                prompt_version_id = promptVersionV1,
                                prompt_version_hash = promptV1Hash,
                                relationship = new
                                {
                                    agent_types = structuredInvestigation ? new[] { "task_planner" } : Array.Empty<string>(),
                                    allowed_dispatch_targets = structuredInvestigation ? new[] { "task_planner" } : Array.Empty<string>()
                                }
                            }
                        },
                        edges = Array.Empty<object>(),
                        canvas_layout = (object?)null,
                        instructions,
                        // NOTE: `orchestration_tier` is deliberately NOT written here. Both publish
                        // paths (AgentTopologyService.cs:656, AgentPackService.cs:1155) omit the key
                        // when a mode declares no tier, so the fixture must produce the production
                        // shape by default. It is added below only when a test asks for it.
                        runtime_policy = structuredInvestigation
                            ? new
                            {
                                kind = "structured_investigation",
                                version = 1,
                                features = new[]
                                {
                                    "causal_trace",
                                    "completion_contract",
                                    "failing_repro_required",
                                    "clarification_ledger",
                                    "user_reproduction_reopens",
                                    "local_hunk_revert"
                                }
                            }
                            : null
                    });
                    // Added to the body after serialization so the default shape is "key absent",
                    // which is what both publish paths actually produce. `writeNullTier` opts into
                    // the explicit-null shape instead, which production never emits but an
                    // imported or hand-edited snapshot can contain.
                    if (orchestrationTier is not null || writeNullTier)
                    {
                        var withTier = JsonNode.Parse(modeBody)!.AsObject();
                        withTier["orchestration_tier"] = orchestrationTier;
                        modeBody = withTier.ToJsonString();
                    }
                    db.ModeVersions.Add(new ModeVersionRecord
                    {
                        Id = modeVersionId,
                        TenantId = scope.TenantId,
                        WorkspaceId = scope.WorkspaceId,
                        AgentModeId = modeId,
                        Version = 1,
                        SnapshotJson = modeBody,
                        TopologyHash = Hash(modeBody),
                        Status = "published",
                        Revision = 1,
                        CreatedAt = now,
                        CreatedByPrincipalId = scope.PrincipalId
                    });
                    await db.SaveChangesAsync();
                }
                var session = await sessions.CreateSessionAsync(project.Id, "Formal mode session", modeVersionId);
                return new FormalModeFixture(root, provider, session.Id, modeVersionId, meetingVersionV1, promptVersionV1);
            }
            catch
            {
                await provider.DisposeAsync();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static AgentDefinitionRecord Definition(
            TenantContext scope,
            Guid id,
            string slug,
            string layer,
            string role,
            int version,
            DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            Slug = slug,
            DisplayName = slug,
            Layer = layer,
            Role = role,
            CapabilitiesJson = "[]",
            ModelStrategyJson = "{\"kind\":\"inherit\"}",
            ToolScopeJson = "[]",
            SourceKind = "custom",
            SourceKey = $"{slug}:{id:N}",
            Status = "published",
            Revision = version,
            Version = version,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByPrincipalId = scope.PrincipalId,
            UpdatedByPrincipalId = scope.PrincipalId
        };

        private static AgentVersionRecord Version(
            TenantContext scope,
            Guid id,
            Guid definitionId,
            int version,
            string layer,
            string role,
            string body,
            string hash,
            DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            AgentDefinitionId = definitionId,
            Version = version,
            Layer = layer,
            Role = role,
            SnapshotJson = body,
            ContentHash = hash,
            ContentLength = Encoding.UTF8.GetByteCount(body),
            Status = "published",
            Revision = 1,
            CreatedAt = now,
            CreatedByPrincipalId = scope.PrincipalId
        };

        private static PromptVersionRecord PromptVersion(
            TenantContext scope,
            Guid id,
            Guid pipelineId,
            int version,
            string body,
            string hash,
            DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            PromptPipelineId = pipelineId,
            Version = version,
            GraphJson = body,
            ContentHash = hash,
            ContentLength = Encoding.UTF8.GetByteCount(body),
            Status = "published",
            Revision = 1,
            CreatedAt = now,
            CreatedByPrincipalId = scope.PrincipalId
        };

        private static string AgentSnapshot(
            Guid id,
            string slug,
            string layer,
            string role,
            string systemPrompt,
            IReadOnlyList<string> capabilities) =>
            JsonSerializer.Serialize(new
            {
                id,
                slug,
                display_name = slug,
                layer,
                role,
                capabilities,
                model_strategy = new { kind = "inherit" },
                tool_scope = Array.Empty<string>(),
                system_prompt = systemPrompt,
                description = slug,
                enabled = true
            });

        private static string Hash(string body) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        private sealed class FrozenPlanOnlyModelResolver : IAgentModelResolver
        {
            public Task<FrozenModelPlan> FreezeAsync(AgentModelFreezeRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new FrozenModelPlan("inherit", request.StrategySource, []));

            public Task<ModelResolutionPreviewDto> PreviewAsync(ModelResolutionPreviewRequestDto request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<ChatResolution>> ResolveInvocationCandidatesAsync(FrozenModelPlan plan, Guid? parentInstanceId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<Guid> StartInvocationAsync(ModelInvocationStart request, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task CompleteInvocationAsync(Guid invocationId, string status, ModelUsage? usage = null, string? errorCategory = null, string? safeErrorMessage = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
