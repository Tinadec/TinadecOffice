using System.Text.Json;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AcpServer;

/// <summary>
/// Dual-mode backend port used by <see cref="AcpAgentServer"/>:
/// <list type="bullet">
/// <item>Mode A (<see cref="EmbeddedAcpCoreBackend"/>): in-process Core DI + SQLite + <c>FullDuplexRunEngine</c> with zero Kestrel HTTP port.</item>
/// <item>Mode B (<see cref="RemoteHttpAcpCoreBackend"/>): HTTP + SSE attachment to a live running Core instance (<c>--core-url</c>).</item>
/// </list>
/// </summary>
internal interface IAcpCoreBackend : IAsyncDisposable
{
    Task<AcpSessionSnapshot> CreateSessionAsync(string cwd, CancellationToken cancellationToken);

    Task<AcpSessionSnapshot> LoadSessionAsync(Guid sessionId, string cwd, CancellationToken cancellationToken);

    Task<IReadOnlyList<AcpHistoryMessage>> ListHistoryAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<AcpSessionSnapshot> SetModeAsync(Guid sessionId, string modeId, CancellationToken cancellationToken);

    Task<AcpSessionSnapshot> SetModelAsync(Guid sessionId, string modelId, CancellationToken cancellationToken);

    Task<Guid> StoreAttachmentAsync(
        Guid sessionId,
        string fileName,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);

    Task<AcpPromptSubmission> SubmitPromptAsync(
        Guid sessionId,
        string content,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AcpRunStreamFrame> FollowRunStreamAsync(
        Guid sessionId,
        Guid runId,
        Guid? turnId,
        long afterSequence,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AcpBackendEvent> SubscribeSessionEventsAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    Task<AcpRunStatusSnapshot?> GetRunStatusAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken);

    Task<AcpToolExecutionDetail?> GetToolExecutionDetailAsync(
        Guid sessionId,
        Guid runId,
        Guid executionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AcpPendingApproval>> ListPendingApprovalsAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken);

    Task<AcpPendingApproval?> GetApprovalAsync(
        Guid sessionId,
        Guid approvalId,
        CancellationToken cancellationToken);

    Task<AcpApprovalDecisionOutcome> DecideApprovalAsync(
        Guid sessionId,
        Guid approvalId,
        AcpApprovalDecisionInput input,
        CancellationToken cancellationToken);

    Task ResumeAwaitingUserSupervisionAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken);

    Task CancelRunAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken);
}

internal sealed record AcpSessionSnapshot(
    Guid SessionId,
    string Cwd,
    AcpSessionModesState Modes,
    AcpSessionModelsState Models);

internal sealed record AcpHistoryMessage(
    Guid MessageId,
    string Role,
    string Content);

internal sealed record AcpPromptSubmission(
    Guid RunId,
    Guid? TurnId,
    Guid MessageId,
    long StreamCursor);

internal sealed record AcpRunStreamFrame(
    long Sequence,
    string Kind,
    string? Delta = null,
    string? FinishReason = null,
    string? ErrorCategory = null,
    string? SafeErrorMessage = null);

internal sealed record AcpBackendEvent(
    string EventId,
    string EventType,
    Guid? RunId,
    long Sequence,
    string? Summary,
    JsonElement Payload);

internal sealed record AcpRunStatusSnapshot(
    Guid RunId,
    string Status,
    long LastEventSequence,
    string? Summary = null,
    bool? IsLeaseHeld = null);

internal sealed record AcpToolExecutionDetail(
    Guid ExecutionId,
    string ToolId,
    string? Summary,
    string? ResourcePath,
    string? Command,
    string? Cwd,
    JsonElement? RawInput,
    JsonElement? RawOutput,
    string? ResultText,
    bool? ToolSuccess);

internal sealed record AcpPendingApproval(
    Guid ApprovalId,
    Guid? SessionId,
    Guid? RunId,
    Guid? ExecutionId,
    string Kind,
    string ToolId,
    string Risk,
    string Status,
    string Summary,
    string? ArgumentsJson,
    string? Command,
    string? Cwd,
    string? ResourcePath);

internal sealed record AcpApprovalDecisionInput(
    string Decision,
    string Scope,
    string? Reason = null,
    ApprovalRememberRuleInputDto? RememberRule = null);

internal sealed record AcpApprovalDecisionOutcome(
    bool Applied,
    bool AlreadyDecidedConflict,
    bool InvalidRuleRejected,
    string? Status = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
