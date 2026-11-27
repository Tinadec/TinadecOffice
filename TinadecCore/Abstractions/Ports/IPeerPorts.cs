using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Canonical RFC 9457 error vocabulary shared across Core, Gateway, and Desktop for peer device operations.
/// </summary>
public static class PeerErrorCodes
{
    public const string Unreachable = "unreachable";
    public const string InboundDisabled = "inbound_disabled";
    public const string PairingCodeRejected = "pairing_code_rejected";
    public const string PairingCodeExpired = "pairing_code_expired";
    public const string FingerprintMismatch = "fingerprint_mismatch";
    public const string Unauthorized = "unauthorized";
    public const string ProtocolMajorMismatch = "protocol_major_mismatch";
    public const string EncryptionUnavailable = "encryption_unavailable";
    public const string HandshakeFailed = "handshake_failed";
    public const string Timeout = "timeout";
    public const string PolicyDenied = "policy_denied";
    public const string DeviceOffline = "device_offline";
    public const string SubmissionUncertain = "submission_uncertain";
    public const string DelegationHopLimitExceeded = "delegation_hop_limit_exceeded";
}

/// <summary>
/// Domain exception mapped directly to RFC 9457 ProblemDetails with the canonical <see cref="PeerErrorCodes"/>.
/// </summary>
public sealed class PeerException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public PeerException(int statusCode, string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
    }
}

public interface IPeerIdentityService
{
    Task<PeerLocalIdentitySnapshot> EnsureLocalIdentityAsync(CancellationToken cancellationToken = default);
    Task<PeerLocalTlsMaterialDto> GetLocalTlsMaterialAsync(CancellationToken cancellationToken = default);
    Task<PeerInboundSettingsDto> GetInboundSettingsAsync(CancellationToken cancellationToken = default);
    Task<PeerInboundSettingsDto> UpdateInboundSettingsAsync(PeerUpdateInboundRequestDto request, CancellationToken cancellationToken = default);
}

public interface IPeerPairingService
{
    Task<PeerPairingCodeStatusDto> GeneratePairingCodeAsync(PeerGeneratePairingCodeRequestDto? request = null, CancellationToken cancellationToken = default);
    Task<PeerPairingCodeStatusDto> GetPairingCodeStatusAsync(CancellationToken cancellationToken = default);
    Task RevokePairingCodeAsync(CancellationToken cancellationToken = default);
    Task<PeerHandshakeVerifyResponseDto> VerifyHandshakeAsync(PeerHandshakeVerifyRequestDto request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerDeviceDto>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task<PeerDeviceDto?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<PeerDeviceDto> UpsertPairedDeviceAsync(PeerPairDeviceRequestDto request, CancellationToken cancellationToken = default);
    Task<PeerDeviceDto> UpdateDeviceAsync(string deviceId, PeerUpdateDeviceRequestDto request, CancellationToken cancellationToken = default);
    Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<PeerDeviceDto> UpdateControllerStateAsync(PeerControllerStateUpdateDto request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerRemoteWorkspaceDto>> ListMountedWorkspacesAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerRemoteWorkspaceDto>> ReplaceMountedWorkspacesAsync(string deviceId, IReadOnlyList<PeerWorkspaceMountInputDto> mounts, CancellationToken cancellationToken = default);
    Task<int> ClearDeviceLocalCacheAsync(string deviceId, CancellationToken cancellationToken = default);
}

public interface IPeerPolicyService
{
    Task<IReadOnlyList<PeerGrantWithPolicyDto>> ListGrantsAsync(CancellationToken cancellationToken = default);
    Task<PeerGrantWithPolicyDto> UpdateGrantPolicyAsync(string controllerDeviceId, PeerUpdateAccessPolicyRequestDto request, CancellationToken cancellationToken = default);
    Task<bool> RevokeGrantAsync(string controllerDeviceId, CancellationToken cancellationToken = default);
    Task<PeerPolicySnapshotDto> GetPolicySnapshotAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<PeerPolicyEventDto> SubscribePolicyEventsAsync(CancellationToken cancellationToken = default);
    void PublishPolicyEvent(PeerPolicyEventDto evt);
}

public sealed record PeerLocalIdentitySnapshot(
    string LocalDeviceId,
    string DisplayName,
    bool InboundEnabled,
    int ListenPort,
    string LocalFingerprint);

public sealed class PeerLocalTlsMaterialDto
{
    [JsonPropertyName("inbound_enabled")]
    public bool InboundEnabled { get; init; }

    [JsonPropertyName("local_device_id")]
    public string LocalDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("listen_port")]
    public int ListenPort { get; init; } = 48735;

    [JsonPropertyName("local_fingerprint")]
    public string LocalFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("cert_pem")]
    public string CertPem { get; init; } = string.Empty;

    [JsonPropertyName("private_key_pem")]
    public string PrivateKeyPem { get; init; } = string.Empty;
}

public sealed class PeerInboundSettingsDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("listening")]
    public bool Listening { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("listen_port")]
    public int ListenPort { get; init; } = 48735;

    [JsonPropertyName("local_device_id")]
    public string LocalDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("local_fingerprint")]
    public string LocalFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("reachable_addresses")]
    public IReadOnlyList<string> ReachableAddresses { get; init; } = [];

    [JsonPropertyName("firewall_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FirewallHint { get; init; }
}

public sealed class PeerUpdateInboundRequestDto
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("listen_port")]
    public int? ListenPort { get; init; }
}

public sealed class PeerGeneratePairingCodeRequestDto
{
    [JsonPropertyName("ttl_seconds")]
    public int? TtlSeconds { get; init; }

    [JsonPropertyName("relay_enrolled")]
    public bool? RelayEnrolled { get; init; }
}

