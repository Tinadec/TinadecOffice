using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            .ResolveAsync(fixture.SessionId, null, null, null);
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

        public static async Task<FormalModeFixture> CreateAsync(bool tamperAgentHash = false)
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
                    var modeBody = JsonSerializer.Serialize(new
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
                                prompt_version_hash = promptV1Hash
                            },
                            new
                            {
                                node_key = "meeting-1",
                                agent_definition_id = meetingDefinitionId,
                                agent_version_id = meetingVersionV1,
                                agent_version_hash = tamperAgentHash ? new string('0', 64) : meetingV1Hash,
                                layer = "operation",
                                effective_tools = Array.Empty<string>(),
                                prompt_pipeline_id = promptPipelineId,
                                prompt_version_id = promptVersionV1,
                                prompt_version_hash = promptV1Hash
                            }
                        },
                        edges = Array.Empty<object>(),
                        canvas_layout = (object?)null
                    });
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
            SourceKey = slug,
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
