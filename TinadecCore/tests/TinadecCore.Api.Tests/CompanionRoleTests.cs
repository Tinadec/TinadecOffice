using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The companion role (autonomous | buddy) and the sidebar buddy designation. A buddy is the
/// work-buddy shown below "new chat"; it is designated independently of the satellite window's
/// main companion. These go through the real HTTP host + SQLite so a missing projection or
/// predicate fails the same way it would in the product.
/// </summary>
public sealed class CompanionRoleTests
{
    private sealed class RoleFactory : IsolatedApiFactory
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-companion-role-tests", Guid.NewGuid().ToString("N"));

        public RoleFactory() => Directory.CreateDirectory(_root);

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

    private static async Task<JsonElement> CreateAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/companions", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task RoleDefaultsToAutonomousAndValidatesOnCreateAndPatch()
    {
        using var factory = new RoleFactory();
        using var client = factory.CreateClient();

        var defaultRole = await CreateAsync(client, new { name = "默认伙伴" });
        var defaultId = defaultRole.GetProperty("id").GetGuid();
        Assert.Equal("autonomous", defaultRole.GetProperty("role").GetString());

        var buddy = await CreateAsync(client, new { name = "搭子", role = "buddy" });
        Assert.Equal("buddy", buddy.GetProperty("role").GetString());

        // The list projection carries the same role vocabulary as the detail.
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/companions");
        Assert.Contains(rows!, row => row.GetProperty("id").GetGuid() == defaultId && row.GetProperty("role").GetString() == "autonomous");

        using var invalidCreate = await client.PostAsJsonAsync("/api/v1/companions", new { name = "坏角色", role = "wizard" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(invalidCreate));

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{defaultId}")
        {
            Content = JsonContent.Create(new { role = "buddy" })
        };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var patched = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("buddy", (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());

        using var invalidPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{defaultId}")
        {
            Content = JsonContent.Create(new { role = "wizard" })
        };
        invalidPatch.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        using var rejected = await client.SendAsync(invalidPatch);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(rejected));
    }

    [Fact]
    public async Task BuddyDesignationPersistsIndependentlyOfMainAndRejectsArchivedTarget()
    {
        using var factory = new RoleFactory();
        using var client = factory.CreateClient();
        var buddyId = (await CreateAsync(client, new { name = "搭子", role = "buddy" })).GetProperty("id").GetGuid();
        var mainId = (await CreateAsync(client, new { name = "主伙伴" })).GetProperty("id").GetGuid();
        var archivedId = (await CreateAsync(client, new { name = "归档伙伴" })).GetProperty("id").GetGuid();

        // No designation yet: both ids are present-but-null, revision 0.
        var initial = await client.GetFromJsonAsync<JsonElement>("/api/v1/companion-settings");
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("buddy_companion_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("main_companion_id").ValueKind);

        using var designate = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { buddy_companion_id = buddyId, main_companion_id = mainId })
        };
        designate.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var saved = await client.SendAsync(designate);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedBody = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(buddyId, savedBody.GetProperty("buddy_companion_id").GetGuid());
        Assert.Equal(mainId, savedBody.GetProperty("main_companion_id").GetGuid());

        // Changing only the main companion leaves the buddy untouched (absent != clear).
        using var mainOnly = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { main_companion_id = (Guid?)null })
        };
        mainOnly.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var mainCleared = await client.SendAsync(mainOnly);
        Assert.Equal(HttpStatusCode.OK, mainCleared.StatusCode);
        var afterMain = await mainCleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, afterMain.GetProperty("main_companion_id").ValueKind);
        Assert.Equal(buddyId, afterMain.GetProperty("buddy_companion_id").GetGuid());

        // An archived companion cannot be the buddy.
        using var archive = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/companions/{archivedId}/archive");
        archive.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var archived = await client.SendAsync(archive);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        using var badBuddy = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { buddy_companion_id = archivedId })
        };
        badBuddy.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        using var rejected = await client.SendAsync(badBuddy);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("companion_not_found", await ErrorCodeAsync(rejected));

        // Explicit null clears the designation.
        using var clear = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { buddy_companion_id = (Guid?)null })
        };
        clear.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        using var cleared = await client.SendAsync(clear);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("buddy_companion_id").ValueKind);
    }
}