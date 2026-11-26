using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.AspNetCore.Endpoints;
using TinadecCore.Companion;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Regression tests for Round 2 performance, cold-start, worker backoff, and SSE cancellation fixes:
/// 1. Durable configuration projection stamp (_tinadec_projection_stamps) skips redundant cold-start TOML re-projection
///    when file bytes + schema stamp match, and invalidates cleanly when TOML changes on disk;
/// 2. O(N) live-source validation still catches dangling active references across large catalogs;
/// 3. CompanionAutonomyService backs off automatic due-list polling after 403/quota/model outages and clears on config update;
/// 4. StorageLifecycleService.GetEventSignalTask wakes immediately on AppendEventAsync instead of polling slowly;
/// 5. DmaeaEndpoints SSE follow propagates HttpContext.RequestAborted to IFullDuplexRunCoordinator.FollowAsync.
/// </summary>
public sealed class PerformanceAndConnectionRegressionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-perf-conn-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConfigurationProjection_DurableStampSkipsColdRestartReparse_AndInvalidatesOnTomlEdit()
    {
        var scopeRoot = Path.Combine(_root, "proj-stamp");
        Directory.CreateDirectory(Path.Combine(scopeRoot, "config"));
        Directory.CreateDirectory(Path.Combine(scopeRoot, "data"));
        await File.WriteAllTextAsync(Path.Combine(scopeRoot, "config", "storage.toml"), "[storage]\nbackend = \"sqlite\"\n");
        await File.WriteAllTextAsync(Path.Combine(scopeRoot, "config", "logging.toml"), "[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");

        var dbPath = Path.Combine(scopeRoot, "data", "tinadec.db");
        var tenant = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");
        var defId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Provider 1: seed schema + write one agent definition so agents.toml + SQLite + _tinadec_projection_stamps are populated.
        EffectiveConfigurationSnapshot firstSnapshot;
        await using (var sp1 = BuildProjectionProvider(scopeRoot, dbPath, tenant))
        {
            await sp1.GetRequiredService<IStorageMigrationRunner>().RunAsync();
            var factory1 = sp1.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
            await using (var db = await factory1.CreateDbContextAsync())
            {
                db.AgentDefinitions.Add(new AgentDefinitionRecord
                {
                    Id = defId,
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    Slug = "perf-agent",
                    DisplayName = "Perf Agent V1",
                    Layer = "operation",
                    Role = "coordinator",
                    Status = "draft",
                    Revision = 1,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    UpdatedByPrincipalId = tenant.PrincipalId
                });
                await db.SaveChangesAsync();
            }
            firstSnapshot = await sp1.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync();
            Assert.Equal(firstSnapshot.Documents["agents"], await ReadProjectionStampAsync(dbPath, "agents"));
        }

        // Remove the projected row directly in SQLite while keeping _tinadec_projection_stamps untouched.
        // If cold start hits the durable stamp (instead of re-parsing & re-projecting unchanged agents.toml),
        // SQLite keeps the stamp-hit shortcut and CompileAsync still returns the identical snapshot cleanly.
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM agent_definitions WHERE upper(id) = upper(@id);";
            del.Parameters.AddWithValue("@id", defId.ToString("D"));
            Assert.Equal(1, await del.ExecuteNonQueryAsync());
        }

        // Provider 2 (cold start): stamp matches agents.toml bytes -> skips re-projecting SQLite, row stays absent, CompileAsync matches.
        await using (var sp2 = BuildProjectionProvider(scopeRoot, dbPath, tenant))
        {
            var coordinator2 = sp2.GetRequiredService<IConfigurationProjectionCoordinator>();
            await coordinator2.ReconcileAsync();
            var factory2 = sp2.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
            await using (var db2 = await factory2.CreateDbContextAsync())
            {
                Assert.Null(await db2.AgentDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == defId));
            }
            var coldSnapshot = await coordinator2.CompileAsync();
            Assert.Equal(firstSnapshot.ContentHash, coldSnapshot.ContentHash);
        }

        // Now modify agents.toml on disk -> SHA-256 stamp mismatches on cold start -> full re-projection restores updated row.
        var agentsTomlPath = Path.Combine(scopeRoot, "config", "agents.toml");
        var tomlText = await File.ReadAllTextAsync(agentsTomlPath);
        tomlText = tomlText.Replace("Perf Agent V1", "Perf Agent V2", StringComparison.Ordinal);
        await File.WriteAllTextAsync(agentsTomlPath, tomlText);

        await using (var sp3 = BuildProjectionProvider(scopeRoot, dbPath, tenant))
        {
            await sp3.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync();
            var factory3 = sp3.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
            await using var db3 = await factory3.CreateDbContextAsync();
            var restored = await db3.AgentDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == defId);
            Assert.NotNull(restored);
            Assert.Equal("Perf Agent V2", restored!.DisplayName);
        }
    }

    [Fact]
    public async Task ConfigurationLiveSourceValidation_StillRejectsDanglingReferenceInLargeCatalog()
    {
        var scopeRoot = Path.Combine(_root, "live-source-large");
        Directory.CreateDirectory(Path.Combine(scopeRoot, "config"));
        Directory.CreateDirectory(Path.Combine(scopeRoot, "data"));
        await File.WriteAllTextAsync(Path.Combine(scopeRoot, "config", "storage.toml"), "[storage]\nbackend = \"sqlite\"\n");
        await File.WriteAllTextAsync(Path.Combine(scopeRoot, "config", "logging.toml"), "[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");

        var dbPath = Path.Combine(scopeRoot, "data", "tinadec.db");
        var tenant = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");
        var now = DateTimeOffset.UtcNow;

        await using var sp = BuildProjectionProvider(scopeRoot, dbPath, tenant);
        await sp.GetRequiredService<IStorageMigrationRunner>().RunAsync();
        var factory = sp.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var modeId = Guid.NewGuid();
        var validModeVersionId = Guid.NewGuid();
        var missingModeVersionId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AgentModes.Add(new AgentModeRecord
            {
                Id = modeId,
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                Slug = "default-mode",
                DisplayName = "Default Mode",
                Status = "draft",
                Version = 1,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedByPrincipalId = tenant.PrincipalId,
                UpdatedByPrincipalId = tenant.PrincipalId
            });
            db.ModeVersions.Add(new ModeVersionRecord
            {
                Id = validModeVersionId,
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                AgentModeId = modeId,
                Version = 1,
                SnapshotJson = "{\"nodes\":[],\"edges\":[]}",
                Status = "published",
                Revision = 1,
                CreatedAt = now,
                CreatedByPrincipalId = tenant.PrincipalId
            });
            for (var i = 0; i < 150; i++)
            {
                db.AgentDefinitions.Add(new AgentDefinitionRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    WorkspaceId = tenant.WorkspaceId,
                    Slug = $"agent-{i:D3}",
                    DisplayName = $"Agent {i:D3}",
                    Layer = "execution",
                    Role = "worker",
                    Status = "draft",
                    SourceKind = "manual",
                    SourceKey = $"agent-{i:D3}",
                    Revision = 1,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedByPrincipalId = tenant.PrincipalId,
                    UpdatedByPrincipalId = tenant.PrincipalId
                });
            }
            db.WorkspaceDefaults.Add(new WorkspaceDefaultsRecord
            {
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                DefaultAgentModeId = modeId,
                DefaultModeVersionId = validModeVersionId,
                Status = "published",
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        // Write dangling version reference into agents.toml via SaveChangesAsync, then verify CompileAsync rejects it across 150+ agents.
        await using (var dbInvalid = await factory.CreateDbContextAsync())
        {
            var wd = await dbInvalid.WorkspaceDefaults.SingleAsync(x => x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId);
            wd.DefaultModeVersionId = missingModeVersionId;
            wd.Revision++;
            await dbInvalid.SaveChangesAsync();
        }

        var ex1 = await Assert.ThrowsAsync<ConfigurationDocumentException>(() =>
            sp.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync());
        Assert.Equal("configuration_source_reference", ex1.Code);

        // Cold provider compile on disk TOML containing 150+ agents + dangling version reference also throws configuration_source_reference.
        await using var sp2 = BuildProjectionProvider(scopeRoot, dbPath, tenant);
        var compileEx = await Assert.ThrowsAsync<ConfigurationDocumentException>(() =>
            sp2.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync());
        Assert.Equal("configuration_source_reference", compileEx.Code);
    }

    [Fact]
    public async Task CompanionAutonomy_BacksOffAutomaticDueChecksOnQuotaFailure_AndResetsOnConfigEdit()
    {
        var scopeRoot = Path.Combine(_root, "companion-backoff");
        var dataRoot = Path.Combine(scopeRoot, "data");
        Directory.CreateDirectory(dataRoot);
        var dbPath = Path.Combine(dataRoot, "tinadec.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Provider"] = "Sqlite",
                ["TinadecPersistence:DataRoot"] = dataRoot,
                ["TinadecPersistence:Sqlite:DatabasePath"] = dbPath
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddTinadecPersistence(config, scopeRoot);
        services.AddTinadecCore();
        services.Replace(ServiceDescriptor.Singleton<IAgentChatClientFactory>(new QuotaFailingChatFactory()));

        await using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IStorageMigrationRunner>().RunAsync();

        var dbFactory = sp.GetRequiredService<IDbContextFactory<CompanionDbContext>>();
        var tenant = sp.GetRequiredService<ITenantContextAccessor>().Current;
        var autonomy = sp.GetRequiredService<CompanionAutonomyService>();
        var companionId = Guid.NewGuid();
        var oldUpdate = DateTimeOffset.UtcNow.AddHours(-2);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Companions.Add(new CompanionAgentRecord
            {
                Id = companionId,
                TenantId = tenant.TenantId,
                Name = "Backoff Companion",
                Status = "active",
                Role = "autonomous",
                AutonomyEnabled = true,
                AutonomyPaused = false,
                AutonomyIntervalMinutes = 1,
                AutonomyDailyBudget = 5,
                CreatedAt = oldUpdate,
                UpdatedAt = oldUpdate
            });
            await db.SaveChangesAsync();
        }

        Assert.Contains(companionId, await autonomy.DueCompanionIdsAsync());

        // Simulate failing with HTTP 403 insufficient_user_quota -> should back off >= 15 min so next poll skips it.
        var tickResult = await autonomy.TickAsync(companionId);
        Assert.Contains("model_unavailable", JsonSerializer.Serialize(tickResult));
        Assert.DoesNotContain(companionId, await autonomy.DueCompanionIdsAsync());

        Assert.Equal(TimeSpan.FromMinutes(15), CompanionAutonomyService.ComputeFailureDelay(1, 1, isQuotaOrAuth: true));
        Assert.Equal(TimeSpan.FromMinutes(30), CompanionAutonomyService.ComputeFailureDelay(1, 2, isQuotaOrAuth: true));
        Assert.Equal(TimeSpan.FromMinutes(60), CompanionAutonomyService.ComputeFailureDelay(1, 5, isQuotaOrAuth: true));

        // Updating companion configuration (UpdatedAt advances beyond failure timestamp) clears failure backoff immediately.
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var row = await db.Companions.SingleAsync(x => x.Id == companionId);
            row.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
        }
        Assert.Contains(companionId, await autonomy.DueCompanionIdsAsync());
    }

    [Fact]
    public async Task StorageLifecycleService_GetEventSignalTask_WakesImmediatelyOnAppendEvent()
    {
        var apiRoot = Path.Combine(_root, "signal-api");
        Directory.CreateDirectory(apiRoot);
        using var factory = new SignalApiFactory(apiRoot);
        using var client = factory.CreateClient();

        var project = await (await client.PostAsJsonAsync("/api/v1/projects",
            new { name = "SignalProj", path = Path.Combine(apiRoot, "ws") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions",
            new { project_id = project.GetProperty("id").GetGuid(), title = "Signal" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var msg = await (await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages",
            new { content = "hello" })).Content.ReadFromJsonAsync<JsonElement>();

        var lifecycle = factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, msg.GetProperty("id").GetGuid());

        var signal = lifecycle.GetEventSignalTask();
        Assert.False(signal.IsCompleted);

        var sw = Stopwatch.StartNew();
        await lifecycle.AppendEventAsync(run.Id, "run.progress", new { step = 1 }, "Progress 1");
        await signal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(sw.ElapsedMilliseconds < 900, $"Expected instant wake, waited {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task DmaeaEndpoints_StreamDisconnectPropagatesRequestAbortedToFollowAsync()
    {
        var apiRoot = Path.Combine(_root, "sse-abort-kestrel");
        var dataRoot = Path.Combine(apiRoot, "data");
        Directory.CreateDirectory(dataRoot);
        var spyCoordinator = new SpyFollowCoordinator();
        var logErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Provider"] = "Sqlite",
            ["TinadecPersistence:DataRoot"] = dataRoot,
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(dataRoot, "tinadec.db")
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ErrorLogCaptureProvider(logErrors));
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddTinadecPersistence(builder.Configuration, apiRoot);
        builder.Services.AddTinadecCore();
        builder.Services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IFullDuplexRunCoordinator>(spyCoordinator));
        await using var app = builder.Build();
        app.MapDmaeaEndpoints();
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First(uri => uri.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using (var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero })
            using (var client = new HttpClient(handler) { BaseAddress = new Uri(address) })
            using (var response = await client.GetAsync(
                $"/api/v1/runs/{Guid.NewGuid()}/stream",
                HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Unexpected status {(int)response.StatusCode}: {body} | logs: {string.Join(" || ", logErrors)}");
                }
                await using var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[512];
                var read = await stream.ReadAsync(buffer.AsMemory());
                Assert.True(read > 0);
            }

            // Disposing HttpClient + SocketsHttpHandler closes the TCP socket while FollowAsync would otherwise block 30s ->
            // Kestrel must immediately trip HttpContext.RequestAborted and cancel FollowAsync.
            await spyCoordinator.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task SqliteConnectionPragmaInterceptor_SkipsRedundantPragmasOnPooledHandleReuse_AndReappliesOnFreshHandle()
    {
        var dbPath = Path.Combine(_root, "pragma-pool.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();

        SQLitePCL.sqlite3 handle1;
        await using (var c1 = new SqliteConnection(cs))
        {
            await c1.OpenAsync();
            await SqliteConnectionPragmaInterceptor.ApplyPragmasAsync(c1);
            handle1 = c1.Handle!;
            Assert.NotNull(handle1);
            Assert.Equal(15_000L, await ReadScalarLongAsync(c1, "PRAGMA busy_timeout;"));
            Assert.Equal(1L, await ReadScalarLongAsync(c1, "PRAGMA synchronous;"));
        }

        // Pooled reopen returns the exact same physical sqlite3 handle: ApplyPragmasAsync must skip issuing redundant PRAGMAs.
        await using (var c2 = new SqliteConnection(cs))
        {
            await c2.OpenAsync();
            Assert.Same(handle1, c2.Handle);
            await using (var setCmd = c2.CreateCommand())
            {
                setCmd.CommandText = "PRAGMA busy_timeout = 1234;";
                await setCmd.ExecuteNonQueryAsync();
            }
            await SqliteConnectionPragmaInterceptor.ApplyPragmasAsync(c2);
            Assert.Equal(1_234L, await ReadScalarLongAsync(c2, "PRAGMA busy_timeout;"));
        }

        // Clearing pools creates a fresh physical sqlite3 handle -> pragmas must be applied again.
        SqliteConnection.ClearAllPools();
        await using (var c3 = new SqliteConnection(cs))
        {
            await c3.OpenAsync();
            Assert.NotSame(handle1, c3.Handle);
            await SqliteConnectionPragmaInterceptor.ApplyPragmasAsync(c3);
            Assert.Equal(15_000L, await ReadScalarLongAsync(c3, "PRAGMA busy_timeout;"));
            Assert.Equal(1L, await ReadScalarLongAsync(c3, "PRAGMA synchronous;"));
        }
    }

    [Fact]
    public async Task ScopeConfigurationDocuments_FileHashCache_HitsStableFileAndDetectsDiskEdits()
    {
        var scopeRoot = Path.Combine(_root, "hash-cache");
        Directory.CreateDirectory(Path.Combine(scopeRoot, "config"));
        Directory.CreateDirectory(Path.Combine(scopeRoot, "data"));
        await File.WriteAllTextAsync(Path.Combine(scopeRoot, "config", "storage.toml"), "[storage]\nbackend = \"sqlite\"\n");
        await File.WriteAllTextAsync(Path.Combine(scopeRoot, "config", "logging.toml"), "[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");

        var agentsPath = Path.Combine(scopeRoot, "config", "agents.toml");
        var v1 = "schema_version = 1\nversion = 1\n# token-aaaa\n";
        await File.WriteAllTextAsync(agentsPath, v1);
        File.SetLastWriteTimeUtc(agentsPath, DateTime.UtcNow.AddMinutes(-5));

        var tenant = new TenantContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");
        var dbPath = Path.Combine(scopeRoot, "data", "tinadec.db");
        await using var sp = BuildProjectionProvider(scopeRoot, dbPath, tenant);
        await sp.GetRequiredService<IStorageMigrationRunner>().RunAsync();
        var docs = sp.GetRequiredService<IScopeConfigurationDocuments>();
        var coordinator = sp.GetRequiredService<IConfigurationProjectionCoordinator>();

        static string Sha256Hex(string text) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

        await coordinator.ReconcileAsync();
        var snap1 = await coordinator.CompileAsync();
        var d1 = await docs.ReadAsync("agents");
        Assert.Equal(Sha256Hex(v1), d1.ContentHash);
        Assert.Equal(d1.ContentHash, snap1.Documents["agents"]);

        // Same byte length in-place edit (both fresh mtime and backdated mtime) must bypass/invalidate cached hash immediately.
        var v2 = "schema_version = 1\nversion = 1\n# token-bbbb\n";
        Assert.Equal(v1.Length, v2.Length);
        await File.WriteAllTextAsync(agentsPath, v2);
        await coordinator.ReconcileAsync();
        var snap2 = await coordinator.CompileAsync();
        Assert.Equal(Sha256Hex(v2), snap2.Documents["agents"]);

        File.SetLastWriteTimeUtc(agentsPath, DateTime.UtcNow.AddMinutes(-4));
        await using var sp2 = BuildProjectionProvider(scopeRoot, dbPath, tenant);
        await sp2.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync();
        var snap3 = await sp2.GetRequiredService<IConfigurationProjectionCoordinator>().CompileAsync();
        Assert.Equal(Sha256Hex(v2), snap3.Documents["agents"]);
    }

    private static async Task<long> ReadScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed class ErrorLogCaptureProvider(System.Collections.Concurrent.ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, logs);
        public void Dispose() { }

        private sealed class CaptureLogger(string category, System.Collections.Concurrent.ConcurrentQueue<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => logs.Enqueue($"[{logLevel}:{category}] {formatter(state, exception)} {exception}");
        }
    }

    private static ServiceProvider BuildProjectionProvider(string scopeRoot, string databasePath, TenantContext tenant)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Provider"] = "Sqlite",
                ["TinadecPersistence:DataRoot"] = Path.Combine(scopeRoot, "data"),
                ["TinadecPersistence:Sqlite:DatabasePath"] = databasePath
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IScopeStorageLocations>(new StorageScopeDescriptor("project", "project", scopeRoot));
        services.AddSingleton<ITenantContextAccessor>(new FixedTenant(tenant));
        services.AddTinadecPersistence(config, scopeRoot);
        services.AddDbContextFactory<AgentConfigurationDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<AgentConfigurationDbContext>>();
        services.AddTinadecConfigurationFiles();
        return services.BuildServiceProvider();
    }

    private static async Task<string?> ReadProjectionStampAsync(string databasePath, string docId)
    {
        await using var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content_hash FROM _tinadec_projection_stamps WHERE document_id = @id;";
        cmd.Parameters.AddWithValue("@id", docId);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private sealed class FixedTenant(TenantContext current) : ITenantContextAccessor
    {
        public TenantContext Current => current;
    }

    private sealed class QuotaFailingChatFactory : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, BaseUrl = "http://localhost", Model = "quota-model", ApiKey = "k", ModelId = "openai/quota-model" });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient>(new ThrowingQuotaClient());

        private sealed class ThrowingQuotaClient : IChatClient
        {
            public ChatClientMetadata Metadata { get; } = new("fake-quota");
            public void Dispose() { }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("HTTP 403 (new_api_error: insufficient_user_quota)");
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }

    private sealed class SpyFollowCoordinator : IFullDuplexRunCoordinator
    {
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CancellationObserved => _cancelled.Task;

        public Task<RunSubmission> SubmitAsync(FullDuplexInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<RunStreamChunk> FollowAsync(
            Guid runId,
            Guid? turnId = null,
            long afterSequence = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new RunStreamChunk(runId, turnId ?? Guid.NewGuid(), null, 1, "ack", OccurredAt: DateTimeOffset.UtcNow);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancelled.TrySetResult();
                throw;
            }
        }

        public Task<RunControlResult> ControlAsync(Guid runId, RunControlCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SignalApiFactory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddDebug(); });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
