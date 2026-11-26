using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Runtime;

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
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
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

        mgmt.MapDelete("/devices/{deviceId}/local-cache", async (string deviceId, HttpContext ctx, CancellationToken ct) =>
        {
            await using var scope = await AcquireUserPeerScopeAsync(ctx, ct).ConfigureAwait(false);
            var pairing = scope.Services.GetRequiredService<IPeerPairingService>();
            try
            {
                var cleared = await pairing.ClearDeviceLocalCacheAsync(deviceId, ct).ConfigureAwait(false);
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

        return app;
    }

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

    private readonly record struct ResolvedPeerScope(IServiceProvider Services, StorageRuntimeLease? Lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private static async ValueTask<ResolvedPeerScope> AcquireUserPeerScopeAsync(HttpContext context, CancellationToken ct)
    {
        if (context.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            var lease = await registry.AcquireAsync("user", ct).ConfigureAwait(false);
            return new ResolvedPeerScope(lease.Services, lease);
        }

        return new ResolvedPeerScope(context.RequestServices, null);
    }
}
