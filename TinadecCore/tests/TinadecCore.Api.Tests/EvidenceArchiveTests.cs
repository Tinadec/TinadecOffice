using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentGraph;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The session evidence archive (todo R4): lower layers' words kept verbatim, recalled by keyword
/// always and semantically when an embedding model is configured — and never "no results" merely
/// because the model is missing.
/// </summary>
public sealed class EvidenceArchiveTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-evidence-tests", Guid.NewGuid().ToString("N"));
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, recursive: true); break; }
            catch (IOException) { await Task.Delay(300); }
        }
    }

    [Fact]
    public async Task WithoutAnEmbeddingModel_RecallFallsBackToKeywords_AndStillFindsTheEvidence()
    {
        _factory = new Factory(_root, embeddings: false);
        var (archive, tenant, workspace) = Services();
        var session = Guid.NewGuid();
        var other = Guid.NewGuid();
        var run = Guid.NewGuid();

        var result = await archive.AppendAsync(new EvidenceEntry(tenant, workspace, session, run, Guid.NewGuid(), EvidenceKinds.TaskResult,
            "写文档", "global_engineering#1", "Status: failed\nResult:\ndocs/a.md 被主机占用，没能写入。", "task:1:1"));
        await archive.AppendAsync(new EvidenceEntry(tenant, workspace, session, run, null, EvidenceKinds.Report,
            "[warning] conflict: docs 被占用", "governance_reviewer", "global_engineering#1 needs docs/a.md, which the host holds.", "report:1"));
        await archive.AppendAsync(new EvidenceEntry(tenant, workspace, session, run, null, EvidenceKinds.Summary,
            "Context summary at revision 3", "compressor", "当前目标：写说明文档。已完成事项：无。", "summary:1"));
        await archive.AppendAsync(new EvidenceEntry(tenant, workspace, other, null, null, EvidenceKinds.TaskResult,
            "别的会话", "search#1", "docs/a.md 在另一个会话里也出现了。", "task:2:1"));

        // Appending the same source again keeps one entry.
        Assert.Equal(result, await archive.AppendAsync(new EvidenceEntry(tenant, workspace, session, run, null, EvidenceKinds.TaskResult,
            "写文档", "global_engineering#1", "a second copy", "task:1:1")));

        var byPath = await archive.RecallAsync(new EvidenceRecallQuery(tenant, workspace, session, "docs/a.md"));
        Assert.Equal("keyword", byPath.Mode);
        Assert.Contains("keyword", byPath.Note!, StringComparison.Ordinal);
        Assert.Equal(2, byPath.Hits.Count);
        Assert.All(byPath.Hits, hit => Assert.Equal("keyword", hit.MatchedBy));
        Assert.DoesNotContain(byPath.Hits, hit => hit.Title == "别的会话");
        Assert.Contains(byPath.Hits, hit => hit.EvidenceId == result && hit.Snippet.Contains("docs/a.md", StringComparison.Ordinal));

        // Chinese has no spaces: bigrams still find it.
        var byWord = await archive.RecallAsync(new EvidenceRecallQuery(tenant, workspace, session, "主机占用"));
        Assert.Equal(result, byWord.Hits[0].EvidenceId);

        var reportsOnly = await archive.RecallAsync(new EvidenceRecallQuery(tenant, workspace, session, "docs", [EvidenceKinds.Report]));
        Assert.Equal(EvidenceKinds.Report, Assert.Single(reportsOnly.Hits).Kind);

        var elsewhere = await archive.RecallAsync(new EvidenceRecallQuery(tenant, workspace, session, "docs", RunId: Guid.NewGuid()));
        Assert.Empty(elsewhere.Hits);

        // The owner reads the same archive over HTTP.
        var client = _factory.CreateClient();
        var page = await client.GetFromJsonAsync<EvidenceRecallDto>($"/api/v1/sessions/{session}/evidence?q=docs/a.md&kinds=task_result,report", SnakeWire);
        Assert.Equal("keyword", page!.Mode);
        Assert.Equal(2, page.Hits.Length);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/sessions/{session}/evidence")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/sessions/{session}/evidence?q=x&kinds=gossip")).StatusCode);

        // A projectless session has no semantic index; the note says why, and the rows are not queued for one.
        var db = _factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentGraphDbContext>>();
        await using var context = await db.CreateDbContextAsync();
        Assert.All(context.Evidence, row => Assert.Equal(EvidenceVectorStatuses.NotApplicable, row.VectorStatus));
    }

    [Fact]
    public async Task WithAnEmbeddingModel_RecallFusesSemanticAndKeywordHits()
    {
        _factory = new Factory(_root, embeddings: true);
        var (archive, tenant, workspace) = Services();
        var session = Guid.NewGuid();
        var project = Guid.NewGuid();
        _factory.Sessions[session] = project;

        var migration = await archive.AppendAsync(new EvidenceEntry(tenant, workspace, session, null, null, EvidenceKinds.TaskResult,
            "迁移", "search#1", "数据库迁移已完成，三张表都补齐了列。", "task:m:1"));
        var ui = await archive.AppendAsync(new EvidenceEntry(tenant, workspace, session, null, null, EvidenceKinds.TaskResult,
            "界面", "search#2", "设置页的界面按钮已对齐。", "task:u:1"));

        var indexer = _factory.Services.GetRequiredService<EvidenceIndexService>();
        Assert.Equal(2, await indexer.RunPassAsync());
        Assert.Equal(0, await indexer.RunPassAsync());

        // No shared word with the Chinese entry: only the semantic half can find it.
        var semantic = await archive.RecallAsync(new EvidenceRecallQuery(tenant, workspace, session, "database migration"));
        Assert.Equal("hybrid", semantic.Mode);
        Assert.Null(semantic.Note);
        var hit = semantic.Hits[0];
        Assert.Equal(migration, hit.EvidenceId);
        Assert.Equal("semantic", hit.MatchedBy);

        // Both halves agree: the hit says so and ranks first.
        var both = await archive.RecallAsync(new EvidenceRecallQuery(tenant, workspace, session, "数据库"));
        Assert.Equal(migration, both.Hits[0].EvidenceId);
        Assert.Equal("both", both.Hits[0].MatchedBy);
        Assert.DoesNotContain(both.Hits, item => item.EvidenceId == ui && item.MatchedBy != "semantic");
    }

    [Fact]
    public async Task AnUnavailableModel_LeavesEntriesForLater_AndRecallStaysKeyword()
    {
        _factory = new Factory(_root, embeddings: false);
        var (archive, tenant, workspace) = Services();
        var session = Guid.NewGuid();
        _factory.Sessions[session] = Guid.NewGuid();
        await archive.AppendAsync(new EvidenceEntry(tenant, workspace, session, null, null, EvidenceKinds.Report,
            "[info] note", "reviewer", "worktree wt-1 is idle", "report:w"));

        Assert.Equal(0, await _factory.Services.GetRequiredService<EvidenceIndexService>().RunPassAsync());
        var db = _factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentGraphDbContext>>();
        await using (var context = await db.CreateDbContextAsync())
        {
            var row = Assert.Single(context.Evidence);
            Assert.Equal(EvidenceVectorStatuses.Unavailable, row.VectorStatus);
            Assert.True(row.VectorNextAttemptAtUnixMs > DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds());
        }

        var recall = await archive.RecallAsync(new EvidenceRecallQuery(tenant, workspace, session, "wt-1"));
        Assert.Equal("keyword", recall.Mode);
        Assert.Single(recall.Hits);
    }

    private static readonly JsonSerializerOptions SnakeWire = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private (IEvidenceArchive Archive, Guid Tenant, Guid Workspace) Services()
    {
        _ = _factory!.CreateClient();
        var scope = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        return (_factory.Services.GetRequiredService<IEvidenceArchive>(), scope.TenantId, scope.WorkspaceId);
    }

    /// <summary>A two-topic toy embedding: "database" and "UI" in either language point the same way.</summary>
    private sealed class TopicEmbeddings : IEmbeddingProvider
    {
        public Task<EmbeddingResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult
            {
                IsAvailable = true,
                ModelId = "fake/topics",
                Dimension = 3,
                Vectors = request.Inputs.Select(text =>
                {
                    var lower = text.ToLowerInvariant();
                    var database = lower.Contains("database", StringComparison.Ordinal) || text.Contains("数据库", StringComparison.Ordinal) ? 1f : 0f;
                    var ui = lower.Contains("ui", StringComparison.Ordinal) || text.Contains("界面", StringComparison.Ordinal) ? 1f : 0f;
                    var norm = MathF.Sqrt(database * database + ui * ui + 0.01f);
                    return new[] { database / norm, ui / norm, 0.1f / norm };
                }).ToList()
            });
    }

    private sealed class NoEmbeddings : IEmbeddingProvider
    {
        public Task<EmbeddingResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingResult { IsAvailable = false, Detail = "No embedding model is configured." });
    }

    /// <summary>Sessions the archive can resolve to a project (only those get a semantic index).</summary>
    private sealed class FixedSessions(Dictionary<Guid, Guid> projects) : ISessionLocator
    {
        public Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionReference?>(projects.TryGetValue(sessionId, out var project)
                ? new SessionReference(sessionId, project, Guid.Empty, Guid.Empty)
                : new SessionReference(sessionId, null, Guid.Empty, Guid.Empty));

        public Task<ProjectReference?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectReference?>(null);
    }

    private sealed class Factory(string root, bool embeddings) : WebApplicationFactory<Program>
    {
        public Dictionary<Guid, Guid> Sessions { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                // Passes are stepped by hand.
                ["TinadecEvidence:IndexEnabled"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            }))
            .ConfigureServices(services =>
            {
                services.Replace(embeddings
                    ? ServiceDescriptor.Singleton<IEmbeddingProvider, TopicEmbeddings>()
                    : ServiceDescriptor.Singleton<IEmbeddingProvider, NoEmbeddings>());
                services.Replace(ServiceDescriptor.Singleton<ISessionLocator>(new FixedSessions(Sessions)));
            });
    }
}
