using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Peers;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Tenancy;
using TinadecCore.Tools;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Local peer management plane (<c>/api/v1/peers/*</c>) and Gateway loopback control
/// plane (<c>/api/v1/peers/internal/*</c>).
///
/// Security invariants:
/// 1. Every <c>/api/v1/peers/*</c> route is strictly loopback-only and unconditionally rejects
///    any remote peer request (<c>x-tinadec-peer-controller-id</c> != "local") with
///    <c>403 policy_denied</c> so a controller can never escalate privileges on a worker.
/// 2. Every <c>/api/v1/peers/internal/*</c> route additionally requires
///    <c>x-tinadec-principal: gateway-internal</c>.
/// </summary>
public static class PeerDeviceEndpoints
{
    public const string PeerControllerIdHeader = "x-tinadec-peer-controller-id";
    public const string PeerControllerNameHeader = "x-tinadec-peer-controller-name";
    public const string PeerRoleHeader = "x-tinadec-peer-role";
    public const string PrincipalHeader = "x-tinadec-principal";
    public const string GatewayInternalPrincipal = "gateway-internal";

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static IEndpointRouteBuilder MapPeerDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        // --- A. Local Management Plane (/api/v1/peers/*) ---
        var mgmt = app.MapGroup("/api/v1/peers")
            .AddEndpointFilter(EnforceLocalManagementOnlyAsync);

        mgmt.MapGet("/inbound", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var identity = scope.Services.GetRequiredService<IPeerIdentityService>();
            return Results.Ok(await identity.GetInboundSettingsAsync(ct).ConfigureAwait(false));
        }).Produces<PeerInboundSettingsDto>();

        mgmt.MapPut("/inbound", async (PeerUpdateInboundRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Inbound settings body is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var identity = scope.Services.GetRequiredService<IPeerIdentityService>();
            return Results.Ok(await identity.UpdateInboundSettingsAsync(input, ct).ConfigureAwait(false));
        }).Produces<PeerInboundSettingsDto>();

        mgmt.MapPost("/pairing-code", async (HttpRequest req, HttpContext ctx, CancellationToken ct) =>
        {
            PeerGeneratePairingCodeRequestDto? input = null;
            if (req.ContentLength is > 0 || req.Headers.ContainsKey("Transfer-Encoding"))
            {
                try
                {
                    input = await req.ReadFromJsonAsync<PeerGeneratePairingCodeRequestDto>(ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Request body must be valid JSON.");
                }
            }
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            return Results.Ok(await pairing.GeneratePairingCodeAsync(input, ct).ConfigureAwait(false));
        }).Produces<PeerPairingCodeStatusDto>();

        mgmt.MapGet("/pairing-code", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            return Results.Ok(await pairing.GetPairingCodeStatusAsync(ct).ConfigureAwait(false));
        }).Produces<PeerPairingCodeStatusDto>();

        mgmt.MapDelete("/pairing-code", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            await pairing.RevokePairingCodeAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { active = false });
        });

        mgmt.MapGet("/devices", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            var devices = await pairing.ListDevicesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { devices });
        });

        mgmt.MapPost("/devices/probe", async (PeerProbeRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            _ = ct;
            if (input is null || string.IsNullOrWhiteSpace(input.Address))
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "address is required.");
            // Network TLS probing is orchestrated by TinadecGateway; direct Core invocation reports transport unreachable.
            return Problem(
                ctx,
                StatusCodes.Status503ServiceUnavailable,
                PeerErrorCodes.Unreachable,
                $"Peer TLS probe for '{input.Address.Trim()}' requires the TinadecGateway mTLS transport layer.");
        });

        mgmt.MapPost("/devices/{deviceId}/probe", async (string deviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            var device = await pairing.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
            if (device is null)
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Peer device '{deviceId}' was not found.");
            return Problem(
                ctx,
                StatusCodes.Status503ServiceUnavailable,
                PeerErrorCodes.Unreachable,
                $"Peer TLS probe for '{device.Address}' requires the TinadecGateway mTLS transport layer.");
        });

        mgmt.MapPost("/devices/pair", async (PeerPairDeviceRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Pair request body is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            var device = await pairing.UpsertPairedDeviceAsync(input, ct).ConfigureAwait(false);
            return Results.Ok(device);
        }).Produces<PeerDeviceDto>();

        mgmt.MapPatch("/devices/{deviceId}", async (string deviceId, PeerUpdateDeviceRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Device update body is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            try
            {
                var updated = await pairing.UpdateDeviceAsync(deviceId, input, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerDeviceDto>();

        mgmt.MapDelete("/devices/{deviceId}", async (string deviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var shadow = scope.Services.GetRequiredService<IPeerShadowService>();
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            await shadow.PurgeDeviceShadowDataAsync(deviceId, ct).ConfigureAwait(false);
            var deleted = await pairing.DeleteDeviceAsync(deviceId, ct).ConfigureAwait(false);
            return deleted
                ? Results.Ok(new { deleted = true, device_id = deviceId })
                : Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Peer device '{deviceId}' was not found.");
        });

        mgmt.MapPost("/devices/{deviceId}/connect", async (string deviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            try
            {
                await pairing.UpdateDeviceAsync(deviceId, new PeerUpdateDeviceRequestDto { ConnectionDesired = true }, ct).ConfigureAwait(false);
                var current = await pairing.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
                if (current is null)
                    return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Peer device '{deviceId}' was not found.");
                if (current.LastState == "disconnected")
                {
                    current = await pairing.UpdateControllerStateAsync(new PeerControllerStateUpdateDto
                    {
                        DeviceId = deviceId,
                        LastState = "connecting"
                    }, ct).ConfigureAwait(false);
                }
                return Results.Ok(current);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerDeviceDto>();

        mgmt.MapPost("/devices/{deviceId}/disconnect", async (string deviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            try
            {
                await pairing.UpdateDeviceAsync(deviceId, new PeerUpdateDeviceRequestDto { ConnectionDesired = false }, ct).ConfigureAwait(false);
                var updated = await pairing.UpdateControllerStateAsync(new PeerControllerStateUpdateDto
                {
                    DeviceId = deviceId,
                    LastState = "disconnected"
                }, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerDeviceDto>();

        mgmt.MapGet("/devices/{deviceId}/workspaces", async (string deviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            try
            {
                var workspaces = await pairing.ListMountedWorkspacesAsync(deviceId, ct).ConfigureAwait(false);
                return Results.Ok(new { workspaces });
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        });

        mgmt.MapPut("/devices/{deviceId}/workspaces/mount", async (string deviceId, PeerMountWorkspacesRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Workspace mount payload is required.");
            var items = input.Mounts ?? input.Workspaces ?? [];
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            try
            {
                var workspaces = await pairing.ReplaceMountedWorkspacesAsync(deviceId, items, ct).ConfigureAwait(false);
                return Results.Ok(new { workspaces });
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        });

        mgmt.MapPost("/devices/{deviceId}/shadow/sync", async (string deviceId, PeerShadowSyncRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Shadow sync payload is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var shadow = scope.Services.GetRequiredService<IPeerShadowService>();
            try
            {
                var synced = await shadow.SyncShadowAsync(deviceId, input, ct).ConfigureAwait(false);
                return Results.Ok(synced);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerShadowSyncResultDto>();

        mgmt.MapGet("/devices/{deviceId}/shadow/sessions", async (string deviceId, string? project_id, string? remote_project_id, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var shadow = scope.Services.GetRequiredService<IPeerShadowService>();
            try
            {
                var filterProject = !string.IsNullOrWhiteSpace(project_id) ? project_id : remote_project_id;
                var result = await shadow.ListShadowSessionsAsync(deviceId, filterProject, ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerShadowSessionListDto>();

        mgmt.MapGet("/devices/{deviceId}/shadow/sessions/{remoteSessionId}", async (string deviceId, string remoteSessionId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var shadow = scope.Services.GetRequiredService<IPeerShadowService>();
            try
            {
                var detail = await shadow.GetShadowSessionAsync(deviceId, remoteSessionId, ct).ConfigureAwait(false);
                return detail is not null
                    ? Results.Ok(detail)
                    : Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Shadow session '{remoteSessionId}' was not found for peer device '{deviceId}'.");
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerShadowSessionDetailDto>();

        mgmt.MapDelete("/devices/{deviceId}/local-cache", async (string deviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var shadow = scope.Services.GetRequiredService<IPeerShadowService>();
            try
            {
                var cleared = await shadow.ClearDeviceLocalCacheAsync(deviceId, ct).ConfigureAwait(false);
                return Results.Ok(new { cleared = true, device_id = deviceId, removed_entries = cleared });
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        });

        mgmt.MapGet("/grants", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var policy = scope.Services.GetRequiredService<IPeerPolicyService>();
            var grants = await policy.ListGrantsAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { grants });
        });

        mgmt.MapPut("/grants/{controllerDeviceId}/policy", async (string controllerDeviceId, PeerUpdateAccessPolicyRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Access policy body is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var policy = scope.Services.GetRequiredService<IPeerPolicyService>();
            try
            {
                var updated = await policy.UpdateGrantPolicyAsync(controllerDeviceId, input, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerGrantWithPolicyDto>();

        mgmt.MapDelete("/grants/{controllerDeviceId}", async (string controllerDeviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var policy = scope.Services.GetRequiredService<IPeerPolicyService>();
            var revoked = await policy.RevokeGrantAsync(controllerDeviceId, ct).ConfigureAwait(false);
            return revoked
                ? Results.Ok(new { revoked = true, controller_device_id = controllerDeviceId })
                : Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Active peer grant for '{controllerDeviceId}' was not found.");
        });

        mgmt.MapGet("/nodes", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var delegation = scope.Services.GetRequiredService<IPeerDelegationCoordinator>();
            return Results.Ok(await delegation.ListNodesAsync(ct).ConfigureAwait(false));
        }).Produces<PeerNodeListResultDto>();

        mgmt.MapGet("/delegations", async (string? run_id, bool? refresh, HttpContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(run_id) || !Guid.TryParse(run_id, out var runGuid))
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Valid query parameter 'run_id' (Guid) is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var delegation = scope.Services.GetRequiredService<IPeerDelegationCoordinator>();
            var items = await delegation.ListRunDelegationsAsync(runGuid, refresh == true, ct).ConfigureAwait(false);
            return Results.Ok(new { run_id = runGuid, delegations = items });
        });

        mgmt.MapPost("/delegations", async (PeerLocalDelegateHttpRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null
                || !Guid.TryParse(input.ParentRunId, out var parentRunGuid)
                || !Guid.TryParse(input.ParentSessionId, out var parentSessionGuid))
            {
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "parent_run_id and parent_session_id (Guid) are required.");
            }
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var delegation = scope.Services.GetRequiredService<IPeerDelegationCoordinator>();
            var created = await delegation.DelegateTaskAsync(new PeerDelegateTaskCommand
            {
                ParentRunId = parentRunGuid,
                ParentSessionId = parentSessionGuid,
                RemoteDeviceId = input.RemoteDeviceId ?? string.Empty,
                RemoteProjectId = input.RemoteProjectId ?? string.Empty,
                Goal = input.Goal ?? string.Empty,
                BranchOrCommit = input.BranchOrCommit,
                WriteScope = input.WriteScope,
                UseProviderProxy = input.UseProviderProxy ?? false,
                CallbackMode = input.CallbackMode,
                TeammateHandle = input.TeammateHandle,
                CurrentHopDepth = input.CurrentHopDepth,
                HopChain = input.HopChain,
                PermissionMode = input.PermissionMode,
                ModelHint = input.ModelHint
            }, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/peers/delegations/{created.DelegationId}/status", created);
        }).Produces<PeerDelegationLinkDto>(StatusCodes.Status201Created);

        // --- B. Gateway Internal Loopback Contract (/api/v1/peers/internal/*) ---
        var internalGroup = app.MapGroup("/api/v1/peers/internal")
            .AddEndpointFilter(EnforceGatewayInternalLoopbackAsync);

        internalGroup.MapGet("/local-tls-material", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var identity = scope.Services.GetRequiredService<IPeerIdentityService>();
            return Results.Ok(await identity.GetLocalTlsMaterialAsync(ct).ConfigureAwait(false));
        }).Produces<PeerLocalTlsMaterialDto>();

        internalGroup.MapPost("/handshake/verify", async (PeerHandshakeVerifyRequestDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, PeerErrorCodes.HandshakeFailed, "Handshake verification body is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            var verified = await pairing.VerifyHandshakeAsync(input, ct).ConfigureAwait(false);
            return Results.Ok(verified);
        }).Produces<PeerHandshakeVerifyResponseDto>();

        internalGroup.MapGet("/policy-snapshot", async (HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var identity = scope.Services.GetRequiredService<IPeerIdentityService>();
            var policy = scope.Services.GetRequiredService<IPeerPolicyService>();
            var local = await identity.EnsureLocalIdentityAsync(ct).ConfigureAwait(false);
            var snapshot = await policy.GetPolicySnapshotAsync(ct).ConfigureAwait(false);
            return Results.Ok(new PeerPolicySnapshotDto
            {
                InboundEnabled = local.InboundEnabled,
                ListenPort = local.ListenPort,
                LocalDeviceId = local.LocalDeviceId,
                DisplayName = local.DisplayName,
                LocalFingerprint = local.LocalFingerprint,
                Grants = snapshot.Grants,
                Policies = snapshot.Policies
            });
        }).Produces<PeerPolicySnapshotDto>();

        internalGroup.MapGet("/policy-events", async (HttpContext ctx, IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.RequestAborted, lifetime.ApplicationStopping);
            var streamToken = linkedCts.Token;

            await using var scope = await AcquireUserPeerScopeAsync(ctx, streamToken).ConfigureAwait(false);
            var policy = scope.Services.GetRequiredService<IPeerPolicyService>();

            ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache, no-store";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await ctx.Response.StartAsync(streamToken).ConfigureAwait(false);
            await ctx.Response.WriteAsync(": connected\n\n", streamToken).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(streamToken).ConfigureAwait(false);

            try
            {
                await foreach (var evt in policy.SubscribePolicyEventsAsync(streamToken).ConfigureAwait(false))
                {
                    var payload = JsonSerializer.Serialize(evt, SseJsonOptions);
                    await ctx.Response.WriteAsync($"data: {payload}\n\n", streamToken).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(streamToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
            {
                // Clean SSE client disconnect or host shutdown.
            }
        });

        internalGroup.MapPost("/controller-state", async (PeerControllerStateUpdateDto? input, HttpContext ctx, CancellationToken ct) =>
        {
            if (input is null)
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Controller state update body is required.");
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            try
            {
                var updated = await pairing.UpdateControllerStateAsync(input, ct).ConfigureAwait(false);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", ex.Message);
            }
        }).Produces<PeerDeviceDto>();

        // --- C. Wave 3 Cross-Device Delegation & Bidirectional Approval Aggregation Plane ---
        app.MapGet("/api/v1/peers/delegate/node-info", HandleGetWorkerNodeInfoAsync)
            .Produces<PeerWorkerNodeInfoDto>();

        app.MapPost("/api/v1/peers/delegate", HandleInboundDelegateAsync)
            .Produces<PeerInboundDelegateResponseDto>(StatusCodes.Status201Created);

        app.MapGet("/api/v1/peers/delegations/{id}/status", HandleGetDelegationStatusAsync);

        app.MapPost("/api/v1/peers/delegations/{id}/cancel", HandleCancelDelegationAsync);

        app.MapPost("/api/v1/peers/delegations/{id}/approvals/{approvalId:guid}/decision", HandleDecideDelegationApprovalAsync);

        return app;
    }

    public sealed class PeerLocalDelegateHttpRequestDto
    {
        public string? ParentRunId { get; init; }
        public string? ParentSessionId { get; init; }
        public string? RemoteDeviceId { get; init; }
        public string? RemoteProjectId { get; init; }
        public string? Goal { get; init; }
        public string? BranchOrCommit { get; init; }
        public IReadOnlyList<string>? WriteScope { get; init; }
        public bool? UseProviderProxy { get; init; }
        public string? CallbackMode { get; init; }
        public string? TeammateHandle { get; init; }
        public int? CurrentHopDepth { get; init; }
        public IReadOnlyList<string>? HopChain { get; init; }
        public string? PermissionMode { get; init; }
        public string? ModelHint { get; init; }
    }

    public sealed class PeerCancelDelegationBodyDto
    {
        public string? RunId { get; init; }
        public string? Reason { get; init; }
    }

    private static async Task<Guid?> ResolveWorkerDelegationModeVersionIdAsync(
        IDbContextFactory<AgentConfigurationDbContext> cfgFactory,
        GlobalAgentConfiguration globalConfig,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken ct)
    {
        static async Task<Guid?> TryFindPublishedModeBySlugAsync(
            IDbContextFactory<AgentConfigurationDbContext> factory,
            Guid tId,
            Guid wId,
            string slug,
            CancellationToken token)
        {
            await using var cfg = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
            var modeIds = await cfg.AgentModes.AsNoTracking()
                .Where(m => m.TenantId == tId && m.WorkspaceId == wId && m.Slug == slug && m.Status == "published")
                .Select(m => m.Id)
                .ToListAsync(token).ConfigureAwait(false);
            if (modeIds.Count == 0) return null;
            var versions = await cfg.ModeVersions.AsNoTracking()
                .Where(v => v.TenantId == tId && v.WorkspaceId == wId && v.Status == "published" && modeIds.Contains(v.AgentModeId))
                .ToListAsync(token).ConfigureAwait(false);
            return versions.OrderByDescending(v => v.Version).FirstOrDefault()?.Id;
        }

        // Prefer 'solo' for delegated worker sessions so the worker root agent holds direct file/shell execution
        // tools (subject to permission_mode & write_scope) while still retaining team/peer delegation capability.
        var soloLocal = await TryFindPublishedModeBySlugAsync(cfgFactory, tenantId, workspaceId, "solo", ct).ConfigureAwait(false);
        if (soloLocal is not null) return soloLocal;

        if (!ReferenceEquals(cfgFactory, globalConfig.Factory))
        {
            var soloGlobal = await TryFindPublishedModeBySlugAsync(globalConfig.Factory, tenantId, workspaceId, "solo", ct).ConfigureAwait(false);
            if (soloGlobal is not null) return soloGlobal;
        }

        return await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(cfgFactory, globalConfig, tenantId, workspaceId, ct).ConfigureAwait(false);
    }

    private static bool IsRemotePeerControllerRequest(HttpContext ctx, out string controllerId, out string controllerName)
    {
        var rawId = ctx.Request.Headers[PeerControllerIdHeader].FirstOrDefault()?.Trim();
        var rawName = ctx.Request.Headers[PeerControllerNameHeader].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(rawId) && !string.Equals(rawId, "local", StringComparison.OrdinalIgnoreCase))
        {
            controllerId = rawId;
            controllerName = !string.IsNullOrWhiteSpace(rawName) ? rawName : rawId;
            return true;
        }

        controllerId = "local";
        controllerName = !string.IsNullOrWhiteSpace(rawName) ? rawName : "local";
        return false;
    }

    private static async Task<IResult> HandleGetWorkerNodeInfoAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Peer delegation endpoints only accept loopback/gateway requests.");

        if (!IsRemotePeerControllerRequest(ctx, out var controllerId, out _))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Worker node-info requires an authenticated remote peer controller header.");

        await using var userScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
        var delegationService = userScope.Services.GetRequiredService<PeerDelegationService>();
        var (localIdentity, policy) = await delegationService
            .AuthorizeInboundDelegationAsync(controllerId, targetProjectId: null, cancellationToken: ct)
            .ConfigureAwait(false);

        var workspaces = await ListWorkerAuthorizedWorkspacesAsync(ctx, policy, ct).ConfigureAwait(false);
        return Results.Ok(new PeerWorkerNodeInfoDto
        {
            LocalDeviceId = localIdentity.LocalDeviceId,
            DisplayName = localIdentity.DisplayName,
            Platform = PeerDelegationConstants.CurrentPlatformId,
            Role = policy.Role,
            AllowAgentDelegation = string.Equals(policy.Role, "owner", StringComparison.OrdinalIgnoreCase) || policy.AllowAgentDelegation,
            AllowProviderProxy = string.Equals(policy.Role, "owner", StringComparison.OrdinalIgnoreCase) || policy.AllowProviderProxy,
            Workspaces = workspaces
        });
    }

    private static async Task<IResult> HandleInboundDelegateAsync(
        PeerInboundDelegateRequestDto? input,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Peer delegation endpoints only accept loopback/gateway requests.");

        if (!IsRemotePeerControllerRequest(ctx, out var controllerId, out var controllerName))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Inbound peer delegation requires an authenticated remote peer controller identity.");

        if (IsObserverPeerRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Observer peer role cannot delegate tasks.");

        if (input is null)
            return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Delegation request body is required.");

        var targetProjectRaw = !string.IsNullOrWhiteSpace(input.RemoteProjectId) ? input.RemoteProjectId!.Trim() : input.ProjectId?.Trim();
        if (string.IsNullOrWhiteSpace(targetProjectRaw) || !Guid.TryParse(targetProjectRaw, out var targetProjectId))
            return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Valid 'remote_project_id' (Guid) is required.");

        var goal = input.Goal?.Trim();
        if (string.IsNullOrWhiteSpace(goal))
            return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Non-empty 'goal' is required.");

        await using var userScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
        var delegationService = userScope.Services.GetRequiredService<PeerDelegationService>();
        var (localIdentity, _) = await delegationService.AuthorizeInboundDelegationAsync(
            controllerId,
            targetProjectId.ToString(),
            hopDepth: input.HopDepth,
            hopChain: input.HopChain,
            requireProviderProxy: input.UseProviderProxy,
            cancellationToken: ct).ConfigureAwait(false);

        await using var projectScope = await AcquireScopeForProjectAsync(ctx, targetProjectId, ct).ConfigureAwait(false);
        if (projectScope is null)
            return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Workspace project '{targetProjectId}' was not found on worker '{localIdentity.DisplayName}'.");

        var sp = projectScope.Value.Services;
        var store = sp.GetRequiredService<ProjectSessionStore>();
        var cfgFactory = sp.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var globalConfig = sp.GetRequiredService<GlobalAgentConfiguration>();
        var tenant = sp.GetRequiredService<ITenantContextAccessor>();
        var coordinator = sp.GetRequiredService<IFullDuplexRunCoordinator>();
        var ingressQueue = sp.GetService<IPeerIngressQueueService>();

        var modeVersionId = await ResolveWorkerDelegationModeVersionIdAsync(
            cfgFactory, globalConfig, tenant.Current.TenantId, tenant.Current.WorkspaceId, ct).ConfigureAwait(false);
        if (modeVersionId is null)
            return Problem(ctx, StatusCodes.Status409Conflict, "agent_mode_not_configured", "No published Agent Mode is configured on the worker workspace.");

        var modeFactory = await AgentConfigurationLookup.SelectModeFactoryAsync(
            cfgFactory, globalConfig, tenant.Current.TenantId, tenant.Current.WorkspaceId, modeVersionId.Value, ct).ConfigureAwait(false);
        ConversationIdentityResolver.ConversationIdentity? identity;
        await using (var cfg = await modeFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var modeVersion = await cfg.ModeVersions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == modeVersionId.Value
                    && x.TenantId == tenant.Current.TenantId
                    && x.WorkspaceId == tenant.Current.WorkspaceId
                    && x.Status == "published", ct).ConfigureAwait(false);
            if (modeVersion is null)
                return Problem(ctx, StatusCodes.Status409Conflict, "agent_mode_not_configured", "Default Agent Mode version is unavailable.");

            var definitions = await cfg.AgentDefinitions.AsNoTracking()
                .Where(x => x.TenantId == tenant.Current.TenantId && x.WorkspaceId == tenant.Current.WorkspaceId)
                .Select(x => new ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson))
                .ToListAsync(ct).ConfigureAwait(false);
            identity = ConversationIdentityResolver.Resolve(modeVersion.SnapshotJson, definitions);
            if (identity is null)
                return Problem(ctx, StatusCodes.Status409Conflict, "agent_mode_not_configured", "Default Agent Mode does not declare a conversation node.");
        }

        var delegationId = string.IsNullOrWhiteSpace(input.DelegationId) ? Guid.NewGuid().ToString("N") : input.DelegationId.Trim();
        var permissionMode = string.IsNullOrWhiteSpace(input.PermissionMode) ? "default" : input.PermissionMode.Trim().ToLowerInvariant();
        var titleGoal = goal.Length <= 80 ? goal : goal[..80] + "…";
        var session = await store.CreateSessionAsync(
            targetProjectId,
            $"[Peer:{controllerName}] {titleGoal}",
            modeVersionId.Value,
            meetingModelOverride: null,
            identity.NodeKey,
            identity.TemplateSlug,
            ct,
            viewMode: "flat",
            permissionMode: permissionMode,
            origin: PeerDelegationConstants.FormatSessionOrigin(input.HopDepth)).ConfigureAwait(false);

        var hopChain = (input.HopChain ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Append(controllerId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        PeerInboundDelegationRuntimeRegistry.Register(new PeerInboundDelegationContext(
            SessionId: session.Id.ToString(),
            RunId: null,
            DelegationId: delegationId,
            ControllerDeviceId: controllerId,
            ControllerDisplayName: controllerName,
            HopDepth: input.HopDepth,
            HopChain: hopChain));

        var effectiveProxyToken = !string.IsNullOrWhiteSpace(input.ProxySessionToken)
            ? input.ProxySessionToken.Trim()
            : ctx.Request.Headers["x-tinadec-peer-proxy-token"].FirstOrDefault()?.Trim();
        var effectiveGatewayLoopbackUrl = !string.IsNullOrWhiteSpace(input.GatewayLoopbackUrl)
            ? input.GatewayLoopbackUrl.Trim()
            : ctx.Request.Headers["x-tinadec-gateway-loopback-url"].FirstOrDefault()?.Trim();

        if ((input.UseProviderProxy || !string.IsNullOrWhiteSpace(effectiveProxyToken))
            && !string.IsNullOrWhiteSpace(effectiveProxyToken))
        {
            var expiresAtMs = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();
            PeerProviderProxyRuntimeRegistry.Register(new PeerProviderProxySessionBinding(
                SessionId: session.Id.ToString(),
                RunId: null,
                DelegationId: delegationId,
                ControllerDeviceId: controllerId,
                ProxySessionToken: effectiveProxyToken,
                UseProviderProxy: input.UseProviderProxy,
                ExpiresAtMs: expiresAtMs,
                GatewayLoopbackUrl: effectiveGatewayLoopbackUrl,
                ModelHint: input.ModelHint));
        }

        var promptBuilder = new System.Text.StringBuilder(goal);
        if (!string.IsNullOrWhiteSpace(input.BranchOrCommit))
        {
            promptBuilder.AppendLine().AppendLine($"[Target branch/commit: {input.BranchOrCommit.Trim()}]");
        }
        if (input.WriteScope is { Count: > 0 })
        {
            var cleanScopes = input.WriteScope.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            if (cleanScopes.Length > 0)
            {
                promptBuilder.AppendLine().AppendLine($"[Authorized write_scope: {string.Join(", ", cleanScopes)}]");
            }
        }
        var effectivePrompt = promptBuilder.ToString();
        var clientReqId = $"delegate-{delegationId}";

        string? remoteRunId = null;
        if (ingressQueue is not null)
        {
            var ingressOutcome = await ingressQueue.ExecuteSerializedAsync(
                new PeerIngressSubmitCommand(
                    SessionId: session.Id.ToString(),
                    ClientRequestId: clientReqId,
                    OriginDeviceId: controllerId,
                    OriginDisplayName: controllerName,
                    Method: "session.send",
                    PayloadJson: JsonSerializer.Serialize(new { content = effectivePrompt, client_request_id = clientReqId })),
                async (_, innerCt) =>
                {
                    var submitted = await coordinator.SubmitAsync(new FullDuplexInvocation(
                        SessionId: session.Id,
                        Content: effectivePrompt,
                        ClientMessageId: clientReqId,
                        PermissionMode: permissionMode,
                        TargetRunId: null,
                        ExpectedContextRevision: null,
                        ModeVersionId: modeVersionId.Value,
                        QueueBehindActiveRun: false), innerCt).ConfigureAwait(false);
                    return new PeerIngressWorkResult(
                        Accepted: true,
                        HttpStatusCode: StatusCodes.Status201Created,
                        RunId: submitted.RunId.ToString(),
                        TurnId: submitted.TurnId.ToString(),
                        MessageId: submitted.MessageId.ToString(),
                        LastEventSeq: 0,
                        ResponseJson: JsonSerializer.Serialize(new { run_id = submitted.RunId, session_id = session.Id }));
                },
                ct).ConfigureAwait(false);
            remoteRunId = ingressOutcome.RunId;
        }
        else
        {
            var submitted = await coordinator.SubmitAsync(new FullDuplexInvocation(
                SessionId: session.Id,
                Content: effectivePrompt,
                ClientMessageId: clientReqId,
                PermissionMode: permissionMode,
                TargetRunId: null,
                ExpectedContextRevision: null,
                ModeVersionId: modeVersionId.Value,
                QueueBehindActiveRun: false), ct).ConfigureAwait(false);
            remoteRunId = submitted.RunId.ToString();
        }

        if (!string.IsNullOrWhiteSpace(remoteRunId))
        {
            PeerInboundDelegationRuntimeRegistry.AttachRun(session.Id.ToString(), remoteRunId);
            if (!string.IsNullOrWhiteSpace(effectiveProxyToken))
            {
                PeerProviderProxyRuntimeRegistry.AttachRun(session.Id.ToString(), remoteRunId);
            }
        }

        var response = new PeerInboundDelegateResponseDto
        {
            DelegationId = delegationId,
            RemoteDeviceId = localIdentity.LocalDeviceId,
            RemoteDeviceName = localIdentity.DisplayName,
            RemoteSessionId = session.Id.ToString(),
            RemoteRunId = remoteRunId,
            RemoteStorageId = projectScope.Value.StorageId,
            TeammateHandle = string.IsNullOrWhiteSpace(input.TeammateHandle) ? $"peer-{localIdentity.DisplayName}" : input.TeammateHandle.Trim(),
            Status = "running",
            HopDepth = input.HopDepth,
            ProviderProxyEnabled = input.UseProviderProxy,
            RuntimeBadge = $"peer:{localIdentity.DisplayName}"
        };

        return Results.Created($"/api/v1/peers/delegations/{session.Id}/status", response);
    }

    private static async Task<IResult> HandleGetDelegationStatusAsync(
        string id,
        string? run_id,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Peer delegation endpoints only accept loopback/gateway requests.");

        // Remote peer controller perspective: `id` is `remoteSessionId` on this worker host.
        if (IsRemotePeerControllerRequest(ctx, out var controllerId, out _))
        {
            if (!Guid.TryParse(id, out var sessionGuid))
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Remote session id must be a valid Guid.");

            await using var userScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var delegationService = userScope.Services.GetRequiredService<PeerDelegationService>();
            var (_, policy) = await delegationService
                .AuthorizeInboundDelegationAsync(controllerId, targetProjectId: null, cancellationToken: ct)
                .ConfigureAwait(false);

            await using var sessionScope = await AcquireScopeForSessionAsync(ctx, sessionGuid, ct).ConfigureAwait(false);
            if (sessionScope is null)
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Delegated session '{sessionGuid}' was not found.");

            var sp = sessionScope.Value.Services;
            var sessions = sp.GetRequiredService<ProjectSessionStore>();
            var sessionRecord = await sessions.FindAsync(sessionGuid, ct).ConfigureAwait(false);
            if (sessionRecord is null)
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Delegated session '{sessionGuid}' was not found.");

            if (sessionRecord.ProjectId is { } projId
                && string.Equals(policy.WorkspaceMode, "selected", StringComparison.OrdinalIgnoreCase)
                && !policy.WorkspaceIds.Contains(projId.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, $"Workspace '{projId}' is not authorized by selected workspace whitelist.");
            }

            var storageLifecycle = sp.GetRequiredService<StorageLifecycleService>();
            var lifecycleManager = sp.GetRequiredService<ILifecycleManager>();
            var conversations = sp.GetRequiredService<IConversationStore>();
            var authorization = sp.GetService<IAuthorizationService>();

            Guid? resolvedRunId = Guid.TryParse(run_id, out var explicitRun) ? explicitRun : null;
            if (resolvedRunId is null)
            {
                var runs = await storageLifecycle.ListRunsAsync(sessionGuid, ct).ConfigureAwait(false);
                resolvedRunId = runs.FirstOrDefault()?.Id;
            }

            if (resolvedRunId is not { } runGuid)
            {
                return Results.Ok(new PeerWorkerSessionStatusDto
                {
                    RemoteSessionId = sessionGuid.ToString(),
                    Status = "queued",
                    RawRunStatus = "queued",
                    AwaitingApproval = false
                });
            }

            RunState runState;
            try
            {
                runState = await lifecycleManager.GetRunStateAsync(runGuid.ToString(), ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Run '{runGuid}' was not found.");
            }

            var isTerminalRun = runState.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
            var pendingApprovals = new List<PeerRemoteApprovalCardDto>();
            var seenApprovalIds = new HashSet<Guid>();
            if (!isTerminalRun && authorization is not null)
            {
                var allReqs = await authorization.ListPermissionRequestsAsync(null, runGuid, null, ct).ConfigureAwait(false);
                foreach (var req in allReqs)
                {
                    if ((req.Status is PermissionRequestStatuses.AwaitingDelegate
                        or PermissionRequestStatuses.AwaitingUser)
                        && seenApprovalIds.Add(req.Id))
                    {
                        pendingApprovals.Add(new PeerRemoteApprovalCardDto
                        {
                            ApprovalId = req.Id.ToString(),
                            RunId = req.RunId?.ToString(),
                            TaskId = req.TaskId?.ToString(),
                            Capability = req.Claim.Capability,
                            Action = req.Claim.Action,
                            Resource = req.Claim.Resource,
                            Risk = req.Risk,
                            Status = req.Status,
                            DecidedByDeviceId = req.DecidedByDeviceId,
                            DecidedByDeviceName = req.DecidedByDeviceName,
                            CreatedAt = req.CreatedAt,
                            ExpiresAt = req.ExpiresAt
                        });
                    }
                }
            }

            if (!isTerminalRun && sp.GetService<IDbContextFactory<LifecycleDbContext>>() is { } lifecycleDbFactory)
            {
                await using var ldb = await lifecycleDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var toolApprovals = await ldb.ApprovalRequests.AsNoTracking()
                    .Where(x => x.RunId == runGuid && x.Status == "pending")
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                if (toolApprovals.Count > 0)
                {
                    var execIds = toolApprovals
                        .Where(x => x.ExecutionId.HasValue)
                        .Select(x => x.ExecutionId!.Value)
                        .Distinct()
                        .ToArray();
                    var settledExecIds = execIds.Length == 0
                        ? new HashSet<Guid>()
                        : (await ldb.ToolExecutions.AsNoTracking()
                            .Where(e => execIds.Contains(e.Id)
                                && (e.Status == "approved" || e.Status == "executing" || e.Status == "completed"
                                    || e.Status == "failed" || e.Status == "timed_out" || e.Status == "cancelled"))
                            .Select(e => e.Id)
                            .ToListAsync(ct)
                            .ConfigureAwait(false)).ToHashSet();

                    toolApprovals.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                    foreach (var row in toolApprovals)
                    {
                        if (row.ExecutionId is { } eid && settledExecIds.Contains(eid)) continue;
                        if (!seenApprovalIds.Add(row.Id)) continue;
                        pendingApprovals.Add(new PeerRemoteApprovalCardDto
                        {
                            ApprovalId = row.Id.ToString(),
                            RunId = row.RunId?.ToString(),
                            Capability = "tool",
                            Action = "invoke",
                            Resource = row.ToolId,
                            Risk = row.Risk,
                            Status = "awaiting_approval",
                            CreatedAt = row.CreatedAt
                        });
                    }
                }
            }

            var rawStatus = RunStatusToWire(runState.Status);
            var awaitingApproval = !isTerminalRun
                && (pendingApprovals.Count > 0
                    || runState.Status is RunStatus.AwaitingApproval or RunStatus.AwaitingDelegate or RunStatus.AwaitingUser);
            var mappedStatus = awaitingApproval
                ? "awaiting_approval"
                : PeerDelegationService.NormalizeDelegationStatus(rawStatus);

            var events = await storageLifecycle.ReplayRunEventsAsync(sessionGuid, runGuid, ct).ConfigureAwait(false);
            var recentEvents = events
                .Where(e => !e.EventType.StartsWith("model.output.delta", StringComparison.OrdinalIgnoreCase))
                .TakeLast(12)
                .Select((e, idx) => new PeerRemoteEventDigestDto
                {
                    Seq = ExtractEventSequence(e) ?? idx + 1,
                    EventType = e.EventType,
                    Summary = ExtractEventSummary(e),
                    Timestamp = e.Timestamp
                })
                .ToArray();

            string? summaryText = null;
            if (runState.Status == RunStatus.Completed)
            {
                var msgs = await conversations.ListMessagesAsync(sessionGuid, cancellationToken: ct).ConfigureAwait(false);
                var lastAssistant = msgs.LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
                summaryText = !string.IsNullOrWhiteSpace(lastAssistant?.Content) ? lastAssistant!.Content : runState.Summary;
            }
            else if (awaitingApproval && pendingApprovals.Count > 0)
            {
                var first = pendingApprovals[0];
                summaryText = $"Awaiting approval ({first.ApprovalId}): {first.Capability}:{first.Action} on '{first.Resource}' (risk={first.Risk}).";
            }
            else
            {
                summaryText = !string.IsNullOrWhiteSpace(runState.Summary)
                    ? runState.Summary
                    : recentEvents.LastOrDefault()?.Summary;
            }

            if (summaryText is { Length: > 2400 })
            {
                summaryText = summaryText[..2400] + "…";
            }

            return Results.Ok(new PeerWorkerSessionStatusDto
            {
                RemoteSessionId = sessionGuid.ToString(),
                RemoteRunId = runGuid.ToString(),
                Status = mappedStatus,
                RawRunStatus = rawStatus,
                AwaitingApproval = awaitingApproval,
                SummaryText = summaryText,
                PendingApprovals = pendingApprovals,
                RecentEvents = recentEvents
            });
        }

        // Local controller perspective: `id` is `delegation_id` in local `peer_delegation_links`.
        await using var localScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
        var coordinatorPort = localScope.Services.GetRequiredService<IPeerDelegationCoordinator>();
        return Results.Ok(await coordinatorPort.ReadTaskAsync(id, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> HandleCancelDelegationAsync(
        string id,
        HttpRequest req,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Peer delegation endpoints only accept loopback/gateway requests.");

        if (IsObserverPeerRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Observer peer role cannot cancel delegated runs.");

        PeerCancelDelegationBodyDto? body = null;
        if (req.ContentLength is > 0 || req.Headers.ContainsKey("Transfer-Encoding"))
        {
            try
            {
                body = await req.ReadFromJsonAsync<PeerCancelDelegationBodyDto>(ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Cancel body must be valid JSON.");
            }
        }

        if (IsRemotePeerControllerRequest(ctx, out var controllerId, out _))
        {
            if (!Guid.TryParse(id, out var sessionGuid))
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Remote session id must be a valid Guid.");

            await using var userScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var delegationService = userScope.Services.GetRequiredService<PeerDelegationService>();
            await delegationService
                .AuthorizeInboundDelegationAsync(controllerId, targetProjectId: null, cancellationToken: ct)
                .ConfigureAwait(false);

            await using var sessionScope = await AcquireScopeForSessionAsync(ctx, sessionGuid, ct).ConfigureAwait(false);
            if (sessionScope is null)
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Delegated session '{sessionGuid}' was not found.");

            var sp = sessionScope.Value.Services;
            var storageLifecycle = sp.GetRequiredService<StorageLifecycleService>();
            var coordinator = sp.GetRequiredService<IFullDuplexRunCoordinator>();
            var terminalControl = sp.GetService<ITerminalSessionControl>();

            Guid? targetRunGuid = !string.IsNullOrWhiteSpace(body?.RunId) && Guid.TryParse(body.RunId, out var parsedRun)
                ? parsedRun
                : (await storageLifecycle.ListRunsAsync(sessionGuid, ct).ConfigureAwait(false)).FirstOrDefault()?.Id;

            if (targetRunGuid is { } runGuid)
            {
                try
                {
                    var ctrl = await coordinator.ControlAsync(runGuid, new RunControlCommand("cancel"), ct).ConfigureAwait(false);
                    if (ctrl.Accepted && terminalControl is not null)
                    {
                        await terminalControl.KillForRunAsync(runGuid, ct).ConfigureAwait(false);
                    }
                }
                catch (RunAdmissionException)
                {
                    // Run already terminal.
                }
            }

            PeerProviderProxyRuntimeRegistry.RemoveBySession(sessionGuid.ToString());
            return Results.Ok(new
            {
                cancelled = true,
                remote_session_id = sessionGuid.ToString(),
                remote_run_id = targetRunGuid?.ToString(),
                status = "cancelled"
            });
        }

        await using var localScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
        var coordinatorPort = localScope.Services.GetRequiredService<IPeerDelegationCoordinator>();
        return Results.Ok(await coordinatorPort.CancelTaskAsync(id, body?.Reason, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> HandleDecideDelegationApprovalAsync(
        string id,
        Guid approvalId,
        PeerRemoteApprovalDecisionRequestDto? input,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Peer delegation endpoints only accept loopback/gateway requests.");

        if (IsObserverPeerRequest(ctx))
            return Problem(ctx, StatusCodes.Status403Forbidden, PeerErrorCodes.PolicyDenied, "Observer peer role is not permitted to decide permission requests.");

        if (input is null)
            return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Approval decision body is required.");

        if (IsRemotePeerControllerRequest(ctx, out var controllerId, out _))
        {
            if (!Guid.TryParse(id, out var sessionGuid))
                return Problem(ctx, StatusCodes.Status400BadRequest, "invalid_request", "Remote session id must be a valid Guid.");

            await using var userScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var delegationService = userScope.Services.GetRequiredService<PeerDelegationService>();
            await delegationService
                .AuthorizeInboundDelegationAsync(controllerId, targetProjectId: null, cancellationToken: ct)
                .ConfigureAwait(false);

            await using var sessionScope = await AcquireScopeForSessionAsync(ctx, sessionGuid, ct).ConfigureAwait(false);
            if (sessionScope is null)
                return Problem(ctx, StatusCodes.Status404NotFound, "not_found", $"Delegated session '{sessionGuid}' was not found.");

            var sp = sessionScope.Value.Services;
            var (deciderDeviceId, deciderDeviceName) = ResolveApprovalDeciderOrigin(ctx);

            await using var reqScope = sp.CreateAsyncScope();
            var controlPlane = reqScope.ServiceProvider.GetService<ControlPlaneService>();
            if (controlPlane is not null)
            {
                var cpResult = await controlPlane.DecideApproval(
                    approvalId,
                    new ApprovalDecisionRequestDto
                    {
                        Decision = input.Approve ? "approved" : "rejected",
                        Reason = input.Reason
                    },
                    ct,
                    deciderDeviceId,
                    deciderDeviceName).ConfigureAwait(false);

                if (cpResult is IStatusCodeHttpResult { StatusCode: >= 400 })
                {
                    return cpResult;
                }

                if (cpResult is IValueHttpResult { Value: ApprovalDecisionResponseDto cpDto })
                {
                    return Results.Ok(new
                    {
                        approval_id = cpDto.Id,
                        status = cpDto.Status,
                        decided_by_device_id = cpDto.DecidedByDeviceId ?? deciderDeviceId,
                        decided_by_device_name = cpDto.DecidedByDeviceName ?? deciderDeviceName,
                        decision = new
                        {
                            id = cpDto.Id,
                            outcome = cpDto.Status,
                            decided_by_device_id = cpDto.DecidedByDeviceId ?? deciderDeviceId,
                            decided_by_device_name = cpDto.DecidedByDeviceName ?? deciderDeviceName
                        }
                    });
                }

                return cpResult;
            }

            var authorization = sp.GetRequiredService<IAuthorizationService>();
            var lifecycle = sp.GetRequiredService<ILifecycleManager>();
            var engine = sp.GetRequiredService<IFullDuplexRunEngine>();

            var resolution = await authorization.DecidePermissionAsync(new PermissionDecisionCommand(
                approvalId,
                input.Approve,
                null,
                null,
                input.Reason ?? string.Empty,
                deciderDeviceId,
                deciderDeviceName), ct).ConfigureAwait(false);

            if (resolution.Request.RunId is { } runId
                && resolution.Request.Status is not (PermissionRequestStatuses.AwaitingDelegate or PermissionRequestStatuses.AwaitingUser))
            {
                var run = await lifecycle.GetRunStateAsync(runId.ToString(), ct).ConfigureAwait(false);
                if (run.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled))
                {
                    if (run.Status is RunStatus.AwaitingApproval or RunStatus.AwaitingDelegate or RunStatus.AwaitingUser)
                    {
                        await lifecycle.SetRunStatusAsync(runId.ToString(), "executing", "Peer permission decision committed; resuming run.", ct).ConfigureAwait(false);
                        await lifecycle.AppendEventAsync(runId, "governance.permission_decided", new
                        {
                            permission_request_id = resolution.Request.Id,
                            authorization_decision_id = resolution.Decision.Id,
                            outcome = resolution.Decision.Outcome,
                            reason_code = resolution.Decision.ReasonCode,
                            decided_by_device_id = resolution.Decision.DecidedByDeviceId ?? deciderDeviceId,
                            decided_by_device_name = resolution.Decision.DecidedByDeviceName ?? deciderDeviceName
                        }, "Permission decision committed by peer controller; run resumed.", cancellationToken: ct).ConfigureAwait(false);
                    }
                    await engine.EnqueueAsync(runId, ct).ConfigureAwait(false);
                }
            }

            return Results.Ok(new
            {
                approval_id = resolution.Request.Id,
                status = resolution.Request.Status,
                decision = new
                {
                    id = resolution.Decision.Id,
                    outcome = resolution.Decision.Outcome,
                    reason_code = resolution.Decision.ReasonCode,
                    decided_by_device_id = resolution.Decision.DecidedByDeviceId ?? deciderDeviceId,
                    decided_by_device_name = resolution.Decision.DecidedByDeviceName ?? deciderDeviceName
                }
            });
        }

        await using var localScope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
        var coordinatorPort = localScope.Services.GetRequiredService<IPeerDelegationCoordinator>();
        var result = await coordinatorPort.DecideRemoteApprovalAsync(id, approvalId, input.Approve, input.Reason, ct).ConfigureAwait(false);

        // Also wake the local parent run if it is parked waiting on this remote delegation!
        var links = await localScope.Services.GetRequiredService<IDbContextFactory<PeerDbContext>>().CreateDbContextAsync(ct).ConfigureAwait(false);
        string? parentSessionIdStr;
        string? parentRunIdStr;
        await using (links)
        {
            var row = await links.DelegationLinks.AsNoTracking().SingleOrDefaultAsync(x => x.DelegationId == id, ct).ConfigureAwait(false);
            parentSessionIdStr = row?.ParentSessionId;
            parentRunIdStr = row?.ParentRunId;
        }
        if (Guid.TryParse(parentSessionIdStr, out var parentSessionGuid)
            && Guid.TryParse(parentRunIdStr, out var parentRunGuid))
        {
            await using var parentScope = await AcquireScopeForSessionAsync(ctx, parentSessionGuid, ct).ConfigureAwait(false);
            if (parentScope is not null && parentScope.Value.Services.GetService<IFullDuplexRunEngine>() is { } parentEngine)
            {
                await parentEngine.EnqueueAsync(parentRunGuid, ct).ConfigureAwait(false);
            }
        }

        return Results.Ok(result);
    }

    private static async Task<IReadOnlyList<PeerNodeWorkspaceDto>> ListWorkerAuthorizedWorkspacesAsync(
        HttpContext ctx,
        PeerAccessPolicyDto policy,
        CancellationToken ct)
    {
        var selectedMode = string.Equals(policy.WorkspaceMode, "selected", StringComparison.OrdinalIgnoreCase);
        var allowedSet = selectedMode
            ? new HashSet<string>(policy.WorkspaceIds, StringComparer.OrdinalIgnoreCase)
            : null;
        var list = new List<PeerNodeWorkspaceDto>();

        if (ctx.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            foreach (var scope in registry.List().Where(x => x.ScopeKind == "project"))
            {
                try
                {
                    await using var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                    var projects = await lease.Services.GetRequiredService<ProjectSessionStore>()
                        .ListProjectsAsync(LifecycleStatuses.Active, ct).ConfigureAwait(false);
                    foreach (var p in projects)
                    {
                        var pid = p.Id.ToString();
                        if (selectedMode && !allowedSet!.Contains(pid)) continue;
                        list.Add(new PeerNodeWorkspaceDto
                        {
                            RemoteProjectId = pid,
                            Name = p.Name,
                            RemoteRootPath = p.RootPath,
                            StorageId = scope.StorageId
                        });
                    }
                }
                catch
                {
                    // Skip unavailable/locked project scope.
                }
            }
            return list;
        }

        if (ctx.RequestServices.GetService<ProjectSessionStore>() is { } directStore)
        {
            var projects = await directStore.ListProjectsAsync(LifecycleStatuses.Active, ct).ConfigureAwait(false);
            foreach (var p in projects)
            {
                var pid = p.Id.ToString();
                if (selectedMode && !allowedSet!.Contains(pid)) continue;
                list.Add(new PeerNodeWorkspaceDto
                {
                    RemoteProjectId = pid,
                    Name = p.Name,
                    RemoteRootPath = p.RootPath,
                    StorageId = ctx.RequestServices.GetService<IScopeStorageLocations>()?.StorageId
                });
            }
        }

        return list;
    }

    private static long? ExtractEventSequence(EventEnvelope item)
    {
        if (!item.Payload.TryGetValue("sequence", out var value) || value is null) return null;
        return value switch
        {
            long seq => seq,
            int seq => seq,
            JsonElement { ValueKind: JsonValueKind.Number } num when num.TryGetInt64(out var seq) => seq,
            string text when long.TryParse(text, out var seq) => seq,
            _ => null
        };
    }

    private static string ExtractEventSummary(EventEnvelope item)
    {
        if (item.Payload.TryGetValue("summary", out var value) && value is not null)
        {
            var text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
                _ => value.ToString()
            };
            if (!string.IsNullOrWhiteSpace(text)) return text!;
        }
        return item.EventType;
    }

    private static string RunStatusToWire(RunStatus status) => status switch
    {
        RunStatus.Planning => "planning",
        RunStatus.Understanding => "understanding",
        RunStatus.Executing => "executing",
        RunStatus.Replanning => "replanning",
        RunStatus.AwaitingApproval => "awaiting_approval",
        RunStatus.AwaitingDelegate => "awaiting_delegate",
        RunStatus.AwaitingUser => "awaiting_user",
        RunStatus.Paused => "paused",
        RunStatus.Reviewing => "reviewing",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => "running"
    };

    internal static (string DeviceId, string DeviceName) ResolveApprovalDeciderOrigin(
        HttpContext context,
        IPeerIdentityService? peerIdentity = null)
    {
        var rawId = context.Request.Headers[PeerControllerIdHeader].FirstOrDefault()?.Trim();
        var rawName = context.Request.Headers[PeerControllerNameHeader].FirstOrDefault()?.Trim();
        var deviceId = string.IsNullOrWhiteSpace(rawId) ? "local" : rawId;
        if (!string.IsNullOrWhiteSpace(rawName))
        {
            return (deviceId, rawName.Length <= 256 ? rawName : rawName[..256]);
        }

        if (string.Equals(deviceId, "local", StringComparison.OrdinalIgnoreCase))
        {
            var machine = Environment.MachineName?.Trim();
            var fallback = string.IsNullOrWhiteSpace(machine) ? "local" : machine;
            return ("local", fallback);
        }

        return (deviceId, deviceId);
    }

    internal static bool IsObserverPeerRequest(HttpContext context)
    {
        var role = context.Request.Headers[PeerRoleHeader].FirstOrDefault()?.Trim();
        return string.Equals(role, "observer", StringComparison.OrdinalIgnoreCase);
    }

    internal static IResult Problem(HttpContext context, int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://tinadec.dev/errors/{code}",
            Title = code,
            Detail = detail,
            Status = status,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        TinadecCoreHttpExtensions.AddClassification(problem, code, status);
        problem.Extensions["trace_id"] = context.TraceIdentifier;
        return Results.Json(
            problem,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower },
            "application/problem+json",
            statusCode: status);
    }

    private static async ValueTask<object?> EnforceLocalManagementOnlyAsync(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
        {
            return Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Peer security management endpoints can only be invoked from local loopback.");
        }

        var controllerHeader = ctx.Request.Headers[PeerControllerIdHeader].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(controllerHeader)
            && !string.Equals(controllerHeader, "local", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Remote peer controllers are strictly forbidden from accessing worker peer security management endpoints.");
        }

        var peerRole = ctx.Request.Headers[PeerRoleHeader].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(peerRole))
        {
            return Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Proxied peer requests cannot invoke peer management endpoints.");
        }

        return await next(invocation).ConfigureAwait(false);
    }

    private static async ValueTask<object?> EnforceGatewayInternalLoopbackAsync(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
        {
            return Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Internal peer control endpoints only accept loopback connections.");
        }

        var controllerHeader = ctx.Request.Headers[PeerControllerIdHeader].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(controllerHeader))
        {
            return Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Proxied peer requests are forbidden on internal peer control endpoints.");
        }

        var principal = ctx.Request.Headers[PrincipalHeader].FirstOrDefault()?.Trim();
        if (!string.Equals(principal, GatewayInternalPrincipal, StringComparison.Ordinal))
        {
            return Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.Unauthorized,
                "Internal peer control endpoints require x-tinadec-principal: gateway-internal.");
        }

        return await next(invocation).ConfigureAwait(false);
    }

    private readonly record struct ResolvedPeerScope(IServiceProvider Services, StorageRuntimeLease? Lease, string? StorageId = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private static async ValueTask<ResolvedPeerScope> AcquireUserPeerScopeAsync(HttpContext context, CancellationToken ct)
    {
        if (context.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            var lease = await registry.AcquireAsync("user", ct).ConfigureAwait(false);
            return new ResolvedPeerScope(lease.Services, lease, "user");
        }

        return new ResolvedPeerScope(context.RequestServices, null, context.RequestServices.GetService<IScopeStorageLocations>()?.StorageId);
    }

    private static async ValueTask<ResolvedPeerScope?> AcquireScopeForProjectAsync(HttpContext context, Guid projectId, CancellationToken ct)
    {
        if (context.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            var matching = registry.List().FirstOrDefault(s => s.ProjectId == projectId);
            if (matching is not null)
            {
                var lease = await registry.AcquireAsync(matching.StorageId, ct).ConfigureAwait(false);
                return new ResolvedPeerScope(lease.Services, lease, matching.StorageId);
            }

            // Also check user scope in case project was created directly in user scope
            var userLease = await registry.AcquireAsync("user", ct).ConfigureAwait(false);
            var userProj = await userLease.Services.GetRequiredService<ProjectSessionStore>()
                .GetProjectAnyStatusAsync(projectId, ct).ConfigureAwait(false);
            if (userProj is not null && userProj.LifecycleStatus == LifecycleStatuses.Active)
            {
                return new ResolvedPeerScope(userLease.Services, userLease, "user");
            }
            await userLease.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        if (context.RequestServices.GetService<ProjectSessionStore>() is { } store)
        {
            var proj = await store.GetProjectAnyStatusAsync(projectId, ct).ConfigureAwait(false);
            if (proj is not null && proj.LifecycleStatus == LifecycleStatuses.Active)
            {
                return new ResolvedPeerScope(context.RequestServices, null, context.RequestServices.GetService<IScopeStorageLocations>()?.StorageId);
            }
        }

        return null;
    }

    private static async ValueTask<ResolvedPeerScope?> AcquireScopeForSessionAsync(HttpContext context, Guid sessionId, CancellationToken ct)
    {
        if (context.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            foreach (var scope in registry.List())
            {
                try
                {
                    var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                    var found = await lease.Services.GetRequiredService<ProjectSessionStore>()
                        .FindAsync(sessionId, ct).ConfigureAwait(false);
                    if (found is not null)
                    {
                        return new ResolvedPeerScope(lease.Services, lease, scope.StorageId);
                    }
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Continue scanning remaining scopes.
                }
            }

            return null;
        }

        if (context.RequestServices.GetService<ProjectSessionStore>() is { } store
            && await store.FindAsync(sessionId, ct).ConfigureAwait(false) is not null)
        {
            return new ResolvedPeerScope(context.RequestServices, null, context.RequestServices.GetService<IScopeStorageLocations>()?.StorageId);
        }

        return null;
    }
}
