namespace TinadecCore.Runtime;

/// <summary>Body contract for <c>POST /api/v1/sessions/{sessionId}/invoke-stream</c>.</summary>
public sealed class InvokeStreamRequest
{
    public string Content { get; set; } = string.Empty;
}
