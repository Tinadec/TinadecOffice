using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The session owner's window onto the session's organization: who is there, the rooms, the board,
/// the reports. Every call acts as the organization's human member under the verified request
/// principal, and only the principal that owns the session gets an answer — another workspace member
/// sees nothing, the same as for the session itself.
/// </summary>
public interface ISessionOrganizationView
{
    /// <summary>Null when the session has no organization yet (nothing has run in it).</summary>
    Task<OrganizationDto?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<OrganizationMessagePage> ReadRoomAsync(Guid sessionId, Guid roomId, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default);

    Task<OrganizationMessageDto> PostAsync(Guid sessionId, Guid roomId, OrganizationPostRequest request, CancellationToken cancellationToken = default);

    Task<OrganizationReportPage> ListReportsAsync(Guid sessionId, string? status = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<OrganizationReportDto> DecideReportAsync(Guid sessionId, Guid reportId, OrganizationReportDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The user sets one member's visibility into run internals (todo E5): "down" (default), "own", or null to clear.</summary>
    Task<OrganizationMemberDto> SetMemberVisibilityAsync(Guid sessionId, Guid participantId, OrganizationMemberVisibilityRequest request, CancellationToken cancellationToken = default);
}
