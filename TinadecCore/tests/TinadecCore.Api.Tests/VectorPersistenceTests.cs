using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class VectorPersistenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-vector-tests", Guid.NewGuid().ToString("N"));
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
    public async Task ProjectVectorDatabase_SearchesWithinProjectAndDeletesSources()
    {
        _ = _factory!.CreateClient();
        var vectors = _factory.Services.GetRequiredService<IProjectVectorDatabase>();
        var tenant = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        var project = Guid.NewGuid();
        var otherProject = Guid.NewGuid();
        var record = new ProjectVectorRecord
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project, Namespace = "memory", SourceType = "summary", SourceId = "one",
            Content = "semantic memory", ContentHash = "a", ModelId = "test-3", Embedding = [1f, 0f, 0f]
        };
        await vectors.UpsertAsync(record);
        await vectors.UpsertAsync(new ProjectVectorRecord
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = otherProject, Namespace = "memory", SourceType = "summary", SourceId = "two",
            Content = "other project", ContentHash = "b", ModelId = "test-3", Embedding = [1f, 0f, 0f]
        });

        var matches = await vectors.SearchAsync(new ProjectVectorSearch
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project, Namespace = "memory", ModelId = "test-3", Embedding = [1f, 0f, 0f]
        });
        Assert.Single(matches);
        Assert.Equal("one", matches[0].SourceId);

        await vectors.DeleteSourceAsync(new ProjectVectorSource
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project, Namespace = "memory", SourceType = "summary", SourceId = "one"
        });
        Assert.Empty(await vectors.SearchAsync(new ProjectVectorSearch
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project, Namespace = "memory", ModelId = "test-3", Embedding = [1f, 0f, 0f]
        }));
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
