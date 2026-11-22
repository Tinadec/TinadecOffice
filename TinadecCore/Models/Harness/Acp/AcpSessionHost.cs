using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TinadecCore.Persistence;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Opens, caches, and tears down hosted ACP sessions. Single-agent runs share one live session per
/// provider instance; Swarm teammates are keyed by <see cref="AcpSessionKey"/> (provider + run +
/// teammate handle) so concurrent teammates using the same CLI/ACP provider run in separate
/// subprocesses with isolated worktree <c>cwd</c>s and independent turn leases.
/// </summary>
internal interface IAcpSessionHost : IAsyncDisposable
{
    /// <summary>
    /// The governed working directory for one provider instance, created on first use and stable
    /// afterwards. A harness reads and writes its <c>cwd</c> with its own tools regardless of declared
    /// capabilities, so this directory — not a capability flag — is the boundary.
    /// <para>
    /// This is a delegate to <see cref="IHarnessWorkspaceRoots"/>, kept on the session port because ACP
    /// callers already hold the host. It is deliberately the same object the headless channel asks, so
    /// a provider does not end up with two workspaces depending on which channel a run chose.
    /// </para>
    /// </summary>
    string ScratchDirectoryFor(Guid providerInstanceId);

    /// <summary>
    /// Governed working directory scoped to a specific Swarm teammate within a run. Defaults to
    /// <see cref="ScratchDirectoryFor(Guid)"/> for single-agent callers and test doubles.
    /// </summary>
    string ScratchDirectoryFor(Guid providerInstanceId, string? runId, string? teammateHandle) =>
        ScratchDirectoryFor(providerInstanceId);

