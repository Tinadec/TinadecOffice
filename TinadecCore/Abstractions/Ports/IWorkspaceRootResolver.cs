namespace TinadecCore.Abstractions.Ports;

/// <summary>Resolves a client cwd to an active project root in the current isolation scope.</summary>
public interface IWorkspaceRootResolver
{
    Task<ProjectReference?> FindByRootAsync(string cwd, CancellationToken cancellationToken = default);
}
