using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

public interface IToolSkillResourceService
{
    Task<ToolSkillListDto> ListAsync(Guid? projectId, CancellationToken cancellationToken = default);
    /// <summary>Same catalog, optionally including the embedded built-in packages. The default
    /// stays built-in-free so settings/management callers keep counting only their own resources;
    /// run admission, the skill index and <c>load_skill</c> ask for built-ins explicitly.</summary>
    Task<ToolSkillListDto> ListAsync(Guid? projectId, bool includeBuiltins, CancellationToken cancellationToken = default) =>
        ListAsync(projectId, cancellationToken);
    Task<ToolSkillResourceDto?> GetAsync(Guid resourceId, Guid? projectId, CancellationToken cancellationToken = default);
    Task<ToolSkillFileDto?> GetFileAsync(Guid resourceId, Guid? projectId, string relativePath, CancellationToken cancellationToken = default);
    Task<ToolSkillResourceDto> SaveAsync(Guid? resourceId, Guid? projectId, ToolSkillWriteDto input, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ToolSkillResourceDto> RequestSaveAsync(Guid? resourceId, Guid? projectId, ToolSkillWriteDto input, long expectedRevision, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid resourceId, Guid? projectId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ToolSkillResourceDto> RequestDeleteAsync(Guid resourceId, Guid? projectId, long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Copies an embedded built-in skill into a project's canonical skills root as a governed file action.</summary>
    Task<ToolSkillResourceDto> RequestCopyToProjectAsync(Guid resourceId, Guid projectId, CancellationToken cancellationToken = default);
}

