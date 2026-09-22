using System.Text.Json;
using TinadecTools.Tools.Mcp;

namespace TinadecTools.Tests;

public sealed class McpPassThroughTests : IAsyncLifetime
{
    private string? _configPath;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await McpRuntime.DisposeAsync();
    }

    [Fact]
    public async Task McpList_ReturnsToolsFromConfiguredServer()
    {
        ConfigureRuntime();

        var response = await McpListTool.HandleAsync(new McpListParams(), CancellationToken.None);

        var server = Assert.Single(response.Servers);
        Assert.Equal("mock", server.Id);
        Assert.Equal("connected", server.Status);
        Assert.Contains(server.Tools, tool => tool.Name == "echo" && tool.Description?.Contains("Echoes") == true);
    }

    [Fact]
    public async Task McpSearch_FuzzyMatchesToolNameAndDescription()
    {
        ConfigureRuntime();

        var response = await McpSearchTool.HandleAsync(new McpSearchParams
        {
            Query = "read file",
            IncludeSchema = false
        }, CancellationToken.None);

        var result = Assert.Single(response.Results, result => result.Tool.Name == "read_file");
        Assert.Equal("mock", result.ServerId);
        Assert.True(result.Score > 0);
    }

    [Fact]
    public async Task McpInvoke_CallsToolAndReturnsRawMcpResult()
    {
        ConfigureRuntime();
        using var arguments = JsonDocument.Parse("{\"message\":\"hello\"}");

        var response = await McpInvokeTool.HandleAsync(new McpInvokeParams
        {
            ServerId = "mock",
            ToolName = "echo",
            Arguments = arguments.RootElement.Clone()
        }, CancellationToken.None);

        Assert.Equal("mock", response.ServerId);
        Assert.Equal("echo", response.ToolName);
        Assert.True(response.Success, response.Error);
        Assert.Contains("echo:hello", response.Result.GetRawText());
    }

    [Fact]
    public async Task McpSearch_WithoutConfiguredServers_ExplainsWhyTheResultIsEmpty()
    {
        // An unexplained empty result is what makes an agent report "the tool
        // returned nothing" and then guess a server id that never existed.
        var missing = Path.Combine(Path.GetTempPath(), $"tinadec-tools-mcp-missing-{Guid.NewGuid():N}.json");
        McpRuntime.ConfigureForTests(missing);

        var response = await McpSearchTool.HandleAsync(new McpSearchParams { Query = "项目" }, CancellationToken.None);

        Assert.Empty(response.Results);
        Assert.NotNull(response.Reason);
        Assert.Contains("No MCP server is configured", response.Reason!, StringComparison.Ordinal);
        Assert.Contains("ls, file_search", response.Reason!, StringComparison.Ordinal);
        Assert.Equal(missing, response.ConfigPath);
        Assert.Equal(0, response.ServersQueried);
    }

    [Fact]
    public async Task McpSearch_QueryMatchingNothing_ExplainsTheMiss()
    {
        ConfigureRuntime();

        var response = await McpSearchTool.HandleAsync(
            new McpSearchParams { Query = "zzz-nonexistent-term-zzz" }, CancellationToken.None);

        Assert.Empty(response.Results);
        Assert.NotNull(response.Reason);
        Assert.Contains("No MCP tool matched", response.Reason!, StringComparison.Ordinal);
        Assert.Equal(1, response.ServersQueried);
        Assert.True(response.ToolsListed > 0, "the reachable server listed tools, so the miss is a scoring miss");
        Assert.Empty(response.Failures);
    }

    [Fact]
    public async Task McpSearch_BlankQuery_ExplainsTheMiss()
    {
        ConfigureRuntime();
        var response = await McpSearchTool.HandleAsync(new McpSearchParams { Query = "   " }, CancellationToken.None);
        Assert.Empty(response.Results);
        Assert.Contains("query is required", response.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpList_WithoutSchemas_StillReachesTheWire()
    {
        ConfigureRuntime();

        var response = await McpListTool.HandleAsync(
            new McpListParams { IncludeSchema = false }, CancellationToken.None);

        var tools = Assert.Single(response.Servers).Tools;
        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.Null(tool.InputSchema));

        // The half the existing handler tests never checked: whatever the handler returns is handed
        // to the source-generated context, and a withheld schema used to be an unwritable
        // default JsonElement — so `mcp_list include_schema:false` failed the whole call with
        // "Operation is not valid due to the current state of the object." while the in-memory
        // object graph looked perfectly fine.
        using (var list = JsonDocument.Parse(
                   JsonSerializer.Serialize(response, McpListToolJsonContext.Default.McpListResponse)))
        {
            AssertWireSchemaIsNull(list.RootElement
                .GetProperty("servers")[0].GetProperty("tools")[0]);
        }
    }

    [Fact]
    public async Task McpSearch_WithholdingSchemas_StillReachesTheWire()
    {
        ConfigureRuntime();

        // No IncludeSchema set: false is the default, which is what a model gets when it passes
        // only a query. Every non-empty search result therefore used to serialize-fail.
        var response = await McpSearchTool.HandleAsync(
            new McpSearchParams { Query = "read file" }, CancellationToken.None);

        Assert.NotEmpty(response.Results);
        using var search = JsonDocument.Parse(
            JsonSerializer.Serialize(response, McpSearchToolJsonContext.Default.McpSearchResponse));
        AssertWireSchemaIsNull(search.RootElement
            .GetProperty("results")[0].GetProperty("tool"));
    }

    /// <summary>
    /// The key must arrive as JSON null, not be absent: absent is what a caller reads as "this row
    /// is malformed", while null is the honest "no schema was requested". Compared after parsing
    /// because the source-generated contexts write indented JSON, and matching on text would make
    /// this test about formatting.
    /// </summary>
    private static void AssertWireSchemaIsNull(JsonElement tool)
    {
        Assert.True(tool.TryGetProperty("input_schema", out var schema),
            "A withheld schema has to be stated, not silently dropped from the row.");
        Assert.Equal(JsonValueKind.Null, schema.ValueKind);
    }

    private void ConfigureRuntime()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"tinadec-tools-mcp-{Guid.NewGuid():N}.json");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "mcp-mock-server.js");
        var config = new McpServersFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "mock",
                    Name = "Mock MCP",
                    Command = "node",
                    Args = [fixturePath]
                }
            ]
        };

        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, McpJsonContext.Default.McpServersFile));
        McpRuntime.ConfigureForTests(_configPath);
    }
}
