namespace TinadecCore.Runtime;

/// <summary>Body contract for run control endpoints (cancel/pause/resume).</summary>
public sealed class RunControlRequest
{
    public string Action { get; set; } = string.Empty;
    public string? ClientControlId { get; set; }
    public long? ExpectedContextRevision { get; set; }
}
