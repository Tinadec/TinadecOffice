using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Standing approval rules over the durable rows (todo E7): every verification is scoped to the
/// run's own session, never the call's — a rule from another session or workspace is never honored.
/// </summary>
public sealed class ApprovalRuleServiceTests : IAsyncLifetime
{
    private readonly ApiEndpointFactory _factory = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => _factory.Dispose();

    private IApprovalRules Rules => _factory.Services.GetRequiredService<IApprovalRules>();

    [Theory]
    [InlineData("git_push")]
    [InlineData("mcp_invoke")]
    [InlineData("write_file")]
    [InlineData("delete_file")]
    [InlineData("*")]
    public async Task DelegateRule_RejectsToolsWithoutASessionOptInContract(string toolId)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/approval-rules", new
        {
            kind = "delegate_tool", tool_id = toolId, session_id = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await Rules.ListAsync(null));
    }

    [Fact]
    public async Task DelegateRule_RequiresASession_AndValidatesPersistedRulesOnRead()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/approval-rules", new { kind = "delegate_tool", tool_id = "shell" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await using (var lifecycle = await _factory.Services.GetRequiredService<IDbContextFactory<TinadecCore.Lifecycle.LifecycleDbContext>>().CreateDbContextAsync())
        {
            lifecycle.Runs.Add(new TinadecCore.Lifecycle.RunRecord { Id = runId, TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId, SessionId = sessionId, Status = "completed" });
            await lifecycle.SaveChangesAsync();
        }
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<TinadecCore.AgentGraph.AgentGraphDbContext>>().CreateDbContextAsync())
        {
            foreach (var tool in new[] { "git_push", "mcp_invoke", "shell" })
                db.ApprovalRules.Add(new TinadecCore.AgentGraph.ApprovalRuleRecord { Id = Guid.NewGuid(),
                    TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, Kind = "delegate_tool", ToolId = tool,
                    SessionId = tool == "shell" ? null : sessionId });
            await db.SaveChangesAsync();
        }
        Assert.False(await Rules.IsDelegatedToolAsync(runId, "git_push"));
        Assert.False(await Rules.IsDelegatedToolAsync(runId, "mcp_invoke"));
        Assert.False(await Rules.IsDelegatedToolAsync(runId, "shell"));
        var allowed = await Rules.CreateAsync(new("delegate_tool", "shell", null, sessionId));
        Assert.True(await Rules.IsDelegatedToolAsync(runId, "shell"));
        Assert.False(await Rules.IsDelegatedToolAsync(Guid.NewGuid(), "shell"));
        Assert.True(await Rules.RevokeAsync(allowed.Id));
        Assert.False(await Rules.IsDelegatedToolAsync(runId, "shell"));
    }

    [Fact]
    public async Task PrefixRule_ScopedToItsSession_OnlyItsSessionHonorsIt()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        await Rules.CreateAsync(new ApprovalRuleCreate(ApprovalRuleKinds.CommandPrefix, "shell", "npm test", sessionA));

        // The session the rule belongs to matches; any other session — and a session that
        // cannot be resolved at all — sees only workspace-wide rules, never sessionA's.
        Assert.NotNull(await Rules.MatchCommandAsync(sessionA, "shell", "npm test -u"));
        Assert.Null(await Rules.MatchCommandAsync(sessionB, "shell", "npm test -u"));

        var rule = (await Rules.ListAsync(sessionA)).Single();
        Assert.True(await Rules.RevokeAsync(rule.Id));
        Assert.Null(await Rules.MatchCommandAsync(sessionA, "shell", "npm test -u"));
        // A revoked or foreign rule named by a caller never verifies, even against a run id
        // that resolves nowhere.
        Assert.False(await Rules.VerifyCommandRuleAsync(rule.Id, Guid.NewGuid(), "shell"));
    }

    [Fact]
    public async Task MatchCommandAsync_ConsidersWorkspaceWideRulesAsWell_AsWorkspaceWide()
    {
        await Rules.CreateAsync(new ApprovalRuleCreate(ApprovalRuleKinds.CommandPrefix, "shell", "npm test", null));
        var sessionId = Guid.NewGuid();

        var covered = await Rules.MatchCommandAsync(sessionId, "shell", "npm test -u");
        Assert.NotNull(covered);
        Assert.Equal("npm test", covered!.Pattern);
        // Neither matching nor verifying counts a use: the one count happens when a call
        // actually consumes the rule (its approval was minted from it).
        Assert.Equal(0, (await Rules.ListAsync(sessionId)).Single().UseCount);
        Assert.Equal(1, (await Rules.RecordUseAsync(covered.Id)).UseCount);

        Assert.Null(await Rules.MatchCommandAsync(sessionId, "shell", "npm test && rm -rf ."));
        Assert.Null(await Rules.MatchCommandAsync(sessionId, "shell", "npm run test"));
        Assert.NotNull(await Rules.MatchCommandAsync(Guid.NewGuid(), "shell", "npm test"));
    }

    [Fact]
    public async Task TheApiRegistersListsAndRevokes_AndRejectsBadCategoriesAndBadPrefixes()
    {
        var client = _factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/approval-rules", new
        {
            kind = "command_prefix", tool_id = "shell", pattern = "npm test"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("shell", body.GetProperty("tool_id").GetString());
        Assert.Equal("npm test", body.GetProperty("pattern").GetString());

        var duplicate = await client.PostAsJsonAsync("/api/v1/approval-rules", new { kind = "command_prefix", tool_id = "shell", pattern = "npm test" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var wrongTool = await client.PostAsJsonAsync("/api/v1/approval-rules", new { kind = "command_prefix", tool_id = "write_file", pattern = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongTool.StatusCode);
        var chained = await client.PostAsJsonAsync("/api/v1/approval-rules", new { kind = "command_prefix", tool_id = "shell", pattern = "npm test && rm -rf ." });
        Assert.Equal(HttpStatusCode.BadRequest, chained.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/approval-rules");
        Assert.Single(list!);
        var revoked = await client.DeleteAsync($"/api/v1/approval-rules/{body.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/v1/approval-rules") ?? []);
    }
}
