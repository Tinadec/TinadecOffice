using System.Text.Json.Serialization;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <param name="Limit">
/// Per-section ceiling the caller asks for. The service clamps it to its own bound; a model can
/// narrow the read but never widen it.
/// </param>
public sealed record RunDossierQuery(
    Guid TenantId,
    Guid WorkspaceId,
    Guid SessionId,
    Guid RunId,
    int Limit = 20);

/// <summary>
/// The run itself as the durable lifecycle row states it. Deliberately not the user's goal: the
/// model already has the conversation, and restating it here would make the dossier look like a
/// second source of truth about what was asked.
/// </summary>
public sealed record RunDossierRunFacts(
    string RunId,
    string Status,
    string RunKind,
    string PermissionMode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Summary,
    string? TerminalErrorCategory);

/// <param name="Summary">The task's own closing words, bounded by the service.</param>
public sealed record RunDossierTask(
    string TaskKey,
    string LaneKey,
    string Status,
    string Summary,
    IReadOnlyList<string> Evidence);

/// <param name="Note">Why it ended the way it did; the last writer's words, bounded.</param>
public sealed record RunDossierIntent(
    int Ordinal,
    string Title,
    string Status,
    string? Note,
    IReadOnlyList<string> EvidenceRefs);

/// <param name="Kind">A <see cref="ExplorationNodeKinds"/> value: fact, finding or hint.</param>
/// <param name="Content">Bounded excerpt; the full text stays in the graph.</param>
public sealed record RunDossierFinding(
    string Kind,
    string Title,
    string? Content,
    IReadOnlyList<string> EvidenceRefs,
    string? Author);

/// <param name="EvidenceRefs">Distinct citations across the returned findings.</param>
/// <param name="Truncated">True when any section stopped at the limit and more rows exist.</param>
public sealed record RunDossierTotals(
    int Tasks,
    int Intents,
    int Findings,
    int OpenQuestions,
    int EvidenceRefs,
    bool Truncated);

/// <param name="Notes">
/// What this host could not read, in the model's own words. An absent source must not look like
/// an empty one: "no findings were recorded" and "the graph is not configured here" are different
/// facts, and only the second one means the dossier under-reports.
/// </param>
/// <param name="OpenQuestions">
/// What the run left unresolved, derived from its own records: blocked intents, tasks that never
/// completed, and hints nobody cited. It is a projection of recorded facts, never a model opinion.
/// </param>
public sealed record RunDossier(
    RunDossierRunFacts Run,
    IReadOnlyList<RunDossierTask> Tasks,
    IReadOnlyList<RunDossierIntent> Intents,
    IReadOnlyList<RunDossierFinding> Findings,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> Notes,
    RunDossierTotals Totals,
    [property: JsonPropertyName("context_health")] ContextHealthSummaryDto? ContextHealth = null);

/// <summary>
/// B9: one run's dossier — what it was told, what its plan claimed, what its workers closed, what
/// the investigation recorded, and what is still open. Read-only and run scoped; the
/// <c>run_dossier</c> feature is checked before the tool is served, and the settlement facts are
/// read from the exploration graph the worker settlement feature writes.
/// </summary>
public interface IRunDossier
{
    /// <summary>
    /// Builds the dossier. Returns null when the run does not exist inside the caller's
    /// tenant/workspace/session — the tool layer answers one error for "unknown" and "not yours",
    /// the same way every other scoped read in this host does.
    /// </summary>
    Task<RunDossier?> BuildAsync(RunDossierQuery query, CancellationToken cancellationToken = default);
}
