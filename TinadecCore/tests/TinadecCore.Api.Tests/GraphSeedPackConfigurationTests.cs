using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;
using TinadecCore.Skills;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Api.Tests;

public sealed class GraphSeedPackConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-graphseed-config-tests", Guid.NewGuid().ToString("N"));
    private readonly TenantContext _actor = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");

    [Fact]
    public async Task PublishedAgentsWithTheSameSlugRemainValidAcrossFileProjectionAndRestart()
    {
        await using var services = await CreateAsync();
        var first = Agent("published", "first-pack:meeting");
        var second = Agent("published", "second-pack:meeting");
        await using (var db = await Agents(services).CreateDbContextAsync())
        {
            db.AgentDefinitions.AddRange(first, second);
            await db.SaveChangesAsync();
        }
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        Assert.Empty((await documents.ReadAsync("agents")).Diagnostics);
        await using var restarted = await CreateAsync(databaseName: "published-rebuilt.db");
        await using var rebuilt = await Agents(restarted).CreateDbContextAsync();
        var rows = await rebuilt.AgentDefinitions.AsNoTracking().OrderBy(x => x.SourceKey).ToArrayAsync();
        Assert.Equal(new[] { first.Id, second.Id }, rows.Select(x => x.Id));
        Assert.All(rows, row => Assert.Equal("published", row.Status));
    }

    [Fact]
    public async Task DuplicateDraftSlugIsRejectedBeforeTheAuthoritativeFileOrSqlChanges()
    {
        await using var services = await CreateAsync();
        await using (var db = await Agents(services).CreateDbContextAsync())
        {
            db.AgentDefinitions.Add(Agent("draft", "first:meeting"));
            await db.SaveChangesAsync();
        }
        await AssertAgentWriteRejectedAsync(services, Agent("draft", "second:meeting"), "Slug");
        // Published rows are outside the draft predicate, even beside a draft.
        await using var published = await Agents(services).CreateDbContextAsync();
        published.AgentDefinitions.Add(Agent("published", "third:meeting"));
        await published.SaveChangesAsync();
        Assert.Equal(2, await published.AgentDefinitions.CountAsync());
    }

    [Fact]
    public async Task UnfilteredSourceIdentityStillRejectsPublishedDuplicatesWithoutChangingBytes()
    {
        await using var services = await CreateAsync();
        await using (var db = await Agents(services).CreateDbContextAsync())
        {
            db.AgentDefinitions.Add(Agent("published", "same-pack:meeting"));
            await db.SaveChangesAsync();
        }
        var duplicate = Agent("published", "same-pack:meeting");
        duplicate.Slug = "another-slug";
        await AssertAgentWriteRejectedAsync(services, duplicate, "SourceKind, SourceKey");
    }

    [Fact]
    public async Task DeletedSkillNamesCanBeReusedButTwoLiveNamesCannotCommit()
    {
        await using var services = await CreateAsync();
        var factory = services.GetRequiredService<IDbContextFactory<IntegrationDbContext>>();
        var deleted = Skill(DateTimeOffset.UtcNow);
        var active = Skill(null);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.SharedSkills.AddRange(deleted, active);
            await db.SaveChangesAsync();
        }
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var before = await documents.ReadAsync("skills");
        Assert.Empty(before.Diagnostics);
        var bytes = await File.ReadAllBytesAsync(before.Path);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.SharedSkills.Add(Skill(null));
            var error = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => db.SaveChangesAsync());
            Assert.Contains(error.Diagnostics, d => d.Code == "configuration_unique" && d.Message.Contains("Name", StringComparison.Ordinal));
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(before.Path));
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("skills")).ContentHash);
        await using var restarted = await CreateAsync(databaseName: "skills-rebuilt.db");
        await using var rebuilt = await restarted.GetRequiredService<IDbContextFactory<IntegrationDbContext>>().CreateDbContextAsync();
        var rows = await rebuilt.SharedSkills.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.DeletedAt is null);
        Assert.Contains(rows, row => row.Id == deleted.Id && row.DeletedAt is not null);
    }

    [Fact]
    public async Task UnknownUniquePredicateReturnsAnExplicitDiagnosticAndNeverCommitsTheFile()
    {
        var root = Path.Combine(_root, "unknown-filter");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var path = Path.Combine(root, "config", "skills.toml");
        await File.WriteAllTextAsync(path, "schema_version = 1\nversion = 1\n");
        var services = BaseServices(root, new MemoryContentStore());
        services.AddDbContextFactory<UnsupportedFilter.IntegrationDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddTinadecConfigurationFiles();
        await using var provider = services.BuildServiceProvider();
        var documents = provider.GetRequiredService<IScopeConfigurationDocuments>();
        var before = await documents.ReadAsync("skills");
        Assert.Empty(before.Diagnostics);
        var proposed = before.Text + "\n[[unknown_filtered_configuration]]\nid = \"" + Guid.NewGuid() + "\"\nstatus = \"published\"\nname = \"probe\"\n";
        var error = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => documents.SaveIfMatchAsync("skills", proposed, before.ContentHash));
        Assert.Contains(error.Diagnostics, d => d.Code == "configuration_unique_filter_unsupported" && d.Message.Contains("status <> 'draft'", StringComparison.Ordinal));
        Assert.Equal(before.Text, await File.ReadAllTextAsync(path));
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("skills")).ContentHash);
    }

    [Fact]
    public async Task ACurrentPromptVersionMustUseItsActualOwnerKeyAndCannotDisappearFromEditingSource()
    {
        await using var services = await CreateAsync();
        var pipeline = Guid.NewGuid(); var version = Guid.NewGuid();
        await using (var db = await Agents(services).CreateDbContextAsync())
        {
            db.PromptPipelines.Add(new() { Id = pipeline, TenantId = _actor.TenantId, WorkspaceId = _actor.WorkspaceId,
                Slug = "current-prompt", DisplayName = "Current prompt", GraphJson = "{}", Status = "published", Version = 1 });
            db.PromptVersions.Add(new() { Id = version, TenantId = _actor.TenantId, WorkspaceId = _actor.WorkspaceId,
                PromptPipelineId = pipeline, GraphJson = "{}", Status = "published", Version = 1 });
            await db.SaveChangesAsync();
        }
        var coordinator = services.GetRequiredService<IConfigurationProjectionCoordinator>();
        await coordinator.CompileAsync();
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var before = await documents.ReadAsync("agents");
        var model = TomlSerializer.Deserialize<TomlTable>(before.Text)!;
        model.Remove("prompt_versions");
        await documents.SaveIfMatchAsync("agents", TomlSerializer.Serialize(model), before.ContentHash);
        var missing = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => coordinator.CompileAsync());
        Assert.Equal("configuration_source_reference", missing.Code);
        Assert.Contains(missing.Diagnostics, d => d.Message.StartsWith("agents.prompt_pipelines:", StringComparison.Ordinal));
        await using var retained = await Agents(services).CreateDbContextAsync();
        var history = await retained.PromptVersions.SingleAsync();
        Assert.Equal(version, history.Id);
        Assert.Equal("retired", history.Status);
        Assert.Equal("{}", history.GraphJson);
    }

    [Fact]
    public async Task GraphSeed310InstallsBesideTheExistingPackAndRebuildsWithoutReplacingItsHistory()
    {
        var content = new MemoryContentStore();
        var graphPath = FindRepositoryFile("apps", "desktop", "src", "agentPacks", "GraphSeedPack", "manifest.json");
        var graphBytes = await File.ReadAllBytesAsync(graphPath);
        var graphDigest = SHA256.HashData(graphBytes);
        var graph = await ReadEnvelopeAsync(graphPath);
        Assert.Equal("3.1.1", graph.Manifest!.Metadata!.Version);
        var bootstrap = await ReadEnvelopeAsync(FindRepositoryFile("TinadecCore", "tests", "TinadecCore.Api.Tests", "Fixtures", "bootstrap-pack.json"));
        var oldRows = new Dictionary<string, string>();
        string? installedDigest = null;
        await using (var services = await CreateAsync(content))
        {
            await InstallAsync(services, bootstrap);
            await using (var db = await Agents(services).CreateDbContextAsync())
            {
                Assert.Equal(14, await db.AgentDefinitions.CountAsync());
                Assert.Equal(7, await db.AgentModes.CountAsync());
                oldRows = await CaptureExistingPackRowsAsync(db);
            }
            var result = await InstallAsync(services, graph);
            Assert.Equal("installed", result.Status);
            Assert.Equal("3.1.1", result.ActiveVersion);
            Assert.Equal(graph.Integrity!.Digest, result.IntegrityDigest);
            installedDigest = result.IntegrityDigest;
            await services.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync();
            await using var installed = await Agents(services).CreateDbContextAsync();
            await AssertExistingRowsUnchangedAsync(installed, oldRows);
            Assert.Equal(2, await installed.AgentDefinitions.CountAsync(x => x.Slug == "meeting" && x.Status == "published"));
            Assert.Equal(2, await installed.AgentPackInstallations.CountAsync());
        }
        // A new DI graph and empty SQL/content projections must restore solely
        // from this test scope's TOML; no user directory or bootstrap host runs.
        content.Clear();
        await using (var restarted = await CreateAsync(content, "pack-rebuilt.db"))
        {
            await restarted.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync();
            await using var rebuilt = await Agents(restarted).CreateDbContextAsync();
            await AssertExistingRowsUnchangedAsync(rebuilt, oldRows);
            Assert.Equal(2, await rebuilt.AgentDefinitions.CountAsync(x => x.Slug == "meeting" && x.Status == "published"));
            var detail = await restarted.GetRequiredService<IAgentPackService>().GetAsync("tinadec.graph.seed-pack");
            Assert.NotNull(detail);
            Assert.Equal("3.1.1", detail.ActiveVersion);
            Assert.Equal(installedDigest, detail.IntegrityDigest);
            Assert.Equal("up_to_date", (await restarted.GetRequiredService<IAgentPackService>().PreviewAsync(graph)).Action);
        }
        Assert.Equal(graphBytes, await File.ReadAllBytesAsync(graphPath));
        Assert.Equal(graphDigest, SHA256.HashData(await File.ReadAllBytesAsync(graphPath)));
    }

    private async Task AssertAgentWriteRejectedAsync(ServiceProvider services, AgentDefinitionRecord row, string identity)
    {
        var documents = services.GetRequiredService<IScopeConfigurationDocuments>();
        var before = await documents.ReadAsync("agents");
        var bytes = await File.ReadAllBytesAsync(before.Path);
        await using (var db = await Agents(services).CreateDbContextAsync())
        {
            db.AgentDefinitions.Add(row);
            var error = await Assert.ThrowsAsync<ConfigurationDocumentException>(() => db.SaveChangesAsync());
            Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "configuration_unique" && diagnostic.Message.Contains(identity, StringComparison.Ordinal));
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(before.Path));
        Assert.Equal(before.ContentHash, (await documents.ReadAsync("agents")).ContentHash);
        await using var verify = await Agents(services).CreateDbContextAsync();
        Assert.Equal(1, await verify.AgentDefinitions.CountAsync());
    }

    private async Task<ServiceProvider> CreateAsync(MemoryContentStore? content = null, string databaseName = "config.db")
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        var storage = Path.Combine(_root, "config", "storage.toml");
        var logging = Path.Combine(_root, "config", "logging.toml");
        if (!File.Exists(storage)) await File.WriteAllTextAsync(storage, "[storage]\nbackend = \"sqlite\"\n");
        if (!File.Exists(logging)) await File.WriteAllTextAsync(logging, "[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");
        var services = BaseServices(_root, content ?? new MemoryContentStore());
        var connection = "Data Source=" + Path.Combine(_root, databaseName) + ";Pooling=false";
        services.AddDbContextFactory<AgentConfigurationDbContext>(options => options.UseSqlite(connection));
        services.AddDbContextFactory<IntegrationDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IAgentPackService, AgentPackService>();
        services.AddTinadecConfigurationFiles();
        var provider = services.BuildServiceProvider();
        await using (var db = await Agents(provider).CreateDbContextAsync()) await DbContextSchemaBootstrapper.EnsureTablesAsync(db);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<IntegrationDbContext>>().CreateDbContextAsync()) await DbContextSchemaBootstrapper.EnsureTablesAsync(db);
        await provider.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync();
        return provider;
    }

    private ServiceCollection BaseServices(string root, IContentStore content)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScopeStorageLocations>(new StorageScopeDescriptor("test-user", "user", root));
        services.AddSingleton<ITenantContextAccessor>(new TenantAccessor(_actor));
        services.AddSingleton(content);
        return services;
    }

    private AgentDefinitionRecord Agent(string status, string sourceKey) => new()
    {
        Id = Guid.NewGuid(), TenantId = _actor.TenantId, WorkspaceId = _actor.WorkspaceId, Slug = "meeting",
        DisplayName = "Meeting", Layer = "operation", Role = "meeting", Status = status, SourceKind = "pack", SourceKey = sourceKey,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        CreatedByPrincipalId = _actor.PrincipalId, UpdatedByPrincipalId = _actor.PrincipalId
    };

    private SharedSkillResourceRecord Skill(DateTimeOffset? deletedAt) => new()
    {
        Id = Guid.NewGuid(), TenantId = _actor.TenantId, WorkspaceId = _actor.WorkspaceId, Name = "same-name", Description = "Skill",
        PackageReference = "packages/skills/test/same-name", ContentHash = "test-hash", Source = "manual", Enabled = true,
        UpdatedAt = DateTimeOffset.UtcNow, DeletedAt = deletedAt
    };

    private static IDbContextFactory<AgentConfigurationDbContext> Agents(IServiceProvider services) => services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();

    private static async Task<AgentPackApplyView> InstallAsync(IServiceProvider services, AgentPackEnvelopeDto envelope)
    {
        var service = services.GetRequiredService<IAgentPackService>();
        var preview = await service.PreviewAsync(envelope);
        Assert.Equal("install", preview.Action);
        return await service.ApplyAsync(preview.PackId, new() { PreviewId = preview.PreviewId, Envelope = envelope }, null, "test-install-" + preview.PackId);
    }

    private static async Task<AgentPackEnvelopeDto> ReadEnvelopeAsync(string path) => AgentPackService.CreateEnvelope(
        JsonSerializer.Deserialize<AgentPackManifestDto>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Repository test resource was not found: " + Path.Combine(segments));
    }

    private static async Task<Dictionary<string, string>> CaptureExistingPackRowsAsync(AgentConfigurationDbContext db)
    {
        var rows = new Dictionary<string, string>();
        foreach (var row in await db.AgentDefinitions.AsNoTracking().ToArrayAsync()) rows["agent:" + row.Id] = JsonSerializer.Serialize(row);
        foreach (var row in await db.AgentVersions.AsNoTracking().ToArrayAsync()) rows["agent-version:" + row.Id] = JsonSerializer.Serialize(row);
        foreach (var row in await db.AgentModes.AsNoTracking().ToArrayAsync()) rows["mode:" + row.Id] = JsonSerializer.Serialize(row);
        foreach (var row in await db.ModeVersions.AsNoTracking().ToArrayAsync()) rows["mode-version:" + row.Id] = JsonSerializer.Serialize(row);
        foreach (var row in await db.PromptPipelines.AsNoTracking().ToArrayAsync()) rows["prompt:" + row.Id] = JsonSerializer.Serialize(row);
        foreach (var row in await db.PromptVersions.AsNoTracking().ToArrayAsync()) rows["prompt-version:" + row.Id] = JsonSerializer.Serialize(row);
        foreach (var row in await db.AgentPackInstallations.AsNoTracking().ToArrayAsync()) rows["installation:" + row.Id] = JsonSerializer.Serialize(row);
        foreach (var row in await db.AgentPackVersions.AsNoTracking().ToArrayAsync()) rows["pack-version:" + row.Id] = JsonSerializer.Serialize(row);
        return rows;
    }

    private static async Task AssertExistingRowsUnchangedAsync(AgentConfigurationDbContext db, Dictionary<string, string> expected)
    {
        var actual = await CaptureExistingPackRowsAsync(db);
        foreach (var (key, value) in expected) Assert.Equal(value, actual[key]);
    }

    private sealed class TenantAccessor(TenantContext actor) : ITenantContextAccessor { public TenantContext Current => actor; }
    private sealed class MemoryContentStore : IContentStore
    {
        private readonly Dictionary<string, byte[]> _bytes = new();
        public void Clear() => _bytes.Clear();
        public async Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream(); await request.Content.CopyToAsync(output, cancellationToken);
            var body = output.ToArray(); var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var path = $"content/tenants/{request.TenantId:N}/{request.WorkspaceId?.ToString("N") ?? "tenant"}/{request.Kind}/{hash}";
            _bytes[path] = body; return new(path, hash, body.Length, request.MediaType);
        }
        public Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(_bytes[reference.Value]));
        public Task<bool> ExistsAsync(ContentReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_bytes.ContainsKey(reference.Value));
        public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken = default) { _bytes.Remove(reference.Value); return Task.CompletedTask; }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static class UnsupportedFilter
    {
        public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options) : ConfigurationProjectionDbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Record>(entity =>
                {
                    entity.ToTable("unknown_filtered_configuration"); entity.HasKey(x => x.Id);
                    entity.Property(x => x.Status).HasColumnName("status");
                    entity.HasIndex(x => x.Name).IsUnique().HasFilter("status <> 'draft'");
                });
            }
        }
        public sealed class Record { public Guid Id { get; set; } public string Name { get; set; } = ""; public string Status { get; set; } = ""; }
    }
}
