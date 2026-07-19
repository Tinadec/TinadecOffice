using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TinadecCore.Api.Tests;

public sealed class ApiEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ApiEndpointTests(WebApplicationFactory<Program> factory)
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
    public async Task Manifest_Returns200_WithSnakeCaseAndEightModules()
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
        Assert.Equal("1.13.0", fwVersion.GetString());
        Assert.True(framework.TryGetProperty("primitives", out var primitives));
        Assert.True(primitives.GetArrayLength() > 0);

        // modules (incremental field — ten modules, including tenancy and VectorStore)
        Assert.True(root.TryGetProperty("modules", out var modules));
        Assert.Equal(10, modules.GetArrayLength());

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

        // Framework ready
        Assert.True(root.TryGetProperty("framework_ready", out var fwReady));
        Assert.True(fwReady.GetBoolean());

        Assert.True(root.TryGetProperty("framework_name", out var fwName));
        Assert.Equal("Microsoft Agent Framework", fwName.GetString());

        Assert.True(root.TryGetProperty("framework_version", out var fwVersion));
        Assert.Equal("1.13.0", fwVersion.GetString());

        // Status should be "warning" because some modules are not_configured
        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("warning", status.GetString());

        // Storage receipt (shared database abstraction)
        Assert.True(root.TryGetProperty("storage", out var storage));
        Assert.True(storage.TryGetProperty("provider", out var storageProvider));
        Assert.Equal("sqlite", storageProvider.GetString());
        Assert.True(storage.TryGetProperty("state", out var storageState));
        Assert.Equal("ready", storageState.GetString());

        // Modules
        Assert.True(root.TryGetProperty("modules", out var modules));
        var moduleList = modules.EnumerateArray().ToList();
        Assert.Equal(10, moduleList.Count);

        // At least some modules should be "not_configured"
        var notConfiguredCount = moduleList.Count(m =>
        {
            return m.TryGetProperty("module_state", out var state) &&
                   state.GetString() == "not_configured";
        });
        Assert.True(notConfiguredCount > 0,
            "Expected at least one module with state 'not_configured'");
    }

    [Fact]
    public async Task Readiness_LoopGuardAndLifecycleAreRegistered()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/readiness");
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);

        var modules = doc.RootElement.GetProperty("modules").EnumerateArray().ToList();
        var moduleStates = modules.Select(m =>
        {
            m.TryGetProperty("module_id", out var id);
            m.TryGetProperty("module_state", out var state);
            return (id.GetString() ?? "", state.GetString() ?? "");
        }).ToDictionary();

        // loop_guard and lifecycle should be "registered" (not "not_configured")
        Assert.Equal("registered", moduleStates["loop_guard"]);
        Assert.Equal("registered", moduleStates["lifecycle"]);
    }
}