public sealed class PeerPairingCodeStatusDto
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("pairing_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PairingCode { get; init; }

    [JsonPropertyName("expires_at_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? ExpiresAtMs { get; init; }

    [JsonPropertyName("remaining_attempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? RemainingAttempts { get; init; }

    [JsonPropertyName("relay_enrolled")]
    public bool RelayEnrolled { get; init; }
}

public sealed class PeerAccessPolicyDto
{
    [JsonPropertyName("controller_device_id")]
    public string ControllerDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "operator";

    [JsonPropertyName("workspace_mode")]
    public string WorkspaceMode { get; init; } = "all";

    [JsonPropertyName("workspace_ids")]
    public IReadOnlyList<string> WorkspaceIds { get; init; } = [];

    [JsonPropertyName("allow_non_workspace_sessions")]
    public bool AllowNonWorkspaceSessions { get; init; }

    [JsonPropertyName("sync_all_sessions")]
    public bool SyncAllSessions { get; init; }

    [JsonPropertyName("allow_agent_delegation")]
    public bool AllowAgentDelegation { get; init; }

    [JsonPropertyName("allow_provider_proxy")]
    public bool AllowProviderProxy { get; init; }

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; init; }
}

public sealed class PeerUpdateAccessPolicyRequestDto
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("workspace_mode")]
    public string? WorkspaceMode { get; init; }

    [JsonPropertyName("workspace_ids")]
    public IReadOnlyList<string>? WorkspaceIds { get; init; }

    [JsonPropertyName("allow_non_workspace_sessions")]
    public bool? AllowNonWorkspaceSessions { get; init; }

    [JsonPropertyName("sync_all_sessions")]
    public bool? SyncAllSessions { get; init; }

    [JsonPropertyName("allow_agent_delegation")]
    public bool? AllowAgentDelegation { get; init; }

    [JsonPropertyName("allow_provider_proxy")]
    public bool? AllowProviderProxy { get; init; }
}

public sealed class PeerGrantWithPolicyDto
{
    [JsonPropertyName("grant_id")]
    public string GrantId { get; init; } = string.Empty;

    [JsonPropertyName("controller_device_id")]
    public string ControllerDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("transport")]
    public string Transport { get; init; } = "lan";

    [JsonPropertyName("certificate_fingerprint")]
    public string CertificateFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("granted_at")]
    public long GrantedAt { get; init; }

    [JsonPropertyName("last_controlled_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LastControlledAt { get; init; }

    [JsonPropertyName("revoked_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? RevokedAt { get; init; }

    [JsonPropertyName("policy")]
    public PeerAccessPolicyDto Policy { get; init; } = new();
}

public sealed class PeerHandshakeVerifyRequestDto
{
    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    [JsonPropertyName("controller_device_id")]
    public string? ControllerDeviceId { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("remote_fingerprint")]
    public string? RemoteFingerprint { get; init; }

    [JsonPropertyName("protocol_major")]
    public int ProtocolMajor { get; init; } = 1;

    [JsonPropertyName("protocol_minor")]
    public int ProtocolMinor { get; init; }

    [JsonPropertyName("pairing_mode")]
    public string? PairingMode { get; init; }

    [JsonPropertyName("pairing_code")]
    public string? PairingCode { get; init; }

    [JsonPropertyName("expected_remote_fingerprint")]
    public string? ExpectedRemoteFingerprint { get; init; }
}

public sealed class PeerHandshakeVerifyResponseDto
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("grant_id")]
    public string GrantId { get; init; } = string.Empty;

    [JsonPropertyName("controller_device_id")]
    public string ControllerDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("local_device_id")]
    public string LocalDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("local_display_name")]
    public string LocalDisplayName { get; init; } = string.Empty;

    [JsonPropertyName("local_fingerprint")]
    public string LocalFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("policy_revision")]
    public string PolicyRevision { get; init; } = string.Empty;

    [JsonPropertyName("policy")]
    public PeerAccessPolicyDto Policy { get; init; } = new();
}

