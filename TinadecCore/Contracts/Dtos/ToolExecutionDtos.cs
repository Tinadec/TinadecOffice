using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>Wire request Core sends to a TinadecTools child process (line-delimited JSON).</summary>
public sealed class ToolWireRequestDto
{
    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("toolcall_id")]
    public long ToolCallId { get; init; }

    [JsonPropertyName("approved")]
    public bool Approved { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

}

/// <summary>Wire response a TinadecTools child process returns for one call.</summary>
public sealed class ToolWireResponseDto
{
    [JsonPropertyName("call_id")]
    public long CallId { get; init; }

    [JsonPropertyName("success")]
    public bool IsSuccess { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Unsolicited notification a TinadecTools child process emits <em>while</em> a call is in
/// flight. Event lines are distinguished from responses by their <c>kind</c> field.
/// <c>call_id</c> &lt;= 0 designates a broadcast event, e.g. the exit of a long-lived
/// terminal session whose originating call has already returned.
/// </summary>
public sealed class ToolWireEventDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "event";

    [JsonPropertyName("call_id")]
    public long CallId { get; init; }

    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}

/// <summary>Manifest handshake payload returned for the reserved <c>#manifest</c> tool call.</summary>
public sealed class ToolManifestDto
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("manifest_hash")]
    public string? ManifestHash { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolManifestEntryDto> Tools { get; init; } = [];
}

public sealed class ToolManifestEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; init; }

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; init; } = ToolManifestSchema.DefaultInputSchema;

    [JsonPropertyName("risk")]
    public string Risk { get; init; } = "low";

    [JsonPropertyName("mutates_workspace")]
    public bool MutatesWorkspace { get; init; }

    [JsonPropertyName("retry_safety")]
    public string RetrySafety { get; init; } = "safe";

    [JsonPropertyName("confirmation_fields")]
    public IReadOnlyList<string> ConfirmationFields { get; init; } = [];
}

/// <summary>Shared v2 manifest defaults and validation-free schema construction.</summary>
public static class ToolManifestSchema
{
    public static JsonElement DefaultInputSchema { get; } =
        JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":true}").RootElement.Clone();
}

/// <summary>Inbound dispatch request from a coordinator worker loop or the execute endpoint.</summary>
public sealed class ToolDispatchRequestDto
{
    // The HTTP execute route supplies this from its path. It is deliberately not
    // deserializable so a client cannot redirect a call to another run.
    [JsonIgnore]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("agent_instance_id")]
    public string AgentInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    /// <summary>
    /// Stable identity assigned by the durable worker checkpoint. HTTP callers
    /// may provide one to make a manual call idempotent; absent values receive a
    /// deterministic request-derived fallback inside Core.
    /// </summary>
    [JsonPropertyName("tool_call_key")]
    public string? ToolCallKey { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }

    [JsonIgnore]
    public int LeaseUses { get; init; } = 1;

    /// <summary>
    /// Lane the calling task belongs to; null reads as the implicit "main" lane.
    /// Deliberately not deserializable: lane ownership is decided by the durable
    /// task graph, and letting a client submit one would let it rewrite the
    /// audit dimension that lane-scoped pre-authorizations are matched against.
    /// </summary>
    [JsonIgnore]
    public string? LaneKey { get; init; }
}

/// <summary>
/// Human recovery decision for an execution whose result cannot be determined
/// after a timeout, process exit, or host-recovery boundary.
/// </summary>
public sealed class ToolExecutionRecoveryDecisionRequestDto
{
    [JsonPropertyName("decision")]
    public string Decision { get; init; } = string.Empty;
}

/// <summary>Outcome of one dispatched tool execution.</summary>
public sealed class ToolDispatchResultDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("execution_id")]
    public string? ExecutionId { get; init; }

    [JsonPropertyName("approval_id")]
    public string? ApprovalId { get; init; }

    [JsonPropertyName("permission_request_id")]
    public string? PermissionRequestId { get; init; }

    [JsonPropertyName("authorization_decision_id")]
    public string? AuthorizationDecisionId { get; init; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error_category")]
    public string? ErrorCategory { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// True when the approval decision window elapsed and the execution was
    /// parked. Deliberately not serializable: it is an engine-internal escalation
    /// signal, not part of the external dispatch contract.
    /// </summary>
    [JsonIgnore]
    public bool ParkExpired { get; init; }
}
