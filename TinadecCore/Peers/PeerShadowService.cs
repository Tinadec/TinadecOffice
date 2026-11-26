using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

/// <summary>
/// Manages local read-only shadow workspaces, sessions, and turn digests on a controller device.
///
/// Safety invariant: clearing or updating local shadow cache operates exclusively on local
/// <c>peer_shadow_*</c> SQLite tables and NEVER deletes remote worker state or <c>peer_devices</c>
/// pairing records.
/// </summary>
public sealed class PeerShadowService : IPeerShadowService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public PeerShadowService(IDbContextFactory<PeerDbContext> dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PeerShadowSyncResultDto> SyncShadowAsync(
        string deviceId,
        PeerShadowSyncRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedDeviceId = RequireNonEmpty(deviceId, nameof(deviceId));
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        return await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var deviceExists = await db.Devices.AsNoTracking()
                .AnyAsync(x => x.DeviceId == normalizedDeviceId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (!deviceExists)
            {
                throw new KeyNotFoundException($"Peer device '{normalizedDeviceId}' was not found.");
            }

            var syncedWorkspaces = 0;
            if (request.Workspaces is { Count: > 0 })
            {
                var existingWs = await db.ShadowWorkspaces
                    .Where(x => x.PeerDeviceId == normalizedDeviceId)
                    .ToDictionaryAsync(x => x.RemoteProjectId, StringComparer.Ordinal, ct)
                    .ConfigureAwait(false);

                foreach (var ws in request.Workspaces)
                {
                    if (string.IsNullOrWhiteSpace(ws.RemoteProjectId))
                        continue;

                    var remoteProjId = ws.RemoteProjectId.Trim();
                    var name = string.IsNullOrWhiteSpace(ws.Name) ? remoteProjId : ws.Name.Trim();
                    if (name.Length > 256) name = name[..256];

                    if (existingWs.TryGetValue(remoteProjId, out var row))
                    {
                        row.Name = name;
                        if (ws.RemoteRootPath is not null)
                            row.RemoteRootPath = string.IsNullOrWhiteSpace(ws.RemoteRootPath) ? null : ws.RemoteRootPath.Trim();
                        if (ws.Visible is { } vis)
                            row.Visible = vis ? 1 : 0;
                        row.SyncedAt = nowMs;
                    }
                    else
                    {
                        var aliasId = string.IsNullOrWhiteSpace(ws.AliasWorkspaceId)
                            ? $"peerws_{Guid.NewGuid():N}"
                            : ws.AliasWorkspaceId.Trim();
                        var created = new PeerShadowWorkspaceRecord
                        {
                            AliasWorkspaceId = aliasId,
                            PeerDeviceId = normalizedDeviceId,
                            RemoteProjectId = remoteProjId,
                            Name = name,
                            RemoteRootPath = string.IsNullOrWhiteSpace(ws.RemoteRootPath) ? null : ws.RemoteRootPath.Trim(),
                            Visible = (ws.Visible ?? true) ? 1 : 0,
                            SyncedAt = nowMs
                        };
                        db.ShadowWorkspaces.Add(created);
                        existingWs[remoteProjId] = created;
                    }
                    syncedWorkspaces++;
                }
            }

            var syncedSessions = 0;
            var syncedTurns = 0;
            if (request.Sessions is { Count: > 0 })
            {
                var existingSessions = await db.ShadowSessions
                    .Where(x => x.PeerDeviceId == normalizedDeviceId)
                    .ToDictionaryAsync(x => x.RemoteSessionId, StringComparer.Ordinal, ct)
                    .ConfigureAwait(false);

                foreach (var sess in request.Sessions)
                {
                    if (string.IsNullOrWhiteSpace(sess.RemoteSessionId))
                        continue;

                    var remoteSessId = sess.RemoteSessionId.Trim();
                    var title = string.IsNullOrWhiteSpace(sess.Title) ? "Untitled Session" : sess.Title.Trim();
                    if (title.Length > 512) title = title[..512];

                    var rawStatus = !string.IsNullOrWhiteSpace(sess.RunStatus) ? sess.RunStatus : sess.Status;
                    var runStatus = string.IsNullOrWhiteSpace(rawStatus) ? "idle" : rawStatus.Trim();
                    if (runStatus.Length > 64) runStatus = runStatus[..64];

                    if (!existingSessions.TryGetValue(remoteSessId, out var sessionRow))
                    {
                        var localShadowId = string.IsNullOrWhiteSpace(sess.LocalShadowId)
                            ? $"peersess_{Guid.NewGuid():N}"
                            : sess.LocalShadowId.Trim();
                        sessionRow = new PeerShadowSessionRecord
                        {
                            LocalShadowId = localShadowId,
                            PeerDeviceId = normalizedDeviceId,
                            RemoteSessionId = remoteSessId,
                            RemoteProjectId = string.IsNullOrWhiteSpace(sess.RemoteProjectId) ? null : sess.RemoteProjectId.Trim(),
                            Title = title,
                            RunStatus = runStatus,
                            InitiatorDeviceId = string.IsNullOrWhiteSpace(sess.InitiatorDeviceId) ? null : sess.InitiatorDeviceId.Trim(),
                            LastEventSeq = Math.Max(0L, sess.LastEventSeq ?? 0L),
                            LastSyncedAt = nowMs
                        };
                        db.ShadowSessions.Add(sessionRow);
                        existingSessions[remoteSessId] = sessionRow;
                    }
                    else
                    {
                        sessionRow.Title = title;
                        sessionRow.RunStatus = runStatus;
                        if (sess.RemoteProjectId is not null)
                            sessionRow.RemoteProjectId = string.IsNullOrWhiteSpace(sess.RemoteProjectId) ? null : sess.RemoteProjectId.Trim();
                        if (sess.InitiatorDeviceId is not null)
                            sessionRow.InitiatorDeviceId = string.IsNullOrWhiteSpace(sess.InitiatorDeviceId) ? null : sess.InitiatorDeviceId.Trim();
                        if (sess.LastEventSeq is { } seq)
                            sessionRow.LastEventSeq = Math.Max(0L, seq);
                        sessionRow.LastSyncedAt = nowMs;
                    }
                    syncedSessions++;

                    if (sess.Turns is { Count: > 0 })
                    {
                        var existingTurns = await db.ShadowTurns
                            .Where(x => x.LocalShadowId == sessionRow.LocalShadowId)
                            .ToDictionaryAsync(x => x.TurnId, StringComparer.Ordinal, ct)
                            .ConfigureAwait(false);

                        foreach (var turn in sess.Turns)
                        {
                            if (string.IsNullOrWhiteSpace(turn.TurnId))
                                continue;

                            var turnId = turn.TurnId.Trim();
                            var digestJson = ResolveDigestJson(turn);
                            if (existingTurns.TryGetValue(turnId, out var turnRow))
                            {
                                turnRow.DigestJson = digestJson;
                                turnRow.UpdatedAt = nowMs;
                            }
                            else
                            {
                                var createdTurn = new PeerShadowTurnRecord
                                {
                                    LocalShadowId = sessionRow.LocalShadowId,
                                    TurnId = turnId,
                                    DigestJson = digestJson,
                                    UpdatedAt = nowMs
                                };
                                db.ShadowTurns.Add(createdTurn);
                                existingTurns[turnId] = createdTurn;
                            }
                            syncedTurns++;
                        }
                    }
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            return new PeerShadowSyncResultDto
            {
                Synced = true,
                DeviceId = normalizedDeviceId,
                SyncedWorkspaces = syncedWorkspaces,
                SyncedSessions = syncedSessions,
                SyncedTurns = syncedTurns,
                SyncedAt = nowMs
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerShadowSessionListDto> ListShadowSessionsAsync(
        string deviceId,
        string? remoteProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeviceId = RequireNonEmpty(deviceId, nameof(deviceId));
        var normalizedProject = string.IsNullOrWhiteSpace(remoteProjectId) ? null : remoteProjectId.Trim();

        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var deviceExists = await db.Devices.AsNoTracking()
                .AnyAsync(x => x.DeviceId == normalizedDeviceId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (!deviceExists)
            {
                throw new KeyNotFoundException($"Peer device '{normalizedDeviceId}' was not found.");
            }

            var query = db.ShadowSessions.AsNoTracking()
                .Where(x => x.PeerDeviceId == normalizedDeviceId);
            if (normalizedProject is not null)
            {
                query = query.Where(x => x.RemoteProjectId == normalizedProject);
            }

            var rows = await query
                .OrderByDescending(x => x.LastSyncedAt)
                .ThenBy(x => x.RemoteSessionId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return new PeerShadowSessionListDto
            {
                DeviceId = normalizedDeviceId,
                Sessions = rows.Select(ToSessionDto).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerShadowSessionDetailDto?> GetShadowSessionAsync(
        string deviceId,
        string remoteSessionId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeviceId = RequireNonEmpty(deviceId, nameof(deviceId));
        var normalizedSessionId = RequireNonEmpty(remoteSessionId, nameof(remoteSessionId));

        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var deviceExists = await db.Devices.AsNoTracking()
                .AnyAsync(x => x.DeviceId == normalizedDeviceId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (!deviceExists)
            {
                throw new KeyNotFoundException($"Peer device '{normalizedDeviceId}' was not found.");
            }

            var session = await db.ShadowSessions.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.PeerDeviceId == normalizedDeviceId
                        && (x.RemoteSessionId == normalizedSessionId || x.LocalShadowId == normalizedSessionId),
                    ct)
                .ConfigureAwait(false);
            if (session is null)
            {
                return null;
            }

            var turns = await db.ShadowTurns.AsNoTracking()
                .Where(x => x.LocalShadowId == session.LocalShadowId)
                .OrderBy(x => x.UpdatedAt)
                .ThenBy(x => x.TurnId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var turnDtos = turns.Select(ToTurnDto).ToArray();
            var messages = BuildMessagePreviews(turns);

            return new PeerShadowSessionDetailDto
            {
                Session = ToSessionDto(session),
                Turns = turnDtos,
                Messages = messages
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ClearDeviceLocalCacheAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var normalizedDeviceId = RequireNonEmpty(deviceId, nameof(deviceId));
        return await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var deviceExists = await db.Devices.AsNoTracking()
                .AnyAsync(x => x.DeviceId == normalizedDeviceId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (!deviceExists)
            {
                throw new KeyNotFoundException($"Peer device '{normalizedDeviceId}' was not found.");
            }

            var removed = await DeleteShadowRowsCoreAsync(db, normalizedDeviceId, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return removed;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeDeviceShadowDataAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        var normalizedDeviceId = deviceId.Trim();
        await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            _ = await DeleteShadowRowsCoreAsync(db, normalizedDeviceId, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DeleteShadowRowsCoreAsync(PeerDbContext db, string deviceId, CancellationToken ct)
    {
        var shadowIds = await db.ShadowSessions.AsNoTracking()
            .Where(x => x.PeerDeviceId == deviceId)
            .Select(x => x.LocalShadowId)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        var removedTurns = 0;
        if (shadowIds.Length > 0)
        {
            removedTurns = await db.ShadowTurns
                .Where(x => shadowIds.Contains(x.LocalShadowId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        var removedSessions = await db.ShadowSessions
            .Where(x => x.PeerDeviceId == deviceId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var removedWorkspaces = await db.ShadowWorkspaces
            .Where(x => x.PeerDeviceId == deviceId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return removedTurns + removedSessions + removedWorkspaces;
    }

    private static string? ResolveDigestJson(PeerShadowTurnSyncItemDto turn)
    {
        if (turn.DigestJson is not null)
        {
            return turn.DigestJson;
        }

        if (turn.Digest is { } el && el.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return JsonSerializer.Serialize(el, JsonOptions);
        }

        return null;
    }

    private static IReadOnlyList<PeerShadowMessagePreviewDto> BuildMessagePreviews(IReadOnlyList<PeerShadowTurnRecord> turns)
    {
        if (turns.Count == 0) return [];
        var list = new List<PeerShadowMessagePreviewDto>(turns.Count * 2);
        foreach (var turn in turns)
        {
            if (string.IsNullOrWhiteSpace(turn.DigestJson))
            {
                list.Add(new PeerShadowMessagePreviewDto
                {
                    Id = $"{turn.TurnId}:pending",
                    TurnId = turn.TurnId,
                    Role = "user",
                    Content = string.Empty,
                    PendingConfirmation = true,
                    UpdatedAt = turn.UpdatedAt
                });
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(turn.DigestJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("user_content", out var userText) && userText.ValueKind == JsonValueKind.String)
                    {
                        list.Add(new PeerShadowMessagePreviewDto
                        {
                            Id = $"{turn.TurnId}:user",
                            TurnId = turn.TurnId,
                            Role = "user",
                            Content = userText.GetString() ?? string.Empty,
                            PendingConfirmation = false,
                            UpdatedAt = turn.UpdatedAt
                        });
                    }

                    if (root.TryGetProperty("assistant_content", out var assistantText) && assistantText.ValueKind == JsonValueKind.String)
                    {
                        list.Add(new PeerShadowMessagePreviewDto
                        {
                            Id = $"{turn.TurnId}:assistant",
                            TurnId = turn.TurnId,
                            Role = "assistant",
                            Content = assistantText.GetString() ?? string.Empty,
                            PendingConfirmation = false,
                            UpdatedAt = turn.UpdatedAt
                        });
                    }
                    else if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    {
                        var role = root.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                            ? roleEl.GetString() ?? "assistant"
                            : "assistant";
                        list.Add(new PeerShadowMessagePreviewDto
                        {
                            Id = $"{turn.TurnId}:0",
                            TurnId = turn.TurnId,
                            Role = role,
                            Content = contentEl.GetString() ?? string.Empty,
                            PendingConfirmation = false,
                            UpdatedAt = turn.UpdatedAt
                        });
                    }
                }
            }
            catch (JsonException)
            {
                list.Add(new PeerShadowMessagePreviewDto
                {
                    Id = $"{turn.TurnId}:raw",
                    TurnId = turn.TurnId,
                    Role = "assistant",
                    Content = turn.DigestJson,
                    PendingConfirmation = false,
                    UpdatedAt = turn.UpdatedAt
                });
            }
        }

        return list;
    }

    private static PeerShadowSessionDto ToSessionDto(PeerShadowSessionRecord row) =>
        new()
        {
            LocalShadowId = row.LocalShadowId,
            PeerDeviceId = row.PeerDeviceId,
            RemoteSessionId = row.RemoteSessionId,
            RemoteProjectId = row.RemoteProjectId,
            Title = row.Title,
            RunStatus = row.RunStatus,
            InitiatorDeviceId = row.InitiatorDeviceId,
            LastEventSeq = row.LastEventSeq,
            LastSyncedAt = row.LastSyncedAt
        };

    private static PeerShadowTurnDto ToTurnDto(PeerShadowTurnRecord row) =>
        new()
        {
            LocalShadowId = row.LocalShadowId,
            TurnId = row.TurnId,
            DigestJson = row.DigestJson,
            UpdatedAt = row.UpdatedAt
        };

    private static string RequireNonEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} is required.", paramName);
        return value.Trim();
    }
}
