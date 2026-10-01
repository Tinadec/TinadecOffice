using System.Text.Json;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// The delegated gates of one approval (architecture §7.2): who was asked, in what order, what each
/// decided and exactly what each was shown. <see cref="Status"/> is the approval's delegated outcome
/// so far — <c>evaluating</c> while a gate is still open, then <c>approved</c>, <c>rejected</c>,
/// <c>escalated</c> (handed back to the person) or <c>superseded</c> (the person decided first).
/// </summary>
public sealed record ApprovalGatesDto(
    Guid ApprovalId,
    Guid? RunId,
    string Status,
    ApprovalGateDto[] Gates);

/// <param name="Evidence">What the gate was shown, verbatim — for the reviewer, the call and the task, never the conversation.</param>
public sealed record ApprovalGateDto(
    int GateIndex,
    string GateKind,
    string Status,
    string? DeciderAgent,
    string? Reason,
    JsonElement? Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);
