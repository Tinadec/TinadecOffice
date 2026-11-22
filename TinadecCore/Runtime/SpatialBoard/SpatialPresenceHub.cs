using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Runtime.SpatialBoard;

public sealed record SpatialPresencePatch(
    string? ClientId = null,
    string? DisplayName = null,
    string? Color = null,
    double? CursorX = null,
    double? CursorY = null,
    bool ClearCursor = false,
    string[]? Selection = null,
    SpatialViewportDto? Viewport = null,
    string? ActiveTool = null,
    string? FollowingClientId = null,
    bool HasFollowingClientId = false);

/// <summary>
/// Thread-safe WebSocket connection wrapper that serializes outbound frames per socket.
/// </summary>
public sealed class SpatialPeerConnection : IDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private int _disposed;

    public SpatialPeerConnection(
        Guid sessionId,
        string connectionId,
        WebSocket socket,
        string clientId,
        string displayName,
        string color,
        long nowUnixMs)
    {
        SessionId = sessionId;
        ConnectionId = connectionId;
        Socket = socket;
        ClientId = clientId;
        DisplayName = displayName;
        Color = color;
        UpdatedAtUnixMs = nowUnixMs;
    }

    public Guid SessionId { get; }
    public string ConnectionId { get; }
    public WebSocket Socket { get; }

    public string ClientId { get; private set; }
    public string DisplayName { get; private set; }
    public string Color { get; private set; }
    public double? CursorX { get; private set; }
    public double? CursorY { get; private set; }
    public string[] Selection { get; private set; } = [];
    public SpatialViewportDto? Viewport { get; private set; }
    public string? ActiveTool { get; private set; } = "select";
    public string? FollowingClientId { get; private set; }
    public long UpdatedAtUnixMs { get; private set; }

    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Socket.State == WebSocketState.Open;

    public SpatialCollaboratorDto ApplyPatch(SpatialPresencePatch patch, long nowUnixMs)
    {
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(patch.ClientId))
                ClientId = patch.ClientId.Trim();
            if (!string.IsNullOrWhiteSpace(patch.DisplayName))
                DisplayName = patch.DisplayName.Trim();
            if (!string.IsNullOrWhiteSpace(patch.Color))
                Color = patch.Color.Trim();

            if (patch.ClearCursor)
            {
                CursorX = null;
                CursorY = null;
            }
            else
            {
                if (patch.CursorX.HasValue && double.IsFinite(patch.CursorX.Value))
                    CursorX = patch.CursorX.Value;
                if (patch.CursorY.HasValue && double.IsFinite(patch.CursorY.Value))
                    CursorY = patch.CursorY.Value;
            }

            if (patch.Selection is not null)
                Selection = patch.Selection.Where(id => !string.IsNullOrWhiteSpace(id)).Take(256).ToArray();

            if (patch.Viewport is { } vp && double.IsFinite(vp.X) && double.IsFinite(vp.Y) && double.IsFinite(vp.Zoom))
                Viewport = vp;

            if (!string.IsNullOrWhiteSpace(patch.ActiveTool))
                ActiveTool = patch.ActiveTool.Trim();

            if (patch.HasFollowingClientId)
                FollowingClientId = string.IsNullOrWhiteSpace(patch.FollowingClientId) ? null : patch.FollowingClientId.Trim();

            UpdatedAtUnixMs = nowUnixMs;
            return ToDtoUnsafe();
        }
    }

    public SpatialCollaboratorDto ToDto()
    {
        lock (_stateLock)
        {
            return ToDtoUnsafe();
        }
    }

    private SpatialCollaboratorDto ToDtoUnsafe() => new()
    {
        ClientId = ClientId,
        DisplayName = DisplayName,
        Color = Color,
        Role = "human",
        Status = "online",
        CursorX = CursorX,
        CursorY = CursorY,
        Selection = Selection,
        Viewport = Viewport,
        ActiveTool = ActiveTool,
        FollowingClientId = FollowingClientId,
        UpdatedAtUnixMs = UpdatedAtUnixMs
    };

    public async Task<bool> TrySendUtf8Async(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) return false;
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (!IsOpen) return false;
            await Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                try { _sendGate.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sendGate.Dispose();
    }
}

