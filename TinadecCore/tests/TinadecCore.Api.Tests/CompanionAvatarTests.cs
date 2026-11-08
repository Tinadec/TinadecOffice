using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The BYO Live2D registry through the real HTTP host and a real SQLite database. Import must copy
/// the bytes into Core's own root (the original directory is not a dependency afterwards), and
/// every read/delete must stay inside the verified tenant.
/// </summary>
public sealed class CompanionAvatarTests
{
    private sealed class AvatarFactory : IsolatedApiFactory
    {
        private readonly string _root = ApiTestStorage.CreateRoot("companion-avatars");
        public MutableIdentity Identity { get; } = new();
        public string DataRoot => Path.Combine(_root, "data");

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = DataRoot,
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddSingleton<ITenantContextAccessor>(Identity));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class MutableIdentity : ITenantContextAccessor
    {
        public TenantContext Current { get; set; } = new(
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d001"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d002"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d003"), "owner", true);
    }

    private static string CreateModelDirectory(string root, string name, string modelJson)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model3.json"), modelJson);
        return directory;
    }

    private static async Task<JsonElement> ImportAsync(HttpClient client, string path)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/companion-avatars/import", new { path });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/companion-avatars");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Strings(JsonElement element) =>
        element.EnumerateArray().Select(value => value.GetString()!).ToArray();

    [Fact]
    public async Task Import_CopiesTheDirectory_AndReportsWhatModel3JsonDeclares()
    {
        using var factory = new AvatarFactory();
        using var client = factory.CreateClient();
        var model = CreateModelDirectory(factory.DataRoot, "Nova Model", """
            {
              "Version": 3,
              "FileReferences": {
                "Moc": "nova.moc3",
                "Motions": {
                  "Idle": [
                    { "File": "motions/idle_01.motion3.json" },
                    { "Name": "greet", "File": "motions/greet.motion3.json" }
                  ]
                },
                "Expressions": [
                  { "Name": "smile", "File": "expressions/smile.exp3.json" }
                ]
              },
              "Groups": [
                { "Target": "Parameter", "Name": "EyeBlink", "Ids": ["ParamEyeLOpen", "ParamEyeROpen"] }
              ],
              "HitAreas": [
                { "Id": "Head", "Name": "Head" }
              ],
              "Parameters": {
                "ParamAngleX": { "min": -30, "max": 30, "default": 0 }
              }
            }
            """);
        Directory.CreateDirectory(Path.Combine(model, "motions"));
        File.WriteAllText(Path.Combine(model, "motions", "idle_01.motion3.json"), "{\"Version\":3}");

        var avatar = await ImportAsync(client, model);
        Assert.Equal("live2d", avatar.GetProperty("kind").GetString());
        Assert.Equal("Nova Model", avatar.GetProperty("name").GetString());
        var sourcePath = avatar.GetProperty("source_path").GetString()!;
        Assert.StartsWith("companion-avatars/tenants/", sourcePath);
        Assert.DoesNotContain(":", sourcePath);

        var capabilities = avatar.GetProperty("capabilities");
        Assert.Equal(["idle_01", "greet"], Strings(capabilities.GetProperty("motions").GetProperty("Idle")));
        Assert.Equal(["smile"], Strings(capabilities.GetProperty("expressions")));
        Assert.Equal(["Head"], Strings(capabilities.GetProperty("hit_areas")));
        Assert.Equal(["ParamEyeLOpen", "ParamEyeROpen"], Strings(capabilities.GetProperty("groups").GetProperty("EyeBlink")));
        var angle = capabilities.GetProperty("parameters").GetProperty("ParamAngleX");
        Assert.Equal(-30d, angle.GetProperty("min").GetDouble());
        Assert.Equal(30d, angle.GetProperty("max").GetDouble());
        Assert.Equal(0d, angle.GetProperty("default").GetDouble());

        // The copy is the durable artifact: the bytes live under Core's data root, not the path
        // the user picked.
        var copied = Path.Combine(factory.DataRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(copied));
        Assert.Equal("{\"Version\":3}", File.ReadAllText(Path.Combine(copied, "motions", "idle_01.motion3.json")));

        var listed = await ListAsync(client);
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal(avatar.GetProperty("id").GetGuid(), listed[0].GetProperty("id").GetGuid());

        using var deleted = await client.DeleteAsync($"/api/v1/companion-avatars/{avatar.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(Directory.Exists(copied));
        Assert.Equal(0, (await ListAsync(client)).GetArrayLength());
    }

    [Fact]
    public async Task Import_RejectsADirectoryWithoutModel3Json_WithTheSpecificReason()
    {
        using var factory = new AvatarFactory();
        using var client = factory.CreateClient();
        var directory = Path.Combine(factory.DataRoot, "not-a-model");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "no model here");

        using var response = await client.PostAsJsonAsync("/api/v1/companion-avatars/import", new { path = directory });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("model3_missing", body.GetProperty("code").GetString());
        Assert.Contains("model3.json", body.GetProperty("message").GetString());
        Assert.Equal(0, (await ListAsync(client)).GetArrayLength());
    }

    [Fact]
    public async Task Import_LeavesAbsentCapabilitySectionsEmpty_InsteadOfInventingThem()
    {
        using var factory = new AvatarFactory();
        using var client = factory.CreateClient();
        var model = CreateModelDirectory(factory.DataRoot, "Minimal", """{ "Version": 3 }""");

        var avatar = await ImportAsync(client, model);
        var capabilities = avatar.GetProperty("capabilities");
        Assert.Equal(0, capabilities.GetProperty("motions").EnumerateObject().Count());
        Assert.Equal(0, capabilities.GetProperty("expressions").GetArrayLength());
        Assert.Equal(0, capabilities.GetProperty("parameters").EnumerateObject().Count());
        Assert.Equal(0, capabilities.GetProperty("hit_areas").GetArrayLength());
        Assert.Equal(0, capabilities.GetProperty("groups").EnumerateObject().Count());
    }

    [Fact]
    public async Task Import_NamesTheMalformedCapabilityField_InsteadOfReturningAGenericInvalid()
    {
        using var factory = new AvatarFactory();
        using var client = factory.CreateClient();
        var model = CreateModelDirectory(factory.DataRoot, "Broken", """
            { "Version": 3, "FileReferences": { "Motions": [] } }
            """);

        using var response = await client.PostAsJsonAsync("/api/v1/companion-avatars/import", new { path = model });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("model3_invalid_capability", body.GetProperty("code").GetString());
        Assert.Contains("FileReferences.Motions", body.GetProperty("message").GetString());
        Assert.Equal(0, (await ListAsync(client)).GetArrayLength());
    }

    /// <summary>
    /// The copy must not land inside the directory it is reading. Picking Core's own avatar
    /// directory as the source is the realistic way to hit this, and without the guard the copy
    /// recurses into its own output until the disk fills.
    /// </summary>
    [Fact]
    public async Task Import_RefusesASourceThatContainsTheDestination()
    {
        using var factory = new AvatarFactory();
        using var client = factory.CreateClient();
        // The destination for any import lives under <data root>/companion-avatars.
        var container = Path.Combine(factory.DataRoot, "companion-avatars");
        Directory.CreateDirectory(container);
        File.WriteAllText(Path.Combine(container, "model3.json"), """{ "Version": 3 }""");

        using var response = await client.PostAsJsonAsync("/api/v1/companion-avatars/import", new { path = container });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("avatar_path_overlaps_storage", body.GetProperty("code").GetString());
        // Nothing was recorded and no copy was attempted.
        Assert.Equal(0, (await ListAsync(client)).GetArrayLength());
    }

    [Fact]
    public async Task Delete_StillRemovesTheRow_WhenTheCopiedDirectoryIsAlreadyGone()
    {
        using var factory = new AvatarFactory();
        using var client = factory.CreateClient();
        var model = CreateModelDirectory(factory.DataRoot, "Gone", """{ "Version": 3 }""");
        var avatar = await ImportAsync(client, model);
        var copied = Path.Combine(factory.DataRoot,
            avatar.GetProperty("source_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        Directory.Delete(copied, recursive: true);

        using var response = await client.DeleteAsync($"/api/v1/companion-avatars/{avatar.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, (await ListAsync(client)).GetArrayLength());
    }

    [Fact]
    public async Task EveryReadAndDelete_IsScopedToTheVerifiedTenant()
    {
        using var factory = new AvatarFactory();
        using var client = factory.CreateClient();
        var model = CreateModelDirectory(factory.DataRoot, "Tenant A", """{ "Version": 3 }""");
        var avatar = await ImportAsync(client, model);
        var avatarId = avatar.GetProperty("id").GetGuid();

        factory.Identity.Current = new TenantContext(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), "owner", true);

        Assert.Equal(0, (await ListAsync(client)).GetArrayLength());
        using (var hidden = await client.DeleteAsync($"/api/v1/companion-avatars/{avatarId}"))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        // The other tenant's row and copied bytes are untouched.
        factory.Identity.Current = new TenantContext(
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d001"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d002"),
            Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d003"), "owner", true);
        Assert.Equal(1, (await ListAsync(client)).GetArrayLength());
        Assert.True(Directory.Exists(Path.Combine(factory.DataRoot,
            avatar.GetProperty("source_path").GetString()!.Replace('/', Path.DirectorySeparatorChar))));
    }
}
