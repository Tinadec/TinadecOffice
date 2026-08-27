namespace TinadecCore.Runtime;

using TinadecCore.Contracts.Dtos;

/// <summary>Body contract for <c>POST /api/v1/sessions/{sessionId}/invoke-stream</c>.</summary>
public sealed class InvokeStreamRequest
{
    public string Content { get; set; } = string.Empty;
    public string? ClientMessageId { get; set; }
    public string? ApplicationMode { get; set; }
    public string? AgentMode { get; set; }
    public string? PermissionMode { get; set; }
    public Guid? TargetRunId { get; set; }
    public long? ExpectedContextRevision { get; set; }
    public MeetingModelOverrideDto? MeetingModelOverride { get; set; }
}

public sealed class RunControlRequest
{
    public string Action { get; set; } = string.Empty;
    public string? ClientControlId { get; set; }
    public long? ExpectedContextRevision { get; set; }
}
