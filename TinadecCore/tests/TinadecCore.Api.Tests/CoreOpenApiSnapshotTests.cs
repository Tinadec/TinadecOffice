using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Lightweight OpenAPI contract smoke and secret-leak guard for Core.
/// </summary>
public sealed class CoreOpenApiSnapshotTests
{
    private sealed class SnapshotFactory : IsolatedApiFactory
    {
        private readonly string _root = ApiTestStorage.CreateRoot("openapi-smoke");
        private readonly bool _managed;
        public SnapshotFactory(bool managed = true)
        {
            _managed = managed;
            Directory.CreateDirectory(_root);
        }
        protected override bool UsesManagedStorage => _managed;
        protected override string? ManagedUserRoot => _managed ? _root : null;
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            var settings = new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Warning" };
            if (_managed)
            {
                settings["TinadecStorage:Enabled"] = "true";
                settings["TinadecStorage:UserRoot"] = _root;
                settings["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(_root, "workspace");
            }
            else
            {
                settings["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db");
                settings["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data");
            }
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ScopelessTestingHost_UsesPackagedRuntimeAndPublishesOpenApi()
    {
        using var factory = new SnapshotFactory(managed: false);
        using var client = factory.CreateClient();
        Assert.NotNull(factory.Services.GetService(typeof(TinadecCore.Abstractions.Ports.IScopeStorageLocations)));
        Assert.Null(factory.Services.GetService(typeof(TinadecCore.Abstractions.Ports.IScopeConfigurationDocuments)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/core.json")).StatusCode);
    }

    [Fact]
    public async Task CoreOpenApi_PublishesValidDocument_AndRejectsApprovalSecretsFromPublicSchemas()
    {
        using var factory = new SnapshotFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/openapi/core.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("openapi", out var ver) && ver.ValueKind == JsonValueKind.String, "openapi field must exist");
        Assert.True(root.TryGetProperty("info", out var info), "info must exist");
        Assert.True(info.TryGetProperty("title", out var titleEl), "info.title must exist");
        Assert.Contains("TinadecCore", titleEl.GetString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object, "paths must be an object");
        Assert.True(paths.EnumerateObject().Any(), "openapi.paths must be non-empty");

        // Security guard: public host-challenge exposes nonce query param, while approval schemas never leak secrets.
        var hostChallenge = paths.GetProperty("/api/v1/host-challenge").GetProperty("get");
        Assert.Contains(hostChallenge.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "nonce"
            && parameter.GetProperty("in").GetString() == "query");
        Assert.DoesNotContain("nonce_secret_reference", body, StringComparison.OrdinalIgnoreCase);
        foreach (var schema in root.GetProperty("components").GetProperty("schemas").EnumerateObject()
            .Where(schema => schema.Name.Contains("Approval", StringComparison.OrdinalIgnoreCase)))
        {
            if (schema.Value.TryGetProperty("properties", out var properties))
                Assert.DoesNotContain(properties.EnumerateObject(), property =>
                    property.Name.Equals("nonce", StringComparison.OrdinalIgnoreCase));
        }
    }
}
