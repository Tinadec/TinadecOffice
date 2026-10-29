using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Companion isolation is a tenant + companion-id predicate, not a name lookup. These tests go
/// through the real HTTP host and a real SQLite database so a missing predicate fails them the
/// same way it would fail the product.
/// </summary>
public sealed class CompanionIsolationTests
{
    private sealed class CompanionFactory : IsolatedApiFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-companion-tests", Guid.NewGuid().ToString("N"));

        public CompanionFactory() => Directory.CreateDirectory(_root);

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
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

    private static async Task<JsonElement> CreateCompanionAsync(HttpClient client, string name, string? persona = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/companions", new { name, persona_document = persona });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CreateMemoryAsync(HttpClient client, Guid companionId, string content)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/companions/{companionId}/memories", new { content, source_kind = "user" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("code").GetString()!;
    }

    private static async Task<JsonElement[]> ListCompanionsAsync(HttpClient client, bool includeArchived = false)
    {
        var url = includeArchived ? "/api/v1/companions?include_archived=true" : "/api/v1/companions";
        var rows = await client.GetFromJsonAsync<JsonElement[]>(url);
        Assert.NotNull(rows);
        return rows!;
    }

    [Fact]
    public async Task SameNameCompanionsDoNotShareMemoriesAndForeignMemoryIdsAre404()
    {
        using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        var first = await CreateCompanionAsync(client, "同名伙伴", "A 的人格");
        var second = await CreateCompanionAsync(client, "同名伙伴", "B 的人格");
        var firstId = first.GetProperty("id").GetGuid();
        var secondId = second.GetProperty("id").GetGuid();
        Assert.NotEqual(firstId, secondId);

        await CreateMemoryAsync(client, firstId, "关键词 shared-token A");
        var secondMemory = await CreateMemoryAsync(client, secondId, "关键词 shared-token B");
        var secondMemoryId = secondMemory.GetProperty("id").GetGuid();

        var rows = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{firstId}/memories?q=shared-token");
        Assert.NotNull(rows);
        var only = Assert.Single(rows!);
        Assert.Equal(firstId, only.GetProperty("companion_id").GetGuid());
        Assert.Contains("A", only.GetProperty("content").GetString());

        // B's memory id routed through A's path is the same 404 as an unknown row.
        using var foreign = await client.PatchAsJsonAsync(
            $"/api/v1/companions/{firstId}/memories/{secondMemoryId}",
            new { content = "hijack" });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("companion_not_found", await ErrorCodeAsync(foreign));

        var secondRows = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{secondId}/memories?q=shared-token");
        Assert.NotNull(secondRows);
        Assert.Equal(secondMemoryId, Assert.Single(secondRows!).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task SameNameSkillAndConnectionBindingsStayOnTheirCompanion()
    {
        using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        var firstId = (await CreateCompanionAsync(client, "同名伙伴")).GetProperty("id").GetGuid();
        var secondId = (await CreateCompanionAsync(client, "同名伙伴")).GetProperty("id").GetGuid();

        using var firstSkill = await client.PostAsJsonAsync($"/api/v1/companions/{firstId}/skills", new { name = "notes", source_kind = "manual", instructions = "A" });
        using var secondSkill = await client.PostAsJsonAsync($"/api/v1/companions/{secondId}/skills", new { name = "notes", source_kind = "manual", instructions = "B" });
        Assert.Equal(HttpStatusCode.OK, firstSkill.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondSkill.StatusCode);
        var firstSkillId = (await firstSkill.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var secondSkillId = (await secondSkill.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotEqual(firstSkillId, secondSkillId);

        using var firstConnection = await client.PostAsJsonAsync($"/api/v1/companions/{firstId}/connections", new { kind = "mcp", name = "mcp-main" });
        using var secondConnection = await client.PostAsJsonAsync($"/api/v1/companions/{secondId}/connections", new { kind = "mcp", name = "mcp-main" });
        Assert.Equal(HttpStatusCode.OK, firstConnection.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondConnection.StatusCode);
        var firstConnectionBody = await firstConnection.Content.ReadFromJsonAsync<JsonElement>();
        var firstConnectionId = firstConnectionBody.GetProperty("id").GetGuid();
        Assert.Equal("unsupported", firstConnectionBody.GetProperty("runtime_status").GetString());
        Assert.Equal(JsonValueKind.Null, firstConnectionBody.GetProperty("last_checked_at").ValueKind);

        using var revoke = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{firstId}/connections/{firstConnectionId}")
        {
            Content = JsonContent.Create(new { authorization = "revoked" })
        };
        revoke.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var revoked = await client.SendAsync(revoke);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("revoked", (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorization").GetString());

        var firstConnections = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{firstId}/connections");
        var secondConnections = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{secondId}/connections");
        Assert.Equal("revoked", Assert.Single(firstConnections!).GetProperty("authorization").GetString());
        Assert.Equal("configured", Assert.Single(secondConnections!).GetProperty("authorization").GetString());

        var firstSkills = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{firstId}/skills");
        var firstSkillRow = Assert.Single(firstSkills!);
        Assert.Equal(firstSkillId, firstSkillRow.GetProperty("id").GetGuid());
        Assert.Equal("A", firstSkillRow.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task StaleIfMatchReturns412AndLeavesTheRowUnchanged()
    {
        using var factory = new CompanionFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(client, "Revision 伙伴")).GetProperty("id").GetGuid();

        using var firstWrite = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{companionId}")
        {
            Content = JsonContent.Create(new { name = "第一版" })
        };
        firstWrite.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var saved = await client.SendAsync(firstWrite);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64());

        using var staleWrite = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{companionId}")
        {
            Content = JsonContent.Create(new { name = "过期写入" })
        };
        staleWrite.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var conflict = await client.SendAsync(staleWrite);
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.Equal("companion_revision_conflict", await ErrorCodeAsync(conflict));

        var reread = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companions/{companionId}");
        Assert.Equal("第一版", reread.GetProperty("name").GetString());
        Assert.Equal(2, reread.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task SoftDeletedMemoryDisappearsFromSearchAndDetail()
    {
        using var factory = new CompanionFactory();
        using var client = factory.CreateClient();
        var companionId = (await CreateCompanionAsync(client, "记忆伙伴")).GetProperty("id").GetGuid();
        var memory = await CreateMemoryAsync(client, companionId, "会被删除的记忆");
        var memoryId = memory.GetProperty("id").GetGuid();

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/companions/{companionId}/memories/{memoryId}");
        delete.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var rows = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/companions/{companionId}/memories?q=会被删除");
        Assert.Empty(rows!);

        using var patchDeleted = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{companionId}/memories/{memoryId}")
        {
            Content = JsonContent.Create(new { content = "复活" })
        };
        using var patchResponse = await client.SendAsync(patchDeleted);
        Assert.Equal(HttpStatusCode.NotFound, patchResponse.StatusCode);
        Assert.Equal("companion_not_found", await ErrorCodeAsync(patchResponse));

        using var deleteAgain = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/companions/{companionId}/memories/{memoryId}");
        using var deleteAgainResponse = await client.SendAsync(deleteAgain);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgainResponse.StatusCode);
    }

    [Fact]
    public async Task ArchivedCompanionLeavesTheDefaultListAndReturnsOnIncludeArchived()
    {
        using var factory = new CompanionFactory();
        using var client = factory.CreateClient();
        var archivedId = (await CreateCompanionAsync(client, "归档伙伴")).GetProperty("id").GetGuid();
        var activeId = (await CreateCompanionAsync(client, "留存伙伴")).GetProperty("id").GetGuid();

        using var archive = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/companions/{archivedId}/archive");
        archive.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var archived = await client.SendAsync(archive);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal("archived", (await archived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var defaults = await ListCompanionsAsync(client);
        Assert.DoesNotContain(defaults, row => row.GetProperty("id").GetGuid() == archivedId);
        Assert.Contains(defaults, row => row.GetProperty("id").GetGuid() == activeId);

        var all = await ListCompanionsAsync(client, includeArchived: true);
        Assert.Contains(all, row => row.GetProperty("id").GetGuid() == archivedId);
        Assert.Contains(all, row => row.GetProperty("id").GetGuid() == activeId);
    }

    [Fact]
    public async Task SettingsRejectAnArchivedCompanionAsMainCompanion()
    {
        using var factory = new CompanionFactory();
        using var client = factory.CreateClient();
        var archivedId = (await CreateCompanionAsync(client, "归档主伙伴")).GetProperty("id").GetGuid();

        using var archive = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/companions/{archivedId}/archive");
        using var archived = await client.SendAsync(archive);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { main_companion_id = archivedId })
        };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var rejected = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("companion_not_found", await ErrorCodeAsync(rejected));

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/v1/companion-settings");
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("main_companion_id").ValueKind);
        Assert.False(settings.GetProperty("autonomy_paused").GetBoolean());
        Assert.Equal(0, settings.GetProperty("revision").GetInt64());
    }
}