public sealed class PeerDeviceDto
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("transport")]
    public string Transport { get; init; } = "lan";

    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_id")]
    public string RemoteDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("certificate_fingerprint")]
    public string CertificateFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("protocol_major")]
    public int ProtocolMajor { get; init; } = 1;

    [JsonPropertyName("protocol_minor")]
    public int ProtocolMinor { get; init; }

    [JsonPropertyName("paired_at")]
    public long PairedAt { get; init; }

    [JsonPropertyName("last_connected_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LastConnectedAt { get; init; }

    [JsonPropertyName("connection_desired")]
    public bool ConnectionDesired { get; init; } = true;

    [JsonPropertyName("adopt_sessions")]
    public bool AdoptSessions { get; init; } = true;

    [JsonPropertyName("last_state")]
    public string LastState { get; init; } = "disconnected";

    [JsonPropertyName("last_failure_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LastFailureCode { get; init; }

    [JsonPropertyName("access_revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AccessRevision { get; init; }
}

public sealed class PeerPairDeviceRequestDto
{
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("remote_device_id")]
    public string? RemoteDeviceId { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("certificate_fingerprint")]
    public string? CertificateFingerprint { get; init; }

    [JsonPropertyName("expected_fingerprint")]
    public string? ExpectedFingerprint { get; init; }

    [JsonPropertyName("protocol_major")]
    public int? ProtocolMajor { get; init; }

    [JsonPropertyName("protocol_minor")]
    public int? ProtocolMinor { get; init; }

    [JsonPropertyName("connection_desired")]
    public bool? ConnectionDesired { get; init; }

    [JsonPropertyName("adopt_sessions")]
    public bool? AdoptSessions { get; init; }

    [JsonPropertyName("last_state")]
    public string? LastState { get; init; }

    [JsonPropertyName("access_revision")]
    public string? AccessRevision { get; init; }
}

public sealed class PeerUpdateDeviceRequestDto
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("connection_desired")]
    public bool? ConnectionDesired { get; init; }

    [JsonPropertyName("adopt_sessions")]
    public bool? AdoptSessions { get; init; }
}

public sealed class PeerControllerStateUpdateDto
{
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("last_state")]
    public string? LastState { get; init; }

    [JsonPropertyName("last_failure_code")]
    public string? LastFailureCode { get; init; }

    [JsonPropertyName("last_connected_at")]
    public long? LastConnectedAt { get; init; }

    [JsonPropertyName("access_revision")]
    public string? AccessRevision { get; init; }
}

public sealed class PeerProbeRequestDto
{
    [JsonPropertyName("address")]
    public string? Address { get; init; }
}

public sealed class PeerRemoteWorkspaceDto
{
    [JsonPropertyName("remote_project_id")]
    public string RemoteProjectId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("remote_root_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RemoteRootPath { get; init; }

    [JsonPropertyName("mounted")]
    public bool Mounted { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;

    [JsonPropertyName("alias_workspace_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AliasWorkspaceId { get; init; }
}

public sealed class PeerWorkspaceMountInputDto
{
    [JsonPropertyName("remote_project_id")]
    public string? RemoteProjectId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("remote_root_path")]
    public string? RemoteRootPath { get; init; }

    [JsonPropertyName("mounted")]
    public bool? Mounted { get; init; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; init; }
}

public sealed class PeerMountWorkspacesRequestDto
{
    [JsonPropertyName("workspaces")]
    public IReadOnlyList<PeerWorkspaceMountInputDto>? Workspaces { get; init; }

    [JsonPropertyName("mounts")]
    public IReadOnlyList<PeerWorkspaceMountInputDto>? Mounts { get; init; }
}

public sealed class PeerPolicySnapshotDto
{
    [JsonPropertyName("inbound_enabled")]
    public bool InboundEnabled { get; init; }

    [JsonPropertyName("listen_port")]
    public int ListenPort { get; init; } = 48735;

    [JsonPropertyName("local_device_id")]
    public string LocalDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("local_fingerprint")]
    public string LocalFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("grants")]
    public IReadOnlyList<PeerGrantWithPolicyDto> Grants { get; init; } = [];

    [JsonPropertyName("policies")]
    public IReadOnlyList<PeerAccessPolicyDto> Policies { get; init; } = [];
}

public sealed class PeerPolicyEventDto
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("controller_device_id")]
    public string? ControllerDeviceId { get; init; }

    [JsonPropertyName("revision")]
    public string? Revision { get; init; }

    [JsonPropertyName("policy")]
    public PeerAccessPolicyDto? Policy { get; init; }

    [JsonPropertyName("inbound_enabled")]
    public bool? InboundEnabled { get; init; }

    [JsonPropertyName("listen_port")]
    public int? ListenPort { get; init; }

    [JsonPropertyName("timestamp_ms")]
    public long TimestampMs { get; init; }
}

// --- Wave 2: Shadow Cache, Multi-Writer FIFO Ingress Queue, and Staged Attachments ---

public interface IPeerShadowService
{
    Task<PeerShadowSyncResultDto> SyncShadowAsync(string deviceId, PeerShadowSyncRequestDto request, CancellationToken cancellationToken = default);
    Task<PeerShadowSessionListDto> ListShadowSessionsAsync(string deviceId, string? remoteProjectId = null, CancellationToken cancellationToken = default);
    Task<PeerShadowSessionDetailDto?> GetShadowSessionAsync(string deviceId, string remoteSessionId, CancellationToken cancellationToken = default);
    Task<int> ClearDeviceLocalCacheAsync(string deviceId, CancellationToken cancellationToken = default);
    Task PurgeDeviceShadowDataAsync(string deviceId, CancellationToken cancellationToken = default);
}

public interface IPeerIngressQueueService
{
    Task<PeerIngressExecutionOutcome> ExecuteSerializedAsync(
        PeerIngressSubmitCommand command,
        Func<PeerIngressTicketContext, CancellationToken, Task<PeerIngressWorkResult>> work,
        CancellationToken cancellationToken = default);

    Task<PeerIngressStatusDto> GetIngressStatusAsync(
        string sessionId,
        string clientRequestId,
        CancellationToken cancellationToken = default);
}

public interface IPeerAttachmentStageService
{
    Task<PeerAttachmentStageResultDto> StageChunkOrReferenceAsync(
        PeerAttachmentStageCommand command,
        CancellationToken cancellationToken = default);

    Task<PeerResolvedStagedAttachment?> ResolveForMessageAsync(
        Guid sessionId,
        string idOrStageId,
        CancellationToken cancellationToken = default);
}

public sealed class PeerShadowSyncRequestDto
{
    [JsonPropertyName("workspaces")]
    public IReadOnlyList<PeerShadowWorkspaceSyncItemDto>? Workspaces { get; init; }

    [JsonPropertyName("sessions")]
    public IReadOnlyList<PeerShadowSessionSyncItemDto>? Sessions { get; init; }
}

public sealed class PeerShadowWorkspaceSyncItemDto
{
    [JsonPropertyName("alias_workspace_id")]
    public string? AliasWorkspaceId { get; init; }

    [JsonPropertyName("remote_project_id")]
    public string? RemoteProjectId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("remote_root_path")]
    public string? RemoteRootPath { get; init; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; init; }
}

public sealed class PeerShadowSessionSyncItemDto
{
    [JsonPropertyName("local_shadow_id")]
    public string? LocalShadowId { get; init; }

    [JsonPropertyName("remote_session_id")]
    public string? RemoteSessionId { get; init; }

