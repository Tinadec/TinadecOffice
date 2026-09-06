using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

public sealed class ApiEndpointTests : IClassFixture<ApiEndpointFactory>
{
    private readonly ApiEndpointFactory _factory;
    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ApiEndpointTests(ApiEndpointFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_Returns200_WithLegacyCompatibleStructure()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("name", out var name));
        Assert.Equal("tinadec-core", name.GetString());

        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("ok", status.GetString());

        Assert.True(root.TryGetProperty("version", out var version));
        Assert.Equal("0.1.0", version.GetString());

        Assert.True(root.TryGetProperty("time", out var time));
        Assert.True(time.TryGetDateTimeOffset(out _));
    }

    [Fact]
    public async Task Manifest_Returns200_WithSnakeCaseAndRegisteredModules()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/harness/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // snake_case: runtime, ownership_model
        Assert.True(root.TryGetProperty("runtime", out _));
        Assert.True(root.TryGetProperty("ownership_model", out _));

        // tool_registry
        Assert.True(root.TryGetProperty("tool_registry", out var toolRegistry));
        Assert.True(toolRegistry.TryGetProperty("declared_tool_count", out _));
        Assert.True(toolRegistry.TryGetProperty("selection_policy", out _));

        // agent_layers (two layers: planning + execution)
        Assert.True(root.TryGetProperty("agent_layers", out var agentLayers));
        Assert.Equal(2, agentLayers.GetArrayLength());

        // framework (incremental field)
        Assert.True(root.TryGetProperty("framework", out var framework));
        Assert.True(framework.TryGetProperty("name", out var fwName));
        Assert.Equal("Microsoft Agent Framework", fwName.GetString());
        Assert.True(framework.TryGetProperty("version", out var fwVersion));
        Assert.Equal("1.18.0", fwVersion.GetString());
        Assert.True(framework.TryGetProperty("primitives", out var primitives));
        Assert.True(primitives.GetArrayLength() > 0);

        // modules include the explicit Governance policy-decision module.
        Assert.True(root.TryGetProperty("modules", out var modules));
        Assert.Equal(13, modules.GetArrayLength());
        Assert.Contains(modules.EnumerateArray(), module => module.GetProperty("module_id").GetString() == "governance");

        // design_notes
        Assert.True(root.TryGetProperty("design_notes", out _));
    }

    [Fact]
    public async Task Manifest_ModuleDescriptorsHaveCorrectFields()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/harness/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);

        var modules = doc.RootElement.GetProperty("modules").EnumerateArray().ToList();
        foreach (var mod in modules)
        {
            Assert.True(mod.TryGetProperty("module_id", out _));
            Assert.True(mod.TryGetProperty("version", out _));
            Assert.True(mod.TryGetProperty("dependencies", out _));
            Assert.True(mod.TryGetProperty("capabilities", out _));
            Assert.True(mod.TryGetProperty("language", out _));
            Assert.True(mod.TryGetProperty("maf_primitives", out _));
            Assert.True(mod.TryGetProperty("registration_status", out _));
        }
    }

    [Fact]
    public async Task Readiness_Returns200_WithCorrectModuleStates()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Unified receipt contract: overall ready|degraded|blocked + fixed items.
        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Contains(status.GetString(), new[] { "ready", "degraded", "blocked" });
        Assert.True(root.TryGetProperty("checked_at", out _));

        Assert.True(root.TryGetProperty("items", out var items));
        var itemList = items.EnumerateArray().ToList();
        var expectedIds = new[]
        {
            "database", "core_storage", "agent_pack", "default_mode", "model_provider",
            "model_secret", "model_route", "model_probe", "tool_provider", "manifest_hash"
        };
        var ids = itemList.Select(item => item.GetProperty("id").GetString()).ToHashSet();
        foreach (var expectedId in expectedIds)
        {
            Assert.True(ids.Contains(expectedId), $"readiness receipt is missing item '{expectedId}'");
        }

        // Every item carries status and a timestamp; reason/action are omitted
        // when null (ready items carry no remediation hint).
        foreach (var item in itemList)
        {
            Assert.True(item.TryGetProperty("status", out _));
            Assert.True(item.TryGetProperty("checked_at", out _));
        }

        var database = itemList.Single(item => item.GetProperty("id").GetString() == "database");
        Assert.Equal("ready", database.GetProperty("status").GetString());
        var coreStorage = itemList.Single(item => item.GetProperty("id").GetString() == "core_storage");
        Assert.Equal("ready", coreStorage.GetProperty("status").GetString());

        // Module registration facts stay on the harness manifest.
        var manifestResponse = await client.GetAsync("/api/v1/harness/manifest");
        manifestResponse.EnsureSuccessStatusCode();
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync();
        using var manifestDoc = await JsonDocument.ParseAsync(manifestStream);
        var modules = manifestDoc.RootElement.GetProperty("modules").EnumerateArray().ToList();
        Assert.Equal(13, modules.Count);
        Assert.All(modules, m =>
        {
            Assert.True(m.TryGetProperty("registration_status", out var state));
            Assert.Contains(state.GetString() ?? "", new[] { "registered", "notconfigured", "not_configured", "disabled" });
        });
    }

    [Fact]
    public async Task Readiness_LoopGuardAndLifecycleAreRegistered()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/harness/manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);

        var modules = doc.RootElement.GetProperty("modules").EnumerateArray().ToList();
        var moduleStates = modules.Select(m =>
        {
            m.TryGetProperty("module_id", out var id);
            m.TryGetProperty("registration_status", out var state);
            return (id.GetString() ?? "", state.GetString() ?? "");
        }).ToDictionary();

        // loop_guard and lifecycle should be "registered" (not "not_configured")
        Assert.Equal("registered", moduleStates["loop_guard"]);
        Assert.Equal("registered", moduleStates["lifecycle"]);
    }

    [Fact]
    public async Task ModelProviderTemplates_ReturnsThreeProtocolTemplates()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/model-provider-templates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);

        var templates = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, templates.Count);

        var byProtocol = templates.ToDictionary(t => t.GetProperty("protocol").GetString() ?? "");
        Assert.Contains("openai-chat", byProtocol.Keys);
        Assert.Contains("openai-responses", byProtocol.Keys);
        Assert.Contains("anthropic-messages", byProtocol.Keys);

        var anthropic = byProtocol["anthropic-messages"];
        Assert.Equal("anthropic", anthropic.GetProperty("driver").GetString());
        Assert.Equal("https://api.anthropic.com/v1", anthropic.GetProperty("default_base_url").GetString());

        foreach (var template in templates)
        {
            Assert.True(template.TryGetProperty("provider_family", out _));
            Assert.True(template.TryGetProperty("capabilities", out _));
        }
    }
}

public sealed class ApiEndpointFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-api-tests", Guid.NewGuid().ToString("N"));

    public ApiEndpointFactory() => Directory.CreateDirectory(_root);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
