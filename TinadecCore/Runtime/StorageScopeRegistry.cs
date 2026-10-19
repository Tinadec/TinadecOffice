using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Abstractions;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Runtime;

public sealed record OpenStorageScopeRequest(string ProjectPath, string? Name = null, string? Backend = null,
    string? StorageRoot = null, string? PostgresConnectionReference = null,
    IReadOnlyList<WorkspaceSourceRoot>? Roots = null, string? PrimaryRootId = null,
    string Icon = "folder", string Color = "default");

public interface IStorageScopeRegistry
{
    StorageScopeDescriptor User { get; }
    IReadOnlyList<StorageScopeDescriptor> List();
    bool GetWritePolicy(string storageId);
    Task<StorageScopeDescriptor> OpenAsync(OpenStorageScopeRequest request, CancellationToken ct = default);
    Task<StorageRuntimeLease> AcquireAsync(string storageId, CancellationToken ct = default);
    Task<StorageRuntimeLease> AcquireExclusiveAsync(string storageId, CancellationToken ct = default);
    Task CloseAsync(string storageId, CancellationToken ct = default);
    Task SetWritePolicyAsync(string storageId, bool allowStorageWrite, CancellationToken ct = default);
    Task<StorageScopeDescriptor> ConfigureAsync(string storageId, string backend, string? storageRoot,
        string? postgresConnectionReference, CancellationToken ct = default);
    Task UnregisterAsync(string storageId, CancellationToken ct = default);
}

public sealed class StorageRuntimeLease : IAsyncDisposable
{
    private Action? _release;
    public StorageScopeDescriptor Descriptor { get; }
    public IServiceProvider Services { get; }
    internal StorageRuntimeLease(StorageScopeDescriptor descriptor, IServiceProvider services, Action release)
    { Descriptor = descriptor; Services = services; _release = release; }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _release, null)?.Invoke(); return ValueTask.CompletedTask; }
}

/// <summary>Host-owned mounts. Connections, singleton stores and hosted workers never change their scope.</summary>
public sealed partial class StorageScopeRegistry : IStorageScopeRegistry, IWorkspaceRegistry, IWorkspaceModelSourceRegistry, IAsyncDisposable
{
    private readonly IServiceProvider _host;
    private readonly IConfiguration _configuration;
    private readonly Action<IServiceCollection> _registerHttp;
    private readonly StorageScopeInitializer _initializer = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Runtime> _runtimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<Runtime>>> _mounts = new(StringComparer.Ordinal);
    private readonly object _mountGate = new();
    private readonly string _registryFile;
    private readonly TenantContext _identity;
    private readonly object _userGate = new();
    private int _userLeases;
    private bool _userMaintenance;
    private int _disposed;
    private Task? _disposeTask;
    public StorageScopeDescriptor User { get; }

    public StorageScopeRegistry(IServiceProvider host, IConfiguration configuration, Action<IServiceCollection> registerHttp)
    {
        _host = host; _configuration = configuration; _registerHttp = registerHttp;
        var locations = host.GetRequiredService<IScopeStorageLocations>();
        User = new StorageScopeDescriptor("user", "user", locations.Root,
            Backend: host.GetRequiredService<IDatabaseConnectionInfo>().ProviderName);
        _identity = host.GetRequiredService<ITenantContextAccessor>().Current;
        _registryFile = Path.Combine(User.State, "projects.toml");
        LoadRegistrations();
    }

    public IReadOnlyList<StorageScopeDescriptor> List() => [User, .. _registrations.Values
        .Where(x => x.OwnerPrincipalId == _identity.PrincipalId).Select(x => x.Scope).OrderBy(x => x.StorageId)];

    public bool GetWritePolicy(string storageId) => storageId != "user" && GetRegistration(storageId).AllowStorageWrite;

