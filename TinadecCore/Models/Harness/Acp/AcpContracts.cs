using System.Text.Json;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>Session-update types that count as user-visible progress. Heartbeats and usage do not.</summary>
internal static class AcpProgress
{
    public static readonly IReadOnlySet<string> UpdateTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "agent_message_chunk",
        "agent_thought_chunk",
        "tool_call",
        "tool_call_update"
    };

    /// <summary>
    /// Only these events may reset the idle timer. A wedged model API still emits
    /// <c>usage_update</c> and keepalives, so counting those as progress would let a hung agent live
    /// forever on its own heartbeats and the run would never be reaped.
    /// </summary>
    public static bool IsProgress(string? sessionUpdate) =>
        sessionUpdate is not null && UpdateTypes.Contains(sessionUpdate);
}

/// <summary>
/// Stable cache and turn-lease key for hosted ACP sessions. Single-agent callers use empty
/// <see cref="RunScope"/> and <see cref="TeammateScope"/> so they share one process per provider;
/// Swarm teammates carry their run and teammate handle so concurrent teammates using the same CLI
/// provider own separate subprocesses, worktree <c>cwd</c>s, and turn leases.
/// </summary>
internal readonly record struct AcpSessionKey
{
    public Guid ProviderInstanceId { get; }
    public string RunScope { get; }
    public string TeammateScope { get; }

    public AcpSessionKey(Guid providerInstanceId, string? runScope = null, string? teammateScope = null)
    {
        ProviderInstanceId = providerInstanceId;
        RunScope = NormalizeScope(runScope);
        TeammateScope = NormalizeScope(teammateScope);
    }

    public bool IsTeammateScoped => RunScope.Length > 0 || TeammateScope.Length > 0;

    public static AcpSessionKey ForProvider(Guid providerInstanceId) => new(providerInstanceId);

    public static AcpSessionKey From(Guid providerInstanceId, string? runId = null, string? teammateHandle = null) =>
        new(providerInstanceId, runId, teammateHandle);

    public static AcpSessionKey FromRequest(AcpSessionRequest request) =>
        new(request.ProviderInstanceId, request.RunId, request.TeammateHandle);

    private static string NormalizeScope(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

/// <summary>How a session is opened: what to spawn, where it is allowed to work, and what to restore.</summary>
/// <param name="ScratchDirectory">
/// The directory handed to the agent as <c>cwd</c>. For single-agent runs this is Core's governed
/// scratch root; for isolated Swarm teammates it may be the teammate's dedicated Git Worktree path.
/// </param>
internal sealed record AcpSessionRequest(
    Guid ProviderInstanceId,
    string HarnessId,
    string BinaryPath,
    IReadOnlyList<string> Argv,
    string ScratchDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? RestoreSessionId = null,
    string? RunId = null,
    string? TeammateHandle = null,
    IReadOnlyList<string>? WriteScope = null);

/// <summary>Agent capabilities as advertised by <c>initialize</c>, flattened to what Core branches on.</summary>
internal sealed record AcpAgentCapabilitiesSnapshot(
    bool LoadSession,
    bool ImageInput,
    bool SessionFork)
{
    public static AcpAgentCapabilitiesSnapshot From(AcpAgentCapabilities? capabilities) => new(
        capabilities?.LoadSession ?? false,
        capabilities?.PromptCapabilities?.Image ?? false,
        capabilities?.SessionCapabilities?.Fork ?? false);
}

/// <summary>Token accounting harvested from <c>usage_update</c>, kept provider-neutral on purpose.</summary>
internal sealed record AcpUsageSnapshot(
    long? Used,
    long? ContextWindow,
    double? CostUsd);

/// <summary>
/// A file the harness changed, read out of a completed <c>tool_call</c>'s diff content. This is the
/// evidence that <c>fs</c> capabilities being false does not stop the agent writing: the diff comes
/// from the agent's own edit, reported after the fact.
/// </summary>
internal sealed record AcpFileChange(
    string Path,
    string Before,
    string After,
    bool Added);

/// <summary>
/// Merged state for one tool call. <c>tool_call_update</c> arrives as a partial patch, so fields are
/// carried forward from the earlier frame rather than replaced wholesale.
/// </summary>
internal sealed record AcpToolCallSnapshot(
    string ToolCallId,
    string? Title,
    string? Kind,
    string Status,
    bool IsSubagent,
    JsonElement? RawInput,
    JsonElement? Content);

/// <summary>Refusal the router made on the agent's behalf, recorded rather than hidden.</summary>
internal sealed record AcpPermissionRefusal(
    string? ToolCallId,
    string? Title,
    string? OptionId,
    string Reason);

/// <summary>Why a turn ended the way it did.</summary>
internal enum AcpTurnCompletion
{
    /// <summary>The agent finished the turn normally.</summary>
    Completed,

    /// <summary>The session was cancelled by us; the partial answer is still what the agent produced.</summary>
    Cancelled,

    /// <summary>Output-length or turn-request limits.</summary>
    Truncated,

    /// <summary>The agent refused the turn.</summary>
    Refused
}

/// <summary>Everything one turn produced.</summary>
internal sealed record AcpTurnResult(
    string Answer,
    string? StopReason,
    AcpTurnCompletion Completion,
    AcpUsageSnapshot Usage);
