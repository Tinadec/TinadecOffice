using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

public sealed class ToolSettingsApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-tool-settings-tests", Guid.NewGuid().ToString("N"));
    private Factory _factory = null!;
    public Task InitializeAsync() { Directory.CreateDirectory(_root); _factory = new(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { _factory.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public async Task ConditionalSettingsWrites_RejectUnknownFieldsAndStaleRevisions()
    {
        var client = _factory.CreateClient();
        var initial = await client.GetAsync("/api/v1/tools/settings/defaults");
        Assert.Equal("\"0\"", initial.Headers.ETag!.Tag);
        var missing = await client.PutAsJsonAsync("/api/v1/tools/settings/defaults", new { settings = new { web = new { timeout_ms = 1000 } } });
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(client, "0", new { web = new { timeout_ms = 999 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(client, "0", new { shell = new { unknown_flag = true } })).StatusCode);
        var saved = await SaveAsync(client, "0", new { shell = new { enabled = false }, search = new { case_sensitive = true } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"1\"", saved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SaveAsync(client, "0", new { })).StatusCode);
        var document = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(document.GetProperty("effective_settings").GetProperty("shell").GetProperty("enabled").GetBoolean());
        Assert.Equal(15000, document.GetProperty("effective_settings").GetProperty("web").GetProperty("timeout_ms").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(client, "1", new { mcp = new { server_resource_ids = new[] { Guid.NewGuid() } } })).StatusCode);
    }

    [Fact]
    public void AgentSettings_DeepMergeBudgetsButCannotWidenSharedLimits()
    {
        var shared = ToolSettingsSchema.Merge(ToolSettingsSchema.Defaults, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = 1000 }, git = new { max_output_chars = 1000, protected_branches = new[] { "release" }, tool_budgets = new { git_log = new { timeout_ms = 10000, max_output_chars = 500 }, git_diff = new { timeout_ms = 20000 } } } }));
        var merged = ToolSettingsSchema.MergeAgent(shared, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = 500 }, git = new { protected_branches = new[] { "topic" }, tool_budgets = new { git_log = new { timeout_ms = 9000 } } } }));
        Assert.Equal(500, merged.GetProperty("read").GetProperty("max_file_bytes").GetInt32());
        Assert.Equal(500, merged.GetProperty("git").GetProperty("tool_budgets").GetProperty("git_log").GetProperty("max_output_chars").GetInt32());
        Assert.Equal(20000, merged.GetProperty("git").GetProperty("tool_budgets").GetProperty("git_diff").GetProperty("timeout_ms").GetInt32());
        Assert.Equal(new[] { "release", "topic" }, merged.GetProperty("git").GetProperty("protected_branches").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Throws<ToolSettingsException>(() => ToolSettingsSchema.MergeAgent(shared, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = (int?)null } })));
        Assert.Throws<ToolSettingsException>(() => ToolSettingsSchema.MergeAgent(shared, JsonSerializer.SerializeToElement(new { git = new { tool_budgets = new { git_log = new { max_output_chars = 501 } } } })));
        var budget = ToolSettingsSchema.WireBudget(shared, "git_diff", null, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(50), budget);
    }

    [Fact]
    public void DeleteFileRespectsWriteCategorySettingsAndCannotLoseItsMutationClassification()
    {
        var disabled = ToolSettingsSchema.Merge(ToolSettingsSchema.Defaults, JsonSerializer.SerializeToElement(new { write = new { enabled = false } }));
        Assert.False(ToolSettingsSchema.IsEnabled(disabled, "delete_file"));
        Assert.False(ToolSettingsSchema.IsEnabled(disabled, "write_file"));
        Assert.True(ToolSettingsSchema.IsEnabled(disabled, "read_file"));
        Assert.True(WorkspaceToolCatalog.IsWorkspaceMutating("delete_file"));
        Assert.Contains("delete_file", WorkspaceToolCatalog.WorkspaceMutatingBuiltins);
    }

    [Fact]
    public async Task SharedLimitChanges_RejectInvalidAgentOverridesWithoutAdvancingRevision()
    {
        var store = _factory.Services.GetRequiredService<IToolSettingsStore>();
        var agentId = Guid.NewGuid();
        await store.SaveAsync(null, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = 1000 } }), 0);
        await store.SaveAsync(agentId, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = 500 } }), 0);
        await Assert.ThrowsAsync<ToolSettingsException>(() => store.SaveAsync(agentId, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = (int?)null } }), 1));
        await Assert.ThrowsAsync<ToolSettingsException>(() => store.SaveAsync(null, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = 400 } }), 1));
        Assert.Equal(1, (await store.GetAsync()).Revision);
        Assert.Equal(1, (await store.GetAsync(agentId)).Revision);
        await store.ResetAsync(agentId, 1);
        await store.SaveAsync(null, JsonSerializer.SerializeToElement(new { read = new { max_file_bytes = 400 } }), 1);
        Assert.Equal(400, (await store.GetAsync(agentId)).EffectiveSettings.GetProperty("read").GetProperty("max_file_bytes").GetInt32());
    }

    [Fact]
    public async Task McpCreate_RequiresTheRevisionZeroPrecondition()
    {
        var client = _factory.CreateClient();
        var resource = new { id = "conditional-create", command = "fake-mcp" };
        Assert.Equal((HttpStatusCode)428, (await client.PostAsJsonAsync("/api/v1/tools/mcp/servers", resource)).StatusCode);
        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tools/mcp/servers") { Content = JsonContent.Create(resource) };
        wrong.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CreateMcpAsync(client, resource)).StatusCode);
    }

    [Fact]
    public async Task McpSecrets_AreMaskedAndOldSnapshotKeepsItsCredentialVersion()
    {
        var client = _factory.CreateClient();
        var response = await CreateMcpAsync(client, new { id = "test-mcp", name = "Test", command = "fake-mcp", env = new Dictionary<string,string> { ["TOKEN"] = "credential-version-one" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("********", dto.GetProperty("env").GetProperty("TOKEN").GetString());
        var id = dto.GetProperty("resource_id").GetGuid();
        var resolver = _factory.Services.GetRequiredService<IToolConfigurationResolver>();
        var snapshot = await resolver.ResolveAsync(null);
        var server = Assert.Single(snapshot.McpServers);
        Assert.Null(server.Env);
        Assert.NotNull(server.SecretReferences);
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tools/mcp/servers/{id}") { Content = JsonContent.Create(new { id = "test-mcp", name = "Test", command = "fake-mcp", env = new Dictionary<string,string> { ["TOKEN"] = "credential-version-two" } }) };
        update.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(update)).StatusCode);
        var materialized = await resolver.MaterializeForCallAsync(snapshot, ["mcp_invoke"]);
        Assert.Equal("credential-version-one", Assert.Single(materialized.McpServers).Env!["TOKEN"]);
        Assert.Null(Assert.Single(materialized.McpServers).SecretReferences);
        var fresh = await resolver.MaterializeForCallAsync(await resolver.ResolveAsync(null), ["mcp_invoke"]);
        Assert.Equal("credential-version-two", Assert.Single(fresh.McpServers).Env!["TOKEN"]);
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.DoesNotContain("credential-version", (await db.McpResources.SingleAsync()).SecretReferencesJson);
    }

    [Fact]
    public async Task McpHttpTransport_RequiresUrlAndKeepsHeaderSecretsOutOfSnapshots()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateMcpAsync(client, new { id = "bad-http", transport = "http", args = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateMcpAsync(client, new { id = "bad-scheme", transport = "http", url = "ftp://example.test/mcp", args = Array.Empty<string>() })).StatusCode);

        var created = await CreateMcpAsync(client, new
        {
            id = "remote-http", name = "Remote HTTP", transport = "http", url = "https://example.test/mcp",
            args = Array.Empty<string>(), headers = new Dictionary<string, string> { ["Authorization"] = "Bearer credential-one" },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("http", dto.GetProperty("transport").GetString());
        Assert.Equal("https://example.test/mcp", dto.GetProperty("url").GetString());
        Assert.Equal("********", dto.GetProperty("headers").GetProperty("Authorization").GetString());

        var resolver = _factory.Services.GetRequiredService<IToolConfigurationResolver>();
        var snapshot = await resolver.ResolveAsync(null);
        var server = Assert.Single(snapshot.McpServers);
        Assert.Equal("http", server.Transport);
        Assert.Null(server.Headers);
        Assert.NotNull(server.HeaderSecretReferences);
        var materialized = await resolver.MaterializeForCallAsync(snapshot, ["mcp_invoke"]);
        Assert.Equal("Bearer credential-one", Assert.Single(materialized.McpServers).Headers!["Authorization"]);

        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ToolsSettingsDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.DoesNotContain("credential-one", (await db.McpResources.SingleAsync()).HeaderReferencesJson);
    }

    [Fact]
    public async Task AdmissionSnapshots_FreezeSharedDefaultsAndPerAgentOverrides()
    {
        var client = _factory.CreateClient();
        var agents = await client.GetFromJsonAsync<JsonElement>("/api/v1/agents");
        var agentId = agents.EnumerateArray().First().GetProperty("id").GetGuid();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tools/settings/agents/{agentId}") { Content = JsonContent.Create(new { settings = new { web = new { enabled = false } } }) };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var resolver = _factory.Services.GetRequiredService<IToolConfigurationResolver>();
        var frozen = await resolver.ResolveForRunAsync(null, [agentId]);
        Assert.False(frozen.AgentContexts[agentId].Settings.GetProperty("web").GetProperty("enabled").GetBoolean());
        Assert.True(frozen.SharedContext.Settings.GetProperty("web").GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(client, "0", new { shell = new { enabled = false } })).StatusCode);
        Assert.True(frozen.AgentContexts[agentId].Settings.GetProperty("shell").GetProperty("enabled").GetBoolean());
        Assert.False((await resolver.ResolveAsync(null, agentId)).Settings.GetProperty("shell").GetProperty("enabled").GetBoolean());
        var call = await resolver.MaterializeForCallAsync(frozen.AgentContexts[agentId], ["shell", "web_fetch", "read_file"], runId: "frozen-test");
        Assert.Equal(new[] { "shell", "read_file" }, call.AllowedToolIds);
        Assert.Equal("frozen-test", call.RunId);
    }

    [Fact]
    public async Task Admission_UsesOneResourceVersionAcrossAllAgentBindings()
    {
        var registry = _factory.Services.GetRequiredService<IMcpResourceRegistry>();
        var original = await registry.SaveAsync(null, new() { Id = "one-admission", Command = "captured-version" }, 0);
        var counting = new AdmissionRegistry(registry, original);
        var resolver = new ToolConfigurationResolver(_factory.Services.GetRequiredService<IToolSettingsStore>(), counting, _factory.Services.GetRequiredService<ISecretStore>(), _factory.Services);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var admitted = await resolver.ResolveForRunAsync(null, ids);
        Assert.Equal(1, counting.Captures);
        Assert.Equal("captured-version", Assert.Single(admitted.SharedContext.McpServers).Command);
        Assert.All(admitted.AgentContexts.Values, context => Assert.Equal("captured-version", Assert.Single(context.McpServers).Command));
        Assert.Equal("saved-after-capture", (await registry.GetAsync(original.ResourceId))!.Command);
    }

    [Fact]
    public async Task LegacyMcpFileIsIgnoredWhenTheNewScopeConfigurationIsAuthoritative()
    {
        File.WriteAllText(Path.Combine(_root, "mcp_servers.json"), "{\"servers\":[{\"id\":\"legacy\",\"name\":\"Legacy\",\"command\":\"original\",\"env\":{\"TOKEN\":\"old-secret\"}}]}");
        var client = _factory.CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/tools/mcp/servers");
        Assert.Empty(first.EnumerateArray());
        File.WriteAllText(Path.Combine(_root, "mcp_servers.json"), "{\"servers\":[{\"id\":\"legacy\",\"command\":\"external-edit\"}]}");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/v1/tools/mcp/servers");
        Assert.Empty(second.EnumerateArray());
    }

    [Fact]
    public async Task BrokenLegacyFileDoesNotAffectCurrentToolConfiguration()
    {
        File.WriteAllText(Path.Combine(_root, "mcp_servers.json"), "broken-json");
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/tools/mcp/servers")).StatusCode);
        var context = await _factory.Services.GetRequiredService<IToolConfigurationResolver>().ResolveAsync(null);
        Assert.Null(context.McpImportError);
        Assert.Empty(context.McpServers);
        Assert.True(context.Settings.GetProperty("shell").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ProjectShadowing_OnlyAppliesToInheritedMcpBindings()
    {
        var client = _factory.CreateClient();
        var projectRoot = Path.Combine(_root, "project"); Directory.CreateDirectory(projectRoot);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Bindings", path = projectRoot });
        var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var shared = await CreateMcpAsync(client, new { id = "same", command = "shared" });
        var local = await CreateMcpAsync(client, new { project_id = projectId, id = "same", command = "project" });
        Assert.Equal(HttpStatusCode.OK, shared.StatusCode); Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        var sharedId = (await shared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resource_id").GetGuid();
        var projectResourceId = (await local.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("resource_id").GetGuid();
        var registry = _factory.Services.GetRequiredService<IMcpResourceRegistry>();
        Assert.Equal(projectResourceId, Assert.Single(await registry.ResolveAsync(projectId,null)).ResourceId);
        Assert.Equal(2, (await registry.ResolveAsync(projectId,[sharedId,projectResourceId])).Count);
        using var settings = new HttpRequestMessage(HttpMethod.Put,$"/api/v1/tools/settings/defaults?project_id={projectId}") { Content = JsonContent.Create(new { settings = new { mcp = new { server_resource_ids = new[] { sharedId,projectResourceId } } } }) };
        settings.Headers.TryAddWithoutValidation("If-Match","\"0\"");
        Assert.Equal(HttpStatusCode.OK,(await client.SendAsync(settings)).StatusCode);
        Assert.Equal(2,(await _factory.Services.GetRequiredService<IToolConfigurationResolver>().ResolveAsync(projectId)).McpServers.Count);
        await Assert.ThrowsAsync<ToolSettingsException>(() => registry.ResolveAsync(null,[projectResourceId]));
    }

    [Fact]
    public async Task EffectiveInspection_ReportsDeletedExactBindingsWithoutFailingTheSettingsPage()
    {
        var client = _factory.CreateClient();
        var created = await (await CreateMcpAsync(client, new { id = "stale-check", command = "missing-after-delete" })).Content.ReadFromJsonAsync<JsonElement>();
        var resourceId = created.GetProperty("resource_id").GetGuid();
        using var save = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tools/settings/defaults") { Content = JsonContent.Create(new { settings = new { mcp = new { server_resource_ids = new[] { resourceId } } } }) };
        save.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(save)).StatusCode);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tools/mcp/servers/{resourceId}");
        delete.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        var response = await client.GetAsync("/api/v1/tools/settings/effective");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var effective = await response.Content.ReadFromJsonAsync<JsonElement>();
        var diagnostic = Assert.Single(effective.GetProperty("resource_diagnostics").EnumerateArray());
        Assert.Equal("missing", diagnostic.GetProperty("status").GetString());
        Assert.Equal(resourceId, diagnostic.GetProperty("resource_id").GetGuid());
        Assert.Empty(effective.GetProperty("mcp_servers").EnumerateArray());
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string revision, object settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tools/settings/defaults") { Content = JsonContent.Create(new { settings }) };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\""); return client.SendAsync(request);
    }
    private static Task<HttpResponseMessage> CreateMcpAsync(HttpClient client, object resource)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tools/mcp/servers") { Content = JsonContent.Create(resource) };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        return client.SendAsync(request);
    }
    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string,string?> { ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root,"tinadec.db"), ["TinadecPersistence:DataRoot"] = Path.Combine(root,"data"), ["TinadecTools:DefaultWorkspaceRoot"] = root, ["Logging:LogLevel:Default"] = "Warning" }));
            builder.ConfigureTestServices(services => { services.RemoveAll<IToolProvider>(); services.AddSingleton<IToolProvider>(new Provider()); });
        }
    }
    private sealed class Provider : IToolProvider
    {
        public Task<ToolManifestDto> EnsureStartedAsync(string root, CancellationToken ct = default) => GetManifestAsync(root,ct);
        public Task<ToolManifestDto> GetManifestAsync(string root, CancellationToken ct = default) => Task.FromResult(new ToolManifestDto { ProtocolVersion = 2, Tools = [new() { Id = "shell" }, new() { Id = "read_file" }, new() { Id = "web_fetch" }] });
        public Task<ToolWireResponseDto> CallAsync(string root, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => Task.FromResult(new ToolWireResponseDto { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) });
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class AdmissionRegistry(IMcpResourceRegistry inner, McpResourceDto resource) : IMcpResourceRegistry
    {
        public int Captures { get; private set; }
        public async Task<McpResourceCatalogSnapshot> CaptureAsync(Guid? projectId, CancellationToken ct = default)
        {
            Captures++;
            var snapshot = await inner.CaptureAsync(projectId, ct);
            await inner.SaveAsync(resource.ResourceId, new() { Id = resource.Id, Command = "saved-after-capture" }, resource.Revision, ct);
            return snapshot;
        }
        public Task<IReadOnlyList<ToolMcpServerSnapshotDto>> ResolveAsync(Guid? projectId, IReadOnlyList<Guid>? ids, CancellationToken ct = default) => throw new InvalidOperationException("Admission must select bindings from its one captured catalog.");
        public Task<IReadOnlyList<McpResourceDto>> ListAsync(Guid? id, CancellationToken ct = default) => inner.ListAsync(id, ct);
        public Task<McpResourceDto?> GetAsync(Guid id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<McpResourceDto> SaveAsync(Guid? id, McpResourceWriteDto input, long revision, CancellationToken ct = default) => inner.SaveAsync(id, input, revision, ct);
        public Task DeleteAsync(Guid id, long revision, CancellationToken ct = default) => inner.DeleteAsync(id, revision, ct);
        public Task EnsureImportedAsync(Guid? id, CancellationToken ct = default) => inner.EnsureImportedAsync(id, ct);
    }
}