    public async Task<StorageScopeDescriptor> OpenAsync(OpenStorageScopeRequest request, CancellationToken ct = default)
    {
        var projectRoot = StorageScopePaths.NormalizeProjectRoot(request.ProjectPath);
        var requestedWorkspace = WorkspaceDefinitionFile.Validate(new WorkspaceDefinition(
            request.Name ?? Path.GetFileName(projectRoot), request.Roots ?? [new("primary", projectRoot)],
            request.PrimaryRootId ?? "primary", request.Icon, request.Color));
        foreach (var root in requestedWorkspace.Roots)
            if (List().Any(scope => IsWithinStorage(scope.Root, root.Path)))
                throw new ArgumentException("A source folder cannot be inside product storage: " + root.Path);
        if (request.StorageRoot is not null) ValidateDistinctStorageRoot(request.StorageRoot, null);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        StorageScopeDescriptor scope;
        try
        {
            var existing = _registrations.Values.FirstOrDefault(x => x.OwnerPrincipalId == _identity.PrincipalId
                && (StorageScopeInitializer.SamePath(x.Scope.ProjectRoot!, projectRoot)
                    || (x.Roots ?? []).Any(root => StorageScopeInitializer.SamePath(root.Path, projectRoot))));
            if (existing is not null)
            {
                if (request.Backend is not null && request.Backend != existing.Scope.Backend
                    || request.StorageRoot is not null && !StorageScopeInitializer.SamePath(request.StorageRoot, existing.Scope.Root))
                    throw new InvalidOperationException("Use the storage configuration action to change an existing scope.");
                scope = existing.Scope;
                // Opening existing data never rewrites its configuration. Explicitly selected roots
                // may rebind a copied/imported manifest, but must match the displayed definition.
                if (request.Roots is not null)
                {
                    var saved = WorkspaceDefinitionFile.Read(scope, existing.Name);
                    RequireSameRoots(saved, requestedWorkspace);
                    _registrations[scope.StorageId] = existing with { Roots = saved.Roots, PrimaryRootId = saved.PrimaryRootId,
                        HistoricalRoots = MergeHistory(HistoricalRoots(existing), saved.Roots) };
                    await SaveRegistrationsAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                var localRoot = Path.Combine(projectRoot, ".tinadec");
                var manifest = Directory.Exists(localRoot) ? StorageScopeInitializer.ReadManifest("new", projectRoot, localRoot) : null;
                var moved = manifest is null ? null : _registrations.Values.FirstOrDefault(x => x.OwnerPrincipalId == _identity.PrincipalId
                    && x.Scope.ProjectId == manifest.ProjectId && !Directory.Exists(x.Scope.ProjectRoot));
                if (moved is not null)
                {
                    await CloseAsync(moved.Scope.StorageId, ct).ConfigureAwait(false);
                    scope = manifest! with { StorageId = moved.Scope.StorageId };
                    var definition = WorkspaceDefinitionFile.Read(scope, moved.Name);
                    RequireSelectedRoots(definition, requestedWorkspace, request.Roots is not null, projectRoot);
                    _registrations[scope.StorageId] = moved with { Scope = scope, Roots = definition.Roots, PrimaryRootId = definition.PrimaryRootId,
                        HistoricalRoots = MergeHistory(HistoricalRoots(moved), definition.Roots) };
                }
                else
                {
                    if (manifest is null && !StorageScopeInitializer.SamePath(projectRoot, requestedWorkspace.PrimaryPath))
                        throw new ArgumentException("A new workspace's project_path must be its primary source folder.");
                    scope = await _initializer.InitializeProjectAsync(Guid.NewGuid().ToString("N"), projectRoot, User,
                        request.Backend, request.StorageRoot, request.PostgresConnectionReference, ct,
                        workspace: requestedWorkspace).ConfigureAwait(false);
                    var definition = WorkspaceDefinitionFile.Read(scope, requestedWorkspace.Name);
                    RequireSelectedRoots(definition, requestedWorkspace, request.Roots is not null, projectRoot);
                    // A workspace initialized here runs on the user's models; a project that arrives with
                    // its own .tinadec configuration keeps it until switched in the model center.
                    _registrations[scope.StorageId] = new(scope, _identity.PrincipalId, false, definition.Name,
                        definition.Roots, definition.PrimaryRootId, FollowUserModels: manifest is null);
                }
                await SaveRegistrationsAsync(ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
        await using var lease = await AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
        return scope;
    }

    public async Task<StorageRuntimeLease> AcquireAsync(string storageId, CancellationToken ct = default)
        => await AcquireCoreAsync(storageId, allowClosing: false, ct).ConfigureAwait(false);

    public Task<StorageRuntimeLease> AcquireRunControlAsync(string storageId, CancellationToken ct) => AcquireCoreAsync(storageId, allowClosing: true, ct);

    private async Task<StorageRuntimeLease> AcquireCoreAsync(string storageId, bool allowClosing, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (storageId == "user")
        {
            lock (_userGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_userMaintenance) throw new InvalidOperationException("User storage is undergoing maintenance.");
                _userLeases++;
            }
            return new(User, _host, () => { lock (_userGate) _userLeases--; });
        }
        var registration = GetRegistration(storageId);
        Lazy<Task<Runtime>> lazy;
        Task<Runtime> mounting;
        lock (_mountGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lazy = _mounts.GetOrAdd(storageId, _ => new Lazy<Task<Runtime>>(() => MountAsync(registration), LazyThreadSafetyMode.ExecutionAndPublication));
            mounting = lazy.Value;
        }
        Runtime runtime;
        try { runtime = await mounting.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted) _mounts.TryRemove(new KeyValuePair<string, Lazy<Task<Runtime>>>(storageId, lazy));
            throw;
        }
        lock (runtime.Gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref runtime.Disposed) != 0, this);
            if (runtime.Closing && !allowClosing || runtime.Maintenance) throw new InvalidOperationException("Storage scope is closing or undergoing maintenance.");
            runtime.Leases++;
        }
        return new(runtime.Scope, runtime.Provider, () => { lock (runtime.Gate) runtime.Leases--; });
    }

