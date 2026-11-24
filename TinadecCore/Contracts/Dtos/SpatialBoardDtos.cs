using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// Logical LWW timestamp ordered by <see cref="Lamport"/> ascending, then ordinal <see cref="WriterId"/>.
/// </summary>
public sealed record SpatialLwwTimestampDto(
    [property: JsonPropertyName("lamport")] long Lamport,
    [property: JsonPropertyName("writer_id")] string WriterId)
{
    public static int Compare(SpatialLwwTimestampDto a, SpatialLwwTimestampDto b)
    {
        var cmp = a.Lamport.CompareTo(b.Lamport);
        if (cmp != 0) return cmp;
        return string.CompareOrdinal(a.WriterId ?? string.Empty, b.WriterId ?? string.Empty);
    }
}

/// <summary>
/// Authoritative object projection on a session's multiplayer spatial board.
/// </summary>
public sealed class SpatialBoardObjectDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Supported kinds: <c>work</c>, <c>section</c>, <c>sticky</c>, <c>text</c>, <c>drawing</c>, <c>media</c>, <c>connector</c>, <c>board</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "sticky";

    [JsonPropertyName("parent_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ParentId { get; init; }

    [JsonPropertyName("sort_key")]
    public string SortKey { get; init; } = "V";

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }

    [JsonPropertyName("rotation")]
    public double Rotation { get; init; }

    [JsonPropertyName("props")]
    public Dictionary<string, JsonElement> Props { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("prop_timestamps")]
    public Dictionary<string, SpatialLwwTimestampDto> PropTimestamps { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("deleted")]
    public bool Deleted { get; init; }

    [JsonPropertyName("deleted_ts")]
    public SpatialLwwTimestampDto? DeletedTs { get; init; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

/// <summary>
/// Property-level LWW mutation operation for one object on the spatial board.
/// </summary>
public sealed class SpatialPropertyOpDto
{
    [JsonPropertyName("client_op_id")]
    public string ClientOpId { get; init; } = string.Empty;

    [JsonPropertyName("object_id")]
    public string ObjectId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; init; }

    [JsonPropertyName("has_parent_id")]
    public bool HasParentId { get; init; }

    [JsonPropertyName("sort_key")]
    public string? SortKey { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    [JsonPropertyName("width")]
    public double? Width { get; init; }

    [JsonPropertyName("height")]
    public double? Height { get; init; }

    [JsonPropertyName("rotation")]
    public double? Rotation { get; init; }

    [JsonPropertyName("props")]
    public Dictionary<string, JsonElement>? Props { get; init; }

    [JsonPropertyName("timestamps")]
    public Dictionary<string, SpatialLwwTimestampDto>? Timestamps { get; init; }

    [JsonPropertyName("deleted")]
    public bool? Deleted { get; init; }

    [JsonPropertyName("deleted_ts")]
    public SpatialLwwTimestampDto? DeletedTs { get; init; }
}

/// <summary>
/// Batched spatial property mutations submitted over REST or WebSocket.
/// </summary>
public sealed class SpatialBoardOpsRequestDto
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; init; } = string.Empty;

    [JsonPropertyName("base_seq")]
    public long BaseSeq { get; init; }

    [JsonPropertyName("ops")]
    public List<SpatialPropertyOpDto> Ops { get; init; } = [];
}

/// <summary>
/// Individual operation rejected during batch validation (e.g., <c>parent_cycle_detected</c>).
/// </summary>
public sealed class SpatialBoardRejectedOpDto
{
    [JsonPropertyName("client_op_id")]
    public string ClientOpId { get; init; } = string.Empty;

    [JsonPropertyName("object_id")]
    public string ObjectId { get; init; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Result of applying a batch of property-level LWW operations.
/// </summary>
public sealed class SpatialBoardOpsResponseDto
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("accepted_op_ids")]
    public List<string> AcceptedOpIds { get; init; } = [];

    [JsonPropertyName("rejected_ops")]
    public List<SpatialBoardRejectedOpDto> RejectedOps { get; init; } = [];

    [JsonPropertyName("objects")]
    public Dictionary<string, SpatialBoardObjectDto> Objects { get; init; } = new(StringComparer.Ordinal);
}

