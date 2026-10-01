namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// A session drawn as a graph (architecture §3.1): its runs, each run's tasks and the instances that
/// work them, the resources they hold, and the organization's members. One read serves the UI's
/// topology view and the governance <c>graph_view</c> tool, so what a reviewer reasons about is what
/// the user sees.
///
/// Every list is bounded and says when it was cut: a session can grow to hundreds of instances, and
/// an unbounded view is exactly what makes a model or a UI fall over in the cluster case.
/// </summary>
public sealed record SessionTopologyDto(
    Guid SessionId,
    SessionTopologyRunDto[] Runs,
    SessionTopologyLeaseDto[] Leases,
    SessionTopologyMemberDto[] Members,
    bool RunsTruncated,
    bool LeasesTruncated,
    bool MembersTruncated,
    DateTimeOffset GeneratedAt);

public sealed record SessionTopologyRunDto(
    Guid RunId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Tier,
    string? Phase,
    SessionTopologyTaskDto[] Tasks,
    SessionTopologyInstanceDto[] Instances,
    bool TasksTruncated,
    bool InstancesTruncated,
    Guid? ParentRunId = null,
    Guid? ParentTaskId = null,
    string RunKind = "root");

public sealed record SessionTopologyTaskDto(
    Guid TaskId,
    string TaskKey,
    string Title,
    string Status,
    string? Handle,
    string? AgentSlug,
    Guid? WorkerInstanceId,
    Guid? DispatchedByTaskId,
    string[] Dependencies,
    string[] WriteScope,
    string? ResultSummary);

public sealed record SessionTopologyInstanceDto(
    Guid InstanceId,
    string AgentSlug,
    string Layer,
    string Role,
    string Status,
    Guid? ParentInstanceId,
    Guid? TaskId,
    int Depth);

public sealed record SessionTopologyLeaseDto(
    Guid LeaseId,
    string Kind,
    string ResourceKey,
    string Purpose,
    bool Exclusive,
    Guid? RunId,
    Guid? TaskId,
    Guid? AgentInstanceId);

public sealed record SessionTopologyMemberDto(
    Guid ParticipantId,
    string Handle,
    string DisplayName,
    string Role,
    string Presence,
    string? AgentSlug,
    Guid? ParentParticipantId,
    Guid? RunId,
    Guid? AgentInstanceId);

/// <summary>
/// A search of the session's evidence archive (task results, reports, member conclusions and
/// summaries, verbatim). <see cref="Mode"/> is <c>hybrid</c> when the semantic index answered and
/// <c>keyword</c> when it could not; <see cref="Note"/> says why.
/// </summary>
public sealed record EvidenceRecallDto(string Mode, string? Note, EvidenceHitDto[] Hits);

/// <param name="MatchedBy"><c>semantic</c>, <c>keyword</c> or <c>both</c>.</param>
public sealed record EvidenceHitDto(
    Guid EvidenceId,
    string Kind,
    string Title,
    string? Author,
    Guid? RunId,
    Guid? TaskId,
    string Snippet,
    double Score,
    string MatchedBy,
    DateTimeOffset CreatedAt);