/// <summary>
/// Transient in-memory presence hub for connected human WebSocket peers and synthesized active AI teammates.
/// Never queries SQLite on high-frequency cursor ticks: AI topology projections are memoized per session with a 2s TTL.
/// </summary>
public sealed class SpatialPresenceHub
{
    private static readonly string[] CollaboratorPalette =
    [
        "#2563EB",
        "#059669",
        "#D97706",
        "#DC2626",
        "#7C3AED",
        "#DB2777",
        "#0891B2",
        "#4F46E5"
    ];

    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const long AiCacheTtlMs = 2_000;
    private const long StaleHumanPeerMs = 120_000;

    private readonly ISessionTopology? _topology;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SpatialPresenceHub>? _logger;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, SpatialPeerConnection>> _peersBySession = new();
    private readonly ConcurrentDictionary<Guid, CachedAiTeam> _aiCacheBySession = new();

    private sealed record CachedAiTeam(long RefreshedAtUnixMs, IReadOnlyList<SpatialCollaboratorDto> Collaborators);

    public SpatialPresenceHub(
        ISessionTopology? topology = null,
        TimeProvider? timeProvider = null,
        ILogger<SpatialPresenceHub>? logger = null)
    {
        _topology = topology;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public SpatialPeerConnection RegisterPeer(
        Guid sessionId,
        WebSocket socket,
        string? clientId = null,
        string? displayName = null,
        string? color = null)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var connectionId = Guid.NewGuid().ToString("N");
        var resolvedClientId = !string.IsNullOrWhiteSpace(clientId) ? clientId.Trim() : $"peer-{connectionId[..8]}";
        var resolvedName = !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : "Collaborator";
        var resolvedColor = !string.IsNullOrWhiteSpace(color) ? color.Trim() : PickColor(resolvedClientId);

        var peer = new SpatialPeerConnection(
            sessionId,
            connectionId,
            socket,
            resolvedClientId,
            resolvedName,
            resolvedColor,
            nowMs);

        var sessionPeers = _peersBySession.GetOrAdd(sessionId, static _ => new ConcurrentDictionary<string, SpatialPeerConnection>(StringComparer.Ordinal));
        sessionPeers[connectionId] = peer;
        return peer;
    }

    public async Task UnregisterPeerAsync(Guid sessionId, string connectionId, CancellationToken cancellationToken = default)
    {
        string? removedClientId = null;
        if (_peersBySession.TryGetValue(sessionId, out var sessionPeers)
            && sessionPeers.TryRemove(connectionId, out var removed))
        {
            removedClientId = removed.ClientId;
            removed.Dispose();
            if (sessionPeers.IsEmpty)
            {
                _peersBySession.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, SpatialPeerConnection>>(sessionId, sessionPeers));
            }
        }

