using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

public sealed class PeerPolicyService : IPeerPolicyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, Channel<PeerPolicyEventDto>> _subscribers = new();
    private long _revisionSequence;

    public PeerPolicyService(IDbContextFactory<PeerDbContext> dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<PeerGrantWithPolicyDto>> ListGrantsAsync(CancellationToken cancellationToken = default)
    {
        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var grants = await db.Grants.AsNoTracking()
                .Where(x => x.RevokedAt == null)
                .OrderByDescending(x => x.GrantedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (grants.Count == 0)
            {
                return (IReadOnlyList<PeerGrantWithPolicyDto>)[];
            }

            var controllerIds = grants.Select(x => x.ControllerDeviceId).Distinct(StringComparer.Ordinal).ToArray();
            var policies = await db.AccessPolicies.AsNoTracking()
                .Where(x => controllerIds.Contains(x.ControllerDeviceId))
                .ToDictionaryAsync(x => x.ControllerDeviceId, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

            return (IReadOnlyList<PeerGrantWithPolicyDto>)grants
                .Select(grant => ToGrantWithPolicyDto(
                    grant,
                    policies.GetValueOrDefault(grant.ControllerDeviceId) ?? CreateDefaultPolicyRecord(grant.ControllerDeviceId, grant.GrantedAt)))
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerGrantWithPolicyDto> UpdateGrantPolicyAsync(
        string controllerDeviceId,
        PeerUpdateAccessPolicyRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(controllerDeviceId))
        {
            throw new ArgumentException("controller_device_id is required.", nameof(controllerDeviceId));
        }

        var normalizedControllerId = controllerDeviceId.Trim();
        var role = NormalizeRole(request.Role);
        var workspaceMode = NormalizeWorkspaceMode(request.WorkspaceMode);
        var workspaceIds = NormalizeWorkspaceIds(workspaceMode, request.WorkspaceIds);
        var workspaceIdsJson = workspaceMode == "all" ? null : JsonSerializer.Serialize(workspaceIds, JsonOptions);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var nextRevision = MintRevision(nowMs);

        var (grant, policy) = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var activeGrant = await db.Grants.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ControllerDeviceId == normalizedControllerId && x.RevokedAt == null, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Active peer grant for controller '{normalizedControllerId}' was not found.");

            var policyRow = await db.AccessPolicies
                .SingleOrDefaultAsync(x => x.ControllerDeviceId == normalizedControllerId, ct)
                .ConfigureAwait(false);

            if (policyRow is null)
            {
                policyRow = new PeerAccessPolicyRecord
                {
                    ControllerDeviceId = normalizedControllerId,
                    Role = role ?? "operator",
                    WorkspaceMode = workspaceMode ?? "all",
                    WorkspaceIdsJson = (workspaceMode ?? "all") == "all" ? null : (workspaceIdsJson ?? "[]"),
                    AllowNonWorkspaceSessions = request.AllowNonWorkspaceSessions == true ? 1 : 0,
                    SyncAllSessions = request.SyncAllSessions == true ? 1 : 0,
                    AllowAgentDelegation = request.AllowAgentDelegation == true ? 1 : 0,
                    AllowProviderProxy = request.AllowProviderProxy == true ? 1 : 0,
                    Revision = nextRevision,
                    UpdatedAt = nowMs
                };
                db.AccessPolicies.Add(policyRow);
            }
            else
            {
                var effectiveMode = workspaceMode ?? policyRow.WorkspaceMode;
                policyRow.Role = role ?? policyRow.Role;
                policyRow.WorkspaceMode = effectiveMode;
                if (effectiveMode == "all")
                {
                    policyRow.WorkspaceIdsJson = null;
                }
                else if (request.WorkspaceIds is not null)
                {
                    policyRow.WorkspaceIdsJson = JsonSerializer.Serialize(NormalizeWorkspaceIds("selected", request.WorkspaceIds), JsonOptions);
                }
                else if (policyRow.WorkspaceIdsJson is null)
                {
                    policyRow.WorkspaceIdsJson = "[]";
                }

                if (request.AllowNonWorkspaceSessions is { } allowNonWs)
                    policyRow.AllowNonWorkspaceSessions = allowNonWs ? 1 : 0;
                if (request.SyncAllSessions is { } syncAll)
                    policyRow.SyncAllSessions = syncAll ? 1 : 0;
                if (request.AllowAgentDelegation is { } allowDelegation)
                    policyRow.AllowAgentDelegation = allowDelegation ? 1 : 0;
                if (request.AllowProviderProxy is { } allowProxy)
                    policyRow.AllowProviderProxy = allowProxy ? 1 : 0;

                policyRow.Revision = nextRevision;
                policyRow.UpdatedAt = nowMs;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (activeGrant, policyRow);
        }, cancellationToken).ConfigureAwait(false);

        var policyDto = ToPolicyDto(policy);
        PublishPolicyEvent(new PeerPolicyEventDto
        {
            Type = "policy_updated",
            ControllerDeviceId = normalizedControllerId,
            Revision = policyDto.Revision,
            Policy = policyDto,
            TimestampMs = nowMs
        });

        return ToGrantWithPolicyDto(grant, policy);
    }

    public async Task<bool> RevokeGrantAsync(string controllerDeviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(controllerDeviceId))
        {
            throw new ArgumentException("controller_device_id is required.", nameof(controllerDeviceId));
        }

        var normalizedControllerId = controllerDeviceId.Trim();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var nextRevision = MintRevision(nowMs);

        var revoked = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var affected = await db.Grants
                .Where(x => x.ControllerDeviceId == normalizedControllerId && x.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, nowMs), ct)
                .ConfigureAwait(false);
            return affected > 0;
        }, cancellationToken).ConfigureAwait(false);

        if (revoked)
        {
            PublishPolicyEvent(new PeerPolicyEventDto
            {
                Type = "grant_revoked",
                ControllerDeviceId = normalizedControllerId,
                Revision = nextRevision,
                TimestampMs = nowMs
            });
        }

        return revoked;
    }

    public async Task<PeerPolicySnapshotDto> GetPolicySnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settings = await PeerMigrations.EnsureInboundSettingsDefaultAsync(_dbFactory, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var grants = await ListGrantsAsync(cancellationToken).ConfigureAwait(false);
        string localFingerprint = string.Empty;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            // Policies corresponding to active grants are authoritative for live guard enforcement.
            _ = db;
        }

        return new PeerPolicySnapshotDto
        {
            InboundEnabled = settings.Enabled != 0,
            ListenPort = settings.ListenPort,
            LocalDeviceId = settings.LocalDeviceId,
            DisplayName = settings.DisplayName,
            LocalFingerprint = localFingerprint,
            Grants = grants,
            Policies = grants.Select(g => g.Policy).ToArray()
        };
    }

    public async IAsyncEnumerable<PeerPolicyEventDto> SubscribePolicyEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateBounded<PeerPolicyEventDto>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[subscriberId] = channel;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            _subscribers.TryRemove(subscriberId, out _);
            channel.Writer.TryComplete();
        }
    }

    public void PublishPolicyEvent(PeerPolicyEventDto evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        foreach (var pair in _subscribers)
        {
            pair.Value.Writer.TryWrite(evt);
        }
    }

    internal string MintRevision(long nowMs) =>
        $"rev_{nowMs}_{Interlocked.Increment(ref _revisionSequence):x4}";

    internal static PeerAccessPolicyRecord CreateDefaultPolicyRecord(string controllerDeviceId, long nowMs, string? revision = null) =>
        new()
        {
            ControllerDeviceId = controllerDeviceId,
            Role = "operator",
            WorkspaceMode = "all",
            WorkspaceIdsJson = null,
            AllowNonWorkspaceSessions = 0,
            SyncAllSessions = 0,
            AllowAgentDelegation = 0,
            AllowProviderProxy = 0,
            Revision = revision ?? $"rev_{nowMs}_0001",
            UpdatedAt = nowMs
        };

    internal static PeerGrantWithPolicyDto ToGrantWithPolicyDto(PeerGrantRecord grant, PeerAccessPolicyRecord policy) =>
        new()
        {
            GrantId = grant.GrantId,
            ControllerDeviceId = grant.ControllerDeviceId,
            DisplayName = grant.DisplayName,
            Transport = grant.Transport,
            CertificateFingerprint = grant.CertificateFingerprint,
            GrantedAt = grant.GrantedAt,
            LastControlledAt = grant.LastControlledAt,
            RevokedAt = grant.RevokedAt,
            Policy = ToPolicyDto(policy)
        };

    internal static PeerAccessPolicyDto ToPolicyDto(PeerAccessPolicyRecord record)
    {
        IReadOnlyList<string> workspaceIds = [];
        if (record.WorkspaceMode == "selected" && !string.IsNullOrWhiteSpace(record.WorkspaceIdsJson))
        {
            try
            {
                workspaceIds = JsonSerializer.Deserialize<string[]>(record.WorkspaceIdsJson, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                workspaceIds = [];
            }
        }

        return new PeerAccessPolicyDto
        {
            ControllerDeviceId = record.ControllerDeviceId,
            Role = record.Role,
            WorkspaceMode = record.WorkspaceMode,
            WorkspaceIds = workspaceIds,
            AllowNonWorkspaceSessions = record.AllowNonWorkspaceSessions != 0,
            SyncAllSessions = record.SyncAllSessions != 0,
            AllowAgentDelegation = record.AllowAgentDelegation != 0,
            AllowProviderProxy = record.AllowProviderProxy != 0,
            Revision = record.Revision,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static string? NormalizeRole(string? role)
    {
        if (role is null) return null;
        var normalized = role.Trim().ToLowerInvariant();
        if (!PeerConstants.Roles.Contains(normalized))
        {
            throw new ArgumentException("role must be 'observer', 'operator', or 'owner'.", nameof(role));
        }
        return normalized;
    }

    private static string? NormalizeWorkspaceMode(string? mode)
    {
        if (mode is null) return null;
        var normalized = mode.Trim().ToLowerInvariant();
        if (!PeerConstants.WorkspaceModes.Contains(normalized))
        {
            throw new ArgumentException("workspace_mode must be 'all' or 'selected'.", nameof(mode));
        }
        return normalized;
    }

    private static string[] NormalizeWorkspaceIds(string? workspaceMode, IReadOnlyList<string>? workspaceIds)
    {
        if (workspaceMode == "all" || workspaceIds is null)
        {
            return [];
        }

        return workspaceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
