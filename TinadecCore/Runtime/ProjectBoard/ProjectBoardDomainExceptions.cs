namespace TinadecCore.Runtime.ProjectBoard;

public sealed class ProjectBoardCycleException(string message) : InvalidOperationException(message)
{
    public string Code => "cycle_detected";
}

public class ProjectBoardConflictException(string code, string message, long? currentRevision = null) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public long? CurrentRevision { get; } = currentRevision;
}

public sealed class ProjectBoardRevisionConflictException(long currentRevision, string message)
    : ProjectBoardConflictException("revision_conflict", message, currentRevision)
{
    public new long CurrentRevision { get; } = currentRevision;
}

public sealed class ProjectBoardConflictStateException(string code, string message)
    : ProjectBoardConflictException(code, message);

internal sealed class ProjectBoardLeaseContentionException(string message) : InvalidOperationException(message)
{
    public string Code => "lease_contention";
}
