namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// ACP <c>session/request_permission</c> check context bridged from an external harness process
/// into TinadecCore's workspace/write-scope policy and interactive approval gate (#36).
/// </summary>
public sealed record AcpPermissionCheckRequest(
    string SessionId,
    string? RunId,
    string? TeammateHandle,
    string? WorkingDirectory,
    IReadOnlyList<string>? WriteScope,
    string? ToolCallId,
    string? Title,
    string? Kind,
    IReadOnlyList<string> AffectedPaths)
{
    /// <summary>Optional ACP option kinds offered by the peer (e.g. <c>allow_once</c>, <c>reject_once</c>).</summary>
    public IReadOnlyList<string>? AvailableOptionKinds { get; init; }

    /// <summary>Optional serialized parameters/locations summary for human approval inspection.</summary>
    public string? RawParametersJson { get; init; }
}

/// <summary>
/// Governed decision returned to the ACP interaction router.
/// </summary>
public sealed record AcpPermissionDecision(
    bool Approved,
    string Reason,
    string? SelectedOptionKind = null);

/// <summary>
/// Evaluates ACP tool/permission requests against canonical <c>WorkingDirectory</c> + <c>WriteScope</c>
/// containment rules and bridges out-of-scope or shell/destructive requests through Core's approval gate.
/// </summary>
public interface IAcpApprovalBridge
{
    Task<AcpPermissionDecision> EvaluateOrWaitForApprovalAsync(
        AcpPermissionCheckRequest request,
        CancellationToken ct = default);
}
