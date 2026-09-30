namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Who may approve a governed action on the user's behalf (architecture §7.2, "权限下放"). The user
/// picks it per message, as a permission mode, because it is the same statement as the other modes:
/// how much of the approval work this run hands away.
///
/// <list type="bullet">
/// <item><c>delegate-conversation</c> — the conversation identity decides, knowing the user's goal.</item>
/// <item><c>delegate-reviewer</c> — a reviewer decides in its own context, seeing only the action and the facts.</item>
/// <item><c>delegate-both</c> — both decide, independently, as two records; one refusal refuses.</item>
/// </list>
///
/// Delegation only ever replaces the human click on the approval gate. It never widens a binding
/// check (the approved call is still consumed against its request hash, manifest and grants), and
/// human-only tools (shell, push, deletes, egress…) always wait for the human.
/// </summary>
public static class ApprovalDelegationModes
{
    public const string Conversation = "delegate-conversation";
    public const string Reviewer = "delegate-reviewer";
    public const string Both = "delegate-both";

    public static readonly IReadOnlyList<string> All = [Conversation, Reviewer, Both];

    public static bool IsDelegated(string? permissionMode) =>
        permissionMode is not null && All.Contains(permissionMode.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The gates a mode asks for, in the order they are evaluated.</summary>
    public static IReadOnlyList<string> GatesFor(string? permissionMode) => permissionMode?.Trim().ToLowerInvariant() switch
    {
        Conversation => [ApprovalGateKindNames.Conversation],
        Reviewer => [ApprovalGateKindNames.Reviewer],
        // The reviewer first: it is the independent look, and a refusal there saves the second call.
        Both => [ApprovalGateKindNames.Reviewer, ApprovalGateKindNames.Conversation],
        _ => [],
    };
}

public static class ApprovalGateKindNames
{
    public const string Conversation = "conversation_identity";
    public const string Reviewer = "reviewer_agent";
}

/// <summary>Which approval one gate is asked about. The judge gathers the facts itself, bounded.</summary>
public sealed record ApprovalGateRequest(
    Guid RunId,
    Guid SessionId,
    Guid ApprovalId,
    Guid ExecutionId,
    string GateKind,
    string PermissionMode);

/// <summary>
/// What one gate is shown. Deliberately bounded — the point of an independent review is what it
/// does NOT see: the reviewer gets the action, the task it serves and objective facts, never the
/// conversation transcript. Only the conversation gate is told the user's goal, because knowing the
/// user's intent is exactly its job. Serialized as the gate's evidence, so "what did it look at"
/// has an exact answer.
/// </summary>
public sealed record ApprovalGateQuestion(
    string GateKind,
    string ToolId,
    string Risk,
    string ParametersJson,
    string? TaskTitle,
    string? TaskDescription,
    string? WorkerHandle,
    IReadOnlyList<string> WriteScope,
    string? WorkspaceRoot,
    IReadOnlyList<string> HeldResources,
    string PermissionMode,
    string? UserGoal);

/// <param name="Decision">approve / reject / escalate (hand the decision back to the user).</param>
/// <param name="EvidenceJson">Exactly what the gate was shown (an <see cref="ApprovalGateQuestion"/>), recorded with its decision.</param>
public sealed record ApprovalGateVerdict(string Decision, string Rationale, string EvidenceJson, string? DeciderAgent);

/// <summary>Judges one gate with a model call in the gate's own context. Implemented where the model plumbing lives.</summary>
public interface IApprovalGateJudge
{
    Task<ApprovalGateVerdict> JudgeAsync(ApprovalGateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Read side of the delegated gates, for the approval card and the audit trail.</summary>
public interface IApprovalGateLedger
{
    /// <summary>Null when the approval was never delegated (no gate was ever opened for it).</summary>
    Task<Contracts.Dtos.ApprovalGatesDto?> GetAsync(Guid approvalId, CancellationToken cancellationToken = default);
}
