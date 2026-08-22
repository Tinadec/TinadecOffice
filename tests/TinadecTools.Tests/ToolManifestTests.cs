using System.Text.Json;
using TinadecTools.Abstractions;

namespace TinadecTools.Tests;

/// <summary>
/// Verifies the `#manifest` line-protocol handshake: Core sends a tool call with
/// tool_id `#manifest` and receives the v2 executable metadata contract.
/// </summary>
public sealed class ToolManifestTests
{
    [Fact]
    public async Task DispatchAsync_Manifest_ReturnsProtocolVersionAndToolEntries()
    {
        GeneratedToolRegistry.RegisterAll();

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "#manifest",
            SessionId = "test",
            ToolCallId = 1,
            Approved = false
        });

        Assert.True(response.IsSuccess);
        Assert.Equal(1, response.CallId);

        using var document = JsonDocument.Parse(response.Response.GetRawText());
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("protocol_version").GetInt32());
        Assert.Matches("^[a-f0-9]{64}$", root.GetProperty("manifest_hash").GetString());

        var tools = root.GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.NotEmpty(tools.EnumerateArray());

        foreach (var tool in tools.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("id").GetString()));
            Assert.True(tool.TryGetProperty("requires_approval", out _));
            Assert.Equal(JsonValueKind.Object, tool.GetProperty("input_schema").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("risk").GetString()));
            Assert.True(tool.TryGetProperty("mutates_workspace", out _));
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("retry_safety").GetString()));
            Assert.Equal(JsonValueKind.Array, tool.GetProperty("confirmation_fields").ValueKind);
        }

        // Approval flags must survive the generated-registration path.
        var byId = tools.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x, StringComparer.OrdinalIgnoreCase);
        Assert.True(byId["write_file"].GetProperty("requires_approval").GetBoolean());
        Assert.True(byId["write_file"].GetProperty("mutates_workspace").GetBoolean());
        Assert.Equal("unsafe", byId["write_file"].GetProperty("retry_safety").GetString());
        Assert.True(byId["write_file"].GetProperty("input_schema").GetProperty("properties").TryGetProperty("filepath", out _));
        Assert.True(byId["write_file"].GetProperty("input_schema").GetProperty("properties").TryGetProperty("content", out _));
        Assert.False(byId["read_file"].GetProperty("requires_approval").GetBoolean());
        Assert.False(byId["read_file"].GetProperty("mutates_workspace").GetBoolean());
        Assert.Equal("safe", byId["read_file"].GetProperty("retry_safety").GetString());
        Assert.True(byId["command_run"].GetProperty("requires_approval").GetBoolean());

        // Git queries and mutations intentionally share the same provider, but
        // their governance metadata must remain distinct. Desktop uses the
        // direct transport for queries while Core gates every mutation.
        Assert.False(byId["git_status"].GetProperty("requires_approval").GetBoolean());
        Assert.False(byId["git_status"].GetProperty("mutates_workspace").GetBoolean());
        Assert.Equal("safe", byId["git_status"].GetProperty("retry_safety").GetString());
        Assert.False(byId["git_diff"].GetProperty("requires_approval").GetBoolean());
        Assert.False(byId["git_log_list"].GetProperty("mutates_workspace").GetBoolean());

        AssertGitMutation(byId, "git_commit", "confirm_commit");
        AssertGitMutation(byId, "git_fetch", "confirm_fetch");
        AssertGitMutation(byId, "git_push", "confirm_push");
        AssertGitMutation(byId, "git_pull", "confirm_pull");
        AssertGitMutation(byId, "git_checkout", "confirm_checkout");
        AssertGitMutation(byId, "git_branch_create", "confirm_branch_create");
        AssertGitMutation(byId, "git_branch_delete", "confirm_branch_delete");
        AssertGitMutation(byId, "git_branch_rename", "confirm_branch_rename");
        AssertGitMutation(byId, "git_merge", "confirm_merge");
        AssertGitMutation(byId, "git_rebase", "confirm_rebase");
        AssertGitMutation(byId, "git_conflict_resolve", "confirm_resolve");
        AssertGitMutation(byId, "git_worktree_create", "confirm_worktree_create");
        AssertGitMutation(byId, "git_worktree_remove", "confirm_worktree_remove");
        Assert.True(byId["git_stage"].GetProperty("requires_approval").GetBoolean());
        Assert.True(byId["git_stage"].GetProperty("mutates_workspace").GetBoolean());
        Assert.Empty(byId["git_stage"].GetProperty("confirmation_fields").EnumerateArray());
    }

    [Fact]
    public async Task DispatchAsync_Manifest_LineProtocolRoundTrip()
    {
        GeneratedToolRegistry.RegisterAll();

        // Mirrors the Program.cs main loop: one JSON request line in, one JSON response line out.
        const string requestLine = """{"tool_id":"#manifest","session_id":"core","toolcall_id":42,"approved":false}""";
        using var parsed = JsonDocument.Parse(requestLine);
        var request = JsonSerializer.Deserialize(parsed.RootElement.GetRawText(), ToolCallJsonContext.Default.ToolCallRequestJsonElement)!;

        var response = await ToolRegistry.DispatchAsync(request);

        var responseLine = JsonSerializer.Serialize(response, ToolCallJsonContext.Default.ToolCallResponseJsonElement);
        using var roundTrip = JsonDocument.Parse(responseLine);
        var root = roundTrip.RootElement;
        Assert.Equal(42, root.GetProperty("call_id").GetInt32());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(2, root.GetProperty("result").GetProperty("protocol_version").GetInt32());
    }

    [Fact]
    public void ListTools_MatchesRegisteredHandlers()
    {
        GeneratedToolRegistry.RegisterAll();

        var descriptors = ToolRegistry.ListTools();
        Assert.NotEmpty(descriptors);
        Assert.Equal(descriptors.Select(x => x.Id), descriptors.Select(x => x.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        foreach (var descriptor in descriptors)
        {
            Assert.True(ToolRegistry.TryResolve(descriptor.Id, out _), $"Manifest lists '{descriptor.Id}' but no handler is registered.");
        }
    }

    private static void AssertGitMutation(
        IReadOnlyDictionary<string, JsonElement> tools,
        string toolId,
        string confirmationField)
    {
        var tool = tools[toolId];
        Assert.True(tool.GetProperty("requires_approval").GetBoolean());
        Assert.True(tool.GetProperty("mutates_workspace").GetBoolean());
        Assert.Equal("unsafe", tool.GetProperty("retry_safety").GetString());
        Assert.Contains(confirmationField, tool.GetProperty("confirmation_fields")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.True(tool.GetProperty("input_schema").GetProperty("properties")
            .TryGetProperty(confirmationField, out _));
    }
}
