using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class HybridMemoryAndRoutingTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("hybrid-memory-routing");
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root, enableFakeEmbeddings: true);
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
    public async Task AutoModelStrategy_ScoresProfilesHealthAndCost_AndPreservesExplicitFixedPriority()
    {
        using var client = _factory!.CreateClient();

        var p1Resp = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Primary Pool",
            connection_kind = "api-key",
            base_url = "https://api.example.com/v1",
            model = "fast-cheap",
            models = new[] { "fast-cheap", "deep-coder", "expensive-vision" },
            api_key = "sk-auto-routing-1",
            enabled = true,
            model_profiles = new Dictionary<string, object>
            {
                ["fast-cheap"] = new
                {
                    specialties = new[] { "coding" },
                    tier = "fast",
                    cost_tier = "low",
                    input_price_per_1m = 0.15m,
                    output_price_per_1m = 0.60m,
                    currency = "USD"
                },
                ["deep-coder"] = new
                {
                    specialties = new[] { "coding", "architecture" },
                    tier = "deep",
                    cost_tier = "medium",
                    input_price_per_1m = 3.0m,
                    output_price_per_1m = 15.0m,
                    currency = "USD"
                },
                ["expensive-vision"] = new
                {
                    specialties = new[] { "coding", "architecture", "vision" },
                    tier = "deep",
                    cost_tier = "high",
                    input_price_per_1m = 15.0m,
                    output_price_per_1m = 75.0m,
                    currency = "USD"
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, p1Resp.StatusCode);
        var providerId = (await p1Resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var parsed = ModelStrategyJson.Parse("""
            {
              "kind": "auto",
              "required_specialties": ["coding", "architecture"],
              "preferred_tier": "deep",
              "max_cost_tier": "medium"
            }
            """);
        Assert.Equal(ModelStrategyKinds.Auto, parsed.Kind);
        Assert.Equal("deep", parsed.PreferredTier);
        Assert.Equal("medium", parsed.MaxCostTier);

        var resolver = _factory.Services.GetRequiredService<IAgentModelResolver>();
        var preview = await resolver.PreviewAsync(new ModelResolutionPreviewRequestDto
        {
            Strategy = parsed
        });

        Assert.NotNull(preview.ExpectedSelection);
        Assert.Equal("deep-coder", preview.ExpectedSelection!.Model);
        Assert.DoesNotContain(preview.Candidates, c => c.Model == "expensive-vision");
        Assert.Equal(
            ["deep-coder", "fast-cheap"],
            preview.Candidates.Where(c => c.ProviderInstanceId == providerId).Select(c => c.Model).ToArray());

        // Cool down deep-coder -> fallback chain promotes fast-cheap
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var modelDbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>();
        await using (var db = await modelDbFactory.CreateDbContextAsync())
        {
            db.Health.Add(new ModelHealthRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                ProviderInstanceId = providerId,
                Model = "deep-coder",
                ConsecutiveFailures = 4,
                CoolingUntil = DateTimeOffset.UtcNow.AddMinutes(10),
                LastCategory = "rate_limit",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var cooledPreview = await resolver.PreviewAsync(new ModelResolutionPreviewRequestDto
        {
            Strategy = parsed
        });
        Assert.NotNull(cooledPreview.ExpectedSelection);
        Assert.Equal("fast-cheap", cooledPreview.ExpectedSelection!.Model);

        // Explicit fixed override still takes priority over auto strategy
        var fixedOverridePreview = await resolver.PreviewAsync(new ModelResolutionPreviewRequestDto
        {
            Strategy = parsed,
            MeetingModelOverride = new MeetingModelOverrideDto
            {
                ProviderInstanceId = providerId,
                Model = "expensive-vision"
            }
        });
        Assert.Equal("session_override", fixedOverridePreview.StrategySource);
        Assert.Equal("expensive-vision", fixedOverridePreview.ExpectedSelection!.Model);
    }

    [Fact]
    public async Task LongTermMemory_HybridVectorAndKeywordRetrieval_FiltersExpiryApplicabilityAndRevocation()
    {
        _ = _factory!.CreateClient();
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var memoryDbFactory = _factory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();

        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedProjectAndSessionAsync(memoryDbFactory, tenant, projectId, sessionId, viewMode: "flat");

        var ltm = _factory.Services.GetRequiredService<ILongTermMemoryService>();
        var memoryStore = _factory.Services.GetRequiredService<IMemoryStore>();

        // 1. Semantically related memory with NO keyword overlap with query "database concurrency"
        var validCand = await ltm.CreateCandidateAsync(new MemoryCandidateProposal(
            SourceRunId: Guid.NewGuid(),
            GeneratedByInstanceId: Guid.NewGuid(),
            Scope: "project",
            Kind: "fact",
            Content: "SQLite WAL mode eliminates lock contention during parallel writes.",
            Confidence: 0.9,
            ProjectId: projectId,
            Applicability: $"project_id:{projectId}",
            ExpiryCondition: "never"));
        var promotedValid = await ltm.DecideCandidateAsync(validCand.Id, "promoted", "verified");

        // 2. Expired memory
        var expiredCand = await ltm.CreateCandidateAsync(new MemoryCandidateProposal(
            SourceRunId: Guid.NewGuid(),
            GeneratedByInstanceId: Guid.NewGuid(),
            Scope: "project",
            Kind: "fact",
            Content: "database concurrency legacy single writer lock tip",
            Confidence: 0.9,
            ProjectId: projectId,
            ExpiryCondition: "2020-01-01T00:00:00Z"));
        await ltm.DecideCandidateAsync(expiredCand.Id, "promoted", "verified");

        // 3. Applicability mismatch (requires space view while session is flat)
        var mismatchCand = await ltm.CreateCandidateAsync(new MemoryCandidateProposal(
            SourceRunId: Guid.NewGuid(),
            GeneratedByInstanceId: Guid.NewGuid(),
            Scope: "project",
            Kind: "fact",
            Content: "database concurrency spatial layout rule",
            Confidence: 0.9,
            ProjectId: projectId,
            Applicability: "view_mode:space"));
        await ltm.DecideCandidateAsync(mismatchCand.Id, "promoted", "verified");

        // 4. Promoted then revoked with reason
        var revokedCand = await ltm.CreateCandidateAsync(new MemoryCandidateProposal(
            SourceRunId: Guid.NewGuid(),
            GeneratedByInstanceId: Guid.NewGuid(),
            Scope: "project",
            Kind: "fact",
            Content: "database concurrency old workaround",
            Confidence: 0.9,
            ProjectId: projectId));
        var promotedRevoked = await ltm.DecideCandidateAsync(revokedCand.Id, "promoted", "verified");
        await ltm.RevokeAsync(promotedRevoked.PromotedMemoryItemId!.Value, "superseded by WAL policy");

        // Verify persisted applicability, expiry_condition, and revocation_reason columns via EnsureColumnsAsync
        await using (var db = await memoryDbFactory.CreateDbContextAsync())
        {
            var validRow = await db.MemoryItems.SingleAsync(x => x.Id == promotedValid.PromotedMemoryItemId!.Value);
            Assert.Equal($"project_id:{projectId}", validRow.Applicability);
            Assert.Equal("never", validRow.ExpiryCondition);

            var revokedRow = await db.MemoryItems.SingleAsync(x => x.Id == promotedRevoked.PromotedMemoryItemId!.Value);
            Assert.Equal("revoked", revokedRow.Status);
            Assert.Equal("superseded by WAL policy", revokedRow.RevocationReason);
        }

        // Retrieve with query having zero literal keyword overlap with SQLite WAL memory
        var retrieved = await memoryStore.RetrieveAsync(sessionId.ToString(), "database concurrency", 5);
        var match = Assert.Single(retrieved);
        Assert.Contains("SQLite WAL mode", match.Content, StringComparison.Ordinal);
        Assert.Equal("hybrid_vector_keyword", match.Provenance["retrieval"]);
        Assert.Equal($"project_id:{projectId}", match.Provenance["applicability"]);
    }

    [Fact]
    public async Task LongTermMemory_FallsBackToKeywordScoring_WhenNoEmbeddingRouteConfigured()
    {
        var noEmbedRoot = ApiTestStorage.CreateRoot("hybrid-memory-no-embed");
        Directory.CreateDirectory(noEmbedRoot);
        try
        {
            using var noEmbedFactory = new Factory(noEmbedRoot, enableFakeEmbeddings: false);
            _ = noEmbedFactory.CreateClient();

            var tenant = noEmbedFactory.Services.GetRequiredService<ITenantContextAccessor>().Current;
            var memoryDbFactory = noEmbedFactory.Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
            var projectId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            await SeedProjectAndSessionAsync(memoryDbFactory, tenant, projectId, sessionId, viewMode: "flat");

            var ltm = noEmbedFactory.Services.GetRequiredService<ILongTermMemoryService>();
            var memoryStore = noEmbedFactory.Services.GetRequiredService<IMemoryStore>();

            var cand = await ltm.CreateCandidateAsync(new MemoryCandidateProposal(
                SourceRunId: Guid.NewGuid(),
                GeneratedByInstanceId: Guid.NewGuid(),
                Scope: "workspace",
                Kind: "convention",
                Content: "Always verify dotnet build passes cleanly before handoff.",
                Confidence: 0.95));
            await ltm.DecideCandidateAsync(cand.Id, "promoted", "verified");

            var results = await memoryStore.RetrieveAsync(sessionId.ToString(), "dotnet build", 5);
            var hit = Assert.Single(results);
            Assert.Contains("dotnet build", hit.Content, StringComparison.Ordinal);
            Assert.Equal("keyword_fallback", hit.Provenance["retrieval"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(noEmbedRoot)) Directory.Delete(noEmbedRoot, true);
        }
    }

    private static async Task SeedProjectAndSessionAsync(
        IDbContextFactory<MemoryDbContext> factory,
        TenantContext tenant,
        Guid projectId,
        Guid sessionId,
        string viewMode)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync();
        await DbContextSchemaBootstrapper.EnsureColumnsAsync(db);
        db.Projects.Add(new ProjectRecord
        {
            Id = projectId,
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            Name = "test-project",
            RootPath = Path.GetTempPath(),
            NormalizedRootPath = Path.GetTempPath(),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Sessions.Add(new SessionRecord
        {
            Id = sessionId,
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            ProjectId = projectId,
            Title = "test-session",
            ViewMode = viewMode,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeSemanticEmbeddingProvider : IEmbeddingProvider
    {
        public Task<EmbeddingResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        {
            var vectors = request.Inputs.Select(Embed).ToList();
            return Task.FromResult(new EmbeddingResult
            {
                IsAvailable = true,
                ModelId = "fake/semantic-embed",
                Dimension = 3,
                Vectors = vectors
            });
        }

        private static float[] Embed(string text)
        {
            var lower = text.ToLowerInvariant();
            // Map both "database concurrency" and "sqlite wal ... parallel writes" into the same semantic direction.
            if (lower.Contains("wal", StringComparison.Ordinal) || lower.Contains("database concurrency", StringComparison.Ordinal))
                return [1.0f, 0.0f, 0.0f];
            if (lower.Contains("spatial", StringComparison.Ordinal))
                return [0.0f, 1.0f, 0.0f];
            return [0.0f, 0.0f, 1.0f];
        }
    }

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;
        private readonly bool _enableFakeEmbeddings;

        public Factory(string root, bool enableFakeEmbeddings)
        {
            _root = root;
            _enableFakeEmbeddings = enableFakeEmbeddings;
        }

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            if (_enableFakeEmbeddings)
            {
                builder.ConfigureServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<IEmbeddingProvider, FakeSemanticEmbeddingProvider>());
                });
            }
        }
    }
}