    [JsonPropertyName("remote_project_id")]
    public string? RemoteProjectId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("run_status")]
    public string? RunStatus { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("initiator_device_id")]
    public string? InitiatorDeviceId { get; init; }

    [JsonPropertyName("last_event_seq")]
    public long? LastEventSeq { get; init; }

    [JsonPropertyName("turns")]
    public IReadOnlyList<PeerShadowTurnSyncItemDto>? Turns { get; init; }
}

public sealed class PeerShadowTurnSyncItemDto
{
    [JsonPropertyName("turn_id")]
    public string? TurnId { get; init; }

    [JsonPropertyName("digest_json")]
    public string? DigestJson { get; init; }

    [JsonPropertyName("digest")]
    public JsonElement? Digest { get; init; }
}

public sealed class PeerShadowSyncResultDto
{
    [JsonPropertyName("synced")]
    public bool Synced { get; init; } = true;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("synced_workspaces")]
    public int SyncedWorkspaces { get; init; }

    [JsonPropertyName("synced_sessions")]
    public int SyncedSessions { get; init; }

    [JsonPropertyName("synced_turns")]
    public int SyncedTurns { get; init; }

    [JsonPropertyName("synced_at")]
    public long SyncedAt { get; init; }
}

public sealed class PeerShadowSessionDto
{
    [JsonPropertyName("local_shadow_id")]
    public string LocalShadowId { get; init; } = string.Empty;

    [JsonPropertyName("peer_device_id")]
    public string PeerDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("remote_session_id")]
    public string RemoteSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RemoteProjectId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("run_status")]
    public string RunStatus { get; init; } = "idle";

    [JsonPropertyName("initiator_device_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? InitiatorDeviceId { get; init; }

    [JsonPropertyName("last_event_seq")]
    public long LastEventSeq { get; init; }

    [JsonPropertyName("last_synced_at")]
    public long LastSyncedAt { get; init; }
}

public sealed class PeerShadowTurnDto
{
    [JsonPropertyName("local_shadow_id")]
    public string LocalShadowId { get; init; } = string.Empty;

    [JsonPropertyName("turn_id")]
    public string TurnId { get; init; } = string.Empty;

    [JsonPropertyName("digest_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DigestJson { get; init; }

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; init; }
}

public sealed class PeerShadowMessagePreviewDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("turn_id")]
    public string TurnId { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("pending_confirmation")]
    public bool PendingConfirmation { get; init; }

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; init; }
}

public sealed class PeerShadowSessionListDto
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("sessions")]
    public IReadOnlyList<PeerShadowSessionDto> Sessions { get; init; } = [];
}

public sealed class PeerShadowSessionDetailDto
{
    [JsonPropertyName("session")]
    public PeerShadowSessionDto Session { get; init; } = new();

    [JsonPropertyName("turns")]
    public IReadOnlyList<PeerShadowTurnDto> Turns { get; init; } = [];

    [JsonPropertyName("messages")]
    public IReadOnlyList<PeerShadowMessagePreviewDto> Messages { get; init; } = [];
}

public sealed record PeerIngressSubmitCommand(
    string SessionId,
    string ClientRequestId,
    string OriginDeviceId,
    string OriginDisplayName,
    string Method,
    string PayloadJson);

public sealed record PeerIngressTicketContext(
    string SessionId,
    string ClientRequestId,
    long EnqueuedSeq,
    string OriginDeviceId,
    string OriginDisplayName);

public sealed record PeerIngressWorkResult(
    bool Accepted,
    int HttpStatusCode,
    string? RunId = null,
    string? TurnId = null,
    string? MessageId = null,
    long? LastEventSeq = null,
    string? RejectErrorCode = null,
    string? ResponseJson = null,
    string? Location = null);

public sealed record PeerIngressExecutionOutcome(
    bool IdempotentReplay,
    int HttpStatusCode,
    string SessionId,
    string ClientRequestId,
    long EnqueuedSeq,
    string Stage,
    string? RunId,
    string? TurnId,
    string? MessageId,
    long? LastEventSeq,
    string? RejectErrorCode,
    string? ResponseJson,
    string? Location);

