using System.Text.Json.Serialization;

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
