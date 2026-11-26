using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

public sealed class PeerPairingService : IPeerPairingService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly IPeerIdentityService _identityService;
    private readonly PeerPolicyService _policyService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _handshakeGate = new(1, 1);

    public PeerPairingService(
        IDbContextFactory<PeerDbContext> dbFactory,
        ISecretStore secretStore,
        IPeerIdentityService identityService,
        PeerPolicyService policyService,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _identityService = identityService;
        _policyService = policyService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        PeerSecretKeys.ValidateAll();
    }

    public async Task<PeerPairingCodeStatusDto> GeneratePairingCodeAsync(
        PeerGeneratePairingCodeRequestDto? request = null,
        CancellationToken cancellationToken = default)
    {
        var ttlSeconds = Math.Clamp(
            request?.TtlSeconds ?? PeerConstants.DefaultPairingCodeTtlSeconds,
            30,
            PeerConstants.DefaultPairingCodeTtlSeconds);
        var relayEnrolled = request?.RelayEnrolled ?? false;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAtMs = nowMs + (ttlSeconds * 1000L);

        // 6-digit cryptographically random one-time pairing code (6..8 digit spec compliant).
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var codeHash = HashPairingCode(salt, code);

        var secret = new ActivePairingCodeSecret(
            CodeHash: codeHash,
            Salt: salt,
            ExpiresAtMs: expiresAtMs,
            RemainingAttempts: PeerConstants.MaxPairingCodeAttempts,
            RelayEnrolled: relayEnrolled);

        await _handshakeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PutActivePairingSecretAsync(secret, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _handshakeGate.Release();
        }

        return new PeerPairingCodeStatusDto
        {
            Active = true,
            PairingCode = code,
            ExpiresAtMs = expiresAtMs,
            RemainingAttempts = PeerConstants.MaxPairingCodeAttempts,
            RelayEnrolled = relayEnrolled
        };
    }

    public async Task<PeerPairingCodeStatusDto> GetPairingCodeStatusAsync(CancellationToken cancellationToken = default)
    {
        await _handshakeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secret = await ReadActivePairingSecretAsync(cancellationToken).ConfigureAwait(false);
            if (secret is null)
            {
                return InactivePairingCodeDto();
            }

            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (secret.ExpiresAtMs <= nowMs || secret.RemainingAttempts <= 0)
            {
                await _secretStore.DeleteAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
                return InactivePairingCodeDto();
            }

            return new PeerPairingCodeStatusDto
            {
                Active = true,
                PairingCode = null,
                ExpiresAtMs = secret.ExpiresAtMs,
                RemainingAttempts = secret.RemainingAttempts,
                RelayEnrolled = secret.RelayEnrolled
            };
        }
        finally
        {
            _handshakeGate.Release();
        }
    }

    public async Task RevokePairingCodeAsync(CancellationToken cancellationToken = default)
    {
        await _handshakeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _secretStore.DeleteAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _handshakeGate.Release();
        }
    }

    public async Task<PeerHandshakeVerifyResponseDto> VerifyHandshakeAsync(
        PeerHandshakeVerifyRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var localIdentity = await _identityService.EnsureLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (!localIdentity.InboundEnabled)
        {
            throw new PeerException(403, PeerErrorCodes.InboundDisabled, "Inbound peer connections are disabled on this device.");
        }

        if (request.ProtocolMajor != PeerConstants.ProtocolMajor)
        {
            throw new PeerException(
                409,
                PeerErrorCodes.ProtocolMajorMismatch,
                $"Peer protocol major version {request.ProtocolMajor} is incompatible with local version {PeerConstants.ProtocolMajor}.");
        }

        var transport = NormalizeTransport(request.Transport ?? "lan");
        var controllerDeviceId = RequireNonEmpty(request.ControllerDeviceId, "controller_device_id", PeerErrorCodes.HandshakeFailed);
        var remoteFingerprint = NormalizeSha256Fingerprint(request.RemoteFingerprint, PeerErrorCodes.HandshakeFailed);
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? controllerDeviceId : request.DisplayName.Trim();
        if (displayName.Length > 256) displayName = displayName[..256];

        if (!string.IsNullOrWhiteSpace(request.ExpectedRemoteFingerprint))
        {
            var expectedLocalFp = NormalizeSha256Fingerprint(request.ExpectedRemoteFingerprint, PeerErrorCodes.FingerprintMismatch);
            if (!FixedTimeEqualsHex(expectedLocalFp, localIdentity.LocalFingerprint))
            {
                throw new PeerException(
                    403,
                    PeerErrorCodes.FingerprintMismatch,
                    "Local certificate fingerprint does not match the controller's expected fingerprint.");
            }
        }

        var pairingMode = (request.PairingMode ?? string.Empty).Trim().ToLowerInvariant();
        if (pairingMode is not ("code" or "grant"))
        {
            throw new PeerException(400, PeerErrorCodes.HandshakeFailed, "pairing_mode must be 'code' or 'grant'.");
        }

        await _handshakeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (pairingMode == "code")
            {
                await ConsumeAndVerifyPairingCodeLockedAsync(request.PairingCode, nowMs, cancellationToken).ConfigureAwait(false);
                var (grant, policy) = await UpsertGrantAndDefaultPolicyAsync(
                    transport,
                    controllerDeviceId,
                    displayName,
                    remoteFingerprint,
                    nowMs,
                    cancellationToken).ConfigureAwait(false);
                var policyDto = PeerPolicyService.ToPolicyDto(policy);
                return new PeerHandshakeVerifyResponseDto
                {
                    Accepted = true,
                    GrantId = grant.GrantId,
                    ControllerDeviceId = grant.ControllerDeviceId,
                    DisplayName = grant.DisplayName,
                    LocalDeviceId = localIdentity.LocalDeviceId,
                    LocalDisplayName = localIdentity.DisplayName,
                    LocalFingerprint = localIdentity.LocalFingerprint,
                    PolicyRevision = policyDto.Revision,
                    Policy = policyDto
                };
            }
            else
            {
                var (grant, policy) = await VerifyExistingGrantAsync(
                    transport,
                    controllerDeviceId,
                    displayName,
                    remoteFingerprint,
                    nowMs,
                    cancellationToken).ConfigureAwait(false);
                var policyDto = PeerPolicyService.ToPolicyDto(policy);
                return new PeerHandshakeVerifyResponseDto
                {
                    Accepted = true,
                    GrantId = grant.GrantId,
                    ControllerDeviceId = grant.ControllerDeviceId,
                    DisplayName = grant.DisplayName,
                    LocalDeviceId = localIdentity.LocalDeviceId,
                    LocalDisplayName = localIdentity.DisplayName,
                    LocalFingerprint = localIdentity.LocalFingerprint,
                    PolicyRevision = policyDto.Revision,
                    Policy = policyDto
                };
            }
        }
        finally
        {
            _handshakeGate.Release();
        }
    }

    public async Task<IReadOnlyList<PeerDeviceDto>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.Devices.AsNoTracking()
                .Where(x => x.ArchivedAt == null)
                .OrderByDescending(x => x.UpdatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return (IReadOnlyList<PeerDeviceDto>)rows.Select(ToDeviceDto).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerDeviceDto?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var normalized = deviceId.Trim();
        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.Devices.AsNoTracking()
                .SingleOrDefaultAsync(x => x.DeviceId == normalized && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            return row is null ? null : ToDeviceDto(row);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerDeviceDto> UpsertPairedDeviceAsync(
        PeerPairDeviceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transport = NormalizeTransport(request.Transport ?? "lan");
        var address = RequireArgument(request.Address, nameof(request.Address));
        var remoteDeviceId = RequireArgument(request.RemoteDeviceId, nameof(request.RemoteDeviceId));
        var certFingerprint = NormalizeSha256Fingerprint(request.CertificateFingerprint, PeerErrorCodes.FingerprintMismatch);

        if (!string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            var expectedFp = NormalizeSha256Fingerprint(request.ExpectedFingerprint, PeerErrorCodes.FingerprintMismatch);
            if (!FixedTimeEqualsHex(certFingerprint, expectedFp))
            {
                throw new PeerException(
                    403,
                    PeerErrorCodes.FingerprintMismatch,
                    "Observed peer certificate fingerprint did not match expected_fingerprint.");
            }
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? remoteDeviceId : request.DisplayName.Trim();
        if (displayName.Length > 256) displayName = displayName[..256];
        var lastState = string.IsNullOrWhiteSpace(request.LastState) ? "ready" : NormalizeState(request.LastState);
        var protocolMajor = request.ProtocolMajor ?? PeerConstants.ProtocolMajor;
        var protocolMinor = request.ProtocolMinor ?? PeerConstants.ProtocolMinor;
        if (protocolMajor != PeerConstants.ProtocolMajor)
        {
            throw new PeerException(409, PeerErrorCodes.ProtocolMajorMismatch, $"Unsupported protocol_major '{protocolMajor}'.");
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var row = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var existing = await db.Devices
                .SingleOrDefaultAsync(x => x.Transport == transport && x.RemoteDeviceId == remoteDeviceId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);

            if (existing is null)
            {
                var requestedId = string.IsNullOrWhiteSpace(request.DeviceId) ? Guid.NewGuid().ToString("N") : request.DeviceId.Trim();
                existing = new PeerDeviceRecord
                {
                    DeviceId = requestedId,
                    DisplayName = displayName,
                    Transport = transport,
                    Address = address,
                    RemoteDeviceId = remoteDeviceId,
                    CertificateFingerprint = certFingerprint,
                    ProtocolMajor = protocolMajor,
                    ProtocolMinor = protocolMinor,
                    PairedAt = nowMs,
                    LastConnectedAt = lastState == "ready" ? nowMs : null,
                    ConnectionDesired = (request.ConnectionDesired ?? true) ? 1 : 0,
                    AdoptSessions = (request.AdoptSessions ?? true) ? 1 : 0,
                    LastState = lastState,
                    LastFailureCode = null,
                    AccessRevision = string.IsNullOrWhiteSpace(request.AccessRevision) ? null : request.AccessRevision.Trim(),
                    CreatedAt = nowMs,
                    UpdatedAt = nowMs
                };
                db.Devices.Add(existing);
            }
            else
            {
                existing.DisplayName = displayName;
                existing.Address = address;
                existing.CertificateFingerprint = certFingerprint;
                existing.ProtocolMajor = protocolMajor;
                existing.ProtocolMinor = protocolMinor;
                existing.PairedAt = nowMs;
                if (lastState == "ready")
                {
                    existing.LastConnectedAt = nowMs;
                    existing.LastFailureCode = null;
                }
                if (request.ConnectionDesired is { } desired)
                    existing.ConnectionDesired = desired ? 1 : 0;
                if (request.AdoptSessions is { } adopt)
                    existing.AdoptSessions = adopt ? 1 : 0;
                existing.LastState = lastState;
                if (request.AccessRevision is not null)
                    existing.AccessRevision = string.IsNullOrWhiteSpace(request.AccessRevision) ? null : request.AccessRevision.Trim();
                existing.UpdatedAt = nowMs;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return existing;
        }, cancellationToken).ConfigureAwait(false);

        return ToDeviceDto(row);
    }

    public async Task<PeerDeviceDto> UpdateDeviceAsync(
        string deviceId,
        PeerUpdateDeviceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedId = RequireArgument(deviceId, nameof(deviceId));
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var row = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var existing = await db.Devices
                .SingleOrDefaultAsync(x => x.DeviceId == normalizedId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Peer device '{normalizedId}' was not found.");

            if (request.DisplayName is not null)
            {
                var name = RequireArgument(request.DisplayName, nameof(request.DisplayName));
                existing.DisplayName = name.Length <= 256 ? name : name[..256];
            }
            if (request.Address is not null)
            {
                existing.Address = RequireArgument(request.Address, nameof(request.Address));
            }
            if (request.ConnectionDesired is { } desired)
            {
                existing.ConnectionDesired = desired ? 1 : 0;
            }
            if (request.AdoptSessions is { } adopt)
            {
                existing.AdoptSessions = adopt ? 1 : 0;
            }
            existing.UpdatedAt = nowMs;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing;
        }, cancellationToken).ConfigureAwait(false);

        return ToDeviceDto(row);
    }

    public async Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var normalizedId = deviceId.Trim();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        return await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var existing = await db.Devices
                .SingleOrDefaultAsync(x => x.DeviceId == normalizedId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (existing is null) return false;

            existing.ArchivedAt = nowMs;
            existing.ConnectionDesired = 0;
            existing.LastState = "disconnected";
            existing.UpdatedAt = nowMs;

            await db.ShadowWorkspaces
                .Where(x => x.PeerDeviceId == normalizedId)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerDeviceDto> UpdateControllerStateAsync(
        PeerControllerStateUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedId = RequireArgument(request.DeviceId, nameof(request.DeviceId));
        var normalizedState = NormalizeState(RequireArgument(request.LastState, nameof(request.LastState)));
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var row = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var existing = await db.Devices
                .SingleOrDefaultAsync(x => x.DeviceId == normalizedId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Peer device '{normalizedId}' was not found.");

            existing.LastState = normalizedState;
            existing.LastFailureCode = string.IsNullOrWhiteSpace(request.LastFailureCode) ? null : request.LastFailureCode.Trim();
            if (request.LastConnectedAt is { } connectedAt)
            {
                existing.LastConnectedAt = connectedAt;
            }
            else if (normalizedState == "ready")
            {
                existing.LastConnectedAt = nowMs;
                existing.LastFailureCode = null;
            }

            if (request.AccessRevision is not null)
            {
                existing.AccessRevision = string.IsNullOrWhiteSpace(request.AccessRevision) ? null : request.AccessRevision.Trim();
            }

            existing.UpdatedAt = nowMs;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing;
        }, cancellationToken).ConfigureAwait(false);

        return ToDeviceDto(row);
    }

    public async Task<IReadOnlyList<PeerRemoteWorkspaceDto>> ListMountedWorkspacesAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = RequireArgument(deviceId, nameof(deviceId));
        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var deviceExists = await db.Devices.AsNoTracking()
                .AnyAsync(x => x.DeviceId == normalizedId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (!deviceExists)
            {
                throw new KeyNotFoundException($"Peer device '{normalizedId}' was not found.");
            }

            var rows = await db.ShadowWorkspaces.AsNoTracking()
                .Where(x => x.PeerDeviceId == normalizedId)
                .OrderBy(x => x.Name)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return (IReadOnlyList<PeerRemoteWorkspaceDto>)rows.Select(ToMountedWorkspaceDto).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PeerRemoteWorkspaceDto>> ReplaceMountedWorkspacesAsync(
        string deviceId,
        IReadOnlyList<PeerWorkspaceMountInputDto> mounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        var normalizedId = RequireArgument(deviceId, nameof(deviceId));
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var rows = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var deviceExists = await db.Devices.AsNoTracking()
                .AnyAsync(x => x.DeviceId == normalizedId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (!deviceExists)
            {
                throw new KeyNotFoundException($"Peer device '{normalizedId}' was not found.");
            }

            var existing = await db.ShadowWorkspaces
                .Where(x => x.PeerDeviceId == normalizedId)
                .ToDictionaryAsync(x => x.RemoteProjectId, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

            foreach (var item in mounts)
            {
                if (string.IsNullOrWhiteSpace(item.RemoteProjectId))
                    continue;
                var remoteId = item.RemoteProjectId.Trim();
                var shouldMount = item.Mounted ?? true;
                if (!shouldMount)
                {
                    if (existing.TryGetValue(remoteId, out var toRemove))
                    {
                        db.ShadowWorkspaces.Remove(toRemove);
                        existing.Remove(remoteId);
                    }
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(item.Name) ? remoteId : item.Name.Trim();
                if (existing.TryGetValue(remoteId, out var row))
                {
                    row.Name = name;
                    if (item.RemoteRootPath is not null)
                        row.RemoteRootPath = string.IsNullOrWhiteSpace(item.RemoteRootPath) ? null : item.RemoteRootPath.Trim();
                    row.Visible = (item.Visible ?? true) ? 1 : 0;
                    row.SyncedAt = nowMs;
                }
                else
                {
                    var created = new PeerShadowWorkspaceRecord
                    {
                        AliasWorkspaceId = $"peerws_{Guid.NewGuid():N}",
                        PeerDeviceId = normalizedId,
                        RemoteProjectId = remoteId,
                        Name = name,
                        RemoteRootPath = string.IsNullOrWhiteSpace(item.RemoteRootPath) ? null : item.RemoteRootPath.Trim(),
                        Visible = (item.Visible ?? true) ? 1 : 0,
                        SyncedAt = nowMs
                    };
                    db.ShadowWorkspaces.Add(created);
                    existing[remoteId] = created;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return existing.Values.OrderBy(x => x.Name).ToArray();
        }, cancellationToken).ConfigureAwait(false);

        return rows.Select(ToMountedWorkspaceDto).ToArray();
    }

    public async Task<int> ClearDeviceLocalCacheAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var normalizedId = RequireArgument(deviceId, nameof(deviceId));
        return await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var deviceExists = await db.Devices.AsNoTracking()
                .AnyAsync(x => x.DeviceId == normalizedId && x.ArchivedAt == null, ct)
                .ConfigureAwait(false);
            if (!deviceExists)
            {
                throw new KeyNotFoundException($"Peer device '{normalizedId}' was not found.");
            }

            return await db.ShadowWorkspaces
                .Where(x => x.PeerDeviceId == normalizedId)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConsumeAndVerifyPairingCodeLockedAsync(
        string? suppliedCode,
        long nowMs,
        CancellationToken cancellationToken)
    {
        var secret = await ReadActivePairingSecretAsync(cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            throw new PeerException(403, PeerErrorCodes.PairingCodeRejected, "No active pairing code exists on this device.");
        }

        if (secret.ExpiresAtMs <= nowMs)
        {
            await _secretStore.DeleteAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
            throw new PeerException(403, PeerErrorCodes.PairingCodeExpired, "The pairing code has expired.");
        }

        if (secret.RemainingAttempts <= 0)
        {
            await _secretStore.DeleteAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
            throw new PeerException(403, PeerErrorCodes.PairingCodeRejected, "The pairing code is locked out after too many failed attempts.");
        }

        var normalizedCode = (suppliedCode ?? string.Empty).Trim();
        var candidateHash = HashPairingCode(secret.Salt, normalizedCode);
        if (!FixedTimeEqualsHex(secret.CodeHash, candidateHash))
        {
            var nextRemaining = secret.RemainingAttempts - 1;
            if (nextRemaining <= 0)
            {
                await _secretStore.DeleteAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
                throw new PeerException(
                    403,
                    PeerErrorCodes.PairingCodeRejected,
                    "Invalid pairing code. Maximum attempts exceeded; the pairing code has been invalidated.");
            }

            await PutActivePairingSecretAsync(secret with { RemainingAttempts = nextRemaining }, cancellationToken)
                .ConfigureAwait(false);
            throw new PeerException(
                403,
                PeerErrorCodes.PairingCodeRejected,
                $"Invalid pairing code ({nextRemaining} attempt(s) remaining).");
        }

        // Single-use guarantee: destroy pairing code secret immediately upon first successful match.
        await _secretStore.DeleteAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(PeerGrantRecord Grant, PeerAccessPolicyRecord Policy)> UpsertGrantAndDefaultPolicyAsync(
        string transport,
        string controllerDeviceId,
        string displayName,
        string remoteFingerprint,
        long nowMs,
        CancellationToken cancellationToken)
    {
        var initialRevision = _policyService.MintRevision(nowMs);
        return await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var grant = await db.Grants
                .SingleOrDefaultAsync(x => x.Transport == transport && x.ControllerDeviceId == controllerDeviceId && x.RevokedAt == null, ct)
                .ConfigureAwait(false);

            if (grant is null)
            {
                grant = new PeerGrantRecord
                {
                    GrantId = $"grant_{Guid.NewGuid():N}",
                    ControllerDeviceId = controllerDeviceId,
                    DisplayName = displayName,
                    Transport = transport,
                    CertificateFingerprint = remoteFingerprint,
                    GrantedAt = nowMs,
                    LastControlledAt = nowMs,
                    RevokedAt = null
                };
                db.Grants.Add(grant);
            }
            else
            {
                grant.DisplayName = displayName;
                grant.CertificateFingerprint = remoteFingerprint;
                grant.LastControlledAt = nowMs;
            }

            var policy = await db.AccessPolicies
                .SingleOrDefaultAsync(x => x.ControllerDeviceId == controllerDeviceId, ct)
                .ConfigureAwait(false);
            if (policy is null)
            {
                policy = PeerPolicyService.CreateDefaultPolicyRecord(controllerDeviceId, nowMs, initialRevision);
                db.AccessPolicies.Add(policy);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (grant, policy);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(PeerGrantRecord Grant, PeerAccessPolicyRecord Policy)> VerifyExistingGrantAsync(
        string transport,
        string controllerDeviceId,
        string displayName,
        string remoteFingerprint,
        long nowMs,
        CancellationToken cancellationToken)
    {
        var fallbackRevision = _policyService.MintRevision(nowMs);
        return await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var grant = await db.Grants
                .SingleOrDefaultAsync(x => x.Transport == transport && x.ControllerDeviceId == controllerDeviceId && x.RevokedAt == null, ct)
                .ConfigureAwait(false);
            if (grant is null)
            {
                throw new PeerException(401, PeerErrorCodes.Unauthorized, "No active peer grant exists for this controller device.");
            }

            if (!FixedTimeEqualsHex(grant.CertificateFingerprint, remoteFingerprint))
            {
                throw new PeerException(
                    403,
                    PeerErrorCodes.FingerprintMismatch,
                    "Controller certificate fingerprint does not match the pinned grant fingerprint.");
            }

            grant.LastControlledAt = nowMs;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                grant.DisplayName = displayName;
            }

            var policy = await db.AccessPolicies
                .SingleOrDefaultAsync(x => x.ControllerDeviceId == controllerDeviceId, ct)
                .ConfigureAwait(false);
            if (policy is null)
            {
                policy = PeerPolicyService.CreateDefaultPolicyRecord(controllerDeviceId, nowMs, fallbackRevision);
                db.AccessPolicies.Add(policy);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (grant, policy);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PutActivePairingSecretAsync(ActivePairingCodeSecret secret, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(secret, JsonOptions);
        try
        {
            await _secretStore.PutAsync(PeerSecretKeys.ActivePairingCode, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            throw new PeerException(
                503,
                PeerErrorCodes.EncryptionUnavailable,
                "Platform secret store is unavailable or read-only; pairing code cannot be stored safely.",
                ex);
        }
    }

    private async Task<ActivePairingCodeSecret?> ReadActivePairingSecretAsync(CancellationToken cancellationToken)
    {
        string? raw;
        try
        {
            raw = await _secretStore.GetAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new PeerException(
                503,
                PeerErrorCodes.EncryptionUnavailable,
                "Failed to read pairing code from platform secret store.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ActivePairingCodeSecret>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            await _secretStore.DeleteAsync(PeerSecretKeys.ActivePairingCode, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private static PeerPairingCodeStatusDto InactivePairingCodeDto() =>
        new()
        {
            Active = false,
            PairingCode = null,
            ExpiresAtMs = null,
            RemainingAttempts = 0,
            RelayEnrolled = false
        };

    private static PeerDeviceDto ToDeviceDto(PeerDeviceRecord record) =>
        new()
        {
            DeviceId = record.DeviceId,
            DisplayName = record.DisplayName,
            Transport = record.Transport,
            Address = record.Address,
            RemoteDeviceId = record.RemoteDeviceId,
            CertificateFingerprint = record.CertificateFingerprint,
            ProtocolMajor = record.ProtocolMajor,
            ProtocolMinor = record.ProtocolMinor,
            PairedAt = record.PairedAt,
            LastConnectedAt = record.LastConnectedAt,
            ConnectionDesired = record.ConnectionDesired != 0,
            AdoptSessions = record.AdoptSessions != 0,
            LastState = record.LastState,
            LastFailureCode = record.LastFailureCode,
            AccessRevision = record.AccessRevision
        };

    private static PeerRemoteWorkspaceDto ToMountedWorkspaceDto(PeerShadowWorkspaceRecord record) =>
        new()
        {
            RemoteProjectId = record.RemoteProjectId,
            Name = record.Name,
            RemoteRootPath = record.RemoteRootPath,
            Mounted = true,
            Visible = record.Visible != 0,
            AliasWorkspaceId = record.AliasWorkspaceId
        };

    private static string HashPairingCode(string salt, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{code}"))).ToLowerInvariant();

    private static bool FixedTimeEqualsHex(string leftHex, string rightHex)
    {
        try
        {
            var left = Convert.FromHexString(leftHex);
            var right = Convert.FromHexString(rightHex);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeTransport(string transport)
    {
        var normalized = transport.Trim().ToLowerInvariant();
        if (!PeerConstants.Transports.Contains(normalized))
        {
            throw new ArgumentException("transport must be 'lan' or 'relay'.", nameof(transport));
        }
        return normalized;
    }

    private static string NormalizeState(string state)
    {
        var normalized = state.Trim().ToLowerInvariant();
        if (!PeerConstants.ConnectionStates.Contains(normalized))
        {
            throw new ArgumentException($"Unsupported peer connection state '{state}'.", nameof(state));
        }
        return normalized;
    }

    private static string NormalizeSha256Fingerprint(string? fingerprint, string failureCode)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new PeerException(400, failureCode, "Certificate SHA-256 fingerprint is required.");
        }

        // Accept either pure 64-hex or colon-delimited hex and normalize to 64 lowercase hex chars.
        var cleaned = fingerprint.Trim().Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (cleaned.Length != 64)
        {
            throw new PeerException(400, failureCode, "Certificate SHA-256 fingerprint must be 64 hexadecimal characters.");
        }

        for (var i = 0; i < cleaned.Length; i++)
        {
            if (!Uri.IsHexDigit(cleaned[i]))
            {
                throw new PeerException(400, failureCode, "Certificate SHA-256 fingerprint contains non-hexadecimal characters.");
            }
        }

        return cleaned;
    }

    private static string RequireNonEmpty(string? value, string fieldName, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PeerException(400, errorCode, $"{fieldName} is required.");
        }
        return value.Trim();
    }

    private static string RequireArgument(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{paramName} is required.", paramName);
        }
        return value.Trim();
    }

    public void Dispose() => _handshakeGate.Dispose();
}
