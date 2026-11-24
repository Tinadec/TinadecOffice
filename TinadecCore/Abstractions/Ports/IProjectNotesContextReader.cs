namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Bounded snapshot of a project's living instructions and <c>notes.md</c> decisions
/// contributed into project-scoped context packs and board task prompts.
/// </summary>
public sealed record ProjectLivingContextSnapshot(
    Guid ProjectId,
    string ProjectInstructions,
    string NotesSummaryMarkdown,
    long NotesRevision);

/// <summary>
/// Port allowing <c>TinadecCore.Context</c> to read bounded project instructions and
/// living <c>notes.md</c> context without referencing <c>TinadecCore.Runtime</c> or <c>TinadecCore.Memory</c>.
/// </summary>
public interface IProjectNotesContextReader
{
    Task<ProjectLivingContextSnapshot?> ReadProjectContextAsync(
        Guid projectId,
        int maxChars = 2400,
        CancellationToken cancellationToken = default);
}
