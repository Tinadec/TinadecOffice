using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.Browser;

namespace TinadecTools.Tests;

public sealed class BrowserToolManifestTests
{
    private static async Task<Dictionary<string, JsonElement>> ManifestAsync()
    {
        GeneratedToolRegistry.RegisterAll();
        BrowserToolRegistration.Register();

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "#manifest",
            SessionId = "test",
            ToolCallId = 1,
            Approved = false,
        });

        using var document = JsonDocument.Parse(response.Response.GetRawText());
        return document.RootElement.GetProperty("tools").EnumerateArray()
            .ToDictionary(tool => tool.GetProperty("id").GetString()!, tool => tool.Clone());
    }

    [Fact]
    public async Task BrowserReadToolsAreLowRiskAndAutoReleasable()
    {
        var tools = await ManifestAsync();
        foreach (var id in new[]
                 {
                     BrowserToolRegistration.SnapshotToolId,
                     BrowserToolRegistration.TabsListToolId,
                     BrowserToolRegistration.WaitToolId,
                     BrowserToolRegistration.ScreenshotToolId,
                 })
        {
            var entry = tools[id];
            Assert.False(entry.GetProperty("requires_approval").GetBoolean(), id);
            Assert.Equal("low", entry.GetProperty("risk").GetString());
            Assert.False(entry.GetProperty("mutates_workspace").GetBoolean(), id);
            Assert.Equal("safe", entry.GetProperty("retry_safety").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()), id);
        }
    }

    [Fact]
    public async Task NavigateIsApprovalGatedHighRiskAndNotAWorkspaceMutation()
    {
        var tools = await ManifestAsync();
        var entry = tools[BrowserToolRegistration.NavigateToolId];

        Assert.True(entry.GetProperty("requires_approval").GetBoolean());
        Assert.Equal("high", entry.GetProperty("risk").GetString());
        Assert.False(entry.GetProperty("mutates_workspace").GetBoolean());
        Assert.Equal("unsafe", entry.GetProperty("retry_safety").GetString());
        var properties = entry.GetProperty("input_schema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("url", out _));
        Assert.True(properties.TryGetProperty("wait_ms", out _));
        Assert.Contains("url", entry.GetProperty("input_schema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task BrowserActToolsAreApprovalGatedHighRiskAndNotWorkspaceMutations()
    {
        var tools = await ManifestAsync();
        foreach (var id in new[]
                 {
                     BrowserToolRegistration.ClickToolId,
                     BrowserToolRegistration.TypeToolId,
                     BrowserToolRegistration.PressToolId,
                     BrowserToolRegistration.ScrollToolId,
                     BrowserToolRegistration.HistoryToolId,
                     BrowserToolRegistration.TabOpenToolId,
                     BrowserToolRegistration.TabSelectToolId,
                     BrowserToolRegistration.TabCloseToolId,
                     BrowserToolRegistration.BatchToolId,
                 })
        {
            var entry = tools[id];
            Assert.True(entry.GetProperty("requires_approval").GetBoolean(), id);
            Assert.Equal("high", entry.GetProperty("risk").GetString());
            Assert.False(entry.GetProperty("mutates_workspace").GetBoolean(), id);
            Assert.Equal("unsafe", entry.GetProperty("retry_safety").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()), id);
        }
    }

    [Fact]
    public async Task BatchSchemaBindsTheStepList()
    {
        var tools = await ManifestAsync();
        var steps = tools[BrowserToolRegistration.BatchToolId].GetProperty("input_schema").GetProperty("properties").GetProperty("steps");
        Assert.Equal("array", steps.GetProperty("type").GetString());
        Assert.Equal(1, steps.GetProperty("minItems").GetInt32());
        Assert.Equal(20, steps.GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public async Task UnapprovedClickIsRefusedBeforeTheRuntimeIsTouched()
    {
        GeneratedToolRegistry.RegisterAll();
        BrowserToolRegistration.Register();
        using var parameters = JsonDocument.Parse("""{"ref":1}""");

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = BrowserToolRegistration.ClickToolId,
            SessionId = "test",
            ToolCallId = 8,
            Approved = false,
            Params = parameters.RootElement.Clone(),
        });

        Assert.False(response.IsSuccess);
        Assert.Equal(NotApprovedResponse.MESSAGE, response.Response.GetString());
    }

    [Fact]
    public async Task SnapshotSchemaExposesTheCharacterBudget()
    {
        var tools = await ManifestAsync();
        var properties = tools[BrowserToolRegistration.SnapshotToolId].GetProperty("input_schema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("max_chars", out var maxChars));
        Assert.Equal("integer", maxChars.GetProperty("type").GetString());
    }

    [Fact]
    public async Task UnapprovedNavigateIsRefusedBeforeTheRuntimeIsTouched()
    {
        GeneratedToolRegistry.RegisterAll();
        BrowserToolRegistration.Register();
        using var parameters = JsonDocument.Parse("""{"url":"https://example.com/"}""");

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = BrowserToolRegistration.NavigateToolId,
            SessionId = "test",
            ToolCallId = 7,
            Approved = false,
            Params = parameters.RootElement.Clone(),
        });

        Assert.False(response.IsSuccess);
        Assert.Equal(NotApprovedResponse.MESSAGE, response.Response.GetString());
    }
}
