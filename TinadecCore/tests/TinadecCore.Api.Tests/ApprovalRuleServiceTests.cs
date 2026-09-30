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
