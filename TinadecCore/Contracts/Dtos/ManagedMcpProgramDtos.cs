namespace TinadecCore.Contracts.Dtos;

public sealed record ManagedMcpProgramPreviewDto(
    Guid ResourceId, Guid? ProjectId, string Action, long ExpectedRevision,
    string Manager, string Package, string Version, string PackageRoot, string PlanHash,
    string ProgramStatus, IReadOnlyList<string> ServerArguments, Guid PreviewId);

public sealed record ManagedMcpProgramApplyDto(ManagedMcpProgramPreviewDto Preview);

public sealed record ManagedMcpProgramResultDto(Guid ResourceId, string Status, string? ProgramRoot,
    string? ProgramHash, bool RetainedForHistory = false);