    Task<IAcpAgentSession> AcquireAsync(AcpSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes turns for one provider instance. ACP gives a session exactly one turn in flight, so
    /// two runs that share a harness provider must queue rather than watch the second one fail with
    /// <c>already has a turn in flight</c>.
    /// </summary>
    Task<IDisposable> AcquireTurnLeaseAsync(Guid providerInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes turns for one scoped <see cref="AcpSessionKey"/> so distinct Swarm teammates sharing
    /// a provider do not block each other's turn leases.
    /// </summary>
    Task<IDisposable> AcquireTurnLeaseAsync(AcpSessionKey key, CancellationToken cancellationToken = default) =>
        AcquireTurnLeaseAsync(key.ProviderInstanceId, cancellationToken);

    /// <summary>
    /// Discards the cached session(s) for a provider instance so the next acquire opens a fresh one.
    /// Used when the stored provider configuration changes underneath a running session.
    /// </summary>
    Task DropAsync(Guid providerInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Discards a single cached session identified by its scoped <see cref="AcpSessionKey"/>.</summary>
    Task DropAsync(AcpSessionKey key, CancellationToken cancellationToken = default) =>
        DropAsync(key.ProviderInstanceId, cancellationToken);

    /// <summary>Discards and terminates every teammate ACP session associated with <paramref name="runId"/>.</summary>
    Task DropRunSessionsAsync(string runId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class AcpSessionHost : IAcpSessionHost
{
    private readonly AcpSessionOptions _options;
    private readonly IHarnessWorkspaceRoots _roots;
    private readonly IAcpInteractionRouterFactory _routers;
    private readonly Func<AcpSessionRequest, IAcpTransport> _transportFactory;
    private readonly ILogger<AcpSessionHost> _logger;
    private readonly ConcurrentDictionary<AcpSessionKey, AcpSession> _sessions = new();
    private readonly ConcurrentDictionary<AcpSessionKey, SemaphoreSlim> _turnGates = new();
    private readonly ConcurrentDictionary<AcpSessionKey, SemaphoreSlim> _openGates = new();

    public AcpSessionHost(
        AcpSessionOptions options,
        IHarnessWorkspaceRoots roots,
        IAcpInteractionRouterFactory routers,
        ILogger<AcpSessionHost> logger,
        Func<AcpSessionRequest, IAcpTransport>? transportFactory = null)
    {
        _options = options;
        _roots = roots;
        _routers = routers;
        _logger = logger;
        _transportFactory = transportFactory ?? DefaultTransport;
    }

    public string ScratchDirectoryFor(Guid providerInstanceId) => _roots.ForProvider(providerInstanceId);

    public string ScratchDirectoryFor(Guid providerInstanceId, string? runId, string? teammateHandle) =>
        _roots.ForTeammate(providerInstanceId, runId, teammateHandle);

    public Task<IDisposable> AcquireTurnLeaseAsync(Guid providerInstanceId, CancellationToken cancellationToken = default) =>
        AcquireTurnLeaseAsync(AcpSessionKey.ForProvider(providerInstanceId), cancellationToken);

    public async Task<IDisposable> AcquireTurnLeaseAsync(AcpSessionKey key, CancellationToken cancellationToken = default)
    {
        var gate = _turnGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new TurnLease(gate);
    }

    public async Task<IAcpAgentSession> AcquireAsync(AcpSessionRequest request, CancellationToken cancellationToken = default)
    {
        var key = AcpSessionKey.FromRequest(request);
        if (_sessions.TryGetValue(key, out var cached) && !cached.IsFaulted && !string.IsNullOrEmpty(cached.SessionId))
        {
            // A scratch/worktree directory decided at open time is not renegotiable: the agent has
            // already written inside it. Silently running the turn somewhere else would be worse than
            // the mismatch the caller is reporting.
            if (!string.Equals(cached.ScratchDirectory, request.ScratchDirectory, StringComparison.Ordinal))
                throw new InvalidOperationException($"ACP provider {request.ProviderInstanceId} already has a session rooted at '{cached.ScratchDirectory}'.");

            return cached;
        }

        var openGate = _openGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(key, out var existing) && !existing.IsFaulted && !string.IsNullOrEmpty(existing.SessionId))
            {
                if (!string.Equals(existing.ScratchDirectory, request.ScratchDirectory, StringComparison.Ordinal))
                    throw new InvalidOperationException($"ACP provider {request.ProviderInstanceId} already has a session rooted at '{existing.ScratchDirectory}'.");

                return existing;
            }

            if (existing is not null)
            {
                _sessions.TryRemove(key, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var routerContext = new AcpInteractionContext(
                SessionId: request.RestoreSessionId ?? request.ProviderInstanceId.ToString("N"),
                RunId: request.RunId,
                TeammateHandle: request.TeammateHandle,
                WorkingDirectory: request.ScratchDirectory,
                WriteScope: request.WriteScope);
            var session = new AcpSession(request, _options, _transportFactory, _routers.Create(routerContext), _logger);
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
            _sessions[key] = session;
            _logger.LogInformation(
                "ACP session {SessionId} open for provider {ProviderInstanceId} (harness {Harness}, run {RunScope}, teammate {TeammateScope}, cwd {Scratch}).",
                session.SessionId, request.ProviderInstanceId, request.HarnessId, key.RunScope, key.TeammateScope, request.ScratchDirectory);
            return session;
        }
        finally
        {
            openGate.Release();
        }
    }

    public async Task DropAsync(Guid providerInstanceId, CancellationToken cancellationToken = default)
    {
        var matchingKeys = _sessions.Keys.Where(key => key.ProviderInstanceId == providerInstanceId).ToArray();
        foreach (var key in matchingKeys)
        {
            await DropAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DropAsync(AcpSessionKey key, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(key, out var session)) return;
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async Task DropRunSessionsAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId)) return;
        var normalized = runId.Trim();

        foreach (var (key, session) in _sessions.ToArray())
        {
            if (!string.Equals(key.RunScope, normalized, StringComparison.Ordinal)) continue;
            if (!_sessions.TryRemove(key, out _)) continue;
            _turnGates.TryRemove(key, out _);
            _openGates.TryRemove(key, out _);
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ACP run session teardown failed for run {RunId}, teammate {Teammate}; continuing.", normalized, key.TeammateScope);
            }
        }

        // Remove any leftover per-run semaphore entries whose session already exited/faulted.
        foreach (var key in _turnGates.Keys.Where(k => string.Equals(k.RunScope, normalized, StringComparison.Ordinal)).ToArray())
            _turnGates.TryRemove(key, out _);
        foreach (var key in _openGates.Keys.Where(k => string.Equals(k.RunScope, normalized, StringComparison.Ordinal)).ToArray())
            _openGates.TryRemove(key, out _);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (key, session) in _sessions.ToArray())
        {
            _sessions.TryRemove(key, out _);
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disposal must not abandon the remaining children: every one of them is a harness
                // process that outlives the host otherwise.
                _logger.LogDebug(ex, "ACP session teardown failed; continuing with the remaining sessions.");
            }
        }

        _sessions.Clear();
        _turnGates.Clear();
        _openGates.Clear();
        await ValueTask.CompletedTask;
    }

    private static IAcpTransport DefaultTransport(AcpSessionRequest request) => new ProcessAcpTransport(
        request.BinaryPath,
        request.Argv,
        request.ScratchDirectory,
        request.Environment,
        logger: null);

    /// <summary>
    /// <see cref="SemaphoreSlim.Dispose"/> only releases the wait handle, and disposing while another
    /// run is queued turns teardown into an <c>ObjectDisposedException</c> in an unrelated caller, so
    /// the per-provider gates are left for the garbage collector.
    /// </summary>
    private sealed class TurnLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