    public async Task<StorageRuntimeLease> AcquireExclusiveAsync(string storageId, CancellationToken ct = default)
    {
        if (storageId == "user")
        {
            lock (_userGate)
            { if (_userLeases != 0 || _userMaintenance) throw new InvalidOperationException("Storage has active requests or streams."); _userMaintenance = true; }
            return new(User, _host, () => { lock (_userGate) _userMaintenance = false; });
        }
        await using (var mounted = await AcquireAsync(storageId, ct).ConfigureAwait(false)) { }
        if (!_runtimes.TryGetValue(storageId, out var runtime)) throw new InvalidOperationException("Storage closed before maintenance.");
        lock (runtime.Gate)
        {
            if (runtime.Leases != 0 || runtime.Closing || runtime.Maintenance) throw new InvalidOperationException("Storage has active requests or streams.");
            runtime.Maintenance = true;
        }
        return new(runtime.Scope, runtime.Provider, () => { lock (runtime.Gate) runtime.Maintenance = false; });
    }

    public async Task CloseAsync(string storageId, CancellationToken ct = default)
    {
        if (storageId == "user") throw new InvalidOperationException("The user storage scope closes with the host.");
        GetRegistration(storageId);
        if (!_runtimes.TryGetValue(storageId, out var runtime)) return;
        lock (runtime.Gate)
        {
            if (runtime.Closing || runtime.Maintenance) throw new InvalidOperationException("Storage scope is already closing or undergoing maintenance.");
            runtime.Closing = true;
        }
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool released;
                lock (runtime.Gate) released = runtime.Leases == 0;
                if (released)
                {
                    await using var db = await runtime.Provider.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync(ct).ConfigureAwait(false);
                    var statuses = await db.Runs.AsNoTracking().Select(x => x.Status).ToListAsync(ct).ConfigureAwait(false);
                    if (statuses.All(RunStatusMachine.IsTerminal)
                        && !await db.UserToolActions.AnyAsync(action => action.Status == "executing", ct).ConfigureAwait(false)
                        && (runtime.Provider.GetService<IContentLeaseRegistry>()?.Count ?? 0) == 0)
                    {
                        lock (runtime.Gate)
                        {
                            if (runtime.Leases != 0) continue;
                            runtime.Maintenance = true; // Final fence: no late control lease can enter after the last check.
                        }
                        break;
                    }
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch { lock (runtime.Gate) { runtime.Closing = false; runtime.Maintenance = false; } throw; }
        await StopRuntimeAsync(runtime, ct).ConfigureAwait(false);
        _mounts.TryRemove(storageId, out _);
        _runtimes.TryRemove(new KeyValuePair<string, Runtime>(storageId, runtime));
    }

    internal async Task CloseForMaintenanceAsync(StorageRuntimeLease lease, CancellationToken ct)
    {
        if (!_runtimes.TryGetValue(lease.Descriptor.StorageId, out var runtime) || !ReferenceEquals(lease.Services, runtime.Provider))
            throw new InvalidOperationException("The storage maintenance lease no longer owns its runtime.");
        lock (runtime.Gate)
        {
            if (!runtime.Maintenance || runtime.Leases != 0 || runtime.Closing) throw new InvalidOperationException("Storage maintenance ownership changed.");
            runtime.Closing = true;
        }
        await StopRuntimeAsync(runtime, ct).ConfigureAwait(false);
        _mounts.TryRemove(runtime.Scope.StorageId, out _);
        _runtimes.TryRemove(new KeyValuePair<string, Runtime>(runtime.Scope.StorageId, runtime));
    }

    public bool FollowsUserModels(string storageId) => storageId == "user" || GetRegistration(storageId).FollowUserModels;

    public async Task SetFollowsUserModelsAsync(string storageId, bool followUser, CancellationToken ct = default)
    {
        if (storageId == "user") throw new ArgumentException("The user scope is the source of user models.");
        if (GetRegistration(storageId).FollowUserModels == followUser) return;
        // The service graph is built per mount, so the change takes effect on a fresh mount. Work in
        // flight keeps the runtime open; rather than wait on it, the switch is refused.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            try { await CloseAsync(storageId, wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new InvalidOperationException("The workspace still has work in flight. Stop its runs before switching its model source."); }
        }
        await SetFollowFlagAsync(storageId, followUser, ct).ConfigureAwait(false);
        try { await using var _ = await AcquireAsync(storageId, ct).ConfigureAwait(false); }
        catch
        {
            // The chosen source does not project into this workspace: keep the one that worked.
            await SetFollowFlagAsync(storageId, !followUser, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SetFollowFlagAsync(string storageId, bool followUser, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _registrations[storageId] = GetRegistration(storageId) with { FollowUserModels = followUser };
            await SaveRegistrationsAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetWritePolicyAsync(string storageId, bool allowStorageWrite, CancellationToken ct = default)
    {
        if (storageId == "user") throw new ArgumentException("Project write policy requires a project scope.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var registration = GetRegistration(storageId);
            _registrations[storageId] = registration with { AllowStorageWrite = allowStorageWrite };
            if (_runtimes.TryGetValue(storageId, out var runtime)) runtime.Policy.Set(allowStorageWrite);
            await SaveRegistrationsAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<StorageScopeDescriptor> ConfigureAsync(string storageId, string backend, string? storageRoot,
        string? postgresConnectionReference, CancellationToken ct = default)
    {
        if (storageId == "user")
        {
            await StorageMaintenanceService.RequireIdleAsync(_host, ct).ConfigureAwait(false);
            backend = StorageScopeInitializer.ValidateBackend(backend);
            var selected = User with { Root = storageRoot is null ? User.Root : Path.GetFullPath(storageRoot) };
            if (backend == "postgresql")
            {
                var paths = new StoragePaths(selected.Root, Microsoft.Extensions.Options.Options.Create(new TinadecPersistenceOptions { DataRoot = selected.Data }), selected);
                var secrets = StorageScopeInitializer.SamePath(selected.Root, User.Root) ? _host.GetRequiredService<ISecretStore>()
                    : SecretStoreFactory.Resolve(_host.GetRequiredService<Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions>>().Value.SecretStore, paths);
                if (postgresConnectionReference is null || await secrets.GetAsync(postgresConnectionReference, ct).ConfigureAwait(false) is null)
                    throw new InvalidOperationException("Bind the PostgreSQL connection reference in the selected user root's security store before selecting it. Credentials are not moved automatically.");
            }
            StorageScopeInitializer.EnsureUser(selected);
            var file = Path.Combine(selected.Config, "storage.toml");
            var text = File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : "version = 1\n[storage]\n";
            text = StorageConfigurationText.Set(text, "backend", backend);
            text = StorageConfigurationText.Set(text, "postgres_connection_reference", postgresConnectionReference);
            await AtomicWriteAsync(file, text, ct).ConfigureAwait(false);
            return User;
        }
        var registration = GetRegistration(storageId);
        backend = StorageScopeInitializer.ValidateBackend(backend);
        if (backend == "postgresql" && (postgresConnectionReference is null
            || await _host.GetRequiredService<ISecretStore>().GetAsync(postgresConnectionReference, ct).ConfigureAwait(false) is null))
            throw new InvalidOperationException("Bind the PostgreSQL connection reference in the user security store before selecting it.");
        if (_runtimes.TryGetValue(storageId, out var mounted)) await EnsureIdleAsync(mounted, ct).ConfigureAwait(false);
        await CloseAsync(storageId, ct).ConfigureAwait(false);
        var newRoot = storageRoot is null ? registration.Scope.Root : Path.GetFullPath(storageRoot);
        ValidateDistinctStorageRoot(newRoot, storageId);
        backend = StorageScopeInitializer.ValidateBackend(backend);
        StorageScopeDescriptor scope;
        if (StorageScopeInitializer.SamePath(newRoot, registration.Scope.Root))
        {
            scope = registration.Scope with { Backend = backend, PostgresConnectionReference = postgresConnectionReference };
            var storageFile = Path.Combine(scope.Config, "storage.toml");
            StorageScopePaths.RejectLinks(scope.Root, storageFile);
            var storageText = await File.ReadAllTextAsync(storageFile, ct).ConfigureAwait(false);
            storageText = StorageConfigurationText.Set(storageText, "backend", backend);
            storageText = StorageConfigurationText.Set(storageText, "postgres_connection_reference", postgresConnectionReference);
            await AtomicWriteAsync(storageFile, storageText, ct).ConfigureAwait(false);
            var manifestPath = Path.Combine(newRoot, "project.toml");
            StorageScopePaths.RejectLinks(newRoot, manifestPath);
            var manifest = TomlSerializer.Deserialize<TomlTable>(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false))!;
            manifest["backend"] = backend;
            manifest.Remove("postgres_connection_reference");
            if (postgresConnectionReference is not null) manifest["postgres_connection_reference"] = postgresConnectionReference;
            await AtomicWriteAsync(manifestPath, TomlSerializer.Serialize(manifest), ct).ConfigureAwait(false);
        }
        else scope = await _initializer.InitializeProjectAsync(storageId, registration.Scope.ProjectRoot!, User,
            backend, newRoot, postgresConnectionReference, ct, registration.Scope.ProjectId,
            workspace: ReadWorkspace(storageId, requireAvailable: true)).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { _registrations[storageId] = registration with { Scope = scope }; await SaveRegistrationsAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
        await using var lease = await AcquireAsync(storageId, ct).ConfigureAwait(false);
        return scope;
    }

    public async Task UnregisterAsync(string storageId, CancellationToken ct = default)
    {
        await CloseAsync(storageId, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { _registrations.TryRemove(storageId, out _); await SaveRegistrationsAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<Runtime> MountAsync(Registration registration)
    {
        var scope = registration.Scope;
        if (!Directory.Exists(scope.ProjectRoot)) throw new DirectoryNotFoundException("The registered project directory is unavailable.");
        var workspace = ReadWorkspace(scope.StorageId);
        var manifest = StorageScopeInitializer.ReadManifest(scope.StorageId, scope.ProjectRoot!, scope.Root, external: scope.External);
        if (manifest.ProjectId != scope.ProjectId || manifest.Backend != scope.Backend
            || manifest.PostgresConnectionReference != scope.PostgresConnectionReference)
            throw new InvalidDataException("The project manifest differs from its host registration. Apply the storage configuration through the trusted host before reopening.");
        var configValues = new Dictionary<string, string?>
        {
            ["TinadecPersistence:DataRoot"] = scope.Data,
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(scope.Data, "tinadec.db"),
            ["TinadecPersistence:Provider"] = scope.Backend == "postgresql" ? "PostgreSql" : "Sqlite",
            ["TinadecPersistence:PostgreSql:Schema"] = "tinadec_" + scope.StorageId,
            ["TinadecAgent:ProfileConfigPath"] = Path.Combine(scope.Config, "runtime.toml"),
            ["TinadecTools:DefaultWorkspaceRoot"] = scope.ProjectRoot
        };
        if (scope.Backend == "postgresql")
        {
            var reference = scope.PostgresConnectionReference ?? throw new InvalidOperationException("PostgreSQL requires a user security-store connection reference.");
            var connection = await _host.GetRequiredService<ISecretStore>().GetAsync(reference).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The PostgreSQL connection reference is not bound on this machine.");
            configValues["ConnectionStrings:TinadecStorage"] = connection;
            configValues["TinadecPersistence:PostgreSql:ConnectionStringName"] = "TinadecStorage";
        }
        var configuration = new ConfigurationBuilder().AddConfiguration(_configuration).AddInMemoryCollection(configValues).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(builder => builder.AddProvider(new ScopeDiagnosticLoggerProvider(scope))));
        if (_host.GetService<IHostEnvironment>() is { } environment) services.AddSingleton(environment);
        if (_host.GetService<IHostApplicationLifetime>() is { } lifetime) services.AddSingleton(lifetime);
        services.AddSingleton<IScopeStorageLocations>(scope);
        services.AddSingleton<IStorageScopeRegistry>(this);
        services.AddSingleton<IWorkspaceRegistry>(this);
        services.AddSingleton<IWorkspaceDefinitionProvider>(new RegisteredWorkspace(this, scope.StorageId));
        services.AddSingleton<IProtectedStorageRoots>(new HostProtectedRoots(this, scope.StorageId));
        services.AddTinadecPersistence(configuration, scope.Root);
        services.AddTinadecCore();
        _registerHttp(services);
        // Agent packs are installed once in the user scope. Project runtimes keep
        // project sessions/runs but resolve pack modes from this host-owned source.
        services.Replace(ServiceDescriptor.Singleton(_host.GetRequiredService<GlobalAgentConfiguration>()));
        var policy = new HostProjectWritePolicy(registration.AllowStorageWrite);
        services.Replace(ServiceDescriptor.Singleton<IProjectStorageWritePolicy>(policy));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(_host.GetRequiredService<ISecretStore>()));
        services.Replace(ServiceDescriptor.Singleton<ITenantContextAccessor>(new HostTenantContext(_identity)));
        services.Replace(ServiceDescriptor.Singleton<ISessionWorkspaceBinder, ProjectSessionWorkspaceBinder>());
        if (_host.GetService<ISessionStorageAdmissionGuard>() is { } transferGuard)
            services.Replace(ServiceDescriptor.Singleton<ISessionStorageAdmissionGuard>(transferGuard));
        if (_host.GetService<ISessionScopeTransferService>() is { } transfers)
            services.AddSingleton<ISessionScopeTransferService>(transfers);
        if (registration.FollowUserModels) services.AddSingleton<IConfigurationDocumentRedirects>(new FollowUserModelsRedirect(User));
        services.AddTinadecConfigurationFiles();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var runtime = new Runtime(scope, provider, policy);
        try
        {
            var lockPath = Path.Combine(scope.State, "host.lock");
            StorageScopePaths.RejectLinks(scope.Root, lockPath);
            runtime.HostLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await provider.GetRequiredService<IStorageMigrationRunner>().RunAsync().ConfigureAwait(false);
            await provider.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync().ConfigureAwait(false);
            var store = provider.GetRequiredService<ProjectSessionStore>();
            // Lifecycle status does not decide whether the scope already owns its project.
            // Archived and trashed projects retain their identity and must remain mountable.
            var project = await store.GetProjectAnyStatusAsync(scope.ProjectId!.Value).ConfigureAwait(false);
            if (project is null)
                await store.CreateProjectAsync(workspace.Name, workspace.PrimaryPath, stableProjectId: scope.ProjectId).ConfigureAwait(false);
            else await store.RebindProjectRootAsync(scope.ProjectId!.Value, workspace.PrimaryPath).ConfigureAwait(false);
            // Opening imported data does not authorize a recovered mutation. Recovery retains the existing approval gates.
            await provider.GetRequiredService<StorageLifecycleService>().ReconcileAsync().ConfigureAwait(false);
            await using (var recoveryScope = provider.CreateAsyncScope())
                await recoveryScope.ServiceProvider.GetRequiredService<ProjectSessionLifecycleService>().RecoverPendingPurgesAsync().ConfigureAwait(false);
            await provider.GetRequiredService<RecoveryCoordinator>().RunStartupPassesAsync().ConfigureAwait(false);
            foreach (var worker in provider.GetServices<IHostedService>())
            {
                await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                runtime.Workers.Add(worker);
            }
            _runtimes[scope.StorageId] = runtime;
            return runtime;
        }
        catch { await StopRuntimeAsync(runtime, CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private Registration GetRegistration(string storageId)
    {
        if (!_registrations.TryGetValue(storageId, out var registration) || registration.OwnerPrincipalId != _identity.PrincipalId)
            throw new KeyNotFoundException("Storage scope is not registered for this principal.");
        return registration;
    }

    private void ValidateDistinctStorageRoot(string root, string? currentStorageId)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var other in List().Where(scope => scope.StorageId != currentStorageId))
        {
            var otherRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.Root));
            if (otherRoot.Equals(root, comparison) || otherRoot.StartsWith(root + Path.DirectorySeparatorChar, comparison)
                || root.StartsWith(otherRoot + Path.DirectorySeparatorChar, comparison) && other.StorageId != "user")
                throw new InvalidOperationException("A project storage root cannot overlap another registered storage scope: " + other.StorageId);
        }
    }

    private static async Task EnsureIdleAsync(Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.Provider.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync(ct).ConfigureAwait(false);
        var statuses = await db.Runs.AsNoTracking().Select(x => x.Status).ToListAsync(ct).ConfigureAwait(false);
        if (statuses.Any(x => !RunStatusMachine.IsTerminal(x))) throw new InvalidOperationException("Storage has active runs; finish or stop them first.");
    }

    private void LoadRegistrations()
    {
        if (!File.Exists(_registryFile)) return;
        StorageScopePaths.RejectLinks(User.Root, _registryFile);
        var table = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(_registryFile))!;
        if (Convert.ToInt64(StorageScopeInitializer.Value(table, "schema_version") ?? 0L) != 1)
            throw new InvalidDataException("Unsupported host project registry schema.");
        if (StorageScopeInitializer.Value(table, "projects") is not TomlTableArray projects) return;
        foreach (var item in projects)
        {
            var id = item["storage_id"].ToString()!;
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid registered storage identifier.");
            var scope = new StorageScopeDescriptor(id, "project", Path.GetFullPath(item["storage_root"].ToString()!),
                Path.GetFullPath(item["project_root"].ToString()!), Guid.Parse(item["project_id"].ToString()!),
                StorageScopeInitializer.ValidateBackend(item["backend"].ToString()!), Convert.ToBoolean(item["external"]),
                StorageScopeInitializer.Value(item, "postgres_connection_reference")?.ToString());
            var roots = StorageScopeInitializer.Value(item, "authorized_roots") is TomlTableArray rows
                ? rows.Select(row => new WorkspaceSourceRoot(row["id"].ToString()!, Path.GetFullPath(row["path"].ToString()!))).ToArray() : null;
            _registrations[id] = new(scope, Guid.Parse(item["owner_principal_id"].ToString()!),
                Convert.ToBoolean(StorageScopeInitializer.Value(item, "allow_storage_write") ?? false), item["name"].ToString()!,
                roots, StorageScopeInitializer.Value(item, "primary_root_id")?.ToString(),
                StorageScopeInitializer.Value(item, "historical_roots") is TomlTableArray historical
                    ? historical.Select(row => new WorkspaceSourceRoot(row["id"].ToString()!, Path.GetFullPath(row["path"].ToString()!))).ToArray() : null,
                Convert.ToBoolean(StorageScopeInitializer.Value(item, "follow_user_models") ?? false));
        }
    }

    private Task SaveRegistrationsAsync(CancellationToken ct)
    {
        var entries = new TomlTableArray();
        foreach (var registration in _registrations.Values.OrderBy(x => x.Scope.StorageId))
        {
            var scope = registration.Scope;
            var entry = new TomlTable
            {
                ["storage_id"] = scope.StorageId, ["storage_root"] = scope.Root, ["project_root"] = scope.ProjectRoot!,
                ["project_id"] = scope.ProjectId!.Value.ToString("D"), ["backend"] = scope.Backend, ["external"] = scope.External,
                ["owner_principal_id"] = registration.OwnerPrincipalId.ToString("D"), ["allow_storage_write"] = registration.AllowStorageWrite,
                ["name"] = registration.Name
            };
            if (scope.PostgresConnectionReference is { } reference) entry["postgres_connection_reference"] = reference;
            if (registration.FollowUserModels) entry["follow_user_models"] = true;
            if (registration.Roots is { } roots)
            {
                entry["primary_root_id"] = registration.PrimaryRootId!;
                var grants = new TomlTableArray();
                foreach (var root in roots) grants.Add(new TomlTable { ["id"] = root.Id, ["path"] = root.Path });
                entry["authorized_roots"] = grants;
            }
            var historical = new TomlTableArray();
            foreach (var root in HistoricalRoots(registration)) historical.Add(new TomlTable { ["id"] = root.Id, ["path"] = root.Path });
            entry["historical_roots"] = historical;
            entries.Add(entry);
        }
        return AtomicWriteAsync(_registryFile, TomlSerializer.Serialize(new TomlTable { ["schema_version"] = 1, ["projects"] = entries }), ct);
    }

    internal static async Task AtomicWriteAsync(string path, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, text, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Task StopRuntimeAsync(Runtime runtime, CancellationToken ct)
    {
        lock (runtime.Gate)
        {
            Volatile.Write(ref runtime.Disposed, 1);
            return runtime.StopTask ??= StopRuntimeCoreAsync(runtime);
        }
    }

    private static async Task StopRuntimeCoreAsync(Runtime runtime)
    {
        List<Exception>? failures = null;
        foreach (var worker in runtime.Workers.AsEnumerable().Reverse())
        {
            try { await worker.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        var connection = runtime.Provider.GetRequiredService<IDatabaseConnectionInfo>();
        try { await runtime.Provider.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            runtime.HostLock?.Dispose();
            if (connection.Provider == DatabaseProvider.Sqlite && connection.ConnectionString is { } text)
            {
                using var database = new Microsoft.Data.Sqlite.SqliteConnection(text);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(database);
                // Vector stores have their own SQLite pools and must release owned handles
                // before a move or explicit storage deletion can succeed on Windows.
                var vectors = Path.Combine(runtime.Scope.Data, "vectors");
                foreach (var file in StorageMaintenanceService.OwnedFiles(runtime.Scope.Root, vectors, CancellationToken.None).Where(file => file.Extension == ".db"))
                {
                    using var vector = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file.FullName }.ToString());
                    Microsoft.Data.Sqlite.SqliteConnection.ClearPool(vector);
                }
            }
        }
        if (failures is not null) throw new AggregateException("Storage workers failed during shutdown after their resources were released.", failures);
    }

    public ValueTask DisposeAsync()
    {
        lock (_mountGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            Volatile.Write(ref _disposed, 1);
            var pending = _mounts.Values.Where(mount => mount.IsValueCreated).Select(mount => mount.Value).ToArray();
            _disposeTask = DisposeCoreAsync(pending);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task<Runtime>[] pending)
    {
        foreach (var mount in pending)
            try { await mount.ConfigureAwait(false); } catch { /* Failed mounts already dispose their graph. */ }
        foreach (var runtime in _runtimes.Values) await StopRuntimeAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        _runtimes.Clear(); _mounts.Clear(); _gate.Dispose();
    }

    private sealed record Registration(StorageScopeDescriptor Scope, Guid OwnerPrincipalId, bool AllowStorageWrite, string Name,
        IReadOnlyList<WorkspaceSourceRoot>? Roots = null, string? PrimaryRootId = null, IReadOnlyList<WorkspaceSourceRoot>? HistoricalRoots = null,
        bool FollowUserModels = false);

    /// <summary>The workspace's models.toml is the user scope's file (see <see cref="IConfigurationDocumentRedirects"/>).</summary>
    private sealed class FollowUserModelsRedirect(StorageScopeDescriptor user) : IConfigurationDocumentRedirects
    {
        public IScopeStorageLocations? For(string documentId) => documentId == "models" ? user : null;
    }
    private sealed class HostTenantContext(TenantContext identity) : ITenantContextAccessor { public TenantContext Current => identity; }
    private sealed class HostProjectWritePolicy(bool allow) : IProjectStorageWritePolicy
    {
        private int _allow = allow ? 1 : 0;
        public bool AllowStorageWrite => Volatile.Read(ref _allow) != 0;
        public void Set(bool value) => Interlocked.Exchange(ref _allow, value ? 1 : 0);
    }
    private sealed class Runtime(StorageScopeDescriptor scope, ServiceProvider provider, HostProjectWritePolicy policy)
    {
        public StorageScopeDescriptor Scope { get; } = scope;
        public ServiceProvider Provider { get; } = provider;
        public HostProjectWritePolicy Policy { get; } = policy;
        public object Gate { get; } = new();
        public int Leases;
        public bool Closing;
        public bool Maintenance;
        public int Disposed;
        public Task? StopTask;
        public List<IHostedService> Workers { get; } = [];
        public FileStream? HostLock;
    }
}
