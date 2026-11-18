using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The workbench tools are Core-owned, but their wiring has three separate seams: the frozen
/// manifest entry, the approval policy, and the runtime gateway. This suite exercises the gateway
/// and the manifest policy together; a full dispatcher run is covered by the tool-chain suites.
/// </summary>
public sealed class TinadecConfigToolGatewayTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-config-tools-tests", Guid.NewGuid().ToString("N"));
    private Factory _factory = null!;

    public Task InitializeAsync() { Directory.CreateDirectory(_root); _factory = new(_root); return Task.CompletedTask; }
    public Task DisposeAsync()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Inspect_ReturnsModesAndSkillsWithoutBodiesOrSecrets()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var scope = Scope();

        var modes = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "modes", limit = 10 }), CancellationToken.None);
        Assert.True(modes.IsSuccess, modes.Error);
        Assert.Equal("modes", modes.Result!.Value.GetProperty("target").GetString());
        Assert.True(modes.Result!.Value.GetProperty("total").GetInt32() >= 0);

        var skills = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecInspectToolId, scope,
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "skills", limit = 50 }), CancellationToken.None);
        Assert.True(skills.IsSuccess, skills.Error);
        var raw = skills.Result!.Value.GetRawText();
        Assert.Contains("tinadec-workbench", raw);
        Assert.DoesNotContain("BODY-MUST-STAY-ON-DISK", raw);
        Assert.DoesNotContain("credential", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteTools_AreApprovalGatedHighRiskAndDoNotMutateTheWorkspace()
    {
        foreach (var id in CoreVirtualToolPolicy.TinadecConfigToolIds.Where(id => !CoreVirtualToolPolicy.IsTinadecInspect(id)))
        {
            var entry = CoreTinadecConfigTools.ManifestEntry(id);
            Assert.True(entry.RequiresApproval, id);
            Assert.Equal("high", entry.Risk);
            Assert.False(entry.MutatesWorkspace);
            Assert.Equal("unsafe", entry.RetrySafety);
        }
        var inspect = CoreTinadecConfigTools.ManifestEntry(CoreVirtualToolPolicy.TinadecInspectToolId);
        Assert.False(inspect.RequiresApproval);
        Assert.Equal("low", inspect.Risk);
        Assert.Equal("safe", inspect.RetrySafety);
    }

    [Fact]
    public async Task AgentApply_RefusesBuiltInAgentsAndTellsTheCallerToClone()
    {
        var client = _factory.CreateClient();
        var agents = await client.GetFromJsonAsync<JsonElement>("/api/v1/agents");
        var builtIn = agents.EnumerateArray().First();
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var result = await gateway.ExecuteAsync(CoreVirtualToolPolicy.TinadecAgentApplyToolId, Scope(),
            Wire(CoreVirtualToolPolicy.TinadecAgentApplyToolId, new
            {
                action = "update",
                agent_id = builtIn.GetProperty("id").GetGuid(),
                display_name = "should fail",
            }), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Contains("built_in_read_only", result.Error);
    }

    [Fact]
    public async Task Dispatcher_ExecutesInspectThroughTheFrozenManifestAndReturnsTheGatewayResult()
    {
        var client = _factory.CreateClient();
        var projectPath = Path.Combine(_root, "dispatch-project");
        Directory.CreateDirectory(projectPath);
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Dispatch", path = projectPath }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Dispatch" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();
        var sessionId = session.GetProperty("id").GetGuid();
        var scopeIds = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var locations = _factory.Services.GetRequiredService<IScopeStorageLocations>();
        var services = _factory.Services;
        var runId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var agentInstanceId = Guid.NewGuid();
        await using (var runDb = await services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync())
        {
            runDb.Runs.Add(new RunRecord
            {
                Id = runId, TenantId = scopeIds.TenantId, WorkspaceId = scopeIds.WorkspaceId, SessionId = sessionId,
                InitiatedByPrincipalId = scopeIds.PrincipalId, TriggerMessageId = Guid.NewGuid(), Status = "running", PermissionMode = "full-access",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await runDb.SaveChangesAsync();
        }
        var definitionId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var versionHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("{\"tool_scope\":[\"tinadec_inspect\"]}")));
        await using (var configurationDb = await services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>().CreateDbContextAsync())
        {
            configurationDb.AgentVersions.Add(new AgentVersionRecord
            {
                Id = versionId, TenantId = scopeIds.TenantId, WorkspaceId = scopeIds.WorkspaceId,
                AgentDefinitionId = definitionId, Version = 1, Layer = "operation", Role = "meeting",
                SnapshotJson = "{\"tool_scope\":[\"tinadec_inspect\"]}", ContentHash = versionHash,
                ContentLength = 32, Status = "published", Revision = 1, CreatedAt = DateTimeOffset.UtcNow,
                CreatedByPrincipalId = scopeIds.PrincipalId,
            });
            await configurationDb.SaveChangesAsync();
        }
        var definitionBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { AllowedTools = new[] { CoreVirtualToolPolicy.TinadecInspectToolId }, AllowedResources = new[] { "read:" } }));
        var definition = await services.GetRequiredService<IContentStore>().PutAsync(new ContentWriteRequest(
            scopeIds.TenantId, scopeIds.WorkspaceId, "agent_definition", "application/json", new MemoryStream(definitionBytes)));
        await using (var agentDb = await services.GetRequiredService<IDbContextFactory<AgentControlDbContext>>().CreateDbContextAsync())
        {
            agentDb.Instances.Add(new AgentInstanceRecord
            {
                Id = agentInstanceId, TenantId = scopeIds.TenantId, WorkspaceId = scopeIds.WorkspaceId, SessionId = sessionId,
                RunId = runId, TaskNodeId = taskId, AgentDefinitionId = definitionId, AgentVersionId = versionId,
                AgentVersionHash = versionHash, Layer = "operation", Role = "meeting", Status = "running",
                DefinitionReference = definition.Value, DefinitionHash = definition.Sha256, DefinitionLength = definition.Length,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await agentDb.SaveChangesAsync();
        }

        var entry = CoreTinadecConfigTools.ManifestEntry(CoreVirtualToolPolicy.TinadecInspectToolId);
        var manifest = new ToolManifestDto
        {
            ProtocolVersion = 2,
            ManifestHash = ToolManifestHasher.Compute([entry]),
            Tools = [entry],
        };
        var frozen = new FrozenToolManifestEntry(entry.Id, entry.Description, entry.InputSchema.Clone(), entry.Risk,
            entry.MutatesWorkspace, entry.RequiresApproval, entry.RetrySafety, entry.ConfirmationFields);
        var lifecycle = services.GetRequiredService<ILifecycleManager>();
        var frozenContent = JsonSerializer.Serialize(new
        {
            permissionMode = "full-access",
            tools = new { defaultTimeoutSeconds = 600, serializeWorkspaceWrites = false },
            scheduling = new { workerRetryLimit = 0 },
            toolManifestProtocolVersion = 2,
            toolManifestHash = manifest.ManifestHash,
            toolManifest = manifest.Tools,
        });
        await lifecycle.FreezeRunConfigurationAsync(runId.ToString(),
            new FrozenRunConfigurationWrite("tinadec.frozen_run/v1", frozenContent, []));
        var scope = new ToolInvocationScope(scopeIds.TenantId, scopeIds.WorkspaceId, scopeIds.PrincipalId,
            projectId, sessionId, runId, taskId, agentInstanceId, projectPath,
            [CoreVirtualToolPolicy.TinadecInspectToolId], ["read:"], manifest.ManifestHash, 600, 0, false,
            AuthorizedToolManifest: [frozen], FrozenToolManifestHash: manifest.ManifestHash, PermissionMode: "full-access",
            ToolExecutionContext: new ToolExecutionContextDto
            {
                StorageId = locations.StorageId, StorageRoot = locations.Root, ProjectRoot = projectPath,
                Settings = JsonSerializer.SerializeToElement(new { }), SettingsHash = "dispatch-test",
                AllowedToolIds = [CoreVirtualToolPolicy.TinadecInspectToolId],
            });

        var dispatcher = new ToolDispatcher(
            new ManifestProvider(manifest),
            new FixedScopeResolver(scope),
            services.GetRequiredService<IToolExecutionCoordinator>(),
            services.GetRequiredService<IAuthorizationService>(),
            services.GetRequiredService<IWorkspaceSnapshotService>(),
            services.GetRequiredService<ILifecycleManager>(),
            services.GetRequiredService<ITerminalSessionRegistry>(),
            services.GetRequiredService<IInFlightToolCallRegistry>(),
            new ToolDispatchOptions(),
            services.GetRequiredService<ILogger<ToolDispatcher>>(),
            toolSettings: services.GetRequiredService<IToolConfigurationResolver>(),
            storageLocations: locations,
            skillResources: services.GetRequiredService<IToolSkillResourceService>(),
            tinadecConfig: services.GetRequiredService<ITinadecConfigToolGateway>());

        var result = await dispatcher.PrepareAsync(new ToolDispatchRequestDto
        {
            RunId = scope.RunId.ToString(), TaskId = scope.TaskId.ToString(), AgentInstanceId = scope.AgentInstanceId.ToString(),
            ToolId = CoreVirtualToolPolicy.TinadecInspectToolId,
            Params = JsonSerializer.SerializeToElement(new { target = "modes" }),
        });

        Assert.Equal(ToolDispatchStatus.Requested, result.Status);
        var resumed = await dispatcher.ResumeAsync(result.ExecutionId!);
        Assert.True(resumed.Status == ToolDispatchStatus.Completed, $"{resumed.Status}: {resumed.Message} / {resumed.ErrorCategory}");
        Assert.NotNull(resumed.Result);
        Assert.Equal("modes", resumed.Result!.Value.GetProperty("target").GetString());
    }

    private sealed class FixedScopeResolver(ToolInvocationScope scope) : IToolInvocationScopeResolver
    {
        public Task<ToolInvocationScope> ResolveAsync(ToolInvocationScopeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(scope);
    }

    private sealed class ManifestProvider(ToolManifestDto manifest) : IToolProvider
    {
        public Task<ToolManifestDto> EnsureStartedAsync(string root, CancellationToken ct = default) => GetManifestAsync(root, ct);
        public Task<ToolManifestDto> GetManifestAsync(string root, CancellationToken ct = default) => Task.FromResult(manifest);
        public Task<ToolWireResponseDto> CallAsync(string root, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolWireResponseDto { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) });
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ManifestFreeze_IncludesCoreOwnedConfigToolsForAFreeSession()
    {
        var client = _factory.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Config tools" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var resolver = _factory.Services.GetRequiredService<IToolManifestSnapshotResolver>();
        var snapshot = await resolver.ResolveAsync(new ToolManifestSnapshotRequest(sessionId,
            [CoreVirtualToolPolicy.TinadecInspectToolId, CoreVirtualToolPolicy.TinadecModeApplyToolId, CoreVirtualToolPolicy.LoadSkillToolId],
            AllowAllTools: false));

        var inspect = Assert.Single(snapshot.AuthorizedTools, x => x.Id == CoreVirtualToolPolicy.TinadecInspectToolId);
        Assert.False(inspect.RequiresApproval);
        var apply = Assert.Single(snapshot.AuthorizedTools, x => x.Id == CoreVirtualToolPolicy.TinadecModeApplyToolId);
        Assert.True(apply.RequiresApproval);
        Assert.Contains(snapshot.AuthorizedTools, x => x.Id == CoreVirtualToolPolicy.LoadSkillToolId);
    }

    private ToolInvocationScope Scope()
    {
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        return new(tenant.TenantId, tenant.WorkspaceId, tenant.PrincipalId,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty,
            _root, CoreVirtualToolPolicy.TinadecConfigToolIds.ToArray(), [], "test", 600, 0, false);
    }

    private static ToolWireRequestDto Wire(string toolId, object parameters) => new()
    {
        ToolId = toolId,
        SessionId = "config-tool-test",
        Params = JsonSerializer.SerializeToElement(parameters),
    };

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecTools:DefaultWorkspaceRoot"] = root,
                ["Logging:LogLevel:Default"] = "Warning",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(new Provider());
            });
        }
    }

    private sealed class Provider : IToolProvider
    {
        public Task<ToolManifestDto> EnsureStartedAsync(string root, CancellationToken ct = default) => GetManifestAsync(root, ct);
        public Task<ToolManifestDto> GetManifestAsync(string root, CancellationToken ct = default) => Task.FromResult(new ToolManifestDto { ProtocolVersion = 2, Tools = [] });
        public Task<ToolWireResponseDto> CallAsync(string root, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolWireResponseDto { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) });
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