        if (removedClientId is not null && HasOpenPeers(sessionId))
        {
            var collaborators = await GetCollaboratorsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            await BroadcastAsync(sessionId, new
            {
                type = "presence_update",
                session_id = sessionId,
                removed_client_id = removedClientId,
                collaborators
            }, excludeConnectionId: connectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SpatialCollaboratorDto?> UpdatePresenceAsync(
        Guid sessionId,
        string connectionId,
        SpatialPresencePatch patch,
        CancellationToken cancellationToken = default)
    {
        if (!_peersBySession.TryGetValue(sessionId, out var sessionPeers)
            || !sessionPeers.TryGetValue(connectionId, out var peer))
        {
            return null;
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var effectivePatch = string.IsNullOrWhiteSpace(patch.Color) && !string.IsNullOrWhiteSpace(patch.ClientId)
            ? patch with { Color = PickColor(patch.ClientId.Trim()) }
            : patch;
        var updated = peer.ApplyPatch(effectivePatch, nowMs);

        var collaborators = await GetCollaboratorsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await BroadcastAsync(sessionId, new
        {
            type = "presence_update",
            session_id = sessionId,
            collaborator = updated,
            collaborators
        }, excludeConnectionId: connectionId, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task<List<SpatialCollaboratorDto>> GetCollaboratorsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var humansByClient = new Dictionary<string, SpatialCollaboratorDto>(StringComparer.Ordinal);

        if (_peersBySession.TryGetValue(sessionId, out var sessionPeers))
        {
            foreach (var kvp in sessionPeers)
            {
                var peer = kvp.Value;
                if (!peer.IsOpen)
                {
                    if (sessionPeers.TryRemove(kvp.Key, out var dead)) dead.Dispose();
                    continue;
                }

                var dto = peer.ToDto();
                if (nowMs - dto.UpdatedAtUnixMs > StaleHumanPeerMs && peer.Socket.State != WebSocketState.Open)
                    continue;

                if (!humansByClient.TryGetValue(dto.ClientId, out var existing) || dto.UpdatedAtUnixMs >= existing.UpdatedAtUnixMs)
                {
                    humansByClient[dto.ClientId] = dto;
                }
            }
        }

        var result = new List<SpatialCollaboratorDto>(humansByClient.Count + 8);
        result.AddRange(humansByClient.Values.OrderBy(c => c.ClientId, StringComparer.Ordinal));

        var aiPeers = await GetCachedAiCollaboratorsAsync(sessionId, nowMs, cancellationToken).ConfigureAwait(false);
        result.AddRange(aiPeers);
        return result;
    }

    public bool HasOpenPeers(Guid sessionId) =>
        _peersBySession.TryGetValue(sessionId, out var peers) && peers.Values.Any(p => p.IsOpen);

    public async Task SendToPeerAsync(SpatialPeerConnection peer, object frame, CancellationToken cancellationToken = default)
    {
        var bytes = SerializeFrame(frame);
        await peer.TrySendUtf8Async(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task BroadcastAsync(
        Guid sessionId,
        object frame,
        string? excludeConnectionId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_peersBySession.TryGetValue(sessionId, out var sessionPeers) || sessionPeers.IsEmpty)
            return;

        var bytes = SerializeFrame(frame);
        var tasks = new List<Task>(sessionPeers.Count);
        foreach (var (connId, peer) in sessionPeers)
        {
            if (excludeConnectionId is not null && string.Equals(connId, excludeConnectionId, StringComparison.Ordinal))
                continue;

            if (!peer.IsOpen)
            {
                if (sessionPeers.TryRemove(connId, out var closed)) closed.Dispose();
                continue;
            }

            tasks.Add(SendAndPruneIfClosedAsync(sessionPeers, connId, peer, bytes, cancellationToken));
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task SendAndPruneIfClosedAsync(
        ConcurrentDictionary<string, SpatialPeerConnection> sessionPeers,
        string connId,
        SpatialPeerConnection peer,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        var ok = await peer.TrySendUtf8Async(payload, ct).ConfigureAwait(false);
        if (!ok && !peer.IsOpen && sessionPeers.TryRemove(connId, out var removed))
        {
            removed.Dispose();
        }
    }

    private async Task<IReadOnlyList<SpatialCollaboratorDto>> GetCachedAiCollaboratorsAsync(
        Guid sessionId,
        long nowMs,
        CancellationToken cancellationToken)
    {
        if (_topology is null) return [];

        if (_aiCacheBySession.TryGetValue(sessionId, out var cached)
            && nowMs - cached.RefreshedAtUnixMs < AiCacheTtlMs)
        {
            return cached.Collaborators;
        }

        try
        {
            var topology = await _topology.GetAsync(
                sessionId,
                new SessionTopologyQuery(
                    MaxRuns: 4,
                    MaxTasksPerRun: 24,
                    MaxInstancesPerRun: 24,
                    MaxLeases: 12,
                    MaxMembers: 32,
                    IncludeFinishedRuns: false),
                cancellationToken).ConfigureAwait(false);

            if (topology is null)
            {
                _aiCacheBySession[sessionId] = new CachedAiTeam(nowMs, []);
                return [];
            }

            var aiList = SynthesizeAiCollaborators(topology, nowMs);
            _aiCacheBySession[sessionId] = new CachedAiTeam(nowMs, aiList);
            return aiList;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Could not synthesize AI spatial collaborators for session {SessionId}.", sessionId);
            return cached?.Collaborators ?? [];
        }
    }

    private static IReadOnlyList<SpatialCollaboratorDto> SynthesizeAiCollaborators(SessionTopologyDto topology, long nowMs)
    {
        var list = new List<SpatialCollaboratorDto>();
        var seenInstanceIds = new HashSet<Guid>();
        var memberByInstance = topology.Members
            .Where(m => m.AgentInstanceId.HasValue)
            .GroupBy(m => m.AgentInstanceId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var run in topology.Runs)
        {
            if (run.Status is "completed" or "failed" or "cancelled") continue;

            var taskByWorker = run.Tasks
                .Where(t => t.WorkerInstanceId.HasValue)
                .GroupBy(t => t.WorkerInstanceId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var instance in run.Instances)
            {
                if (instance.Status is "completed" or "failed" or "cancelled" or "terminated") continue;
                if (!seenInstanceIds.Add(instance.InstanceId)) continue;

                memberByInstance.TryGetValue(instance.InstanceId, out var member);
                taskByWorker.TryGetValue(instance.InstanceId, out var activeTask);

                var clientId = $"ai:{instance.InstanceId:N}";
                var displayName = member?.DisplayName
                    ?? (!string.IsNullOrWhiteSpace(activeTask?.Handle) ? activeTask!.Handle! : null)
                    ?? instance.AgentSlug;

                var selection = instance.TaskId.HasValue
                    ? new[] { instance.TaskId.Value.ToString() }
                    : activeTask is not null
                        ? new[] { activeTask.TaskId.ToString() }
                        : [];

                list.Add(new SpatialCollaboratorDto
                {
                    ClientId = clientId,
                    DisplayName = displayName,
                    Color = PickColor(clientId),
                    Role = "ai",
                    AgentInstanceId = instance.InstanceId.ToString(),
                    Status = instance.Status,
                    Selection = selection,
                    ActiveTool = "select",
                    UpdatedAtUnixMs = nowMs
                });
            }
        }

        foreach (var member in topology.Members)
        {
            if (string.Equals(member.Role, "human", StringComparison.OrdinalIgnoreCase)) continue;
            if (member.AgentInstanceId.HasValue && seenInstanceIds.Contains(member.AgentInstanceId.Value)) continue;
            if (member.Presence is not ("online" or "working" or "busy" or "active")) continue;

            var clientId = $"ai-member:{member.ParticipantId:N}";
            list.Add(new SpatialCollaboratorDto
            {
                ClientId = clientId,
                DisplayName = !string.IsNullOrWhiteSpace(member.DisplayName) ? member.DisplayName : member.Handle,
                Color = PickColor(clientId),
                Role = "ai",
                AgentInstanceId = member.AgentInstanceId?.ToString(),
                Status = member.Presence,
                Selection = [],
                ActiveTool = "select",
                UpdatedAtUnixMs = nowMs
            });
        }

        return list;
    }

    public static string PickColor(string key)
    {
        if (string.IsNullOrEmpty(key)) return CollaboratorPalette[0];
        uint hash = 2166136261;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return CollaboratorPalette[hash % (uint)CollaboratorPalette.Length];
    }

    private static ReadOnlyMemory<byte> SerializeFrame(object frame)
    {
        var json = JsonSerializer.Serialize(frame, WireJson);
        return Encoding.UTF8.GetBytes(json);
    }
}
