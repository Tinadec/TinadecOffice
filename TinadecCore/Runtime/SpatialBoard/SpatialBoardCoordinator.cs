using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime.SpatialBoard;

/// <summary>
/// Authoritative per-session spatial board coordinator enforcing property-level LWW `(lamport, writer_id)`,
/// strict `parent_id` DAG cycle rejection (`parent_cycle_detected`), deterministic fractional `sort_key`
/// normalization, monotonic batch sequence numbers, atomic SQLite/PostgreSQL persistence, and WebSocket broadcasts.
/// </summary>
public sealed class SpatialBoardCoordinator
{
    private const string Base62Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static readonly HashSet<string> AllowedObjectKinds = new(StringComparer.Ordinal)
    {
        "work",
        "section",
        "sticky",
        "text",
        "drawing",
        "media",
        "connector",
        "board"
    };

    private static readonly HashSet<string> ForbiddenBoardPropKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "tasks",
        "items",
        "cards",
        "columns_data",
        "columnsdata",
        "task_list",
        "tasklist"
    };

    private static readonly JsonSerializerOptions StorageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IDbContextFactory<SpatialBoardDbContext> _dbFactory;
    private readonly SpatialPresenceHub _presenceHub;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SpatialBoardCoordinator>? _logger;

    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaEnsured;

    private readonly ConcurrentDictionary<Guid, SessionBoardState> _sessions = new();

    private sealed class SessionBoardState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Loaded { get; set; }
        public long Seq { get; set; }
        public long UpdatedAtUnixMs { get; set; }
        public Dictionary<string, MutableBoardObject> Objects { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SpatialBoardCommentPinDto> Comments { get; } = new(StringComparer.Ordinal);
    }

    private sealed class MutableBoardObject
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = "sticky";
        public string? ParentId { get; set; }
        public string SortKey { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 220;
        public double Height { get; set; } = 160;
        public double Rotation { get; set; }
        public Dictionary<string, JsonElement> Props { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, SpatialLwwTimestampDto> PropTimestamps { get; set; } = new(StringComparer.Ordinal);
        public bool Deleted { get; set; }
        public SpatialLwwTimestampDto? DeletedTs { get; set; }
        public long UpdatedAtUnixMs { get; set; }

        public SpatialBoardObjectDto ToDto() => new()
        {
            Id = Id,
            Kind = Kind,
            ParentId = ParentId,
            SortKey = SortKey,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            Rotation = Rotation,
            Props = Props.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone(), StringComparer.Ordinal),
            PropTimestamps = new Dictionary<string, SpatialLwwTimestampDto>(PropTimestamps, StringComparer.Ordinal),
            Deleted = Deleted,
            DeletedTs = DeletedTs,
            UpdatedAtUnixMs = UpdatedAtUnixMs
        };
    }

    public SpatialBoardCoordinator(
        IDbContextFactory<SpatialBoardDbContext> dbFactory,
        SpatialPresenceHub presenceHub,
        TimeProvider? timeProvider = null,
        ILogger<SpatialBoardCoordinator>? logger = null)
    {
        _dbFactory = dbFactory;
        _presenceHub = presenceHub;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<SpatialBoardSnapshotDto> GetSnapshotAsync(
        Guid sessionId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var state = GetOrCreateSessionState(sessionId);

        long seq;
        long updatedAt;
        Dictionary<string, SpatialBoardObjectDto> objects;
        List<SpatialBoardCommentPinDto> comments;

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnderLockAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            seq = state.Seq;
            updatedAt = state.UpdatedAtUnixMs;

            objects = new Dictionary<string, SpatialBoardObjectDto>(StringComparer.Ordinal);
            foreach (var obj in state.Objects.Values
                         .Where(o => includeDeleted || !o.Deleted)
                         .OrderBy(o => o.SortKey, StringComparer.Ordinal)
                         .ThenBy(o => o.Id, StringComparer.Ordinal))
            {
                objects[obj.Id] = obj.ToDto();
            }

            comments = state.Comments.Values
                .OrderBy(c => c.CreatedAtUnixMs)
                .ThenBy(c => c.PinId, StringComparer.Ordinal)
                .Select(CloneCommentPin)
                .ToList();
        }
        finally
        {
            state.Gate.Release();
        }

        var collaborators = await _presenceHub.GetCollaboratorsAsync(sessionId, cancellationToken).ConfigureAwait(false);

        return new SpatialBoardSnapshotDto
        {
            SessionId = sessionId,
            Seq = seq,
            Objects = objects,
            Comments = comments,
            Collaborators = collaborators,
            UpdatedAtUnixMs = updatedAt
        };
    }

    public async Task<SpatialBoardOpsResponseDto> ApplyOpsAsync(
        Guid sessionId,
        SpatialBoardOpsRequestDto request,
        string? senderConnectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        var state = GetOrCreateSessionState(sessionId);
        var writerId = !string.IsNullOrWhiteSpace(request.ClientId) ? request.ClientId.Trim() : "anonymous";
        var ops = request.Ops ?? [];

        long resultingSeq;
        var acceptedIds = new List<string>(ops.Count);
        var rejectedOps = new List<SpatialBoardRejectedOpDto>();
        var touchedObjects = new Dictionary<string, SpatialBoardObjectDto>(StringComparer.Ordinal);

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnderLockAsync(sessionId, state, cancellationToken).ConfigureAwait(false);

            if (ops.Count == 0)
            {
                return new SpatialBoardOpsResponseDto
                {
                    Seq = state.Seq,
                    AcceptedOpIds = [],
                    RejectedOps = [],
                    Objects = new Dictionary<string, SpatialBoardObjectDto>(StringComparer.Ordinal)
                };
            }

            ValidateBoardObjectPropsOrThrow(ops, state.Objects, writerId);

            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var dirtyObjectIds = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < ops.Count; index++)
            {
                var op = ops[index];
                var clientOpId = !string.IsNullOrWhiteSpace(op.ClientOpId) ? op.ClientOpId.Trim() : $"op-{index}";
                var objectId = op.ObjectId?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(objectId) || objectId.Length > 128)
                {
                    rejectedOps.Add(new SpatialBoardRejectedOpDto
                    {
                        ClientOpId = clientOpId,
                        ObjectId = objectId,
                        Code = "invalid_object_id",
                        Message = "object_id must be a non-empty string up to 128 characters."
                    });
                    continue;
                }

                string? normalizedKind = null;
                if (!string.IsNullOrWhiteSpace(op.Kind))
                {
                    normalizedKind = op.Kind.Trim().ToLowerInvariant();
                    if (!AllowedObjectKinds.Contains(normalizedKind))
                    {
                        rejectedOps.Add(new SpatialBoardRejectedOpDto
                        {
                            ClientOpId = clientOpId,
                            ObjectId = objectId,
                            Code = "invalid_object_kind",
                            Message = $"Unsupported spatial object kind '{op.Kind}'."
                        });
                        continue;
                    }
                }

                var mutatesParent = op.HasParentId
                    || op.ParentId is not null
                    || HasTimestampKey(op.Timestamps, "parent_id", "parentId");
                var candidateParentId = string.IsNullOrWhiteSpace(op.ParentId) ? null : op.ParentId.Trim();

                if (mutatesParent && WouldCreateParentCycle(objectId, candidateParentId, state.Objects))
                {
                    rejectedOps.Add(new SpatialBoardRejectedOpDto
                    {
                        ClientOpId = clientOpId,
                        ObjectId = objectId,
                        Code = "parent_cycle_detected",
                        Message = $"Setting parent_id '{candidateParentId}' on object '{objectId}' would create a cycle."
                    });
                    continue;
                }

                if (!state.Objects.TryGetValue(objectId, out var obj))
                {
                    var initialKind = normalizedKind ?? "sticky";
                    var (defaultW, defaultH) = DefaultDimensionsForKind(initialKind);
                    obj = new MutableBoardObject
                    {
                        Id = objectId,
                        Kind = initialKind,
                        Width = defaultW,
                        Height = defaultH,
                        UpdatedAtUnixMs = nowMs
                    };
                    state.Objects[objectId] = obj;
                }

                var fallbackTs = BuildFallbackTimestamp(obj, op, writerId);

                if (normalizedKind is not null)
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "kind", null, writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "kind", null)))
                    {
                        obj.Kind = normalizedKind;
                        obj.PropTimestamps["kind"] = incomingTs;
                    }
                }

                if (mutatesParent)
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "parent_id", "parentId", writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "parent_id", "parentId")))
                    {
                        if (WouldCreateParentCycle(objectId, candidateParentId, state.Objects))
                        {
                            rejectedOps.Add(new SpatialBoardRejectedOpDto
                            {
                                ClientOpId = clientOpId,
                                ObjectId = objectId,
                                Code = "parent_cycle_detected",
                                Message = $"Setting parent_id '{candidateParentId}' on object '{objectId}' would create a cycle."
                            });
                            continue;
                        }

                        obj.ParentId = candidateParentId;
                        obj.PropTimestamps.Remove("parentId");
                        obj.PropTimestamps["parent_id"] = incomingTs;
                    }
                }

                if (!string.IsNullOrWhiteSpace(op.SortKey))
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "sort_key", "sortKey", writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "sort_key", "sortKey")))
                    {
                        obj.SortKey = op.SortKey.Trim();
                        obj.PropTimestamps.Remove("sortKey");
                        obj.PropTimestamps["sort_key"] = incomingTs;
                    }
                }

                if (op.X.HasValue && double.IsFinite(op.X.Value))
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "x", null, writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "x", null)))
                    {
                        obj.X = op.X.Value;
                        obj.PropTimestamps["x"] = incomingTs;
                    }
                }

                if (op.Y.HasValue && double.IsFinite(op.Y.Value))
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "y", null, writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "y", null)))
                    {
                        obj.Y = op.Y.Value;
                        obj.PropTimestamps["y"] = incomingTs;
                    }
                }

                if (op.Width.HasValue && double.IsFinite(op.Width.Value))
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "width", null, writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "width", null)))
                    {
                        obj.Width = Math.Max(1.0, op.Width.Value);
                        obj.PropTimestamps["width"] = incomingTs;
                    }
                }

                if (op.Height.HasValue && double.IsFinite(op.Height.Value))
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "height", null, writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "height", null)))
                    {
                        obj.Height = Math.Max(1.0, op.Height.Value);
                        obj.PropTimestamps["height"] = incomingTs;
                    }
                }

                if (op.Rotation.HasValue && double.IsFinite(op.Rotation.Value))
                {
                    var incomingTs = ResolveFieldTimestamp(op.Timestamps, "rotation", null, writerId, fallbackTs);
                    if (ShouldWinLww(incomingTs, GetExistingFieldTimestamp(obj, "rotation", null)))
                    {
                        obj.Rotation = op.Rotation.Value;
                        obj.PropTimestamps["rotation"] = incomingTs;
                    }
                }

                if (op.Props is not null)
                {
                    foreach (var (propKey, propValue) in op.Props)
                    {
                        if (string.IsNullOrWhiteSpace(propKey)) continue;
                        var trimmedKey = propKey.Trim();
                        var incomingTs = ResolvePropTimestamp(op.Timestamps, trimmedKey, writerId, fallbackTs);
                        var existingTs = GetExistingPropTimestamp(obj, trimmedKey);
                        if (ShouldWinLww(incomingTs, existingTs))
                        {
                            obj.Props[trimmedKey] = propValue.Clone();
                            obj.PropTimestamps[$"props.{trimmedKey}"] = incomingTs;
                        }
                    }
                }

                if (op.Deleted.HasValue)
                {
                    var incomingDelTs = NormalizeTimestamp(op.DeletedTs, writerId)
                        ?? ResolveFieldTimestamp(op.Timestamps, "deleted", null, writerId, fallbackTs);

                    if (ShouldWinLww(incomingDelTs, obj.DeletedTs))
                    {
                        obj.Deleted = op.Deleted.Value;
                        obj.DeletedTs = incomingDelTs;
                        obj.PropTimestamps["deleted"] = incomingDelTs;
                    }
                }

                if (string.IsNullOrWhiteSpace(obj.SortKey))
                {
                    obj.SortKey = GenerateDeterministicSortKey(state.Objects, obj.ParentId, obj.Id);
                    obj.PropTimestamps.TryAdd("sort_key", fallbackTs);
                }

                obj.UpdatedAtUnixMs = nowMs;
                acceptedIds.Add(clientOpId);
                dirtyObjectIds.Add(objectId);
            }

            if (acceptedIds.Count > 0)
            {
                state.Seq += 1;
                state.UpdatedAtUnixMs = nowMs;
                await PersistDirtyObjectsUnderLockAsync(sessionId, state, dirtyObjectIds, cancellationToken).ConfigureAwait(false);
            }

            resultingSeq = state.Seq;
            foreach (var dirtyId in dirtyObjectIds)
            {
                if (state.Objects.TryGetValue(dirtyId, out var updatedObj))
                {
                    touchedObjects[dirtyId] = updatedObj.ToDto();
                }
            }
        }
        finally
        {
            state.Gate.Release();
        }

        if (acceptedIds.Count > 0 && _presenceHub.HasOpenPeers(sessionId))
        {
            await _presenceHub.BroadcastAsync(sessionId, new
            {
                type = "ops_broadcast",
                session_id = sessionId,
                client_id = writerId,
                seq = resultingSeq,
                accepted_op_ids = acceptedIds,
                objects = touchedObjects
            }, excludeConnectionId: senderConnectionId, cancellationToken).ConfigureAwait(false);
        }

        return new SpatialBoardOpsResponseDto
        {
            Seq = resultingSeq,
            AcceptedOpIds = acceptedIds,
            RejectedOps = rejectedOps,
            Objects = touchedObjects
        };
    }

    public async Task<SpatialBoardCommentPinDto> CreateCommentAsync(
        Guid sessionId,
        CreateSpatialCommentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Body?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(body))
            throw new ArgumentException("Comment body is required.", nameof(request));
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y))
            throw new ArgumentException("Comment coordinates x and y must be finite numbers.", nameof(request));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var state = GetOrCreateSessionState(sessionId);

        SpatialBoardCommentPinDto createdDto;
        long seq;

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnderLockAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var pinId = !string.IsNullOrWhiteSpace(request.PinId) ? request.PinId.Trim() : Guid.NewGuid().ToString("N");

            if (state.Comments.TryGetValue(pinId, out var existing))
            {
                return CloneCommentPin(existing);
            }

            var authorId = !string.IsNullOrWhiteSpace(request.AuthorId) ? request.AuthorId.Trim() : "owner";
            var authorName = !string.IsNullOrWhiteSpace(request.AuthorName) ? request.AuthorName.Trim() : "User";
            var authorKind = NormalizeAuthorKind(request.AuthorKind);
            var mentions = NormalizeMentions(request.Mentions);
            var objectId = string.IsNullOrWhiteSpace(request.ObjectId) ? null : request.ObjectId.Trim();

            var pin = new SpatialBoardCommentPinDto
            {
                PinId = pinId,
                SessionId = sessionId,
                ObjectId = objectId,
                X = request.X,
                Y = request.Y,
                AuthorId = authorId,
                AuthorName = authorName,
                AuthorKind = authorKind,
                Body = body,
                Mentions = mentions,
                Replies = [],
                Resolved = false,
                CreatedAtUnixMs = nowMs,
                UpdatedAtUnixMs = nowMs
            };

            state.Seq += 1;
            state.UpdatedAtUnixMs = nowMs;
            state.Comments[pinId] = pin;

            await PersistCommentUpsertUnderLockAsync(sessionId, state, pin, cancellationToken).ConfigureAwait(false);
            seq = state.Seq;
            createdDto = CloneCommentPin(pin);
        }
        finally
        {
            state.Gate.Release();
        }

        if (_presenceHub.HasOpenPeers(sessionId))
        {
            await _presenceHub.BroadcastAsync(sessionId, new
            {
                type = "comment_broadcast",
                action = "created",
                session_id = sessionId,
                seq,
                comment = createdDto
            }, excludeConnectionId: null, cancellationToken).ConfigureAwait(false);
        }

        return createdDto;
    }

    public async Task<SpatialBoardCommentPinDto?> AddCommentReplyAsync(
        Guid sessionId,
        string pinId,
        ReplySpatialCommentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(pinId)) return null;

        var body = request.Body?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(body))
            throw new ArgumentException("Reply body is required.", nameof(request));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var state = GetOrCreateSessionState(sessionId);
        var normalizedPinId = pinId.Trim();

        SpatialBoardCommentPinDto updatedDto;
        long seq;

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnderLockAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            if (!state.Comments.TryGetValue(normalizedPinId, out var existing))
                return null;

            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var replyId = !string.IsNullOrWhiteSpace(request.ReplyId) ? request.ReplyId.Trim() : Guid.NewGuid().ToString("N");
            if (existing.Replies.All(r => !string.Equals(r.ReplyId, replyId, StringComparison.Ordinal)))
            {
                var reply = new SpatialBoardCommentReplyDto
                {
                    ReplyId = replyId,
                    AuthorId = !string.IsNullOrWhiteSpace(request.AuthorId) ? request.AuthorId.Trim() : "owner",
                    AuthorName = !string.IsNullOrWhiteSpace(request.AuthorName) ? request.AuthorName.Trim() : "User",
                    AuthorKind = NormalizeAuthorKind(request.AuthorKind),
                    Body = body,
                    Mentions = NormalizeMentions(request.Mentions),
                    CreatedAtUnixMs = nowMs
                };

                var updatedReplies = new List<SpatialBoardCommentReplyDto>(existing.Replies) { reply };
                var updatedPin = new SpatialBoardCommentPinDto
                {
                    PinId = existing.PinId,
                    SessionId = existing.SessionId,
                    ObjectId = existing.ObjectId,
                    X = existing.X,
                    Y = existing.Y,
                    AuthorId = existing.AuthorId,
                    AuthorName = existing.AuthorName,
                    AuthorKind = existing.AuthorKind,
                    Body = existing.Body,
                    Mentions = existing.Mentions,
                    Replies = updatedReplies,
                    Resolved = existing.Resolved,
                    CreatedAtUnixMs = existing.CreatedAtUnixMs,
                    UpdatedAtUnixMs = nowMs
                };

                state.Seq += 1;
                state.UpdatedAtUnixMs = nowMs;
                state.Comments[normalizedPinId] = updatedPin;
                await PersistCommentUpsertUnderLockAsync(sessionId, state, updatedPin, cancellationToken).ConfigureAwait(false);
                existing = updatedPin;
            }

            seq = state.Seq;
            updatedDto = CloneCommentPin(existing);
        }
        finally
        {
            state.Gate.Release();
        }

        if (_presenceHub.HasOpenPeers(sessionId))
        {
            await _presenceHub.BroadcastAsync(sessionId, new
            {
                type = "comment_broadcast",
                action = "replied",
                session_id = sessionId,
                seq,
                comment = updatedDto
            }, excludeConnectionId: null, cancellationToken).ConfigureAwait(false);
        }

        return updatedDto;
    }

    public async Task<SpatialBoardCommentPinDto?> PatchCommentAsync(
        Guid sessionId,
        string pinId,
        PatchSpatialCommentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(pinId)) return null;

        if (request.Body is not null && string.IsNullOrWhiteSpace(request.Body))
            throw new ArgumentException("Comment body cannot be empty.", nameof(request));
        if ((request.X.HasValue && !double.IsFinite(request.X.Value)) || (request.Y.HasValue && !double.IsFinite(request.Y.Value)))
            throw new ArgumentException("Comment coordinates must be finite numbers.", nameof(request));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var state = GetOrCreateSessionState(sessionId);
        var normalizedPinId = pinId.Trim();

        SpatialBoardCommentPinDto updatedDto;
        long seq;

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnderLockAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            if (!state.Comments.TryGetValue(normalizedPinId, out var existing))
                return null;

            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var nextObjectId = request.HasObjectId || request.ObjectId is not null
                ? (string.IsNullOrWhiteSpace(request.ObjectId) ? null : request.ObjectId.Trim())
                : existing.ObjectId;

            var updatedPin = new SpatialBoardCommentPinDto
            {
                PinId = existing.PinId,
                SessionId = existing.SessionId,
                ObjectId = nextObjectId,
                X = request.X ?? existing.X,
                Y = request.Y ?? existing.Y,
                AuthorId = existing.AuthorId,
                AuthorName = existing.AuthorName,
                AuthorKind = existing.AuthorKind,
                Body = request.Body is not null ? request.Body.Trim() : existing.Body,
                Mentions = request.Mentions is not null ? NormalizeMentions(request.Mentions) : existing.Mentions,
                Replies = existing.Replies,
                Resolved = request.Resolved ?? existing.Resolved,
                CreatedAtUnixMs = existing.CreatedAtUnixMs,
                UpdatedAtUnixMs = nowMs
            };

            state.Seq += 1;
            state.UpdatedAtUnixMs = nowMs;
            state.Comments[normalizedPinId] = updatedPin;
            await PersistCommentUpsertUnderLockAsync(sessionId, state, updatedPin, cancellationToken).ConfigureAwait(false);

            seq = state.Seq;
            updatedDto = CloneCommentPin(updatedPin);
        }
        finally
        {
            state.Gate.Release();
        }

        if (_presenceHub.HasOpenPeers(sessionId))
        {
            await _presenceHub.BroadcastAsync(sessionId, new
            {
                type = "comment_broadcast",
                action = "updated",
                session_id = sessionId,
                seq,
                comment = updatedDto
            }, excludeConnectionId: null, cancellationToken).ConfigureAwait(false);
        }

        return updatedDto;
    }

    public async Task<(bool Deleted, long Seq)> DeleteCommentAsync(
        Guid sessionId,
        string pinId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pinId)) return (false, 0);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var state = GetOrCreateSessionState(sessionId);
        var normalizedPinId = pinId.Trim();
        long seq;

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnderLockAsync(sessionId, state, cancellationToken).ConfigureAwait(false);
            if (!state.Comments.Remove(normalizedPinId))
                return (false, state.Seq);

            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            state.Seq += 1;
            state.UpdatedAtUnixMs = nowMs;
            seq = state.Seq;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.BoardComments
                .FirstOrDefaultAsync(x => x.SessionId == sessionId && x.PinId == normalizedPinId, cancellationToken)
                .ConfigureAwait(false);
            if (row is not null)
                db.BoardComments.Remove(row);

            await UpsertMetaRowAsync(db, sessionId, state.Seq, state.UpdatedAtUnixMs, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }

        if (_presenceHub.HasOpenPeers(sessionId))
        {
            await _presenceHub.BroadcastAsync(sessionId, new
            {
                type = "comment_broadcast",
                action = "deleted",
                session_id = sessionId,
                seq,
                pin_id = normalizedPinId
            }, excludeConnectionId: null, cancellationToken).ConfigureAwait(false);
        }

        return (true, seq);
    }

    private SessionBoardState GetOrCreateSessionState(Guid sessionId) =>
        _sessions.GetOrAdd(sessionId, static _ => new SessionBoardState());

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured) return;
        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaEnsured) return;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await DbContextSchemaBootstrapper.EnsureTablesAsync(db, cancellationToken).ConfigureAwait(false);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private async Task EnsureLoadedUnderLockAsync(Guid sessionId, SessionBoardState state, CancellationToken cancellationToken)
    {
        if (state.Loaded) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var meta = await db.BoardMeta.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionId == sessionId, cancellationToken)
            .ConfigureAwait(false);

        var objectRows = await db.BoardObjects.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var commentRows = await db.BoardComments.AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        state.Seq = meta?.Seq ?? 0;
        state.UpdatedAtUnixMs = meta?.UpdatedAtUnixMs ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        state.Objects.Clear();
        state.Comments.Clear();

        foreach (var row in objectRows)
        {
            var props = DeserializeProps(row.PropsJson);
            var timestamps = DeserializeTimestamps(row.PropTimestampsJson);
            var deletedTs = row.DeletedLamport.HasValue
                ? new SpatialLwwTimestampDto(row.DeletedLamport.Value, row.DeletedWriterId ?? string.Empty)
                : null;

            state.Objects[row.ObjectId] = new MutableBoardObject
            {
                Id = row.ObjectId,
                Kind = AllowedObjectKinds.Contains(row.Kind) ? row.Kind : "sticky",
                ParentId = string.IsNullOrWhiteSpace(row.ParentId) ? null : row.ParentId,
                SortKey = string.IsNullOrWhiteSpace(row.SortKey) ? "V" : row.SortKey,
                X = row.X,
                Y = row.Y,
                Width = row.Width,
                Height = row.Height,
                Rotation = row.Rotation,
                Props = props,
                PropTimestamps = timestamps,
                Deleted = row.Deleted,
                DeletedTs = deletedTs,
                UpdatedAtUnixMs = row.UpdatedAtUnixMs
            };
        }

        foreach (var row in commentRows)
        {
            var mentions = DeserializeStringList(row.MentionsJson);
            var replies = DeserializeReplies(row.RepliesJson);
            state.Comments[row.PinId] = new SpatialBoardCommentPinDto
            {
                PinId = row.PinId,
                SessionId = row.SessionId,
                ObjectId = string.IsNullOrWhiteSpace(row.ObjectId) ? null : row.ObjectId,
                X = row.X,
                Y = row.Y,
                AuthorId = row.AuthorId,
                AuthorName = row.AuthorName,
                AuthorKind = row.AuthorKind,
                Body = row.Body,
                Mentions = mentions,
                Replies = replies,
                Resolved = row.Resolved,
                CreatedAtUnixMs = row.CreatedAtUnixMs,
                UpdatedAtUnixMs = row.UpdatedAtUnixMs
            };
        }

        state.Loaded = true;
    }

    private async Task PersistDirtyObjectsUnderLockAsync(
        Guid sessionId,
        SessionBoardState state,
        HashSet<string> dirtyObjectIds,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = dirtyObjectIds.ToArray();
        var existingRows = await db.BoardObjects
            .Where(x => x.SessionId == sessionId && ids.Contains(x.ObjectId))
            .ToDictionaryAsync(x => x.ObjectId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var id in ids)
        {
            if (!state.Objects.TryGetValue(id, out var obj)) continue;

            if (!existingRows.TryGetValue(id, out var row))
            {
                row = new SpatialBoardObjectRecord
                {
                    SessionId = sessionId,
                    ObjectId = id
                };
                db.BoardObjects.Add(row);
            }

            row.Kind = obj.Kind;
            row.ParentId = obj.ParentId;
            row.SortKey = obj.SortKey;
            row.X = obj.X;
            row.Y = obj.Y;
            row.Width = obj.Width;
            row.Height = obj.Height;
            row.Rotation = obj.Rotation;
            row.PropsJson = JsonSerializer.Serialize(obj.Props, StorageJson);
            row.PropTimestampsJson = JsonSerializer.Serialize(obj.PropTimestamps, StorageJson);
            row.Deleted = obj.Deleted;
            row.DeletedLamport = obj.DeletedTs?.Lamport;
            row.DeletedWriterId = obj.DeletedTs?.WriterId;
            row.UpdatedAtUnixMs = obj.UpdatedAtUnixMs;
        }

        await UpsertMetaRowAsync(db, sessionId, state.Seq, state.UpdatedAtUnixMs, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistCommentUpsertUnderLockAsync(
        Guid sessionId,
        SessionBoardState state,
        SpatialBoardCommentPinDto pin,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.BoardComments
            .FirstOrDefaultAsync(x => x.PinId == pin.PinId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new SpatialBoardCommentRecord
            {
                PinId = pin.PinId,
                SessionId = sessionId,
                CreatedAtUnixMs = pin.CreatedAtUnixMs
            };
            db.BoardComments.Add(row);
        }

        row.SessionId = sessionId;
        row.ObjectId = pin.ObjectId;
        row.X = pin.X;
        row.Y = pin.Y;
        row.AuthorId = pin.AuthorId;
        row.AuthorName = pin.AuthorName;
        row.AuthorKind = pin.AuthorKind;
        row.Body = pin.Body;
        row.MentionsJson = JsonSerializer.Serialize(pin.Mentions, StorageJson);
        row.RepliesJson = JsonSerializer.Serialize(pin.Replies, StorageJson);
        row.Resolved = pin.Resolved;
        row.UpdatedAtUnixMs = pin.UpdatedAtUnixMs;

        await UpsertMetaRowAsync(db, sessionId, state.Seq, state.UpdatedAtUnixMs, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertMetaRowAsync(
        SpatialBoardDbContext db,
        Guid sessionId,
        long seq,
        long updatedAtUnixMs,
        CancellationToken cancellationToken)
    {
        var meta = await db.BoardMeta
            .FirstOrDefaultAsync(x => x.SessionId == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (meta is null)
        {
            db.BoardMeta.Add(new SpatialBoardMetaRecord
            {
                SessionId = sessionId,
                Seq = seq,
                UpdatedAtUnixMs = updatedAtUnixMs
            });
        }
        else
        {
            meta.Seq = seq;
            meta.UpdatedAtUnixMs = updatedAtUnixMs;
        }
    }

    private static bool WouldCreateParentCycle(
        string objectId,
        string? candidateParentId,
        IReadOnlyDictionary<string, MutableBoardObject> objects)
    {
        if (string.IsNullOrEmpty(candidateParentId)) return false;
        if (string.Equals(objectId, candidateParentId, StringComparison.Ordinal)) return true;

        var visited = new HashSet<string>(StringComparer.Ordinal) { objectId };
        var cursor = candidateParentId;
        while (!string.IsNullOrEmpty(cursor))
        {
            if (!visited.Add(cursor)) return true;
            if (!objects.TryGetValue(cursor, out var parentObj))
                break;
            cursor = parentObj.ParentId;
        }
        return false;
    }

    private static bool ShouldWinLww(SpatialLwwTimestampDto incoming, SpatialLwwTimestampDto? existing) =>
        existing is null || SpatialLwwTimestampDto.Compare(incoming, existing) >= 0;

    private static bool HasTimestampKey(
        IReadOnlyDictionary<string, SpatialLwwTimestampDto>? timestamps,
        string primaryKey,
        string? aliasKey)
    {
        if (timestamps is null || timestamps.Count == 0) return false;
        if (timestamps.ContainsKey(primaryKey)) return true;
        return aliasKey is not null && timestamps.ContainsKey(aliasKey);
    }

    private static SpatialLwwTimestampDto? NormalizeTimestamp(SpatialLwwTimestampDto? ts, string fallbackWriterId)
    {
        if (ts is null) return null;
        return string.IsNullOrWhiteSpace(ts.WriterId)
            ? new SpatialLwwTimestampDto(ts.Lamport, fallbackWriterId)
            : ts;
    }

    private static SpatialLwwTimestampDto ResolveFieldTimestamp(
        IReadOnlyDictionary<string, SpatialLwwTimestampDto>? timestamps,
        string primaryKey,
        string? aliasKey,
        string fallbackWriterId,
        SpatialLwwTimestampDto fallback)
    {
        if (timestamps is not null)
        {
            if (timestamps.TryGetValue(primaryKey, out var exact) && exact is not null)
                return NormalizeTimestamp(exact, fallbackWriterId)!;
            if (aliasKey is not null && timestamps.TryGetValue(aliasKey, out var alias) && alias is not null)
                return NormalizeTimestamp(alias, fallbackWriterId)!;
            if (timestamps.TryGetValue("*", out var wildcard) && wildcard is not null)
                return NormalizeTimestamp(wildcard, fallbackWriterId)!;
        }
        return fallback;
    }

    private static SpatialLwwTimestampDto ResolvePropTimestamp(
        IReadOnlyDictionary<string, SpatialLwwTimestampDto>? timestamps,
        string propKey,
        string fallbackWriterId,
        SpatialLwwTimestampDto fallback)
    {
        if (timestamps is not null)
        {
            if (timestamps.TryGetValue($"props.{propKey}", out var prefixed) && prefixed is not null)
                return NormalizeTimestamp(prefixed, fallbackWriterId)!;
            if (timestamps.TryGetValue(propKey, out var direct) && direct is not null)
                return NormalizeTimestamp(direct, fallbackWriterId)!;
            if (timestamps.TryGetValue("props", out var groupTs) && groupTs is not null)
                return NormalizeTimestamp(groupTs, fallbackWriterId)!;
            if (timestamps.TryGetValue("*", out var wildcard) && wildcard is not null)
                return NormalizeTimestamp(wildcard, fallbackWriterId)!;
        }
        return fallback;
    }

    private static SpatialLwwTimestampDto? GetExistingFieldTimestamp(
        MutableBoardObject obj,
        string primaryKey,
        string? aliasKey)
    {
        if (obj.PropTimestamps.TryGetValue(primaryKey, out var exact))
            return exact;
        if (aliasKey is not null && obj.PropTimestamps.TryGetValue(aliasKey, out var alias))
            return alias;
        return null;
    }

    private static SpatialLwwTimestampDto? GetExistingPropTimestamp(MutableBoardObject obj, string propKey)
    {
        if (obj.PropTimestamps.TryGetValue($"props.{propKey}", out var prefixed))
            return prefixed;
        if (obj.PropTimestamps.TryGetValue(propKey, out var direct))
            return direct;
        return null;
    }

    private static SpatialLwwTimestampDto BuildFallbackTimestamp(
        MutableBoardObject obj,
        SpatialPropertyOpDto op,
        string writerId)
    {
        long maxExisting = obj.DeletedTs?.Lamport ?? 0;
        foreach (var ts in obj.PropTimestamps.Values)
        {
            if (ts.Lamport > maxExisting) maxExisting = ts.Lamport;
        }

        long maxIncoming = op.DeletedTs?.Lamport ?? 0;
        if (op.Timestamps is not null)
        {
            foreach (var ts in op.Timestamps.Values)
            {
                if (ts is not null && ts.Lamport > maxIncoming)
                    maxIncoming = ts.Lamport;
            }
        }

        var lamport = maxIncoming > 0 ? maxIncoming : maxExisting + 1;
        return new SpatialLwwTimestampDto(lamport, writerId);
    }

    /// <summary>
    /// Generates a deterministic base-62 Fractional Index key strictly greater than any sibling key,
    /// without trailing <c>'0'</c> so arbitrary midpoint insertions remain valid.
    /// </summary>
    private static string GenerateDeterministicSortKey(
        IReadOnlyDictionary<string, MutableBoardObject>? objects,
        string? parentId,
        string excludeObjectId)
    {
        string? maxSiblingKey = null;
        if (objects is not null)
        {
            foreach (var candidate in objects.Values)
            {
                if (candidate.Deleted) continue;
                if (string.Equals(candidate.Id, excludeObjectId, StringComparison.Ordinal)) continue;
                if (!string.Equals(candidate.ParentId, parentId, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(candidate.SortKey)) continue;

                if (maxSiblingKey is null || string.CompareOrdinal(candidate.SortKey, maxSiblingKey) > 0)
                {
                    maxSiblingKey = candidate.SortKey;
                }
            }
        }

        return NextFractionalKeyAfter(maxSiblingKey);
    }

    public static string NextFractionalKeyAfter(string? lastKey)
    {
        if (string.IsNullOrWhiteSpace(lastKey))
            return "V";

        var trimmed = lastKey.Trim();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var digitIdx = Base62Digits.IndexOf(trimmed[i]);
            if (digitIdx < 0)
                return "V";
            if (digitIdx < Base62Digits.Length - 1)
            {
                var mid = (digitIdx + Base62Digits.Length) / 2;
                return string.Concat(trimmed.AsSpan(0, i), Base62Digits.AsSpan(mid, 1));
            }
        }

        return trimmed + "V";
    }

    private static void ValidateBoardObjectPropsOrThrow(
        IReadOnlyList<SpatialPropertyOpDto> ops,
        IReadOnlyDictionary<string, MutableBoardObject> existingObjects,
        string writerId)
    {
        var simulatedKinds = new Dictionary<string, (string Kind, SpatialLwwTimestampDto? KindTs)>(StringComparer.Ordinal);

        foreach (var op in ops)
        {
            var objectId = op.ObjectId?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(objectId) || objectId.Length > 128)
                continue;

            string? normalizedKind = null;
            if (!string.IsNullOrWhiteSpace(op.Kind))
            {
                normalizedKind = op.Kind.Trim().ToLowerInvariant();
                if (!AllowedObjectKinds.Contains(normalizedKind))
                    continue;
            }

            existingObjects.TryGetValue(objectId, out var existingObj);

            string currentKind;
            SpatialLwwTimestampDto? currentKindTs;
            if (simulatedKinds.TryGetValue(objectId, out var sim))
            {
                currentKind = sim.Kind;
                currentKindTs = sim.KindTs;
            }
            else if (existingObj is not null)
            {
                currentKind = existingObj.Kind;
                currentKindTs = GetExistingFieldTimestamp(existingObj, "kind", null);
            }
            else
            {
                currentKind = normalizedKind ?? "sticky";
                currentKindTs = null;
            }

            var effectiveKind = currentKind;
            var effectiveKindTs = currentKindTs;
            if (normalizedKind is not null)
            {
                var fallbackTs = existingObj is not null
                    ? BuildFallbackTimestamp(existingObj, op, writerId)
                    : new SpatialLwwTimestampDto(1, writerId);
                var incomingKindTs = ResolveFieldTimestamp(op.Timestamps, "kind", null, writerId, fallbackTs);
                if (ShouldWinLww(incomingKindTs, currentKindTs))
                {
                    effectiveKind = normalizedKind;
                    effectiveKindTs = incomingKindTs;
                }
            }

            simulatedKinds[objectId] = (effectiveKind, effectiveKindTs);

            if (!string.Equals(effectiveKind, "board", StringComparison.Ordinal))
                continue;

            if (op.Props is not null)
            {
                foreach (var propKey in op.Props.Keys)
                {
                    if (string.IsNullOrWhiteSpace(propKey)) continue;
                    var trimmedKey = propKey.Trim();
                    if (ForbiddenBoardPropKeys.Contains(trimmedKey))
                    {
                        throw new SpatialBoardValidationException(
                            "board_props_invalid",
                            $"Spatial object '{objectId}' of kind 'board' must only store view projection metadata in props; forbidden task payload key '{trimmedKey}' is not allowed.");
                    }
                }
            }

            if (existingObj is not null && !string.Equals(existingObj.Kind, "board", StringComparison.Ordinal))
            {
                foreach (var existingKey in existingObj.Props.Keys)
                {
                    var trimmedExisting = existingKey.Trim();
                    if (ForbiddenBoardPropKeys.Contains(trimmedExisting))
                    {
                        throw new SpatialBoardValidationException(
                            "board_props_invalid",
                            $"Spatial object '{objectId}' cannot be converted to kind 'board' while props contain forbidden task payload key '{trimmedExisting}'.");
                    }
                }
            }
        }
    }

    private static (double Width, double Height) DefaultDimensionsForKind(string kind) => kind switch
    {
        "board" => (780, 480),
        "section" => (640, 420),
        "work" => (320, 180),
        "sticky" => (220, 220),
        "text" => (240, 56),
        "media" => (320, 220),
        "drawing" => (200, 120),
        "connector" => (120, 40),
        _ => (220, 160)
    };

    private static string NormalizeAuthorKind(string? kind) =>
        string.Equals(kind?.Trim(), "ai", StringComparison.OrdinalIgnoreCase) ? "ai" : "human";

    private static List<string> NormalizeMentions(List<string>? mentions) =>
        mentions is null
            ? []
            : mentions
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();

    private static SpatialBoardCommentPinDto CloneCommentPin(SpatialBoardCommentPinDto src) => new()
    {
        PinId = src.PinId,
        SessionId = src.SessionId,
        ObjectId = src.ObjectId,
        X = src.X,
        Y = src.Y,
        AuthorId = src.AuthorId,
        AuthorName = src.AuthorName,
        AuthorKind = src.AuthorKind,
        Body = src.Body,
        Mentions = [.. src.Mentions],
        Replies = src.Replies.Select(r => new SpatialBoardCommentReplyDto
        {
            ReplyId = r.ReplyId,
            AuthorId = r.AuthorId,
            AuthorName = r.AuthorName,
            AuthorKind = r.AuthorKind,
            Body = r.Body,
            Mentions = [.. r.Mentions],
            CreatedAtUnixMs = r.CreatedAtUnixMs
        }).ToList(),
        Resolved = src.Resolved,
        CreatedAtUnixMs = src.CreatedAtUnixMs,
        UpdatedAtUnixMs = src.UpdatedAtUnixMs
    };

    private static Dictionary<string, JsonElement> DeserializeProps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, StorageJson);
            if (parsed is null) return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            return parsed.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, SpatialLwwTimestampDto> DeserializeTimestamps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, SpatialLwwTimestampDto>(StringComparer.Ordinal);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, SpatialLwwTimestampDto>>(json, StorageJson);
            return parsed is null
                ? new Dictionary<string, SpatialLwwTimestampDto>(StringComparer.Ordinal)
                : new Dictionary<string, SpatialLwwTimestampDto>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, SpatialLwwTimestampDto>(StringComparer.Ordinal);
        }
    }

    private static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, StorageJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<SpatialBoardCommentReplyDto> DeserializeReplies(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SpatialBoardCommentReplyDto>>(json, StorageJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
