using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.AgentGraph;
using TinadecCore.DmaEA;
using TinadecCore.Governance;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.TinaChat;

namespace TinadecCore.Api.Tests;

public sealed class SessionScopeTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-transfer-tests", Guid.NewGuid().ToString("N"));
    private static readonly TenantContext Actor = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner");

    [Fact]
    public async Task TransferCopiesMixedGraphAndFrozenBytesThenDeletesOnlyTheSourceSession()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        var sessionId = Guid.NewGuid(); var otherSession = Guid.NewGuid(); var runId = Guid.NewGuid(); var actorId = Guid.NewGuid();
        var instanceId = Guid.NewGuid(); var requestId = Guid.NewGuid(); var organizationId = Guid.NewGuid(); var conversationId = Guid.NewGuid();
        var message = await fixture.PutAsync(fixture.Source, "message", "original message");
        var model = await fixture.PutAsync(fixture.Source, "model-config", "{\"model\":\"historic-model\"}");
        var frozenText = JsonSerializer.Serialize(new { schemaVersion = "frozen-run-configuration/v3", nested = model.Value, originalMode = Guid.NewGuid() });
        var frozen = await fixture.PutAsync(fixture.Source, "run_configuration", frozenText);
        var attachment = await fixture.PutAsync(fixture.Source, "attachment", "binary attachment bytes");
        await fixture.WithAsync<MemoryDbContext>(fixture.Source, db =>
        {
            db.Sessions.AddRange(new SessionRecord { Id = sessionId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, Title = "move", ModeVersionId = Guid.NewGuid(), MeetingModelOverrideProviderInstanceId = Guid.NewGuid(), MeetingModelOverrideModel = "old" },
                new SessionRecord { Id = otherSession, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, Title = "keep" });
            db.Messages.Add(new() { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SessionId = sessionId, RunId = runId, Sequence = 1, ContentReference = message.Value, ContentHash = message.Sha256, ContentLength = message.Length });
            db.MessageAttachments.Add(new() { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SessionId = sessionId, FileName = "file.txt", ContentReference = attachment.Value, ContentHash = attachment.Sha256, ContentLength = attachment.Length });
            db.MemoryItems.Add(new() { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, Kind = "shared" });
        });
        var controlId = Guid.NewGuid(); const string controlPayload = "{\"session_owned\":true}\n"; const string unrelatedPayload = "{\"other_session\":true}\n";
        var controlPath = Path.Combine(fixture.SourcePaths.Root, "events", "control.events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(controlPath)!);
        await File.WriteAllTextAsync(controlPath, unrelatedPayload + controlPayload);
        await fixture.WithAsync<LifecycleDbContext>(fixture.Source, db =>
        {
            db.Runs.Add(new() { Id = runId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SessionId = sessionId, Status = "completed", FrozenConfigurationReference = frozen.Value, FrozenConfigurationHash = frozen.Sha256, FrozenConfigurationLength = frozen.Length });
            db.RunConfigurationBindings.Add(new() { RunId = runId, TenantId = Actor.TenantId, ConfigurationKind = "mode", ConfigurationId = Guid.NewGuid(), ConfigurationVersionId = Guid.NewGuid(), ManifestHash = "retained" });
            db.ControlEventIndex.Add(new() { Id = controlId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, AggregateType = "run", AggregateId = runId, EventType = "test", RelativeFilePath = Path.Combine("events", "control.events.jsonl"), ByteOffset = Encoding.UTF8.GetByteCount(unrelatedPayload), ByteLength = Encoding.UTF8.GetByteCount(controlPayload), PayloadHash = Hash(controlPayload.TrimEnd('\n')) });
        });
        await fixture.WithAsync<AgentControlDbContext>(fixture.Source, db => db.Set<AgentInstanceRecord>().Add(new() { Id = instanceId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SessionId = sessionId, RunId = runId, Layer = "execution", Role = "worker" }));
        await fixture.WithAsync<GovernanceDbContext>(fixture.Source, db => db.PermissionRequests.Add(new() { Id = requestId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, RunId = runId, IdempotencyKey = "move-permission", Capability = "exec", Action = "exec", Resource = "test" }));
        await fixture.WithAsync<AgentGraphDbContext>(fixture.Source, db => db.ApprovalGates.Add(new() { Id = Guid.NewGuid(), TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, PermissionRequestId = requestId, RunId = runId }));
        await fixture.WithAsync<TinaChatDbContext>(fixture.Source, db =>
        {
            db.Participants.Add(new() { Id = actorId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, OwnerPrincipalId = Actor.PrincipalId, Handle = "shared-actor", DisplayName = "Shared Actor" });
            db.Organizations.Add(new() { Id = organizationId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SessionId = sessionId, HostParticipantId = actorId, HumanParticipantId = actorId });
            db.Conversations.Add(new() { Id = conversationId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, CreatorId = actorId, OrganizationId = organizationId, ClientRequestId = "move-room", Title = "room" });
            db.Members.Add(new() { ConversationId = conversationId, ParticipantId = actorId });
            var chatMessage = new ChatMessage { Id = Guid.NewGuid(), TenantId = Actor.TenantId, ConversationId = conversationId, SenderId = actorId, Sequence = 1, ClientMessageId = "one", ContentReference = message.Value, ContentHash = message.Sha256, ContentLength = message.Length };
            db.Messages.Add(chatMessage);
            db.Set<ChatAudience>().Add(new() { MessageId = chatMessage.Id, ParticipantId = actorId, CanReadOriginal = true });
        });
        var sourceGraph = await ScopeSessionDataGraph.ReadAsync(fixture.Source, sessionId);
        Assert.Equal(6, sourceGraph.Entities.Select(e => e.ContextType).Distinct().Count());
        var receipt = await fixture.Transfers.RequestAsync(sessionId, fixture.TargetScope.StorageId);
        Assert.Equal("pending", receipt.Status);
        await fixture.Transfers.ProcessPendingAsync();
        Assert.Equal("completed", fixture.Transfers.Get(receipt.TransferId).Status);
        await fixture.WithAsync<MemoryDbContext>(fixture.Source, db =>
        {
            Assert.DoesNotContain(db.Sessions, s => s.Id == sessionId); Assert.Contains(db.Sessions, s => s.Id == otherSession);
            Assert.Empty(db.Messages); Assert.Empty(db.MessageAttachments); Assert.Single(db.MemoryItems);
        }, save: false);
        await fixture.WithAsync<MemoryDbContext>(fixture.Target, db =>
        {
            var session = Assert.Single(db.Sessions);
            Assert.Equal(fixture.TargetScope.ProjectId, session.ProjectId); Assert.Equal(fixture.TargetModeId, session.ModeVersionId);
            Assert.Equal("project-director", session.ConversationTemplateSlug); Assert.Null(session.MeetingModelOverrideProviderInstanceId);
        }, save: false);
        await fixture.WithAsync<LifecycleDbContext>(fixture.Target, db => Assert.Equal(frozen.Value, Assert.Single(db.Runs).FrozenConfigurationReference), save: false);
        Assert.Equal(frozenText, await fixture.ReadAsync(fixture.Target, frozen));
        Assert.Equal("{\"model\":\"historic-model\"}", await fixture.ReadAsync(fixture.Target, model));
        Assert.Equal("binary attachment bytes", await fixture.ReadAsync(fixture.Target, attachment));
        await fixture.WithAsync<TinaChatDbContext>(fixture.Source, db => Assert.Single(db.Participants), save: false);
        await fixture.WithAsync<TinaChatDbContext>(fixture.Target, db => { Assert.Single(db.Participants); Assert.Single(db.Set<ChatAudience>()); }, save: false);
        await fixture.WithAsync<LifecycleDbContext>(fixture.Target, db =>
        {
            var index = Assert.Single(db.ControlEventIndex);
            Assert.Equal(0, index.ByteOffset); Assert.Equal(fixture.TargetScope.ProjectId, index.ProjectId);
            Assert.Equal(controlPayload, File.ReadAllText(Path.Combine(fixture.TargetPaths.Root, index.RelativeFilePath)));
        }, save: false);
        Assert.True(File.Exists(Path.Combine(fixture.TargetScope.Data, "transfers", receipt.TransferId.ToString("N"), "graph.toml")));
        Assert.True(File.Exists(Path.Combine(fixture.TargetScope.Data, "transfers", receipt.TransferId.ToString("N"), "configuration", "agents.toml")));
        // Restart cleanup is idempotent and keeps the other session and shared identities.
        await ScopeSessionDataGraph.DeleteAsync(fixture.Source, sourceGraph);
        await fixture.Transfers.ProcessPendingAsync();
        Assert.Equal(2, fixture.Registry.ExclusiveAcquisitions);
        await fixture.Transfers.PurgeCompletedAsync(sessionId, fixture.TargetScope.StorageId);
        Assert.False(Directory.Exists(Path.Combine(fixture.SourceScope.State, "transfers", receipt.TransferId.ToString("N"))));
        Assert.False(Directory.Exists(Path.Combine(fixture.TargetScope.Data, "transfers", receipt.TransferId.ToString("N"))));
        Assert.Throws<KeyNotFoundException>(() => fixture.Transfers.Get(receipt.TransferId));
    }

    [Fact]
    public async Task ActiveRunPersistsPendingAcrossRestartAndBlocksNewAdmissionUntilTerminal()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        var sessionId = Guid.NewGuid(); var runId = Guid.NewGuid();
        await fixture.SeedSessionAsync(sessionId, runId, "running");
        var receipt = await fixture.Transfers.RequestAsync(sessionId, fixture.TargetScope.StorageId);
        await fixture.Transfers.ProcessPendingAsync();
        Assert.Equal("pending", fixture.Transfers.Get(receipt.TransferId).Status);
        var editing = Assert.Throws<RunAdmissionException>(() => fixture.Transfers.AssertSessionWritable(sessionId, "user"));
        Assert.Equal("session_transfer_pending", editing.Code);
        fixture.Transfers.AssertSessionWritable(sessionId, "independent-third-scope");
        await using (await fixture.Transfers.AcquireAsync(sessionId, "independent-third-scope", true)) { }
        var restart = new SessionScopeTransferService(fixture.Registry, NullLogger<SessionScopeTransferService>.Instance);
        Assert.Equal(receipt.TransferId, restart.Get(receipt.TransferId).TransferId);
        var blocked = await Assert.ThrowsAsync<RunAdmissionException>(() => restart.AcquireAsync(sessionId, "user", true));
        Assert.Equal("session_transfer_pending", blocked.Code);
        await using (await restart.AcquireAsync(sessionId, "user", false)) { }
        await fixture.WithAsync<LifecycleDbContext>(fixture.Source, db => db.Runs.Single(r => r.Id == runId).Status = "completed");
        await restart.ProcessPendingAsync();
        Assert.Equal("completed", restart.Get(receipt.TransferId).Status);
        Assert.Equal("session_storage_changed", Assert.Throws<RunAdmissionException>(() => restart.AssertSessionWritable(sessionId, "user")).Code);
        restart.AssertSessionWritable(sessionId, fixture.TargetScope.StorageId);
        var moved = await Assert.ThrowsAsync<RunAdmissionException>(() => restart.AcquireAsync(sessionId, "user", true));
        Assert.Equal("session_storage_changed", moved.Code);
        await using (await restart.AcquireAsync(sessionId, fixture.TargetScope.StorageId, true)) { }
    }

    [Fact]
    public async Task AcceptedQueueMustDrainBeforeTransferAndItsFrozenSelectionIsUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        var sessionId = Guid.NewGuid(); var runId = Guid.NewGuid(); var directiveId = Guid.NewGuid();
        await fixture.SeedSessionAsync(sessionId, runId, "completed");
        const string accepted = "{\"mode_version_id\":\"accepted-before-transfer\"}";
        await fixture.WithAsync<LifecycleDbContext>(fixture.Source, db => db.RunDirectives.Add(new() { Id = directiveId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SessionId = sessionId, RunId = runId, Kind = "queued_interaction", Status = "queued", PayloadJson = accepted }));
        var rejected = await Assert.ThrowsAsync<RunAdmissionException>(() => fixture.Transfers.RequestAsync(sessionId, fixture.TargetScope.StorageId));
        Assert.Equal("queued_interactions_pending", rejected.Code);
        Assert.False(File.Exists(Path.Combine(fixture.SourceScope.State, "session-transfers.toml")));
        await fixture.WithAsync<LifecycleDbContext>(fixture.Source, db => { var row = db.RunDirectives.Single(); Assert.Equal(accepted, row.PayloadJson); row.Status = "drained"; });
        var receipt = await fixture.Transfers.RequestAsync(sessionId, fixture.TargetScope.StorageId);
        await fixture.Transfers.ProcessPendingAsync();
        Assert.Equal("completed", fixture.Transfers.Get(receipt.TransferId).Status);
        await fixture.WithAsync<LifecycleDbContext>(fixture.Target, db => Assert.Equal(accepted, Assert.Single(db.RunDirectives).PayloadJson), save: false);
    }

    [Fact]
    public async Task HostWorkspaceBinderLeavesAnActiveSessionInUserStorageUntilTransferCompletes()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        var sessionId = Guid.NewGuid(); var runId = Guid.NewGuid();
        await fixture.SeedSessionAsync(sessionId, runId, "running");
        var binder = new HostSessionWorkspaceBinder(fixture.Registry, fixture.Transfers);
        var binding = await binder.BindSessionToWorkspaceAsync(sessionId, "project", fixture.TargetScope.ProjectRoot!);
        Assert.Equal("pending", binding.TransferStatus); Assert.Equal(fixture.TargetScope.StorageId, binding.StorageId);
        await fixture.WithAsync<MemoryDbContext>(fixture.Source, db => Assert.Null(Assert.Single(db.Sessions).ProjectId), save: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectSessionWorkspaceBinder().BindSessionToWorkspaceAsync(sessionId, "other", Path.Combine(_root, "other")));
    }

    [Fact]
    public async Task RestartReplaysTheSameTargetFactsAfterAPartialModuleCommit()
    {
        await using var fixture = await Fixture.CreateAsync(_root);
        var sessionId = Guid.NewGuid(); var runId = Guid.NewGuid(); var requestId = Guid.NewGuid();
        await fixture.SeedSessionAsync(sessionId, runId, "completed");
        await fixture.WithAsync<GovernanceDbContext>(fixture.Source, db => db.PermissionRequests.Add(new() { Id = requestId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, RunId = runId, IdempotencyKey = "frozen-original", Resource = "original" }));
        await fixture.WithAsync<GovernanceDbContext>(fixture.Target, db => db.PermissionRequests.Add(new() { Id = requestId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, RunId = runId, IdempotencyKey = "collision", Resource = "different" }));
        var receipt = await fixture.Transfers.RequestAsync(sessionId, fixture.TargetScope.StorageId);
        await fixture.Transfers.ProcessPendingAsync();
        Assert.Equal("copying", fixture.Transfers.Get(receipt.TransferId).Status);
        Assert.NotNull(fixture.Transfers.Get(receipt.TransferId).Error);
        Assert.Equal("session_transfer_pending", Assert.Throws<RunAdmissionException>(() => fixture.Transfers.EnsurePurgeAllowed(sessionId, "user")).Code);
        Assert.Equal("session_transfer_pending", Assert.Throws<RunAdmissionException>(() => fixture.Transfers.EnsurePurgeAllowed(sessionId, fixture.TargetScope.StorageId)).Code);
        fixture.Transfers.EnsurePurgeAllowed(sessionId, "independent-third-scope");
        DateTimeOffset firstTimestamp = default;
        await fixture.WithAsync<MemoryDbContext>(fixture.Target, db => firstTimestamp = Assert.Single(db.Sessions).UpdatedAt, save: false);
        await fixture.WithAsync<MemoryDbContext>(fixture.Source, db => Assert.Null(Assert.Single(db.Sessions).ProjectId), save: false);
        await fixture.WithAsync<GovernanceDbContext>(fixture.Target, db => db.PermissionRequests.Remove(db.PermissionRequests.Single()));
        var restart = new SessionScopeTransferService(fixture.Registry, NullLogger<SessionScopeTransferService>.Instance);
        await restart.ProcessPendingAsync();
        Assert.Equal("completed", restart.Get(receipt.TransferId).Status);
        restart.EnsurePurgeAllowed(sessionId, fixture.TargetScope.StorageId);
        Assert.Equal("session_storage_changed", Assert.Throws<RunAdmissionException>(() => restart.EnsurePurgeAllowed(sessionId, "user")).Code);
        await fixture.WithAsync<MemoryDbContext>(fixture.Target, db => Assert.Equal(firstTimestamp, Assert.Single(db.Sessions).UpdatedAt), save: false);
        await fixture.WithAsync<MemoryDbContext>(fixture.Source, db => Assert.Empty(db.Sessions), save: false);
        await fixture.WithAsync<GovernanceDbContext>(fixture.Target, db => Assert.Equal("original", Assert.Single(db.PermissionRequests).Resource), save: false);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class Fixture : IAsyncDisposable
    {
        public ServiceProvider Source { get; private init; } = null!;
        public ServiceProvider Target { get; private init; } = null!;
        public StorageScopeDescriptor SourceScope { get; private init; } = null!;
        public StorageScopeDescriptor TargetScope { get; private init; } = null!;
        public StoragePaths SourcePaths => Source.GetRequiredService<StoragePaths>();
        public StoragePaths TargetPaths => Target.GetRequiredService<StoragePaths>();
        public TestRegistry Registry { get; private init; } = null!;
        public SessionScopeTransferService Transfers { get; private init; } = null!;
        public Guid TargetModeId { get; private init; }

        public static async Task<Fixture> CreateAsync(string root)
        {
            var sourceScope = new StorageScopeDescriptor("user", "user", Path.Combine(root, "user"));
            var targetScope = new StorageScopeDescriptor(Guid.NewGuid().ToString("N"), "project", Path.Combine(root, "project", ".tinadec"), Path.Combine(root, "project"), Guid.NewGuid());
            var source = await BuildAsync(sourceScope); var target = await BuildAsync(targetScope);
            var registry = new TestRegistry(sourceScope, source, targetScope, target);
            var fixture = new Fixture { Source = source, Target = target, SourceScope = sourceScope, TargetScope = targetScope, Registry = registry,
                Transfers = new(registry, NullLogger<SessionScopeTransferService>.Instance), TargetModeId = Guid.NewGuid() };
            var definitionId = Guid.NewGuid();
            await fixture.WithAsync<AgentConfigurationDbContext>(target, db =>
            {
                db.AgentDefinitions.Add(new() { Id = definitionId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, Slug = "project-director", Layer = "operation" });
                db.ModeVersions.Add(new() { Id = fixture.TargetModeId, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SnapshotJson = "{\"nodes\":[{\"node_key\":\"meeting\",\"agent_definition_id\":\"" + definitionId + "\",\"layer\":\"operation\"}]}" });
                db.WorkspaceDefaults.Add(new() { TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, DefaultModeVersionId = fixture.TargetModeId });
            });
            return fixture;
        }

        private static async Task<ServiceProvider> BuildAsync(StorageScopeDescriptor scope)
        {
            Directory.CreateDirectory(scope.Root);
            foreach (var (_, directory) in scope.Categories) Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(scope.Config, "agents.toml"), "# source configuration history\nschema_version = 1\n");
            var services = new ServiceCollection(); services.AddSingleton<IScopeStorageLocations>(scope); services.AddSingleton<ITenantContextAccessor>(new TenantAccessor());
            services.AddSingleton(new StoragePaths(scope.Root, Options.Create(new TinadecPersistenceOptions { DataRoot = scope.Data }), scope));
            services.AddSingleton<IContentStore, TestContentStore>();
            var connection = "Data Source=" + Path.Combine(scope.Data, "test.db");
            services.AddDbContextFactory<MemoryDbContext>(o => o.UseSqlite(connection)); services.AddDbContextFactory<LifecycleDbContext>(o => o.UseSqlite(connection));
            services.AddDbContextFactory<AgentControlDbContext>(o => o.UseSqlite(connection)); services.AddDbContextFactory<AgentGraphDbContext>(o => o.UseSqlite(connection));
            services.AddDbContextFactory<GovernanceDbContext>(o => o.UseSqlite(connection)); services.AddDbContextFactory<TinaChatDbContext>(o => o.UseSqlite(connection));
            services.AddDbContextFactory<AgentConfigurationDbContext>(o => o.UseSqlite(connection));
            // A real scope provider registers this from AddTinadecCore, and the target rebind
            // resolves it to pick the destination project's default mode. Without it the transfer
            // stops at "copying" with "No service for type GlobalAgentConfiguration", which is what
            // this fixture used to do.
            services.AddSingleton<GlobalAgentConfiguration>();
            var provider = services.BuildServiceProvider();
            await InitializeAsync<MemoryDbContext>(provider); await InitializeAsync<LifecycleDbContext>(provider); await InitializeAsync<AgentControlDbContext>(provider);
            await InitializeAsync<AgentGraphDbContext>(provider); await InitializeAsync<GovernanceDbContext>(provider); await InitializeAsync<TinaChatDbContext>(provider); await InitializeAsync<AgentConfigurationDbContext>(provider);
            return provider;
        }

        private static async Task InitializeAsync<T>(ServiceProvider provider) where T : DbContext
        { await using var db = await provider.GetRequiredService<IDbContextFactory<T>>().CreateDbContextAsync(); await DbContextSchemaBootstrapper.EnsureTablesAsync(db); }
        public async Task WithAsync<T>(ServiceProvider provider, Action<T> action, bool save = true) where T : DbContext
        { await using var db = await provider.GetRequiredService<IDbContextFactory<T>>().CreateDbContextAsync(); action(db); if (save) await db.SaveChangesAsync(); }
        public async Task SeedSessionAsync(Guid session, Guid run, string status)
        {
            await WithAsync<MemoryDbContext>(Source, db => db.Sessions.Add(new() { Id = session, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId }));
            await WithAsync<LifecycleDbContext>(Source, db => db.Runs.Add(new() { Id = run, TenantId = Actor.TenantId, WorkspaceId = Actor.WorkspaceId, SessionId = session, Status = status }));
        }
        public Task<ContentReference> PutAsync(ServiceProvider provider, string kind, string text) => provider.GetRequiredService<IContentStore>().PutAsync(new(Actor.TenantId, Actor.WorkspaceId, kind, "application/octet-stream", new MemoryStream(Encoding.UTF8.GetBytes(text))));
        public async Task<string> ReadAsync(ServiceProvider provider, ContentReference reference)
        { await using var input = await provider.GetRequiredService<IContentStore>().OpenReadAsync(reference); return await new StreamReader(input).ReadToEndAsync(); }
        public async ValueTask DisposeAsync() { await Source.DisposeAsync(); await Target.DisposeAsync(); }
    }

    private sealed class TestRegistry(StorageScopeDescriptor user, ServiceProvider source, StorageScopeDescriptor project, ServiceProvider target) : IStorageScopeRegistry
    {
        private readonly Dictionary<string, int> _leases = new();
        public int ExclusiveAcquisitions { get; private set; }
        public StorageScopeDescriptor User => user;
        public IReadOnlyList<StorageScopeDescriptor> List() => [user, project];
        public bool GetWritePolicy(string storageId) => false;
        public Task<StorageScopeDescriptor> OpenAsync(OpenStorageScopeRequest request, CancellationToken ct = default) => Task.FromResult(project);
        public Task<StorageRuntimeLease> AcquireAsync(string id, CancellationToken ct = default)
        { _leases[id] = _leases.GetValueOrDefault(id) + 1; return Task.FromResult(new StorageRuntimeLease(id == "user" ? user : project, id == "user" ? source : target, () => _leases[id]--)); }
        public Task<StorageRuntimeLease> AcquireExclusiveAsync(string id, CancellationToken ct = default)
        { Assert.Equal(0, _leases.GetValueOrDefault(id)); ExclusiveAcquisitions++; return Task.FromResult(new StorageRuntimeLease(id == "user" ? user : project, id == "user" ? source : target, () => { })); }
        public Task CloseAsync(string storageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWritePolicyAsync(string storageId, bool value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StorageScopeDescriptor> ConfigureAsync(string id, string backend, string? root, string? reference, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnregisterAsync(string storageId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TenantAccessor : ITenantContextAccessor { public TenantContext Current => Actor; }
    private sealed class TestContentStore(StoragePaths paths) : IContentStore
    {
        public async Task<ContentReference> PutAsync(ContentWriteRequest request, CancellationToken ct = default)
        {
            using var memory = new MemoryStream(); await request.Content.CopyToAsync(memory, ct); var bytes = memory.ToArray(); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var reference = paths.ContentReference(request.TenantId, request.WorkspaceId, request.Kind, hash); var destination = paths.ResolveContentReference(reference);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); await File.WriteAllBytesAsync(destination, bytes, ct); return new(reference, hash, bytes.Length, request.MediaType);
        }
        public Task<Stream> OpenReadAsync(ContentReference reference, CancellationToken ct = default) => Task.FromResult<Stream>(File.OpenRead(paths.ResolveContentReference(reference.Value)));
        public Task<bool> ExistsAsync(ContentReference reference, CancellationToken ct = default) => Task.FromResult(File.Exists(paths.ResolveContentReference(reference.Value)));
        public Task DeleteAsync(ContentReference reference, CancellationToken ct = default) { File.Delete(paths.ResolveContentReference(reference.Value)); return Task.CompletedTask; }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