public sealed class PeerIngressStatusDto
{
    [JsonPropertyName("found")]
    public bool Found { get; init; }

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("client_request_id")]
    public string ClientRequestId { get; init; } = string.Empty;

    [JsonPropertyName("enqueued_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EnqueuedSeq { get; init; }

    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; init; }

    [JsonPropertyName("origin_device_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginDeviceId { get; init; }

    [JsonPropertyName("origin_display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginDisplayName { get; init; }

    [JsonPropertyName("run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("turn_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }

    [JsonPropertyName("message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageId { get; init; }

    [JsonPropertyName("last_event_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LastEventSeq { get; init; }

    [JsonPropertyName("stream_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StreamCursor { get; init; }

    [JsonPropertyName("reject_error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RejectErrorCode { get; init; }

    [JsonPropertyName("after_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AfterSeq { get; init; }

    [JsonPropertyName("events")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<object>? Events { get; init; }
}

public sealed class PeerAttachmentStageJsonRequestDto
{
    [JsonPropertyName("stage_id")]
    public string? StageId { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    [JsonPropertyName("total_bytes")]
    public long? TotalBytes { get; init; }

    [JsonPropertyName("sha256_hex")]
    public string? Sha256Hex { get; init; }

    [JsonPropertyName("chunk_index")]
    public int? ChunkIndex { get; init; }

    [JsonPropertyName("total_chunks")]
    public int? TotalChunks { get; init; }

    [JsonPropertyName("chunk_base64")]
    public string? ChunkBase64 { get; init; }

    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("source_path")]
    public string? SourcePath { get; init; }
}

public sealed record PeerAttachmentStageCommand(
    string? StageId,
    Guid? SessionId,
    Guid? ProjectId,
    string FileName,
    string MediaType,
    string Origin,
    int ChunkIndex,
    int TotalChunks,
    long? DeclaredTotalBytes,
    string? ExpectedSha256Hex,
    string? ValidatedWorkspaceSourcePath,
    long? WorkspaceSourceFileBytes,
    byte[]? ChunkBytes);

public sealed class PeerAttachmentStageResultDto
{
    [JsonPropertyName("stage_id")]
    public string StageId { get; init; } = string.Empty;

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("zero_copy")]
    public bool ZeroCopy { get; init; }

    [JsonPropertyName("origin")]
    public string Origin { get; init; } = "composer";

    [JsonPropertyName("source_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourcePath { get; init; }

    [JsonPropertyName("attachment_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttachmentId { get; init; }

    [JsonPropertyName("attachment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageAttachmentDto? Attachment { get; init; }

    [JsonPropertyName("received_chunks")]
    public int ReceivedChunks { get; init; }

    [JsonPropertyName("total_chunks")]
    public int TotalChunks { get; init; }
}

public sealed record PeerResolvedStagedAttachment(
    string StageId,
    string Origin,
    bool ZeroCopy,
    Guid? AttachmentId,
    string FileName,
    string MediaType,
    long TotalBytes,
    string? SourcePath);

// --- Wave 3: Cross-Device Multi-Agent Delegation, Observability, and Provider Proxy Session Binding ---

public static class PeerDelegationConstants
{
    public const int MaxDelegationHopDepth = 2;
    public const string DefaultGatewayBaseUrl = "http://127.0.0.1:48730";
    public const string ProxyTokenHeader = "x-tinadec-peer-proxy-token";
    public const string DelegationOriginPrefix = "peer_delegation:hop=";

    public static string CurrentPlatformId =>
        OperatingSystem.IsMacOS() ? "darwin"
        : OperatingSystem.IsWindows() ? "win32"
        : "linux";

    public static string ResolveGatewayBaseUrl(string? configuredUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl.Trim().TrimEnd('/');
        }

        var env = Environment.GetEnvironmentVariable("TINADEC_GATEWAY_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim().TrimEnd('/');
        }

        return DefaultGatewayBaseUrl;
    }

    public static string FormatSessionOrigin(int hopDepth) =>
        $"{DelegationOriginPrefix}{Math.Max(1, hopDepth)}";

    public static int? TryParseHopFromOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        var trimmed = origin.Trim();
        if (!trimmed.StartsWith(DelegationOriginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = trimmed[DelegationOriginPrefix.Length..];
        var sep = suffix.IndexOfAny([':', ';', ' ']);
        if (sep >= 0) suffix = suffix[..sep];
        return int.TryParse(suffix, out var hop) && hop >= 1 ? hop : null;
    }
}

public sealed record PeerProviderProxyTokenIssueRequestDto
{
    [JsonPropertyName("controller_device_id")]
    public string ControllerDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("worker_device_id")]
    public string WorkerDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("delegation_id")]
    public string DelegationId { get; init; } = string.Empty;

    [JsonPropertyName("remote_session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteSessionId { get; init; }

    [JsonPropertyName("expires_at_ms")]
    public long ExpiresAtMs { get; init; }

    [JsonPropertyName("max_requests")]
    public int MaxRequests { get; init; } = 128;

    [JsonPropertyName("model_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelHint { get; init; }
}

public sealed record PeerProviderProxyTokenGrantDto
{
    [JsonPropertyName("proxy_session_token")]
    public string ProxySessionToken { get; init; } = string.Empty;

    [JsonPropertyName("expires_at_ms")]
    public long ExpiresAtMs { get; init; }

    [JsonPropertyName("max_requests")]
    public int MaxRequests { get; init; }
}

public interface IPeerProviderProxyTokenIssuer
{
    Task<PeerProviderProxyTokenGrantDto> IssueTokenAsync(
        PeerProviderProxyTokenIssueRequestDto request,
        CancellationToken cancellationToken = default);
}

public sealed record PeerProviderProxySessionBinding(
    string SessionId,
    string? RunId,
    string? DelegationId,
    string ControllerDeviceId,
    string ProxySessionToken,
    bool UseProviderProxy,
    long ExpiresAtMs,
    string? GatewayLoopbackUrl = null,
    string? ModelHint = null);

public interface IPeerProviderProxyContextStore
{
    void Register(PeerProviderProxySessionBinding binding);
    void AttachRun(string sessionId, string runId);
    bool TryGetBySession(string sessionId, out PeerProviderProxySessionBinding? binding);
    bool TryGetByRun(string runId, out PeerProviderProxySessionBinding? binding);
    void RemoveBySession(string sessionId);
}

/// <summary>
/// Ephemeral process-wide in-memory registry mapping delegated worker sessions/runs to their
/// short-lived <c>proxy_session_token</c> and controller device identity.
/// Never persisted to SQLite, SecretStore, or logs; shared across user and project storage scopes.
/// </summary>
public sealed class PeerProviderProxyRuntimeRegistry : IPeerProviderProxyContextStore
{
    private static readonly ConcurrentDictionary<string, PeerProviderProxySessionBinding> BySession = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> RunToSession = new(StringComparer.OrdinalIgnoreCase);

    public static PeerProviderProxyRuntimeRegistry Default { get; } = new();

    void IPeerProviderProxyContextStore.Register(PeerProviderProxySessionBinding binding) => Register(binding);
    void IPeerProviderProxyContextStore.AttachRun(string sessionId, string runId) => AttachRun(sessionId, runId);
    bool IPeerProviderProxyContextStore.TryGetBySession(string sessionId, out PeerProviderProxySessionBinding? binding) => TryGetBySession(sessionId, out binding);
    bool IPeerProviderProxyContextStore.TryGetByRun(string runId, out PeerProviderProxySessionBinding? binding) => TryGetByRun(runId, out binding);
    void IPeerProviderProxyContextStore.RemoveBySession(string sessionId) => RemoveBySession(sessionId);

    public static void Register(PeerProviderProxySessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(binding.SessionId)) return;

        var normalizedSession = binding.SessionId.Trim();
        BySession[normalizedSession] = binding;
        if (!string.IsNullOrWhiteSpace(binding.RunId))
        {
            RunToSession[binding.RunId.Trim()] = normalizedSession;
        }
    }

    public static void AttachRun(string sessionId, string runId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(runId)) return;
        var normalizedSession = sessionId.Trim();
        var normalizedRun = runId.Trim();
        RunToSession[normalizedRun] = normalizedSession;
        if (BySession.TryGetValue(normalizedSession, out var existing))
        {
            BySession[normalizedSession] = existing with { RunId = normalizedRun };
        }
    }

    public static bool TryGetBySession(string? sessionId, out PeerProviderProxySessionBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (!BySession.TryGetValue(sessionId.Trim(), out var found)) return false;
        if (found.ExpiresAtMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > found.ExpiresAtMs)
        {
            RemoveBySession(sessionId);
            return false;
        }

        binding = found;
        return true;
    }

    public static PeerProviderProxySessionBinding? TryGetBySession(Guid sessionId) =>
        TryGetBySession(sessionId.ToString(), out var binding) ? binding : null;

    public static bool TryGetByRun(string? runId, out PeerProviderProxySessionBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(runId)) return false;
        if (!RunToSession.TryGetValue(runId.Trim(), out var sessionId)) return false;
        return TryGetBySession(sessionId, out binding);
    }

    public static PeerProviderProxySessionBinding? TryGetByRun(Guid runId) =>
        TryGetByRun(runId.ToString(), out var binding) ? binding : null;

    public static void RemoveBySession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var normalized = sessionId.Trim();
        if (BySession.TryRemove(normalized, out var removed) && !string.IsNullOrWhiteSpace(removed.RunId))
        {
            RunToSession.TryRemove(removed.RunId, out _);
        }
    }

    public static void ClearForTesting()
    {
        BySession.Clear();
        RunToSession.Clear();
    }
}

public sealed record PeerInboundDelegationContext(
    string SessionId,
    string? RunId,
    string DelegationId,
    string ControllerDeviceId,
    string ControllerDisplayName,
    int HopDepth,
    IReadOnlyList<string> HopChain);

/// <summary>
/// Process-wide inbound delegation hop-depth tracker shared across storage scopes so a worker run
/// spawned at hop_depth=1 or hop_depth=2 enforces <c>MAX_DELEGATION_HOP_DEPTH = 2</c> locally on
/// any nested <c>peer_delegate_task</c> attempt.
/// </summary>
public static class PeerInboundDelegationRuntimeRegistry
{
    private static readonly ConcurrentDictionary<string, PeerInboundDelegationContext> BySession = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> RunToSession = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(PeerInboundDelegationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.SessionId)) return;
        var normalizedSession = context.SessionId.Trim();
        BySession[normalizedSession] = context;
        if (!string.IsNullOrWhiteSpace(context.RunId))
        {
            RunToSession[context.RunId.Trim()] = normalizedSession;
        }
    }

    public static void AttachRun(string sessionId, string runId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(runId)) return;
        var normalizedSession = sessionId.Trim();
        var normalizedRun = runId.Trim();
        RunToSession[normalizedRun] = normalizedSession;
        if (BySession.TryGetValue(normalizedSession, out var existing))
        {
            BySession[normalizedSession] = existing with { RunId = normalizedRun };
        }
    }

    public static PeerInboundDelegationContext? TryGet(Guid? sessionId, Guid? runId)
    {
        if (runId is { } rid && rid != Guid.Empty && RunToSession.TryGetValue(rid.ToString(), out var mappedSession)
            && BySession.TryGetValue(mappedSession, out var byRun))
        {
            return byRun;
        }

        if (sessionId is { } sid && sid != Guid.Empty && BySession.TryGetValue(sid.ToString(), out var bySession))
        {
            return bySession;
        }

        return null;
    }

    public static int ResolveHopDepth(Guid? sessionId, Guid? runId, string? sessionOrigin = null)
    {
        if (TryGet(sessionId, runId) is { } ctx)
        {
            return Math.Max(1, ctx.HopDepth);
        }

        if (PeerDelegationConstants.TryParseHopFromOrigin(sessionOrigin) is { } parsedHop)
        {
            return parsedHop;
        }

        return 0;
    }

    public static IReadOnlyList<string> ResolveHopChain(Guid? sessionId, Guid? runId) =>
        TryGet(sessionId, runId)?.HopChain ?? [];

    public static void ClearForTesting()
    {
        BySession.Clear();
        RunToSession.Clear();
    }
}

public sealed record PeerProxyHttpResponse(
    int StatusCode,
    string Body,
    string? ContentType = null)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;
}

public interface IPeerTransportLoopbackClient
{
    Task<PeerProxyHttpResponse> SendProxyAsync(
        string deviceId,
        HttpMethod method,
        string remoteApiPath,
        string? jsonBody = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default);

    Task<PeerProxyHttpResponse> SendGatewayLocalAsync(
        HttpMethod method,
        string gatewayPath,
        string? jsonBody = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default);
}

public sealed record PeerNodeWorkspaceDto
{
    [JsonPropertyName("remote_project_id")]
    public string RemoteProjectId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("remote_root_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRootPath { get; init; }

    [JsonPropertyName("storage_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StorageId { get; init; }
}

public sealed record PeerExecutionNodeDto
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_id")]
    public string RemoteDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("transport")]
    public string Transport { get; init; } = "lan";

    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = "ready";

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "linux";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "operator";

    [JsonPropertyName("allow_agent_delegation")]
    public bool AllowAgentDelegation { get; init; }

    [JsonPropertyName("allow_provider_proxy")]
    public bool AllowProviderProxy { get; init; }

    [JsonPropertyName("workspaces")]
    public IReadOnlyList<PeerNodeWorkspaceDto> Workspaces { get; init; } = [];
}

public sealed record PeerNodeListResultDto
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<PeerExecutionNodeDto> Nodes { get; init; } = [];
}

public sealed record PeerWorkerNodeInfoDto
{
    [JsonPropertyName("local_device_id")]
    public string LocalDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = PeerDelegationConstants.CurrentPlatformId;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "operator";

    [JsonPropertyName("allow_agent_delegation")]
    public bool AllowAgentDelegation { get; init; }

    [JsonPropertyName("allow_provider_proxy")]
    public bool AllowProviderProxy { get; init; }

    [JsonPropertyName("workspaces")]
    public IReadOnlyList<PeerNodeWorkspaceDto> Workspaces { get; init; } = [];
}

public sealed record PeerDelegateTaskCommand
{
    public Guid ParentRunId { get; init; }
    public Guid ParentSessionId { get; init; }
    public string RemoteDeviceId { get; init; } = string.Empty;
    public string RemoteProjectId { get; init; } = string.Empty;
    public string Goal { get; init; } = string.Empty;
    public string? BranchOrCommit { get; init; }
    public IReadOnlyList<string>? WriteScope { get; init; }
    public bool UseProviderProxy { get; init; }
    public string? CallbackMode { get; init; }
    public string? TeammateHandle { get; init; }
    public int? CurrentHopDepth { get; init; }
    public IReadOnlyList<string>? HopChain { get; init; }
    public string? PermissionMode { get; init; }
    public string? ModelHint { get; init; }
}

public sealed record PeerInboundDelegateRequestDto
{
    [JsonPropertyName("delegation_id")]
    public string? DelegationId { get; init; }

    [JsonPropertyName("parent_run_id")]
    public string? ParentRunId { get; init; }

    [JsonPropertyName("parent_session_id")]
    public string? ParentSessionId { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("remote_project_id")]
    public string? RemoteProjectId { get; init; }

    [JsonPropertyName("teammate_handle")]
    public string? TeammateHandle { get; init; }

    [JsonPropertyName("goal")]
    public string? Goal { get; init; }

    [JsonPropertyName("branch_or_commit")]
    public string? BranchOrCommit { get; init; }

    [JsonPropertyName("write_scope")]
    public IReadOnlyList<string>? WriteScope { get; init; }

    [JsonPropertyName("hop_depth")]
    public int HopDepth { get; init; } = 1;

    [JsonPropertyName("hop_chain")]
    public IReadOnlyList<string>? HopChain { get; init; }

    [JsonPropertyName("use_provider_proxy")]
    public bool UseProviderProxy { get; init; }

    [JsonPropertyName("proxy_session_token")]
    public string? ProxySessionToken { get; init; }

    [JsonPropertyName("callback_mode")]
    public string? CallbackMode { get; init; }

    [JsonPropertyName("permission_mode")]
    public string? PermissionMode { get; init; }

    [JsonPropertyName("model_hint")]
    public string? ModelHint { get; init; }

    [JsonPropertyName("gateway_loopback_url")]
    public string? GatewayLoopbackUrl { get; init; }
}

public sealed record PeerInboundDelegateResponseDto
{
    [JsonPropertyName("delegation_id")]
    public string DelegationId { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_id")]
    public string RemoteDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_name")]
    public string RemoteDeviceName { get; init; } = string.Empty;

    [JsonPropertyName("remote_session_id")]
    public string RemoteSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRunId { get; init; }

    [JsonPropertyName("remote_storage_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteStorageId { get; init; }

    [JsonPropertyName("teammate_handle")]
    public string TeammateHandle { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    [JsonPropertyName("hop_depth")]
    public int HopDepth { get; init; } = 1;

    [JsonPropertyName("provider_proxy_enabled")]
    public bool ProviderProxyEnabled { get; init; }

    [JsonPropertyName("runtime_badge")]
    public string RuntimeBadge { get; init; } = string.Empty;
}

public sealed record PeerDelegationLinkDto
{
    [JsonPropertyName("delegation_id")]
    public string DelegationId { get; init; } = string.Empty;

    [JsonPropertyName("parent_run_id")]
    public string ParentRunId { get; init; } = string.Empty;

    [JsonPropertyName("parent_session_id")]
    public string ParentSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_id")]
    public string RemoteDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_name")]
    public string RemoteDeviceName { get; init; } = string.Empty;

    [JsonPropertyName("remote_session_id")]
    public string RemoteSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRunId { get; init; }

    [JsonPropertyName("teammate_handle")]
    public string TeammateHandle { get; init; } = string.Empty;

    [JsonPropertyName("runtime_badge")]
    public string RuntimeBadge { get; init; } = string.Empty;

    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("write_scope")]
    public IReadOnlyList<string> WriteScope { get; init; } = [];

    [JsonPropertyName("hop_depth")]
    public int HopDepth { get; init; } = 1;

    [JsonPropertyName("provider_proxy_enabled")]
    public bool ProviderProxyEnabled { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    [JsonPropertyName("awaiting_approval")]
    public bool AwaitingApproval { get; init; }

    [JsonPropertyName("summary_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SummaryText { get; init; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; init; }
}

public sealed record PeerRemoteApprovalCardDto
{
    [JsonPropertyName("approval_id")]
    public string ApprovalId { get; init; } = string.Empty;

    [JsonPropertyName("run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("task_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }

    [JsonPropertyName("capability")]
    public string Capability { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("resource")]
    public string Resource { get; init; } = string.Empty;

    [JsonPropertyName("risk")]
    public string Risk { get; init; } = "medium";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "awaiting_approval";

    [JsonPropertyName("decided_by_device_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecidedByDeviceId { get; init; }

    [JsonPropertyName("decided_by_device_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecidedByDeviceName { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record PeerRemoteEventDigestDto
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record PeerDelegationTaskStatusDto
{
    [JsonPropertyName("delegation_id")]
    public string DelegationId { get; init; } = string.Empty;

    [JsonPropertyName("parent_run_id")]
    public string ParentRunId { get; init; } = string.Empty;

    [JsonPropertyName("parent_session_id")]
    public string ParentSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_id")]
    public string RemoteDeviceId { get; init; } = string.Empty;

    [JsonPropertyName("remote_device_name")]
    public string RemoteDeviceName { get; init; } = string.Empty;

    [JsonPropertyName("remote_session_id")]
    public string RemoteSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRunId { get; init; }

    [JsonPropertyName("teammate_handle")]
    public string TeammateHandle { get; init; } = string.Empty;

    [JsonPropertyName("runtime_badge")]
    public string RuntimeBadge { get; init; } = string.Empty;

    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("write_scope")]
    public IReadOnlyList<string> WriteScope { get; init; } = [];

    [JsonPropertyName("hop_depth")]
    public int HopDepth { get; init; } = 1;

    [JsonPropertyName("provider_proxy_enabled")]
    public bool ProviderProxyEnabled { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    [JsonPropertyName("awaiting_approval")]
    public bool AwaitingApproval { get; init; }

    [JsonPropertyName("summary_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SummaryText { get; init; }

    [JsonPropertyName("pending_approvals")]
    public IReadOnlyList<PeerRemoteApprovalCardDto> PendingApprovals { get; init; } = [];

    [JsonPropertyName("recent_events")]
    public IReadOnlyList<PeerRemoteEventDigestDto> RecentEvents { get; init; } = [];

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; init; }
}

public sealed record PeerWorkerSessionStatusDto
{
    [JsonPropertyName("remote_session_id")]
    public string RemoteSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRunId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    [JsonPropertyName("raw_run_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawRunStatus { get; init; }

    [JsonPropertyName("awaiting_approval")]
    public bool AwaitingApproval { get; init; }

    [JsonPropertyName("summary_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SummaryText { get; init; }

    [JsonPropertyName("pending_approvals")]
    public IReadOnlyList<PeerRemoteApprovalCardDto> PendingApprovals { get; init; } = [];

    [JsonPropertyName("recent_events")]
    public IReadOnlyList<PeerRemoteEventDigestDto> RecentEvents { get; init; } = [];
}

public sealed record PeerWaitTasksCommand
{
    public Guid? ParentRunId { get; init; }
    public IReadOnlyList<string>? DelegationIds { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int PollIntervalMs { get; init; } = 150;
}

public sealed record PeerWaitTasksResultDto
{
    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("timed_out")]
    public bool TimedOut { get; init; }

    [JsonPropertyName("awaiting_approval")]
    public bool AwaitingApproval { get; init; }

    [JsonPropertyName("woken_reason")]
    public string WokenReason { get; init; } = "completed";

    [JsonPropertyName("tasks")]
    public IReadOnlyList<PeerDelegationTaskStatusDto> Tasks { get; init; } = [];
}

public sealed record PeerRemoteApprovalDecisionRequestDto
{
    [JsonPropertyName("approve")]
    public bool Approve { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record PeerRemoteApprovalDecisionResultDto
{
    [JsonPropertyName("delegation_id")]
    public string DelegationId { get; init; } = string.Empty;

    [JsonPropertyName("approval_id")]
    public string ApprovalId { get; init; } = string.Empty;

    [JsonPropertyName("approved")]
    public bool Approved { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("decided_by_device_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecidedByDeviceId { get; init; }

    [JsonPropertyName("decided_by_device_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecidedByDeviceName { get; init; }

    [JsonPropertyName("remote_session_id")]
    public string RemoteSessionId { get; init; } = string.Empty;

    [JsonPropertyName("remote_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRunId { get; init; }
}

public interface IPeerDelegationCoordinator
{
    Task<PeerNodeListResultDto> ListNodesAsync(CancellationToken cancellationToken = default);

    Task<PeerDelegationLinkDto> DelegateTaskAsync(
        PeerDelegateTaskCommand command,
        CancellationToken cancellationToken = default);

    Task<PeerDelegationTaskStatusDto> ReadTaskAsync(
        string delegationId,
        CancellationToken cancellationToken = default);

    Task<PeerWaitTasksResultDto> WaitTasksAsync(
        PeerWaitTasksCommand command,
        CancellationToken cancellationToken = default);

    Task<PeerDelegationTaskStatusDto> CancelTaskAsync(
        string delegationId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PeerDelegationLinkDto>> ListRunDelegationsAsync(
        Guid parentRunId,
        bool refreshActive = false,
        CancellationToken cancellationToken = default);

    Task<PeerRemoteApprovalDecisionResultDto> DecideRemoteApprovalAsync(
        string delegationId,
        Guid approvalId,
        bool approve,
        string? reason = null,
        CancellationToken cancellationToken = default);

    int ResolveCurrentHopDepth(
        Guid? sessionId,
        Guid? runId,
        string? sessionOrigin = null);
}
