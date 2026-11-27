using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

/// <summary>
/// Default HTTP loopback transport client talking to local <c>TinadecGateway</c>
/// (<c>TINADEC_GATEWAY_URL</c>, default <c>http://127.0.0.1:48730</c>).
/// </summary>
public sealed class HttpPeerTransportLoopbackClient : IPeerTransportLoopbackClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration? _configuration;
    private readonly bool _ownsClient;

    public HttpPeerTransportLoopbackClient(IConfiguration? configuration = null, HttpClient? httpClient = null)
    {
        _configuration = configuration;
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                UseProxy = false
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(2)
            };
            _ownsClient = true;
        }
    }

    public Task<PeerProxyHttpResponse> SendProxyAsync(
        string deviceId,
        HttpMethod method,
        string remoteApiPath,
        string? jsonBody = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("device_id is required.", nameof(deviceId));
        }

        var normalizedRemote = string.IsNullOrWhiteSpace(remoteApiPath)
            ? "/api/v1"
            : remoteApiPath.StartsWith('/') ? remoteApiPath : $"/{remoteApiPath}";
        var proxyPath = $"/api/v1/peers/{Uri.EscapeDataString(deviceId.Trim())}/proxy{normalizedRemote}";
        return SendCoreAsync(method, proxyPath, jsonBody, extraHeaders, internalPrincipal: false, cancellationToken);
    }

    public Task<PeerProxyHttpResponse> SendGatewayLocalAsync(
        HttpMethod method,
        string gatewayPath,
        string? jsonBody = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(gatewayPath)
            ? "/"
            : gatewayPath.StartsWith('/') ? gatewayPath : $"/{gatewayPath}";
        var isInternal = normalized.StartsWith("/api/v1/peers/internal/", StringComparison.OrdinalIgnoreCase);
        return SendCoreAsync(method, normalized, jsonBody, extraHeaders, internalPrincipal: isInternal, cancellationToken);
    }

    private async Task<PeerProxyHttpResponse> SendCoreAsync(
        HttpMethod method,
        string relativePathAndQuery,
        string? jsonBody,
        IReadOnlyDictionary<string, string>? extraHeaders,
        bool internalPrincipal,
        CancellationToken cancellationToken)
    {
        var baseUrl = PeerDelegationConstants.ResolveGatewayBaseUrl(_configuration?["TinadecGateway:BaseUrl"]);
        var targetUri = new Uri($"{baseUrl}{relativePathAndQuery}", UriKind.Absolute);

        using var req = new HttpRequestMessage(method, targetUri);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString("N"));
        req.Headers.TryAddWithoutValidation(
            "x-tinadec-principal",
            internalPrincipal ? "gateway-internal" : "local-user");

        var hostToken = (_configuration?["TinadecHostControl:Token"]
            ?? Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN"))?.Trim();
        if (!string.IsNullOrWhiteSpace(hostToken))
        {
            req.Headers.TryAddWithoutValidation("x-tinadec-host-control", hostToken);
        }

        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                if (!string.IsNullOrWhiteSpace(key) && value is not null)
                {
                    req.Headers.Remove(key);
                    req.Headers.TryAddWithoutValidation(key, value);
                }
            }
        }

        if (jsonBody is not null && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            return new PeerProxyHttpResponse((int)resp.StatusCode, body, contentType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PeerException(504, PeerErrorCodes.Timeout, $"Gateway loopback request to '{relativePathAndQuery}' timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new PeerException(
                502,
                PeerErrorCodes.Unreachable,
                $"Local TinadecGateway at '{baseUrl}' could not be reached: {ex.Message}",
                ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}

/// <summary>
/// Wave 3 cross-device Multi-Agent delegation coordinator:
/// - Discovers online delegation-authorized peer execution nodes (<c>peer_list_nodes</c>)
/// - Enforces dual-ended <c>MAX_DELEGATION_HOP_DEPTH = 2</c> loop prevention
/// - Mints/attaches optional short-lived single-session <c>proxy_session_token</c>
/// - Persists <c>peer_delegation_links</c> and projects remote <c>awaiting_approval</c> &amp; <c>runtime_badge: "peer:&lt;deviceName&gt;"</c>
/// </summary>
public sealed class PeerDelegationService : IPeerDelegationCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly IPeerIdentityService _identityService;
    private readonly IPeerPairingService _pairingService;
    private readonly IPeerPolicyService _policyService;
    private readonly IPeerTransportLoopbackClient _transportClient;
    private readonly IServiceProvider _services;
    private readonly IConfiguration? _configuration;
    private readonly TimeProvider _timeProvider;

    public PeerDelegationService(
        IDbContextFactory<PeerDbContext> dbFactory,
        IPeerIdentityService identityService,
        IPeerPairingService pairingService,
        IPeerPolicyService policyService,
        IPeerTransportLoopbackClient transportClient,
        IServiceProvider services,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _identityService = identityService;
        _pairingService = pairingService;
        _policyService = policyService;
        _transportClient = transportClient;
        _services = services;
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int ResolveCurrentHopDepth(Guid? sessionId, Guid? runId, string? sessionOrigin = null) =>
        PeerInboundDelegationRuntimeRegistry.ResolveHopDepth(sessionId, runId, sessionOrigin);

    public async Task<PeerNodeListResultDto> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        var pairedDevices = await _pairingService.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (pairedDevices.Count == 0)
        {
            return new PeerNodeListResultDto { Nodes = [] };
        }

        var liveStates = await TryFetchGatewayLiveDeviceStatesAsync(cancellationToken).ConfigureAwait(false);
        var nodes = new List<PeerExecutionNodeDto>();

        foreach (var dev in pairedDevices)
        {
            var effectiveState = liveStates.TryGetValue(dev.DeviceId, out var liveState) && !string.IsNullOrWhiteSpace(liveState)
                ? liveState
                : dev.LastState;
            if (!string.Equals(effectiveState, "ready", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var infoResp = await _transportClient.SendProxyAsync(
                    dev.DeviceId,
                    HttpMethod.Get,
                    "/api/v1/peers/delegate/node-info",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (infoResp.StatusCode == 403)
                {
                    // Policy explicitly denies agent delegation on this peer; omit from candidate nodes.
                    continue;
                }

                if (infoResp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(infoResp.Body))
                {
                    var nodeInfo = JsonSerializer.Deserialize<PeerWorkerNodeInfoDto>(infoResp.Body, JsonOptions);
                    if (nodeInfo is not null)
                    {
                        var delegationAllowed = string.Equals(nodeInfo.Role, "owner", StringComparison.OrdinalIgnoreCase)
                            || nodeInfo.AllowAgentDelegation;
                        if (!delegationAllowed)
                        {
                            continue;
                        }

                        var workspaces = nodeInfo.Workspaces.Count > 0
                            ? nodeInfo.Workspaces
                            : await LoadMountedFallbackWorkspacesAsync(dev.DeviceId, cancellationToken).ConfigureAwait(false);

                        nodes.Add(new PeerExecutionNodeDto
                        {
                            DeviceId = dev.DeviceId,
                            RemoteDeviceId = string.IsNullOrWhiteSpace(nodeInfo.LocalDeviceId) ? dev.RemoteDeviceId : nodeInfo.LocalDeviceId,
                            DisplayName = string.IsNullOrWhiteSpace(dev.DisplayName) ? nodeInfo.DisplayName : dev.DisplayName,
                            Transport = dev.Transport,
                            Address = dev.Address,
                            State = "ready",
                            Platform = NormalizePlatform(nodeInfo.Platform),
                            Role = nodeInfo.Role,
                            AllowAgentDelegation = delegationAllowed,
                            AllowProviderProxy = nodeInfo.AllowProviderProxy,
                            Workspaces = workspaces
                        });
                    }
                }
            }
            catch (PeerException)
            {
                // Unreachable or timed-out peer is not currently ready for delegation.
            }
        }

        return new PeerNodeListResultDto { Nodes = nodes };
    }

    public async Task<PeerDelegationLinkDto> DelegateTaskAsync(
        PeerDelegateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ParentRunId == Guid.Empty)
            throw new PeerException(400, "invalid_request", "parent_run_id is required for peer_delegate_task.");
        if (command.ParentSessionId == Guid.Empty)
            throw new PeerException(400, "invalid_request", "parent_session_id is required for peer_delegate_task.");

        var remoteDeviceLookup = command.RemoteDeviceId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(remoteDeviceLookup))
            throw new PeerException(400, "invalid_request", "remote_device_id is required.");

        var remoteProjectId = command.RemoteProjectId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(remoteProjectId))
            throw new PeerException(400, "invalid_request", "remote_project_id is required.");

        var goal = command.Goal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(goal))
            throw new PeerException(400, "invalid_request", "goal is required.");

        // 1. Local anti-loop hard ceiling (MAX_DELEGATION_HOP_DEPTH = 2) BEFORE any network call!
        var inheritedHop = ResolveCurrentHopDepth(command.ParentSessionId, command.ParentRunId);
        var currentHop = Math.Max(command.CurrentHopDepth ?? 0, inheritedHop);
        var nextHop = currentHop + 1;
        if (nextHop < 1 || nextHop > PeerDelegationConstants.MaxDelegationHopDepth)
        {
            throw new PeerException(
                403,
                PeerErrorCodes.DelegationHopLimitExceeded,
                $"Cross-device delegation hop_depth={nextHop} exceeds MAX_DELEGATION_HOP_DEPTH ({PeerDelegationConstants.MaxDelegationHopDepth}).");
        }

        var localIdentity = await _identityService.EnsureLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        var existingChain = command.HopChain
            ?? PeerInboundDelegationRuntimeRegistry.ResolveHopChain(command.ParentSessionId, command.ParentRunId);
        var hopChain = existingChain
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Append(localIdentity.LocalDeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 2. Resolve paired peer device (by local device_id or remote_device_id) and verify online 'ready' state.
        var device = await ResolvePairedDeviceAsync(remoteDeviceLookup, cancellationToken).ConfigureAwait(false)
            ?? throw new PeerException(404, "not_found", $"Paired peer device '{remoteDeviceLookup}' was not found.");

        if (hopChain.Contains(device.RemoteDeviceId, StringComparer.OrdinalIgnoreCase))
        {
            throw new PeerException(
                403,
                PeerErrorCodes.DelegationHopLimitExceeded,
                $"Circular cross-device delegation loop detected: target device '{device.DisplayName}' ({device.RemoteDeviceId}) is already in hop_chain.");
        }

        var effectiveState = device.LastState;
        if (!string.Equals(effectiveState, "ready", StringComparison.OrdinalIgnoreCase))
        {
            var liveStates = await TryFetchGatewayLiveDeviceStatesAsync(cancellationToken).ConfigureAwait(false);
            if (liveStates.TryGetValue(device.DeviceId, out var liveState) && !string.IsNullOrWhiteSpace(liveState))
            {
                effectiveState = liveState;
            }
        }

        if (!string.Equals(effectiveState, "ready", StringComparison.OrdinalIgnoreCase))
        {
            throw new PeerException(
                503,
                PeerErrorCodes.DeviceOffline,
                $"Peer device '{device.DisplayName}' ({device.DeviceId}) is '{effectiveState}', not 'ready'.");
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var delegationId = Guid.NewGuid().ToString("N");
        var writeScope = NormalizeWriteScope(command.WriteScope);
        var writeScopeJson = writeScope.Count > 0 ? JsonSerializer.Serialize(writeScope, JsonOptions) : null;
        var teammateHandle = NormalizeTeammateHandle(command.TeammateHandle, device.DisplayName, delegationId);

        // 3. Optional short-lived single-session provider proxy token (ZERO API keys ever leave controller!)
        string? proxySessionToken = null;
        if (command.UseProviderProxy)
        {
            proxySessionToken = await IssueProviderProxySessionTokenAsync(
                localIdentity.LocalDeviceId,
                device.RemoteDeviceId,
                delegationId,
                nowMs,
                command.ModelHint,
                cancellationToken).ConfigureAwait(false);
        }

        var inboundReq = new PeerInboundDelegateRequestDto
        {
            DelegationId = delegationId,
            ParentRunId = command.ParentRunId.ToString(),
            ParentSessionId = command.ParentSessionId.ToString(),
            ProjectId = remoteProjectId,
            RemoteProjectId = remoteProjectId,
            TeammateHandle = teammateHandle,
            Goal = goal,
            BranchOrCommit = string.IsNullOrWhiteSpace(command.BranchOrCommit) ? null : command.BranchOrCommit.Trim(),
            WriteScope = writeScope,
            HopDepth = nextHop,
            HopChain = hopChain,
            UseProviderProxy = command.UseProviderProxy,
            ProxySessionToken = proxySessionToken,
            CallbackMode = string.IsNullOrWhiteSpace(command.CallbackMode) ? "poll" : command.CallbackMode.Trim(),
            PermissionMode = string.IsNullOrWhiteSpace(command.PermissionMode) ? "default" : command.PermissionMode.Trim(),
            ModelHint = command.ModelHint
        };

        var payloadJson = JsonSerializer.Serialize(inboundReq, JsonOptions);
        var proxyResp = await _transportClient.SendProxyAsync(
            device.DeviceId,
            HttpMethod.Post,
            "/api/v1/peers/delegate",
            payloadJson,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-tinadec-client-request-id"] = $"delegate-{delegationId}"
            },
            cancellationToken).ConfigureAwait(false);

        if (!proxyResp.IsSuccessStatusCode)
        {
            ThrowFromProxyFailure(proxyResp, $"Peer delegation to '{device.DisplayName}' failed.");
        }

        var accepted = JsonSerializer.Deserialize<PeerInboundDelegateResponseDto>(proxyResp.Body, JsonOptions)
            ?? throw new PeerException(502, PeerErrorCodes.Unreachable, "Worker returned an empty delegation response.");

        if (string.IsNullOrWhiteSpace(accepted.RemoteSessionId))
        {
            throw new PeerException(502, PeerErrorCodes.Unreachable, "Worker delegation response did not include remote_session_id.");
        }

        var initialStatus = NormalizeDelegationStatus(accepted.Status);
        var record = new PeerDelegationLinkRecord
        {
            DelegationId = delegationId,
            ParentRunId = command.ParentRunId.ToString(),
            ParentSessionId = command.ParentSessionId.ToString(),
            RemoteDeviceId = device.DeviceId,
            RemoteSessionId = accepted.RemoteSessionId.Trim(),
            RemoteRunId = string.IsNullOrWhiteSpace(accepted.RemoteRunId) ? null : accepted.RemoteRunId.Trim(),
            TeammateHandle = teammateHandle,
            Goal = goal,
            WriteScopeJson = writeScopeJson,
            HopDepth = nextHop,
            ProviderProxyEnabled = command.UseProviderProxy ? 1 : 0,
            Status = initialStatus,
            SummaryText = $"Delegated to peer:{device.DisplayName} (hop {nextHop}).",
            CreatedAt = nowMs,
            UpdatedAt = nowMs
        };

        await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.DelegationLinks.Add(record);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return ToLinkDto(record, device.DisplayName);
    }

    public async Task<PeerDelegationTaskStatusDto> ReadTaskAsync(
        string delegationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delegationId))
            throw new PeerException(400, "invalid_request", "delegation_id is required.");

        var normalizedId = delegationId.Trim();
        var (record, deviceName) = await LoadLinkWithDeviceNameAsync(normalizedId, cancellationToken).ConfigureAwait(false);

        if (IsTerminalDelegationStatus(record.Status))
        {
            return ToTaskStatusDto(record, deviceName, [], []);
        }

        IReadOnlyList<PeerRemoteApprovalCardDto> pendingApprovals = [];
        IReadOnlyList<PeerRemoteEventDigestDto> recentEvents = [];

        try
        {
            var query = string.IsNullOrWhiteSpace(record.RemoteRunId)
                ? string.Empty
                : $"?run_id={Uri.EscapeDataString(record.RemoteRunId)}";
            var path = $"/api/v1/peers/delegations/{Uri.EscapeDataString(record.RemoteSessionId)}/status{query}";
            var resp = await _transportClient.SendProxyAsync(
                record.RemoteDeviceId,
                HttpMethod.Get,
                path,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(resp.Body))
            {
                var remote = JsonSerializer.Deserialize<PeerWorkerSessionStatusDto>(resp.Body, JsonOptions);
                if (remote is not null)
                {
                    var normalizedRemoteStatus = NormalizeDelegationStatus(remote.RawRunStatus ?? remote.Status);
                    var remoteTerminal = IsTerminalDelegationStatus(normalizedRemoteStatus);
                    pendingApprovals = remoteTerminal ? [] : (remote.PendingApprovals ?? []);
                    recentEvents = remote.RecentEvents ?? [];
                    var mappedStatus = remoteTerminal
                        ? normalizedRemoteStatus
                        : (pendingApprovals.Count > 0 || remote.AwaitingApproval
                            ? "awaiting_approval"
                            : NormalizeDelegationStatus(remote.Status));
                    var nextRunId = !string.IsNullOrWhiteSpace(remote.RemoteRunId) ? remote.RemoteRunId.Trim() : record.RemoteRunId;
                    var nextSummary = !string.IsNullOrWhiteSpace(remote.SummaryText) ? remote.SummaryText.Trim() : record.SummaryText;

                    if (!string.Equals(record.Status, mappedStatus, StringComparison.Ordinal)
                        || !string.Equals(record.RemoteRunId, nextRunId, StringComparison.Ordinal)
                        || !string.Equals(record.SummaryText, nextSummary, StringComparison.Ordinal))
                    {
                        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                        record = await UpdateLinkStateAsync(
                            record.DelegationId,
                            mappedStatus,
                            nextRunId,
                            nextSummary,
                            nowMs,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (PeerException)
        {
            // Preserve last durable link state when peer is transiently unreachable during status read.
        }

        return ToTaskStatusDto(record, deviceName, pendingApprovals, recentEvents);
    }

    public async Task<PeerWaitTasksResultDto> WaitTasksAsync(
        PeerWaitTasksCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var requestedIds = (command.DelegationIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requestedIds.Length == 0 && command.ParentRunId is { } parentRunId && parentRunId != Guid.Empty)
        {
            requestedIds = await SqliteTransientRetry.ReadAsync(async ct =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                return await db.DelegationLinks.AsNoTracking()
                    .Where(x => x.ParentRunId == parentRunId.ToString())
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => x.DelegationId)
                    .ToArrayAsync(ct)
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        if (requestedIds.Length == 0)
        {
            return new PeerWaitTasksResultDto
            {
                Completed = true,
                TimedOut = false,
                AwaitingApproval = false,
                WokenReason = "no_tasks",
                Tasks = []
            };
        }

        var timeoutSeconds = Math.Clamp(command.TimeoutSeconds <= 0 ? 30 : command.TimeoutSeconds, 1, 600);
        var pollIntervalMs = Math.Clamp(command.PollIntervalMs <= 0 ? 150 : command.PollIntervalMs, 25, 2000);
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = new List<PeerDelegationTaskStatusDto>(requestedIds.Length);
            foreach (var id in requestedIds)
            {
                snapshots.Add(await ReadTaskAsync(id, cancellationToken).ConfigureAwait(false));
            }

            if (snapshots.Any(s => s.AwaitingApproval || string.Equals(s.Status, "awaiting_approval", StringComparison.OrdinalIgnoreCase)))
            {
                return new PeerWaitTasksResultDto
                {
                    Completed = false,
                    TimedOut = false,
                    AwaitingApproval = true,
                    WokenReason = "awaiting_approval",
                    Tasks = snapshots
                };
            }

            if (snapshots.All(s => IsTerminalDelegationStatus(s.Status)))
            {
                return new PeerWaitTasksResultDto
                {
                    Completed = true,
                    TimedOut = false,
                    AwaitingApproval = false,
                    WokenReason = "completed",
                    Tasks = snapshots
                };
            }

            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new PeerWaitTasksResultDto
                {
                    Completed = false,
                    TimedOut = true,
                    AwaitingApproval = false,
                    WokenReason = "timeout",
                    Tasks = snapshots
                };
            }

            var delay = remaining < TimeSpan.FromMilliseconds(pollIntervalMs)
                ? remaining
                : TimeSpan.FromMilliseconds(pollIntervalMs);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<PeerDelegationTaskStatusDto> CancelTaskAsync(
        string delegationId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delegationId))
            throw new PeerException(400, "invalid_request", "delegation_id is required.");

        var normalizedId = delegationId.Trim();
        var (record, deviceName) = await LoadLinkWithDeviceNameAsync(normalizedId, cancellationToken).ConfigureAwait(false);

        if (!IsTerminalDelegationStatus(record.Status))
        {
            var cancelBody = JsonSerializer.Serialize(new
            {
                run_id = record.RemoteRunId,
                reason = string.IsNullOrWhiteSpace(reason) ? "Cancelled by controller via peer_cancel_task." : reason.Trim()
            }, JsonOptions);

            try
            {
                var cancelResp = await _transportClient.SendProxyAsync(
                    record.RemoteDeviceId,
                    HttpMethod.Post,
                    $"/api/v1/peers/delegations/{Uri.EscapeDataString(record.RemoteSessionId)}/cancel",
                    cancelBody,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (cancelResp.StatusCode == 403)
                {
                    ThrowFromProxyFailure(cancelResp, "Remote peer denied cancellation.");
                }
            }
            catch (PeerException ex) when (ex.StatusCode is 502 or 503 or 504)
            {
                // Local link still transitions to cancelled even if worker already disconnected.
            }
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var summary = string.IsNullOrWhiteSpace(reason)
            ? (record.SummaryText ?? "Cancelled by controller.")
            : reason.Trim();
        var updated = await UpdateLinkStateAsync(
            record.DelegationId,
            "cancelled",
            record.RemoteRunId,
            summary,
            nowMs,
            cancellationToken).ConfigureAwait(false);

        return ToTaskStatusDto(updated, deviceName, [], []);
    }

    public async Task<PeerRemoteApprovalDecisionResultDto> DecideRemoteApprovalAsync(
        string delegationId,
        Guid approvalId,
        bool approve,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delegationId))
            throw new PeerException(400, "invalid_request", "delegation_id is required.");
        if (approvalId == Guid.Empty)
            throw new PeerException(400, "invalid_request", "approval_id must be a valid non-empty Guid.");

        var (record, _) = await LoadLinkWithDeviceNameAsync(delegationId.Trim(), cancellationToken).ConfigureAwait(false);
        var decisionBody = JsonSerializer.Serialize(new PeerRemoteApprovalDecisionRequestDto
        {
            Approve = approve,
            Reason = reason ?? (approve ? "Approved by peer controller." : "Rejected by peer controller.")
        }, JsonOptions);

        var path = $"/api/v1/peers/delegations/{Uri.EscapeDataString(record.RemoteSessionId)}/approvals/{approvalId}/decision";
        var resp = await _transportClient.SendProxyAsync(
            record.RemoteDeviceId,
            HttpMethod.Post,
            path,
            decisionBody,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            ThrowFromProxyFailure(resp, "Remote approval decision failed.");
        }

        string? decidedById = null;
        string? decidedByName = null;
        string decisionStatus = approve ? "approved" : "denied";

        if (!string.IsNullOrWhiteSpace(resp.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(resp.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("decision", out var decEl) && decEl.ValueKind == JsonValueKind.Object)
                {
                    if (decEl.TryGetProperty("decided_by_device_id", out var dbId) && dbId.ValueKind == JsonValueKind.String)
                        decidedById = dbId.GetString();
                    if (decEl.TryGetProperty("decided_by_device_name", out var dbName) && dbName.ValueKind == JsonValueKind.String)
                        decidedByName = dbName.GetString();
                    if (decEl.TryGetProperty("outcome", out var outcomeEl) && outcomeEl.ValueKind == JsonValueKind.String)
                        decisionStatus = outcomeEl.GetString() ?? decisionStatus;
                }
                else
                {
                    if (root.TryGetProperty("decided_by_device_id", out var dbId) && dbId.ValueKind == JsonValueKind.String)
                        decidedById = dbId.GetString();
                    if (root.TryGetProperty("decided_by_device_name", out var dbName) && dbName.ValueKind == JsonValueKind.String)
                        decidedByName = dbName.GetString();
                }
            }
            catch (JsonException) { }
        }

        var refreshed = await ReadTaskAsync(record.DelegationId, cancellationToken).ConfigureAwait(false);
        return new PeerRemoteApprovalDecisionResultDto
        {
            DelegationId = record.DelegationId,
            ApprovalId = approvalId.ToString(),
            Approved = approve,
            Status = refreshed.Status.Length > 0 ? refreshed.Status : decisionStatus,
            DecidedByDeviceId = decidedById,
            DecidedByDeviceName = decidedByName,
            RemoteSessionId = record.RemoteSessionId,
            RemoteRunId = refreshed.RemoteRunId ?? record.RemoteRunId
        };
    }

    public async Task<IReadOnlyList<PeerDelegationLinkDto>> ListRunDelegationsAsync(
        Guid parentRunId,
        bool refreshActive = false,
        CancellationToken cancellationToken = default)
    {
        if (parentRunId == Guid.Empty) return [];

        var rows = await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var links = await db.DelegationLinks.AsNoTracking()
                .Where(x => x.ParentRunId == parentRunId.ToString())
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (links.Count == 0) return (Links: links, DeviceNames: new Dictionary<string, string>(StringComparer.Ordinal));

            var deviceIds = links.Select(x => x.RemoteDeviceId).Distinct(StringComparer.Ordinal).ToArray();
            var names = await db.Devices.AsNoTracking()
                .Where(x => deviceIds.Contains(x.DeviceId))
                .ToDictionaryAsync(x => x.DeviceId, x => x.DisplayName, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);
            return (Links: links, DeviceNames: names);
        }, cancellationToken).ConfigureAwait(false);

        if (rows.Links.Count == 0) return [];

        if (!refreshActive)
        {
            return rows.Links
                .Select(link => ToLinkDto(link, rows.DeviceNames.GetValueOrDefault(link.RemoteDeviceId, link.RemoteDeviceId)))
                .ToArray();
        }

        var refreshed = new List<PeerDelegationLinkDto>(rows.Links.Count);
        foreach (var link in rows.Links)
        {
            if (!IsTerminalDelegationStatus(link.Status))
            {
                try
                {
                    var status = await ReadTaskAsync(link.DelegationId, cancellationToken).ConfigureAwait(false);
                    refreshed.Add(new PeerDelegationLinkDto
                    {
                        DelegationId = status.DelegationId,
                        ParentRunId = status.ParentRunId,
                        ParentSessionId = status.ParentSessionId,
                        RemoteDeviceId = status.RemoteDeviceId,
                        RemoteDeviceName = status.RemoteDeviceName,
                        RemoteSessionId = status.RemoteSessionId,
                        RemoteRunId = status.RemoteRunId,
                        TeammateHandle = status.TeammateHandle,
                        RuntimeBadge = status.RuntimeBadge,
                        Goal = status.Goal,
                        WriteScope = status.WriteScope,
                        HopDepth = status.HopDepth,
                        ProviderProxyEnabled = status.ProviderProxyEnabled,
                        Status = status.Status,
                        AwaitingApproval = status.AwaitingApproval,
                        SummaryText = status.SummaryText,
                        CreatedAt = link.CreatedAt,
                        UpdatedAt = status.UpdatedAt
                    });
                    continue;
                }
                catch
                {
                    // Fall back to stored link row on transient failure.
                }
            }

            refreshed.Add(ToLinkDto(link, rows.DeviceNames.GetValueOrDefault(link.RemoteDeviceId, link.RemoteDeviceId)));
        }

        return refreshed;
    }

    /// <summary>
    /// Authoritative worker-side inbound policy + hop-depth validation for <c>/api/v1/peers/delegate*</c>.
    /// Enforces both <c>allow_agent_delegation</c> policy and <c>MAX_DELEGATION_HOP_DEPTH = 2</c>.
    /// </summary>
    public async Task<(PeerLocalIdentitySnapshot LocalIdentity, PeerAccessPolicyDto Policy)> AuthorizeInboundDelegationAsync(
        string? controllerDeviceId,
        string? targetProjectId,
        int? hopDepth = null,
        IReadOnlyList<string>? hopChain = null,
        bool requireProviderProxy = false,
        CancellationToken cancellationToken = default)
    {
        var localIdentity = await _identityService.EnsureLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (!localIdentity.InboundEnabled)
        {
            throw new PeerException(403, PeerErrorCodes.InboundDisabled, "Inbound peer connections are disabled on this device.");
        }

        var normalizedController = controllerDeviceId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedController)
            || string.Equals(normalizedController, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw new PeerException(403, PeerErrorCodes.PolicyDenied, "Inbound peer delegation requires an authenticated remote peer controller identity.");
        }

        var grants = await _policyService.ListGrantsAsync(cancellationToken).ConfigureAwait(false);
        var activeGrant = grants.FirstOrDefault(g =>
            g.RevokedAt is null
            && string.Equals(g.ControllerDeviceId, normalizedController, StringComparison.Ordinal));
        if (activeGrant is null)
        {
            throw new PeerException(403, PeerErrorCodes.PolicyDenied, $"No active peer grant exists for controller '{normalizedController}'.");
        }

        var policy = activeGrant.Policy;
        var delegationAllowed = string.Equals(policy.Role, "owner", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(policy.Role, "operator", StringComparison.OrdinalIgnoreCase) && policy.AllowAgentDelegation);
        if (!delegationAllowed)
        {
            throw new PeerException(
                403,
                PeerErrorCodes.PolicyDenied,
                $"Agent task delegation is disabled by peer access policy for controller '{normalizedController}' (role='{policy.Role}', allow_agent_delegation={policy.AllowAgentDelegation.ToString().ToLowerInvariant()}).");
        }

        if (!string.IsNullOrWhiteSpace(targetProjectId)
            && string.Equals(policy.WorkspaceMode, "selected", StringComparison.OrdinalIgnoreCase)
            && !policy.WorkspaceIds.Contains(targetProjectId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new PeerException(
                403,
                PeerErrorCodes.PolicyDenied,
                $"Workspace '{targetProjectId.Trim()}' is not authorized by selected workspace whitelist.");
        }

        if (requireProviderProxy
            && !string.Equals(policy.Role, "owner", StringComparison.OrdinalIgnoreCase)
            && !policy.AllowProviderProxy)
        {
            throw new PeerException(
                403,
                PeerErrorCodes.PolicyDenied,
                $"Provider proxy is disabled by worker peer access policy for controller '{normalizedController}'.");
        }

        if (hopDepth is { } depth)
        {
            if (depth < 1 || depth > PeerDelegationConstants.MaxDelegationHopDepth)
            {
                throw new PeerException(
                    403,
                    PeerErrorCodes.DelegationHopLimitExceeded,
                    $"Inbound delegation hop_depth={depth} exceeds MAX_DELEGATION_HOP_DEPTH ({PeerDelegationConstants.MaxDelegationHopDepth}).");
            }
        }

        if (hopChain is { Count: > 0 } chain
            && chain.Any(id => string.Equals(id?.Trim(), localIdentity.LocalDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PeerException(
                403,
                PeerErrorCodes.DelegationHopLimitExceeded,
                $"Inbound delegation hop_chain contains local device '{localIdentity.LocalDeviceId}' (delegation cycle rejected).");
        }

        return (localIdentity, policy);
    }

    private async Task<string> IssueProviderProxySessionTokenAsync(
        string controllerDeviceId,
        string workerDeviceId,
        string delegationId,
        long nowMs,
        string? modelHint,
        CancellationToken cancellationToken)
    {
        var expiresAtMs = nowMs + (30 * 60 * 1000L);
        const int maxRequests = 128;

        if (_services.GetService<IPeerProviderProxyTokenIssuer>() is { } issuer)
        {
            var issued = await issuer.IssueTokenAsync(new PeerProviderProxyTokenIssueRequestDto
            {
                ControllerDeviceId = controllerDeviceId,
                WorkerDeviceId = workerDeviceId,
                DelegationId = delegationId,
                ExpiresAtMs = expiresAtMs,
                MaxRequests = maxRequests,
                ModelHint = modelHint
            }, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(issued.ProxySessionToken))
            {
                return issued.ProxySessionToken;
            }
        }

        try
        {
            var reqJson = JsonSerializer.Serialize(new
            {
                controller_device_id = controllerDeviceId,
                worker_device_id = workerDeviceId,
                delegation_id = delegationId,
                expires_at_ms = expiresAtMs,
                max_requests = maxRequests,
                model_hint = modelHint
            }, JsonOptions);

            var gwResp = await _transportClient.SendGatewayLocalAsync(
                HttpMethod.Post,
                "/api/v1/peers/internal/provider-proxy/tokens",
                reqJson,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (gwResp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(gwResp.Body))
            {
                using var doc = JsonDocument.Parse(gwResp.Body);
                if (doc.RootElement.TryGetProperty("proxy_session_token", out var tokEl) && tokEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(tokEl.GetString()))
                {
                    return tokEl.GetString()!;
                }
                if (doc.RootElement.TryGetProperty("token", out var altEl) && altEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(altEl.GetString()))
                {
                    return altEl.GetString()!;
                }
            }
        }
        catch (PeerException)
        {
            // Fall through to ephemeral opaque token when Gateway token endpoint is absent in isolated Core tests.
        }

        return $"ptk_{delegationId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
    }

    private async Task<PeerDeviceDto?> ResolvePairedDeviceAsync(string idOrRemoteId, CancellationToken ct)
    {
        var direct = await _pairingService.GetDeviceAsync(idOrRemoteId, ct).ConfigureAwait(false);
        if (direct is not null) return direct;

        var all = await _pairingService.ListDevicesAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(d =>
            string.Equals(d.DeviceId, idOrRemoteId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.RemoteDeviceId, idOrRemoteId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Dictionary<string, string>> TryFetchGatewayLiveDeviceStatesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var resp = await _transportClient.SendGatewayLocalAsync(HttpMethod.Get, "/api/v1/peers/devices", cancellationToken: ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(resp.Body)) return map;

            using var doc = JsonDocument.Parse(resp.Body);
            if (!doc.RootElement.TryGetProperty("devices", out var devicesEl) || devicesEl.ValueKind != JsonValueKind.Array)
                return map;

            foreach (var item in devicesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("device_id", out var idEl) ? idEl.GetString() : null;
                var state = item.TryGetProperty("last_state", out var stEl) ? stEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(state))
                {
                    map[id!] = state!;
                }
            }
        }
        catch
        {
            // Best-effort enrichment only.
        }

        return map;
    }

    private async Task<IReadOnlyList<PeerNodeWorkspaceDto>> LoadMountedFallbackWorkspacesAsync(
        string deviceId,
        CancellationToken ct)
    {
        try
        {
            var mounted = await _pairingService.ListMountedWorkspacesAsync(deviceId, ct).ConfigureAwait(false);
            return mounted
                .Select(w => new PeerNodeWorkspaceDto
                {
                    RemoteProjectId = w.RemoteProjectId,
                    Name = w.Name,
                    RemoteRootPath = w.RemoteRootPath
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task<(PeerDelegationLinkRecord Record, string DeviceName)> LoadLinkWithDeviceNameAsync(
        string delegationId,
        CancellationToken ct)
    {
        return await SqliteTransientRetry.ReadAsync(async innerCt =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(innerCt).ConfigureAwait(false);
            var row = await db.DelegationLinks.AsNoTracking()
                .SingleOrDefaultAsync(x => x.DelegationId == delegationId, innerCt)
                .ConfigureAwait(false)
                ?? throw new PeerException(404, "not_found", $"Peer delegation '{delegationId}' was not found.");

            var deviceName = await db.Devices.AsNoTracking()
                .Where(x => x.DeviceId == row.RemoteDeviceId)
                .Select(x => x.DisplayName)
                .FirstOrDefaultAsync(innerCt)
                .ConfigureAwait(false);

            return (row, string.IsNullOrWhiteSpace(deviceName) ? row.RemoteDeviceId : deviceName!);
        }, ct).ConfigureAwait(false);
    }

    private async Task<PeerDelegationLinkRecord> UpdateLinkStateAsync(
        string delegationId,
        string status,
        string? remoteRunId,
        string? summaryText,
        long nowMs,
        CancellationToken ct)
    {
        var normalizedStatus = NormalizeDelegationStatus(status);
        return await SqliteTransientRetry.WriteInTransactionAsync(async innerCt =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(innerCt).ConfigureAwait(false);
            var row = await db.DelegationLinks
                .SingleOrDefaultAsync(x => x.DelegationId == delegationId, innerCt)
                .ConfigureAwait(false)
                ?? throw new PeerException(404, "not_found", $"Peer delegation '{delegationId}' was not found.");

            row.Status = normalizedStatus;
            if (!string.IsNullOrWhiteSpace(remoteRunId))
            {
                row.RemoteRunId = remoteRunId;
            }
            if (summaryText is not null)
            {
                row.SummaryText = summaryText;
            }
            row.UpdatedAt = nowMs;
            await db.SaveChangesAsync(innerCt).ConfigureAwait(false);
            return row;
        }, ct).ConfigureAwait(false);
    }

    public static string NormalizeDelegationStatus(string? rawStatus)
    {
        var lower = rawStatus?.Trim().ToLowerInvariant();
        return lower switch
        {
            "queued" => "queued",
            "awaiting_approval" or "awaiting_delegate" or "awaiting_user" => "awaiting_approval",
            "completed" => "completed",
            "failed" => "failed",
            "cancelled" or "canceled" => "cancelled",
            _ => "running"
        };
    }

    internal static bool IsTerminalDelegationStatus(string? status) =>
        status is "completed" or "failed" or "cancelled";

    private static string NormalizePlatform(string? platform)
    {
        var lower = platform?.Trim().ToLowerInvariant();
        return lower switch
        {
            "darwin" or "macos" or "osx" => "darwin",
            "win32" or "windows" => "win32",
            "linux" => "linux",
            _ => PeerDelegationConstants.CurrentPlatformId
        };
    }

    private static IReadOnlyList<string> NormalizeWriteScope(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return [];
        return raw
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ParseWriteScopeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeTeammateHandle(string? requested, string deviceDisplayName, string delegationId)
    {
        var candidate = string.IsNullOrWhiteSpace(requested)
            ? $"peer-{deviceDisplayName}-{delegationId[..6]}"
            : requested.Trim().TrimStart('@');

        var sb = new StringBuilder(candidate.Length);
        foreach (var ch in candidate)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is ' ' or '.' or ':')
            {
                if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
        }

        var cleaned = sb.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "coordinator" or "all")
        {
            cleaned = $"peer-{delegationId[..8]}";
        }

        return cleaned.Length <= 48 ? cleaned : cleaned[..48];
    }

    private static PeerDelegationLinkDto ToLinkDto(PeerDelegationLinkRecord record, string deviceName)
    {
        var label = string.IsNullOrWhiteSpace(deviceName) ? record.RemoteDeviceId : deviceName;
        return new PeerDelegationLinkDto
        {
            DelegationId = record.DelegationId,
            ParentRunId = record.ParentRunId,
            ParentSessionId = record.ParentSessionId,
            RemoteDeviceId = record.RemoteDeviceId,
            RemoteDeviceName = label,
            RemoteSessionId = record.RemoteSessionId,
            RemoteRunId = record.RemoteRunId,
            TeammateHandle = record.TeammateHandle,
            RuntimeBadge = $"peer:{label}",
            Goal = record.Goal,
            WriteScope = ParseWriteScopeJson(record.WriteScopeJson),
            HopDepth = record.HopDepth,
            ProviderProxyEnabled = record.ProviderProxyEnabled != 0,
            Status = record.Status,
            AwaitingApproval = string.Equals(record.Status, "awaiting_approval", StringComparison.OrdinalIgnoreCase),
            SummaryText = record.SummaryText,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static PeerDelegationTaskStatusDto ToTaskStatusDto(
        PeerDelegationLinkRecord record,
        string deviceName,
        IReadOnlyList<PeerRemoteApprovalCardDto> pendingApprovals,
        IReadOnlyList<PeerRemoteEventDigestDto> recentEvents)
    {
        var label = string.IsNullOrWhiteSpace(deviceName) ? record.RemoteDeviceId : deviceName;
        var awaiting = string.Equals(record.Status, "awaiting_approval", StringComparison.OrdinalIgnoreCase)
            || pendingApprovals.Count > 0;
        return new PeerDelegationTaskStatusDto
        {
            DelegationId = record.DelegationId,
            ParentRunId = record.ParentRunId,
            ParentSessionId = record.ParentSessionId,
            RemoteDeviceId = record.RemoteDeviceId,
            RemoteDeviceName = label,
            RemoteSessionId = record.RemoteSessionId,
            RemoteRunId = record.RemoteRunId,
            TeammateHandle = record.TeammateHandle,
            RuntimeBadge = $"peer:{label}",
            Goal = record.Goal,
            WriteScope = ParseWriteScopeJson(record.WriteScopeJson),
            HopDepth = record.HopDepth,
            ProviderProxyEnabled = record.ProviderProxyEnabled != 0,
            Status = record.Status,
            AwaitingApproval = awaiting,
            SummaryText = record.SummaryText,
            PendingApprovals = pendingApprovals,
            RecentEvents = recentEvents,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static void ThrowFromProxyFailure(PeerProxyHttpResponse resp, string fallbackMessage)
    {
        var code = resp.StatusCode switch
        {
            401 => PeerErrorCodes.Unauthorized,
            403 => PeerErrorCodes.PolicyDenied,
            503 => PeerErrorCodes.DeviceOffline,
            504 => PeerErrorCodes.Timeout,
            _ => PeerErrorCodes.Unreachable
        };
        var detail = fallbackMessage;

        if (!string.IsNullOrWhiteSpace(resp.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(resp.Body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(codeEl.GetString()))
                    {
                        code = codeEl.GetString()!;
                    }
                    if (root.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(detailEl.GetString()))
                    {
                        detail = detailEl.GetString()!;
                    }
                    else if (root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(msgEl.GetString()))
                    {
                        detail = msgEl.GetString()!;
                    }
                }
            }
            catch (JsonException)
            {
                // Keep HTTP status mapped fallback when body is not JSON.
            }
        }

        throw new PeerException(resp.StatusCode, code, detail);
    }
}
