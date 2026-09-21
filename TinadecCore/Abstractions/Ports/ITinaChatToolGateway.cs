using System.Text.Json;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Executes the TinaChat tools from inside a governed run. The Tools module owns dispatch and
/// never reads communication state itself, and the communication module never sees a run: this
/// port is the whole seam between them.
///
/// Every call carries the run's own frozen identity (tenant/workspace/principal/session) rather
/// than an ambient request principal, so a background turn can never speak as whoever happened to
/// make the last HTTP call, and a session with no binding has nothing to say with.
/// </summary>
public interface ITinaChatToolGateway
{
    Task<TinaChatToolOutcome> ExecuteAsync(TinaChatToolCall call, CancellationToken ct = default);

    /// <summary>
    /// The participant this session speaks as, with the same workspace-membership, active-status and
    /// ownership checks every tool call already applies. Exposed so a handoff served from above this
    /// module acts as that identity without re-implementing — or diverging from — the rules. Throws
    /// <c>TinaChatException</c> with an actionable sentence when there is nothing to act as.
    /// </summary>
    Task<Guid> RequireActorAsync(TinaChatToolCall call, CancellationToken ct = default);
}

public sealed record TinaChatToolCall(
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId,
    Guid SessionId,
    Guid RunId,
    /// <summary>Stable per model tool call; reused as the idempotency key so a replayed call returns the original write instead of a second message.</summary>
    long ToolCallId,
    string ToolId,
    JsonElement? Arguments);

/// <summary>
/// <paramref name="ResultJson"/> is the tool result handed straight back to the model; on failure
/// <paramref name="Error"/> must stay actionable (what was missing, what the caller may address),
/// never a bare "not allowed".
/// </summary>
public sealed record TinaChatToolOutcome(bool IsSuccess, string ResultJson, string? Error = null)
{
    public static TinaChatToolOutcome Failed(string error) => new(false, "{}", error);
}
