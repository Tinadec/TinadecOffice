namespace TinadecCore.Runtime.ProjectBoard;

public sealed class ProjectBoardCycleException(string message) : InvalidOperationException(message)
{
    public string Code => "cycle_detected";
}

public sealed class ProjectBoardRevisionConflictException(long currentRevision, string message) : InvalidOperationException(message)
{
    public string Code => "revision_conflict";
    public long CurrentRevision { get; } = currentRevision;
}

public sealed class ProjectBoardConflictStateException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