public sealed class SpatialBoardCommentReplyDto
{
    [JsonPropertyName("reply_id")]
    public string ReplyId { get; init; } = string.Empty;

    [JsonPropertyName("author_id")]
    public string AuthorId { get; init; } = string.Empty;

    [JsonPropertyName("author_name")]
    public string AuthorName { get; init; } = string.Empty;

    [JsonPropertyName("author_kind")]
    public string AuthorKind { get; init; } = "human";

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("mentions")]
    public List<string> Mentions { get; init; } = [];

    [JsonPropertyName("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; init; }
}

public sealed class SpatialBoardCommentPinDto
{
    [JsonPropertyName("pin_id")]
    public string PinId { get; init; } = string.Empty;

    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("object_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ObjectId { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("author_id")]
    public string AuthorId { get; init; } = string.Empty;

    [JsonPropertyName("author_name")]
    public string AuthorName { get; init; } = string.Empty;

    [JsonPropertyName("author_kind")]
    public string AuthorKind { get; init; } = "human";

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("mentions")]
    public List<string> Mentions { get; init; } = [];

    [JsonPropertyName("replies")]
    public List<SpatialBoardCommentReplyDto> Replies { get; init; } = [];

    [JsonPropertyName("resolved")]
    public bool Resolved { get; init; }

    [JsonPropertyName("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; init; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

public sealed class CreateSpatialCommentRequestDto
{
    [JsonPropertyName("pin_id")]
    public string? PinId { get; init; }

    [JsonPropertyName("object_id")]
    public string? ObjectId { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("author_id")]
    public string? AuthorId { get; init; }

    [JsonPropertyName("author_name")]
    public string? AuthorName { get; init; }

    [JsonPropertyName("author_kind")]
    public string? AuthorKind { get; init; }

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("mentions")]
    public List<string>? Mentions { get; init; }
}

public sealed class ReplySpatialCommentRequestDto
{
    [JsonPropertyName("reply_id")]
    public string? ReplyId { get; init; }

    [JsonPropertyName("author_id")]
    public string? AuthorId { get; init; }

    [JsonPropertyName("author_name")]
    public string? AuthorName { get; init; }

    [JsonPropertyName("author_kind")]
    public string? AuthorKind { get; init; }

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;

    [JsonPropertyName("mentions")]
    public List<string>? Mentions { get; init; }
}

public sealed class PatchSpatialCommentRequestDto
{
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("mentions")]
    public List<string>? Mentions { get; init; }

    [JsonPropertyName("resolved")]
    public bool? Resolved { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    [JsonPropertyName("object_id")]
    public string? ObjectId { get; init; }

    [JsonPropertyName("has_object_id")]
    public bool HasObjectId { get; init; }
}

public sealed record SpatialViewportDto(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("zoom")] double Zoom);

/// <summary>
/// Live human or AI teammate collaborator presence on a spatial board.
/// </summary>
public sealed class SpatialCollaboratorDto
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; init; } = "#2563EB";

    /// <summary><c>human</c> or <c>ai</c>.</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = "human";

    [JsonPropertyName("agent_instance_id")]
    public string? AgentInstanceId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("cursor_x")]
    public double? CursorX { get; init; }

    [JsonPropertyName("cursor_y")]
    public double? CursorY { get; init; }

    [JsonPropertyName("selection")]
    public string[] Selection { get; init; } = [];

    [JsonPropertyName("viewport")]
    public SpatialViewportDto? Viewport { get; init; }

    [JsonPropertyName("active_tool")]
    public string? ActiveTool { get; init; }

    [JsonPropertyName("following_client_id")]
    public string? FollowingClientId { get; init; }

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}

/// <summary>
/// Full authoritative board state + active collaborators for a session.
/// </summary>
public sealed class SpatialBoardSnapshotDto
{
    [JsonPropertyName("session_id")]
    public Guid SessionId { get; init; }

    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("objects")]
    public Dictionary<string, SpatialBoardObjectDto> Objects { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("comments")]
    public List<SpatialBoardCommentPinDto> Comments { get; init; } = [];

    [JsonPropertyName("collaborators")]
    public List<SpatialCollaboratorDto> Collaborators { get; init; } = [];

    [JsonPropertyName("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; init; }
}
