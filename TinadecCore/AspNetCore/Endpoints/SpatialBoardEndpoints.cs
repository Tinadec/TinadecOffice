using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Runtime.SpatialBoard;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Session-scoped multiplayer spatial board HTTP + WebSocket endpoints.
/// </summary>
public static class SpatialBoardEndpoints
{
    private const int MaxWebSocketMessageBytes = 4 * 1024 * 1024;

    public static IEndpointRouteBuilder MapSpatialBoardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sessions/{sessionId:guid}/spatial-board")
            .WithTags("SpatialBoard");

        group.MapGet(string.Empty, GetBoardSnapshotAsync)
            .Produces<SpatialBoardSnapshotDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/ops", PostBoardOpsAsync)
            .Accepts<SpatialBoardOpsRequestDto>("application/json")
            .Produces<SpatialBoardOpsResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/comments", CreateCommentAsync)
            .Accepts<CreateSpatialCommentRequestDto>("application/json")
            .Produces<SpatialBoardCommentPinDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/comments/{pinId}/replies", ReplyCommentAsync)
            .Accepts<ReplySpatialCommentRequestDto>("application/json")
            .Produces<SpatialBoardCommentPinDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/comments/{pinId}", PatchCommentAsync)
            .Accepts<PatchSpatialCommentRequestDto>("application/json")
            .Produces<SpatialBoardCommentPinDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/comments/{pinId}", DeleteCommentAsync)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/ws", HandleWebSocketAsync)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetBoardSnapshotAsync(
        Guid sessionId,
        bool? include_deleted,
        ISessionLocator sessions,
        SpatialBoardCoordinator coordinator,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
            return SessionNotFound();

        var snapshot = await coordinator.GetSnapshotAsync(sessionId, include_deleted == true, ct).ConfigureAwait(false);
        return Results.Ok(snapshot);
    }

    private static async Task<IResult> PostBoardOpsAsync(
        Guid sessionId,
        HttpRequest request,
        ISessionLocator sessions,
        SpatialBoardCoordinator coordinator,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
            return SessionNotFound();

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { code = "invalid_json", message = "Request body must be valid JSON." });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { code = "invalid_request", message = "Request body must be a JSON object." });

            if (!TryParseOpsRequest(doc.RootElement, fallbackClientId: "http-client", out var opsRequest, out var parseError))
                return Results.BadRequest(new { code = "invalid_request", message = parseError });

            try
            {
                var response = await coordinator.ApplyOpsAsync(sessionId, opsRequest!, senderConnectionId: null, ct).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (SpatialBoardValidationException ex)
            {
                return Results.BadRequest(new { code = ex.Code, message = ex.Message });
            }
        }
    }

    private static async Task<IResult> CreateCommentAsync(
        Guid sessionId,
        HttpRequest request,
        ISessionLocator sessions,
        SpatialBoardCoordinator coordinator,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
            return SessionNotFound();

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { code = "invalid_json", message = "Request body must be valid JSON." });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { code = "invalid_request", message = "Request body must be a JSON object." });

            var createDto = ParseCreateCommentRequest(doc.RootElement);
            try
            {
                var created = await coordinator.CreateCommentAsync(sessionId, createDto, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/sessions/{sessionId}/spatial-board/comments/{Uri.EscapeDataString(created.PinId)}", created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_request", message = ex.Message });
            }
        }
    }

    private static async Task<IResult> ReplyCommentAsync(
        Guid sessionId,
        string pinId,
        HttpRequest request,
        ISessionLocator sessions,
        SpatialBoardCoordinator coordinator,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
            return SessionNotFound();

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { code = "invalid_json", message = "Request body must be valid JSON." });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { code = "invalid_request", message = "Request body must be a JSON object." });

            var replyDto = ParseReplyCommentRequest(doc.RootElement);
            try
            {
                var updated = await coordinator.AddCommentReplyAsync(sessionId, pinId, replyDto, ct).ConfigureAwait(false);
                if (updated is null)
                    return CommentNotFound();
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_request", message = ex.Message });
            }
        }
    }

    private static async Task<IResult> PatchCommentAsync(
        Guid sessionId,
        string pinId,
        HttpRequest request,
        ISessionLocator sessions,
        SpatialBoardCoordinator coordinator,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
            return SessionNotFound();

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { code = "invalid_json", message = "Request body must be valid JSON." });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { code = "invalid_request", message = "Request body must be a JSON object." });

            var patchDto = ParsePatchCommentRequest(doc.RootElement);
            try
            {
                var updated = await coordinator.PatchCommentAsync(sessionId, pinId, patchDto, ct).ConfigureAwait(false);
                if (updated is null)
                    return CommentNotFound();
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_request", message = ex.Message });
            }
        }
    }

    private static async Task<IResult> DeleteCommentAsync(
        Guid sessionId,
        string pinId,
        ISessionLocator sessions,
        SpatialBoardCoordinator coordinator,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
            return SessionNotFound();

        var (deleted, seq) = await coordinator.DeleteCommentAsync(sessionId, pinId, ct).ConfigureAwait(false);
        if (!deleted)
            return CommentNotFound();

        return Results.Ok(new { deleted = true, pin_id = pinId, seq });
    }

    private static async Task<IResult> HandleWebSocketAsync(
        HttpContext httpContext,
        Guid sessionId,
        ISessionLocator sessions,
        SpatialBoardCoordinator coordinator,
        SpatialPresenceHub presenceHub,
        CancellationToken ct)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(new
            {
                code = "websocket_upgrade_required",
                message = "Expected a WebSocket upgrade request."
            });
        }

        var session = await sessions.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
            return SessionNotFound();

        var query = httpContext.Request.Query;
        var initialClientId = FirstNonEmpty(query["client_id"], query["clientId"]);
        var initialDisplayName = FirstNonEmpty(query["display_name"], query["displayName"]);
        var initialColor = FirstNonEmpty(query["color"]);

        using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var peer = presenceHub.RegisterPeer(sessionId, webSocket, initialClientId, initialDisplayName, initialColor);

        try
        {
            var initialSnapshot = await coordinator.GetSnapshotAsync(sessionId, includeDeleted: false, ct).ConfigureAwait(false);
            await presenceHub.SendToPeerAsync(peer, new
            {
                type = "snapshot",
                session_id = initialSnapshot.SessionId,
                seq = initialSnapshot.Seq,
                objects = initialSnapshot.Objects,
                comments = initialSnapshot.Comments,
                collaborators = initialSnapshot.Collaborators,
                updated_at_unix_ms = initialSnapshot.UpdatedAtUnixMs,
                snapshot = initialSnapshot
            }, ct).ConfigureAwait(false);

            await presenceHub.BroadcastAsync(sessionId, new
            {
                type = "presence_update",
                session_id = sessionId,
                collaborator = peer.ToDto(),
                collaborators = initialSnapshot.Collaborators
            }, excludeConnectionId: peer.ConnectionId, ct).ConfigureAwait(false);

            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                using var messageBuffer = new MemoryStream();
                while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    messageBuffer.SetLength(0);
                    WebSocketReceiveResult receiveResult;
                    var oversized = false;

                    do
                    {
                        receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                            break;

                        if (receiveResult.Count > 0)
                        {
                            if (messageBuffer.Length + receiveResult.Count > MaxWebSocketMessageBytes)
                            {
                                oversized = true;
                            }
                            else
                            {
                                messageBuffer.Write(buffer, 0, receiveResult.Count);
                            }
                        }
                    }
                    while (!receiveResult.EndOfMessage);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                        break;

                    if (oversized)
                    {
                        await presenceHub.SendToPeerAsync(peer, new
                        {
                            type = "error",
                            code = "message_too_large",
                            message = $"WebSocket frame exceeds {MaxWebSocketMessageBytes} bytes."
                        }, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (receiveResult.MessageType != WebSocketMessageType.Text || messageBuffer.Length == 0)
                        continue;

                    if (!messageBuffer.TryGetBuffer(out var segment))
                        segment = new ArraySegment<byte>(messageBuffer.ToArray());

                    await ProcessInboundWebSocketFrameAsync(
                        sessionId,
                        peer,
                        segment.AsMemory(),
                        coordinator,
                        presenceHub,
                        ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
            // Normal client disconnect or aborted transport.
        }
        finally
        {
            await presenceHub.UnregisterPeerAsync(sessionId, peer.ConnectionId, CancellationToken.None).ConfigureAwait(false);
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session closed", closeCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Socket already closed by peer.
                }
            }
        }

        return Results.Empty;
    }

    private static async Task ProcessInboundWebSocketFrameAsync(
        Guid sessionId,
        SpatialPeerConnection peer,
        ReadOnlyMemory<byte> utf8Payload,
        SpatialBoardCoordinator coordinator,
        SpatialPresenceHub presenceHub,
        CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Payload);
        }
        catch (JsonException)
        {
            await presenceHub.SendToPeerAsync(peer, new
            {
                type = "error",
                code = "invalid_json",
                message = "WebSocket message must be valid JSON."
            }, ct).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await presenceHub.SendToPeerAsync(peer, new
                {
                    type = "error",
                    code = "invalid_frame",
                    message = "WebSocket message must be a JSON object."
                }, ct).ConfigureAwait(false);
                return;
            }

            var frameType = ReadString(root, "type")?.Trim().ToLowerInvariant() ?? string.Empty;
            switch (frameType)
            {
                case "hello":
                {
                    var patch = ParsePresencePatch(root, includeIdentity: true);
                    var updated = await presenceHub.UpdatePresenceAsync(sessionId, peer.ConnectionId, patch, ct).ConfigureAwait(false);
                    var collaborators = await presenceHub.GetCollaboratorsAsync(sessionId, ct).ConfigureAwait(false);
                    await presenceHub.SendToPeerAsync(peer, new
                    {
                        type = "presence_update",
                        session_id = sessionId,
                        collaborator = updated ?? peer.ToDto(),
                        collaborators
                    }, ct).ConfigureAwait(false);
                    break;
                }

                case "presence":
                {
                    var patch = ParsePresencePatch(root, includeIdentity: true);
                    await presenceHub.UpdatePresenceAsync(sessionId, peer.ConnectionId, patch, ct).ConfigureAwait(false);
                    break;
                }

                case "ops":
                {
                    var requestId = ReadString(root, "request_id", "requestId");
                    if (!TryParseOpsRequest(root, fallbackClientId: peer.ClientId, out var opsRequest, out var parseError))
                    {
                        await presenceHub.SendToPeerAsync(peer, new
                        {
                            type = "error",
                            request_id = requestId,
                            code = "invalid_ops_frame",
                            message = parseError
                        }, ct).ConfigureAwait(false);
                        return;
                    }

                    SpatialBoardOpsResponseDto result;
                    try
                    {
                        result = await coordinator.ApplyOpsAsync(
                            sessionId,
                            opsRequest!,
                            senderConnectionId: peer.ConnectionId,
                            ct).ConfigureAwait(false);
                    }
                    catch (SpatialBoardValidationException ex)
                    {
                        await presenceHub.SendToPeerAsync(peer, new
                        {
                            type = "error",
                            request_id = requestId,
                            code = ex.Code,
                            message = ex.Message
                        }, ct).ConfigureAwait(false);
                        return;
                    }

                    await presenceHub.SendToPeerAsync(peer, new
                    {
                        type = "ops_ack",
                        request_id = requestId,
                        session_id = sessionId,
                        seq = result.Seq,
                        accepted_op_ids = result.AcceptedOpIds,
                        rejected_ops = result.RejectedOps,
                        objects = result.Objects
                    }, ct).ConfigureAwait(false);
                    break;
                }

                case "ping":
                {
                    long? clientTs = ReadInt64(root, "ts", "timestamp");
                    await presenceHub.SendToPeerAsync(peer, new
                    {
                        type = "pong",
                        ts = clientTs,
                        server_time_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }, ct).ConfigureAwait(false);
                    break;
                }

                default:
                {
                    await presenceHub.SendToPeerAsync(peer, new
                    {
                        type = "error",
                        code = "unsupported_frame_type",
                        message = $"Unsupported spatial board WebSocket frame type '{frameType}'."
                    }, ct).ConfigureAwait(false);
                    break;
                }
            }
        }
    }

    private static bool TryParseOpsRequest(
        JsonElement root,
        string fallbackClientId,
        out SpatialBoardOpsRequestDto? request,
        out string error)
    {
        request = null;
        error = string.Empty;

        var payloadRoot = root;
        if (!root.TryGetProperty("ops", out _)
            && root.TryGetProperty("payload", out var nestedPayload)
            && nestedPayload.ValueKind == JsonValueKind.Object)
        {
            payloadRoot = nestedPayload;
        }

        if (!payloadRoot.TryGetProperty("ops", out var opsArray) || opsArray.ValueKind != JsonValueKind.Array)
        {
            error = "Field 'ops' must be a JSON array.";
            return false;
        }

        var clientId = ReadString(payloadRoot, "client_id", "clientId")
            ?? ReadString(root, "client_id", "clientId")
            ?? fallbackClientId;
        var baseSeq = ReadInt64(payloadRoot, "base_seq", "baseSeq") ?? 0;

        var ops = new List<SpatialPropertyOpDto>(opsArray.GetArrayLength());
        var idx = 0;
        foreach (var item in opsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"ops[{idx}] must be a JSON object.";
                return false;
            }

            var hasParentProp = item.TryGetProperty("parent_id", out var parentSnake)
                || item.TryGetProperty("parentId", out parentSnake);
            string? parentVal = null;
            if (hasParentProp && parentSnake.ValueKind == JsonValueKind.String)
                parentVal = parentSnake.GetString();

            var hasParentFlag = ReadBool(item, "has_parent_id", "hasParentId") ?? hasParentProp;

            Dictionary<string, JsonElement>? props = null;
            if (item.TryGetProperty("props", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
            {
                props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var prop in propsEl.EnumerateObject())
                {
                    props[prop.Name] = prop.Value.Clone();
                }
            }

            Dictionary<string, SpatialLwwTimestampDto>? timestamps = null;
            if ((item.TryGetProperty("timestamps", out var tsEl) || item.TryGetProperty("prop_timestamps", out tsEl) || item.TryGetProperty("propTimestamps", out tsEl))
                && tsEl.ValueKind == JsonValueKind.Object)
            {
                timestamps = new Dictionary<string, SpatialLwwTimestampDto>(StringComparer.Ordinal);
                foreach (var entry in tsEl.EnumerateObject())
                {
                    if (TryParseTimestamp(entry.Value, clientId, out var parsedTs))
                    {
                        timestamps[entry.Name] = parsedTs!;
                    }
                }
            }

            SpatialLwwTimestampDto? deletedTs = null;
            if ((item.TryGetProperty("deleted_ts", out var delTsEl) || item.TryGetProperty("deletedTs", out delTsEl))
                && TryParseTimestamp(delTsEl, clientId, out var parsedDelTs))
            {
                deletedTs = parsedDelTs;
            }

            ops.Add(new SpatialPropertyOpDto
            {
                ClientOpId = ReadString(item, "client_op_id", "clientOpId") ?? $"op-{idx}",
                ObjectId = ReadString(item, "object_id", "objectId", "id") ?? string.Empty,
                Kind = ReadString(item, "kind"),
                ParentId = parentVal,
                HasParentId = hasParentFlag,
                SortKey = ReadString(item, "sort_key", "sortKey"),
                X = ReadDouble(item, "x"),
                Y = ReadDouble(item, "y"),
                Width = ReadDouble(item, "width"),
                Height = ReadDouble(item, "height"),
                Rotation = ReadDouble(item, "rotation"),
                Props = props,
                Timestamps = timestamps,
                Deleted = ReadBool(item, "deleted"),
                DeletedTs = deletedTs
            });

            idx++;
        }

        request = new SpatialBoardOpsRequestDto
        {
            ClientId = clientId,
            BaseSeq = baseSeq,
            Ops = ops
        };
        return true;
    }

    private static bool TryParseTimestamp(JsonElement el, string fallbackWriterId, out SpatialLwwTimestampDto? ts)
    {
        ts = null;
        if (el.ValueKind != JsonValueKind.Object) return false;
        var lamport = ReadInt64(el, "lamport") ?? 0;
        var writerId = ReadString(el, "writer_id", "writerId") ?? fallbackWriterId;
        ts = new SpatialLwwTimestampDto(lamport, writerId);
        return true;
    }

    private static SpatialPresencePatch ParsePresencePatch(JsonElement root, bool includeIdentity)
    {
        var payload = root.TryGetProperty("presence", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;

        double? cursorX = ReadDouble(payload, "cursor_x", "cursorX");
        double? cursorY = ReadDouble(payload, "cursor_y", "cursorY");
        var clearCursor = false;

        if (payload.TryGetProperty("cursor", out var cursorEl))
        {
            if (cursorEl.ValueKind == JsonValueKind.Null)
            {
                clearCursor = true;
            }
            else if (cursorEl.ValueKind == JsonValueKind.Object)
            {
                cursorX ??= ReadDouble(cursorEl, "x");
                cursorY ??= ReadDouble(cursorEl, "y");
            }
        }

        string[]? selection = null;
        if (payload.TryGetProperty("selection", out var selEl) && selEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>(selEl.GetArrayLength());
            foreach (var item in selEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } str)
                    list.Add(str);
            }
            selection = list.ToArray();
        }

        SpatialViewportDto? viewport = null;
        if (payload.TryGetProperty("viewport", out var vpEl) && vpEl.ValueKind == JsonValueKind.Object)
        {
            var vx = ReadDouble(vpEl, "x");
            var vy = ReadDouble(vpEl, "y");
            var vzoom = ReadDouble(vpEl, "zoom");
            if (vx.HasValue && vy.HasValue && vzoom.HasValue)
            {
                viewport = new SpatialViewportDto(vx.Value, vy.Value, vzoom.Value);
            }
        }

        var hasFollowing = payload.TryGetProperty("following_client_id", out var followEl)
            || payload.TryGetProperty("followingClientId", out followEl);
        string? followingId = hasFollowing && followEl.ValueKind == JsonValueKind.String
            ? followEl.GetString()
            : null;

        return new SpatialPresencePatch(
            ClientId: includeIdentity ? (ReadString(payload, "client_id", "clientId") ?? ReadString(root, "client_id", "clientId")) : null,
            DisplayName: includeIdentity ? (ReadString(payload, "display_name", "displayName") ?? ReadString(root, "display_name", "displayName")) : null,
            Color: includeIdentity ? (ReadString(payload, "color") ?? ReadString(root, "color")) : null,
            CursorX: cursorX,
            CursorY: cursorY,
            ClearCursor: clearCursor,
            Selection: selection,
            Viewport: viewport,
            ActiveTool: ReadString(payload, "active_tool", "activeTool"),
            FollowingClientId: followingId,
            HasFollowingClientId: hasFollowing);
    }

    private static CreateSpatialCommentRequestDto ParseCreateCommentRequest(JsonElement root) => new()
    {
        PinId = ReadString(root, "pin_id", "pinId", "id"),
        ObjectId = ReadString(root, "object_id", "objectId"),
        X = ReadDouble(root, "x") ?? 0,
        Y = ReadDouble(root, "y") ?? 0,
        AuthorId = ReadString(root, "author_id", "authorId"),
        AuthorName = ReadString(root, "author_name", "authorName"),
        AuthorKind = ReadString(root, "author_kind", "authorKind"),
        Body = ReadString(root, "body", "text", "content") ?? string.Empty,
        Mentions = ReadStringList(root, "mentions")
    };

    private static ReplySpatialCommentRequestDto ParseReplyCommentRequest(JsonElement root) => new()
    {
        ReplyId = ReadString(root, "reply_id", "replyId", "id"),
        AuthorId = ReadString(root, "author_id", "authorId"),
        AuthorName = ReadString(root, "author_name", "authorName"),
        AuthorKind = ReadString(root, "author_kind", "authorKind"),
        Body = ReadString(root, "body", "text", "content") ?? string.Empty,
        Mentions = ReadStringList(root, "mentions")
    };

    private static PatchSpatialCommentRequestDto ParsePatchCommentRequest(JsonElement root)
    {
        var hasObj = root.TryGetProperty("object_id", out var objEl) || root.TryGetProperty("objectId", out objEl);
        return new PatchSpatialCommentRequestDto
        {
            Body = ReadString(root, "body"),
            Mentions = ReadStringList(root, "mentions"),
            Resolved = ReadBool(root, "resolved"),
            X = ReadDouble(root, "x"),
            Y = ReadDouble(root, "y"),
            ObjectId = hasObj && objEl.ValueKind == JsonValueKind.String ? objEl.GetString() : null,
            HasObjectId = hasObj || (ReadBool(root, "has_object_id", "hasObjectId") ?? false)
        };
    }

    private static string? ReadString(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        return null;
    }

    private static double? ReadDouble(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
                return d;
        }
        return null;
    }

    private static long? ReadInt64(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var val))
                return val;
        }
        return null;
    }

    private static bool? ReadBool(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return el.GetBoolean();
        }
        return null;
    }

    private static List<string>? ReadStringList(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } str)
                list.Add(str);
        }
        return list;
    }

    private static string? FirstNonEmpty(params ReadOnlySpan<Microsoft.Extensions.Primitives.StringValues> values)
    {
        foreach (var value in values)
        {
            var str = value.ToString();
            if (!string.IsNullOrWhiteSpace(str))
                return str.Trim();
        }
        return null;
    }

    private static IResult SessionNotFound() =>
        Results.NotFound(new { code = "session_not_found", message = "Session not found." });

    private static IResult CommentNotFound() =>
        Results.NotFound(new { code = "comment_not_found", message = "Comment pin not found." });
}
