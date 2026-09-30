namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// The project commander's desk (todo E4, first slice — visibility before authority): everything a
/// project-level chairman would need to read, rolled up from the sessions of one project. Read-only;
/// the day the chairman exists it acts on exactly this shape.
/// </summary>
public sealed record ProjectOverviewDto(
    Guid ProjectId,
    string Name,
    int Sessions,
    ProjectOverviewRunDto[] ActiveRuns,
    ProjectOverviewReportDto[] OpenReports,
    ProjectOverviewLeaseDto[] HeldResources,
    bool ReportsTruncated);

/// <summary>One unfinished run, with the session it belongs to — parked runs count: they wait on a decision, not on compute.</summary>
public sealed record ProjectOverviewRunDto(Guid RunId, Guid SessionId, string Status, string? Phase, DateTimeOffset StartedAt);

/// <summary>An open governance report, attributed to its session and author.</summary>
public sealed record ProjectOverviewReportDto(Guid SessionId, Guid ReportId, string AuthorDisplayName, string ReportKind, string Severity, string Finding, DateTimeOffset CreatedAt);

/// <summary>A resource somebody holds: the worktree, path or environment slot that serializes work right now.</summary>
public sealed record ProjectOverviewLeaseDto(Guid LeaseId, string Kind, string ResourceKey, string Purpose, Guid? RunId, string? Reason);
