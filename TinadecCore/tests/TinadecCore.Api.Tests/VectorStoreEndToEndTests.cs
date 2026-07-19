using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class VectorStoreEndToEndTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-vector-e2e", Guid.NewGuid().ToString("N"));
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
    public async Task VectorStore_IndexAndSearch_FlowsThroughEmbeddingProviderAndProjectDatabase()
    {
        _ = _factory!.CreateClient();
        var vectors = _factory.Services.GetRequiredService<IVectorStore>();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var tenant = scope.TenantId;
        var workspace = scope.WorkspaceId;
        var project = Guid.NewGuid();

        var indexResult = await vectors.IndexAsync(new VectorIndexRequest
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project,
            Namespace = "memory", SourceType = "summary", SourceId = "alpha", SourceRevision = "1",
            Content = "semantic memory alpha"
        });

        Assert.Equal(1, indexResult.IndexedChunks);
        Assert.Equal("fake/model", indexResult.ModelId);

        var matches = await vectors.SearchAsync(new VectorSearchRequest
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project,
            Namespace = "memory", Query = "memory", Limit = 5
        });

        var match = Assert.Single(matches);
        Assert.Equal("alpha", match.SourceId);
        Assert.Equal("summary", match.SourceType);
        Assert.Equal("fake/model", indexResult.ModelId);

        await vectors.DeleteSourceAsync(new VectorSourceReference
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project,
            Namespace = "memory", SourceType = "summary", SourceId = "alpha"
        });

        Assert.Empty(await vectors.SearchAsync(new VectorSearchRequest
        {
            TenantId = tenant, WorkspaceId = workspace, ProjectId = project,
            Namespace = "memory", Query = "memory"
        }));
    }

    [Fact]
    public async Task VectorStore_RejectsQueriesForOtherProjects()
    {
        _ = _factory!.CreateClient();
        var vectors = _factory.Services.GetRequiredService<IVectorStore>();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var project = Guid.NewGuid();
        var other = Guid.NewGuid();

        await vectors.IndexAsync(new VectorIndexRequest
        {
            TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = project,
            Namespace = "memory", SourceType = "summary", SourceId = "only", SourceRevision = "1",
            Content = "isolated"
        });

        var cross = await vectors.SearchAsync(new VectorSearchRequest
        {
            TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId, ProjectId = other,
            Namespace = "memory", Query = "isolated"
        });
        Assert.Empty(cross);
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public Task<EmbeddingResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        {
            // Deterministic, content-sensitive unit vector so identical inputs map identically.
            var vectors = request.Inputs.Select(text =>
            {
                var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
                return new float[] { hash[0] / 255f, hash[1] / 255f, hash[2] / 255f };
            }).ToList();
            return Task.FromResult(new EmbeddingResult
            {
                IsAvailable = true,
                ModelId = "fake/model",
                Dimension = 3,
                Vectors = vectors
            });
        }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public Factory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }))
            .ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IEmbeddingProvider, FakeEmbeddingProvider>());
            });
    }
}