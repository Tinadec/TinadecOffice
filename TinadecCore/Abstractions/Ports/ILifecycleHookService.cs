using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

public sealed record LifecycleHookToolContext(
    string? WorkspaceRoot,
    string ToolId,
    long ToolCallId,
    Guid SessionId,
    Guid RunId,
    Guid TaskId,
    string? AgentSlug = null,
    string? TargetPath = null,
    string? ParametersJson = null,
    bool? IsSuccess = null,
    string? ResultSummary = null);

public sealed record LifecycleHookPreToolResult(
    bool Allowed,
    string? Reason = null,
    string? HookId = null,
    IReadOnlyList<string>? Feedback = null);

public sealed record LifecycleHookPostToolResult(
    IReadOnlyList<string> Feedback,
    bool HasBlockingFailure = false,
    IReadOnlyList<string>? Warnings = null);

public sealed record LifecycleHookRunContext(
    string? WorkspaceRoot,
    Guid SessionId,
    Guid RunId,
    Guid? TaskId = null,
    string? AgentSlug = null,
    string? Status = null,
    string? Summary = null);

public sealed class LifecycleHookException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public interface ILifecycleHookService
{
    Task<LifecycleHookPreToolResult> EvaluatePreToolUseAsync(
        LifecycleHookToolContext context,
        CancellationToken cancellationToken = default);

    Task<LifecycleHookPostToolResult> EvaluatePostToolUseAsync(
        LifecycleHookToolContext context,
        CancellationToken cancellationToken = default);

    Task NotifyTaskCompleteAsync(
        LifecycleHookRunContext context,
        CancellationToken cancellationToken = default);

    Task NotifySessionIdleAsync(
        LifecycleHookRunContext context,
        CancellationToken cancellationToken = default);

    Task<LifecycleHooksDocumentDto> GetHooksAsync(
        string? scope = null,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default);

    Task<LifecycleHooksDocumentDto> SaveHooksAsync(
        LifecycleHooksWriteRequestDto request,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);
}
