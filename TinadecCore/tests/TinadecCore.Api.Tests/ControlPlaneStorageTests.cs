using System.Text;
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
        Assert.Equal(1, await db.Providers.CountAsync());
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
