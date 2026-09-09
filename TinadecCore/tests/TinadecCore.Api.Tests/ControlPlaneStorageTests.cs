using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Models;
using TinadecCore.Persistence;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

public sealed class ControlPlaneStorageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-control-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ContentStoreAndModelControlTables_AreDurableAndContentAddressed()
    {
        _ = _factory!.CreateClient();
        var store = _factory.Services.GetRequiredService<IContentStore>();
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("provider configuration"));
        var content = await store.PutAsync(new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "model-config", "application/json", input));

        Assert.StartsWith("content/tenants/", content.Value, StringComparison.Ordinal);
        Assert.True(await store.ExistsAsync(content));
        await using var read = await store.OpenReadAsync(content);
        using var reader = new StreamReader(read);
        Assert.Equal("provider configuration", await reader.ReadToEndAsync());

        var factory = _factory.Services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var version = new ModelProviderVersionRecord { Id = Guid.NewGuid(), Version = 1, ContentReference = content.Value, ContentHash = content.Sha256, ContentLength = content.Length, CreatedByPrincipalId = tenant.PrincipalId, CreatedAt = now };
        var provider = new ModelProviderRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Scope = "workspace", Driver = "test", DisplayName = "Test", ConnectionKind = "api-key", Revision = 1, CurrentVersionId = version.Id, CreatedByPrincipalId = tenant.PrincipalId, UpdatedByPrincipalId = tenant.PrincipalId, CreatedAt = now, UpdatedAt = now };
        version.ProviderId = provider.Id;
        db.Providers.Add(provider);
        db.ProviderVersions.Add(version);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Providers.CountAsync(p => p.Driver == "test"));
        Assert.Equal(1, await db.ProviderVersions.CountAsync(v => v.ProviderId == provider.Id));
    }

    [Fact]
    public async Task ProviderEdit_OnSeededRow_SucceedsAndIncrementsVersion()
    {
        // Regression: DevSeed creates a provider with Revision=0 while the version
        // row is already version 1. Deriving the next version from Revision
        // produced a duplicate (provider_id, version) and every edit of a seeded
        // provider failed with SQLite UNIQUE constraint. The next version must be
        // max(existing version)+1, like SaveRoute already did.
        var client = _factory!.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/model-providers");
        var seeded = listed.EnumerateArray().First();
        var providerId = seeded.GetProperty("id").GetGuid();
        var revision = seeded.GetProperty("revision").GetInt64();
        Assert.Equal(0, revision); // seeded state: revision 0, version 1

        var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/model-providers/{providerId}")
        {
            Content = JsonContent.Create(new
            {
                driver = "openai",
                display_name = "edited-provider",
                connection_kind = "api-key",
                base_url = "https://api.example.com/v1",
                model = "edited-model"
            })
        };
        edit.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");

        var response = await client.SendAsync(edit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("edited-provider", updated.GetProperty("display_name").GetString());
        Assert.Equal(1, updated.GetProperty("revision").GetInt64());

        // A second edit must also succeed (version 3, no collision).
        var edit2 = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/model-providers/{providerId}")
        {
            Content = JsonContent.Create(new
            {
                driver = "openai",
                display_name = "edited-again",
                connection_kind = "api-key",
                base_url = "https://api.example.com/v1",
                model = "edited-model"
            })
        };
        edit2.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var response2 = await client.SendAsync(edit2);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
