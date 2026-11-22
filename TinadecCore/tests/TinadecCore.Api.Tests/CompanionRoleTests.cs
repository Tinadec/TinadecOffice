using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Companion;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Companion role (<c>autonomous | session_partner</c>), complete removal of legacy <c>buddy</c>
/// and <c>buddy_companion_id</c>, and configurable <c>max_bot_hop_depth</c> (<c>0..10</c>, default <c>3</c>).
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
    public async Task RoleSupportsAutonomousAndSessionPartner_RejectsBuddy_AndNormalizesLegacyRows()
    {
        using var factory = new RoleFactory();
        using var client = factory.CreateClient();

        var defaultRole = await CreateAsync(client, new { name = "默认伙伴" });
        var defaultId = defaultRole.GetProperty("id").GetGuid();
        Assert.Equal("autonomous", defaultRole.GetProperty("role").GetString());

        var partner = await CreateAsync(client, new { name = "会话拍档", role = "session_partner" });
        var partnerId = partner.GetProperty("id").GetGuid();
        Assert.Equal("session_partner", partner.GetProperty("role").GetString());

        using var rejectedBuddy = await client.PostAsJsonAsync("/api/v1/companions", new { name = "旧搭子", role = "buddy" });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedBuddy.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(rejectedBuddy));

        using var patchToPartner = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/companions/{defaultId}")
        {
            Content = JsonContent.Create(new { role = "session_partner" })
        };
        patchToPartner.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var patched = await client.SendAsync(patchToPartner);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("session_partner", (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());

        // Legacy database rows carrying "buddy" normalize cleanly to "autonomous" on read.
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<CompanionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == partnerId);
            row.Role = "buddy";
            await db.SaveChangesAsync();
        }

        var normalized = await client.GetFromJsonAsync<JsonElement>($"/api/v1/companions/{partnerId}");
        Assert.Equal("autonomous", normalized.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Settings_ExposesMaxBotHopDepth_PurgesBuddyCompanionId_AndValidatesRange()
    {
        using var factory = new RoleFactory();
        using var client = factory.CreateClient();
        var mainId = (await CreateAsync(client, new { name = "主伙伴" })).GetProperty("id").GetGuid();

        var initial = await client.GetFromJsonAsync<JsonElement>("/api/v1/companion-settings");
        Assert.False(initial.TryGetProperty("buddy_companion_id", out _));
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("main_companion_id").ValueKind);
        Assert.Equal(3, initial.GetProperty("max_bot_hop_depth").GetInt32());

        using var rejectedBuddySetting = await client.PatchAsJsonAsync("/api/v1/companion-settings", new { buddy_companion_id = mainId });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedBuddySetting.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(rejectedBuddySetting));

        using var outOfRangeHop = await client.PatchAsJsonAsync("/api/v1/companion-settings", new { max_bot_hop_depth = 11 });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRangeHop.StatusCode);
        Assert.Equal("invalid_request", await ErrorCodeAsync(outOfRangeHop));

        using var saveZero = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/companion-settings")
        {
            Content = JsonContent.Create(new { main_companion_id = mainId, max_bot_hop_depth = 0 })
        };
        saveZero.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var saved = await client.SendAsync(saveZero);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedBody = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(mainId, savedBody.GetProperty("main_companion_id").GetGuid());
        Assert.Equal(0, savedBody.GetProperty("max_bot_hop_depth").GetInt32());
        Assert.False(savedBody.TryGetProperty("buddy_companion_id", out _));
    }
}
