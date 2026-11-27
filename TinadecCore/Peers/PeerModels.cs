using System.Text.Json.Serialization;

namespace TinadecCore.Peers;

public static class PeerConstants
{
    public const int DefaultListenPort = 48735;
    public const int ProtocolMajor = 1;
    public const int ProtocolMinor = 0;
    public const int DefaultPairingCodeTtlSeconds = 300;
    public const int MaxPairingCodeAttempts = 5;

    public static readonly IReadOnlySet<string> Transports = new HashSet<string>(StringComparer.Ordinal)
    {
        "lan",
        "relay"
    };

    public static readonly IReadOnlySet<string> ConnectionStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "disconnected",
        "connecting",
        "handshaking",
        "ready",
        "version_incompatible",
        "unauthorized",
        "fingerprint_mismatch",
        "error"
    };

    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal)
    {
        "observer",
        "operator",
        "owner"
    };

    public static readonly IReadOnlySet<string> WorkspaceModes = new HashSet<string>(StringComparer.Ordinal)
    {
        "all",
        "selected"
    };

    public static readonly IReadOnlySet<string> IngressStages = new HashSet<string>(StringComparer.Ordinal)
    {
        "received",
        "processing",
        "processed",
        "rejected"
    };

    public static readonly IReadOnlySet<string> StagedAttachmentStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "staging",
        "completed",
        "expired",
        "failed"
    };

    public static readonly IReadOnlySet<string> DelegationStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "queued",
        "running",
        "awaiting_approval",
        "completed",
        "failed",
        "cancelled"
    };
}

public sealed class PeerInboundSettingsRecord
{
    public int Id { get; set; } = 1;
    public string LocalDeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Enabled { get; set; }
    public int ListenPort { get; set; } = PeerConstants.DefaultListenPort;
    public long UpdatedAt { get; set; }
}

public sealed class PeerDeviceRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Transport { get; set; } = "lan";
    public string Address { get; set; } = string.Empty;
    public string RemoteDeviceId { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
    public int ProtocolMajor { get; set; } = PeerConstants.ProtocolMajor;
    public int ProtocolMinor { get; set; } = PeerConstants.ProtocolMinor;
    public string? AccountIdentity { get; set; }
    public string? OrganizationId { get; set; }
    public long PairedAt { get; set; }
    public long? LastConnectedAt { get; set; }
    public int ConnectionDesired { get; set; } = 1;
    public int AdoptSessions { get; set; } = 1;
    public string LastState { get; set; } = "disconnected";
    public string? LastFailureCode { get; set; }
    public string? AccessRevision { get; set; }
    public long? ArchivedAt { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class PeerGrantRecord
{
    public string GrantId { get; set; } = string.Empty;
    public string ControllerDeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Transport { get; set; } = "lan";
    public string CertificateFingerprint { get; set; } = string.Empty;
    public string? AccountIdentity { get; set; }
    public string? OrganizationId { get; set; }
    public long GrantedAt { get; set; }
    public long? LastControlledAt { get; set; }
    public long? RevokedAt { get; set; }
}

public sealed class PeerAccessPolicyRecord
{
    public string ControllerDeviceId { get; set; } = string.Empty;
    public string Role { get; set; } = "operator";
    public string WorkspaceMode { get; set; } = "all";
    public string? WorkspaceIdsJson { get; set; }
    public int AllowNonWorkspaceSessions { get; set; }
    public int SyncAllSessions { get; set; }
    public int AllowAgentDelegation { get; set; }
    public int AllowProviderProxy { get; set; }
    public string Revision { get; set; } = string.Empty;
    public long UpdatedAt { get; set; }
}

public sealed class PeerShadowWorkspaceRecord
{
    public string AliasWorkspaceId { get; set; } = string.Empty;
    public string PeerDeviceId { get; set; } = string.Empty;
    public string RemoteProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? RemoteRootPath { get; set; }
    public int Visible { get; set; } = 1;
    public long SyncedAt { get; set; }
}

public sealed class PeerShadowSessionRecord
{
    public string LocalShadowId { get; set; } = string.Empty;
    public string PeerDeviceId { get; set; } = string.Empty;
    public string RemoteSessionId { get; set; } = string.Empty;
    public string? RemoteProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string RunStatus { get; set; } = "idle";
    public string? InitiatorDeviceId { get; set; }
    public long LastEventSeq { get; set; }
    public long LastSyncedAt { get; set; }
}

public sealed class PeerShadowTurnRecord
{
    public string LocalShadowId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string? DigestJson { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class PeerIngressQueueRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string ClientRequestId { get; set; } = string.Empty;
    public long EnqueuedSeq { get; set; }
    public string OriginDeviceId { get; set; } = string.Empty;
    public string OriginDisplayName { get; set; } = string.Empty;
    public string Method { get; set; } = "session.send";
    public string PayloadJson { get; set; } = "{}";
    public string Stage { get; set; } = "received";
    public string? RejectErrorCode { get; set; }
    public string? RunId { get; set; }
    public string? TurnId { get; set; }
    public string? MessageId { get; set; }
    public long? LastEventSeq { get; set; }
    public string? ResponseJson { get; set; }
    public long ReceivedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class PeerStagedAttachmentRecord
{
    public string StageId { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? ProjectId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string MediaType { get; set; } = "application/octet-stream";
    public string Origin { get; set; } = "composer";
    public long TotalBytes { get; set; }
    public int TotalChunks { get; set; } = 1;
    public string? Sha256Hex { get; set; }
    public string ReceivedChunksJson { get; set; } = "[]";
    public string Status { get; set; } = "staging";
    public string? SourcePath { get; set; }
    public string? AttachmentId { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class PeerDelegationLinkRecord
{
    public string DelegationId { get; set; } = string.Empty;
    public string ParentRunId { get; set; } = string.Empty;
    public string ParentSessionId { get; set; } = string.Empty;
    public string RemoteDeviceId { get; set; } = string.Empty;
    public string RemoteSessionId { get; set; } = string.Empty;
    public string? RemoteRunId { get; set; }
    public string TeammateHandle { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string? WriteScopeJson { get; set; }
    public int HopDepth { get; set; } = 1;
    public int ProviderProxyEnabled { get; set; }
    public string Status { get; set; } = "queued";
    public string? SummaryText { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>
/// Ephemeral pairing secret stored exclusively inside <c>ISecretStore</c> under
/// <see cref="PeerSecretKeys.ActivePairingCode"/>. Never persisted in SQLite.
/// </summary>
internal sealed record ActivePairingCodeSecret(
    [property: JsonPropertyName("codeHash")] string CodeHash,
    [property: JsonPropertyName("salt")] string Salt,
    [property: JsonPropertyName("expiresAtMs")] long ExpiresAtMs,
    [property: JsonPropertyName("remainingAttempts")] int RemainingAttempts,
    [property: JsonPropertyName("relayEnrolled")] bool RelayEnrolled);
