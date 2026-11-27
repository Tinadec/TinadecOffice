using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Internal loopback endpoints for Wave 3 Optional Controller Model Provider Proxy:
/// <list type="bullet">
///   <item><c>POST /api/v1/peers/internal/provider-proxy/tokens</c> —Controller loopback token minting</item>
///   <item><c>POST /api/v1/peers/internal/provider-proxy/session-binding</c> — Worker loopback in-memory session binding</item>
///   <item><c>POST /api/v1/peers/internal/provider-proxy/completions</c> — Controller loopback upstream LLM proxy (ONLY reachable from Gateway's authenticated mTLS tunnel handler)</item>
/// </list>
/// </summary>
public static class PeerProviderProxyEndpoints
{
    public const string VerifiedWorkerIdHeader = "x-tinadec-peer-verified-worker-id";
    public const string VerifiedDeviceIdHeader = "x-tinadec-peer-verified-device-id";

    private static readonly SocketsHttpHandler UpstreamHttpHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        UseCookies = false,
        AllowAutoRedirect = false
    };

    private static readonly HttpClient UpstreamHttpClient = new(UpstreamHttpHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly JsonWriterOptions RewriteJsonOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public sealed class IssueProxyTokenHttpRequestDto
    {
        [JsonPropertyName("controller_device_id")]
        public string? ControllerDeviceId { get; init; }

        [JsonPropertyName("worker_device_id")]
        public string? WorkerDeviceId { get; init; }

        [JsonPropertyName("delegation_id")]
        public string? DelegationId { get; init; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("remote_session_id")]
        public string? RemoteSessionId { get; init; }

        [JsonPropertyName("expires_at_ms")]
        public long? ExpiresAtMs { get; init; }

        [JsonPropertyName("ttl_seconds")]
        public int? TtlSeconds { get; init; }

        [JsonPropertyName("max_requests")]
        public int? MaxRequests { get; init; }
    }

    public sealed class IssueProxyTokenHttpResponseDto
    {
        [JsonPropertyName("proxy_session_token")]
        public string ProxySessionToken { get; init; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;

        [JsonPropertyName("controller_device_id")]
        public string ControllerDeviceId { get; init; } = string.Empty;

        [JsonPropertyName("worker_device_id")]
        public string WorkerDeviceId { get; init; } = string.Empty;

        [JsonPropertyName("delegation_id")]
        public string? DelegationId { get; init; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("expires_at_ms")]
        public long ExpiresAtMs { get; init; }

        [JsonPropertyName("max_requests")]
        public int MaxRequests { get; init; }
    }

    public sealed class BindWorkerProxySessionRequestDto
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("run_id")]
        public string? RunId { get; init; }

        [JsonPropertyName("delegation_id")]
        public string? DelegationId { get; init; }

        [JsonPropertyName("controller_device_id")]
        public string? ControllerDeviceId { get; init; }

        [JsonPropertyName("proxy_session_token")]
        public string? ProxySessionToken { get; init; }

        [JsonPropertyName("use_provider_proxy")]
        public bool? UseProviderProxy { get; init; }

        [JsonPropertyName("expires_at_ms")]
        public long? ExpiresAtMs { get; init; }

        [JsonPropertyName("gateway_loopback_url")]
        public string? GatewayLoopbackUrl { get; init; }

        [JsonPropertyName("model_hint")]
        public string? ModelHint { get; init; }
    }

    public static IEndpointRouteBuilder MapPeerProviderProxyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/peers/internal/provider-proxy")
            .ExcludeFromDescription()
            .AddEndpointFilter(EnforceInternalProviderProxyLoopbackAsync);

        group.MapPost("/tokens", HandleIssueTokenAsync);
        group.MapPost("/session-binding", HandleBindWorkerSessionAsync);
        group.MapPost("/completions", HandleControllerCompletionsAsync);

        return app;
    }

    private static async Task<IResult> HandleIssueTokenAsync(
        IssueProxyTokenHttpRequestDto? input,
        HttpContext ctx,
        CancellationToken ct)
    {
        await using var userScope = await AcquireUserScopeAsync(ctx, ct).ConfigureAwait(false);
        var secrets = userScope.Services.GetRequiredService<ISecretStore>();
        var hostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(secrets);

        if (!IsControllerProxyPolicyEnabled(userScope.Services, hostKey))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Controller provider proxy is disabled by policy.");
        }

        var controllerDeviceId = input?.ControllerDeviceId?.Trim();
        if (string.IsNullOrWhiteSpace(controllerDeviceId)
            && userScope.Services.GetService<IPeerIdentityService>() is { } identity)
        {
            var local = await identity.EnsureLocalIdentityAsync(ct).ConfigureAwait(false);
            controllerDeviceId = local.LocalDeviceId;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAtMs = input?.ExpiresAtMs is > 0
            ? input.ExpiresAtMs.Value
            : input?.TtlSeconds is > 0
                ? nowMs + (input.TtlSeconds.Value * 1000L)
                : nowMs + (PeerProviderProxyHostRegistry.DefaultTokenTtlSeconds * 1000L);

        var sessionId = !string.IsNullOrWhiteSpace(input?.RemoteSessionId)
            ? input!.RemoteSessionId
            : input?.SessionId;

        var issued = PeerProviderProxyHostRegistry.IssueToken(
            hostKey,
            controllerDeviceId ?? string.Empty,
            input?.WorkerDeviceId ?? string.Empty,
            input?.DelegationId,
            sessionId,
            expiresAtMs,
            input?.MaxRequests,
            nowMs);

        return Results.Ok(new IssueProxyTokenHttpResponseDto
        {
            ProxySessionToken = issued.Token,
            Token = issued.Token,
            ControllerDeviceId = issued.ControllerDeviceId,
            WorkerDeviceId = issued.WorkerDeviceId,
            DelegationId = issued.DelegationId,
            SessionId = issued.SessionId,
            ExpiresAtMs = issued.ExpiresAtMs,
            MaxRequests = issued.MaxRequests
        });
    }

    private static async Task<IResult> HandleBindWorkerSessionAsync(
        BindWorkerProxySessionRequestDto? input,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (input is null
            || string.IsNullOrWhiteSpace(input.ControllerDeviceId)
            || string.IsNullOrWhiteSpace(input.ProxySessionToken))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "controller_device_id and proxy_session_token are required.");
        }

        await using var userScope = await AcquireUserScopeAsync(ctx, ct).ConfigureAwait(false);
        var secrets = userScope.Services.GetRequiredService<ISecretStore>();
        var hostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(secrets);

        // Enforce Worker-side allow_provider_proxy = 1 in PeerPolicyService!
        if (userScope.Services.GetService<IPeerPolicyService>() is { } policyService)
        {
            var grants = await policyService.ListGrantsAsync(ct).ConfigureAwait(false);
            var activeGrant = grants.FirstOrDefault(g =>
                g.RevokedAt is null
                && string.Equals(g.ControllerDeviceId, input.ControllerDeviceId.Trim(), StringComparison.Ordinal));
            if (activeGrant is null || !activeGrant.Policy.AllowProviderProxy)
            {
                PeerProviderProxyHostRegistry.ClearWorkerControllerBindings(hostKey, input.ControllerDeviceId);
                return PeerDeviceEndpoints.Problem(
                    ctx,
                    StatusCodes.Status403Forbidden,
                    PeerErrorCodes.PolicyDenied,
                    $"Worker access policy for controller '{input.ControllerDeviceId.Trim()}' denies allow_provider_proxy.");
            }
        }

        var sessionGuid = Guid.TryParse(input.SessionId?.Trim(), out var parsedSid) ? parsedSid : Guid.Empty;
        var runGuid = Guid.TryParse(input.RunId?.Trim(), out var parsedRid) ? parsedRid : (Guid?)null;
        var expiresAtMs = input.ExpiresAtMs is > 0
            ? input.ExpiresAtMs.Value
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (PeerProviderProxyHostRegistry.DefaultTokenTtlSeconds * 1000L);

        string? validatedGwUrl = PeerProviderProxyHostRegistry.TryNormalizeLoopbackBaseUrl(input.GatewayLoopbackUrl, out var normGw)
            ? normGw
            : null;

        if (sessionGuid != Guid.Empty)
        {
            PeerProviderProxyRuntimeRegistry.Register(new PeerProviderProxySessionBinding(
                SessionId: sessionGuid.ToString(),
                RunId: runGuid?.ToString(),
                DelegationId: input.DelegationId,
                ControllerDeviceId: input.ControllerDeviceId.Trim(),
                ProxySessionToken: input.ProxySessionToken.Trim(),
                UseProviderProxy: input.UseProviderProxy ?? false,
                ExpiresAtMs: expiresAtMs,
                GatewayLoopbackUrl: validatedGwUrl,
                ModelHint: input.ModelHint));
        }

        PeerProviderProxyHostRegistry.BindWorkerSession(
            hostKey,
            new PeerProviderProxyHostRegistry.WorkerProxyContextEntry(
                SessionId: sessionGuid,
                RunId: runGuid,
                DelegationId: input.DelegationId,
                ControllerDeviceId: input.ControllerDeviceId.Trim(),
                ProxySessionToken: input.ProxySessionToken.Trim(),
                ForceProviderProxy: input.UseProviderProxy ?? false,
                ExpiresAtMs: expiresAtMs,
                GatewayLoopbackUrl: validatedGwUrl,
                ModelHint: input.ModelHint));

        return Results.Ok(new { bound = true, session_id = input.SessionId });
    }

    private static async Task<IResult> HandleControllerCompletionsAsync(
        HttpContext ctx,
        IHostApplicationLifetime lifetime,
        CancellationToken ct)
    {
        var verifiedWorkerId = ctx.Request.Headers[VerifiedWorkerIdHeader].FirstOrDefault()?.Trim();
        var verifiedDeviceId = ctx.Request.Headers[VerifiedDeviceIdHeader].FirstOrDefault()?.Trim();
        var rawToken = ctx.Request.Headers[PeerProxyModelProvider.ProxyTokenHeader].FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(verifiedWorkerId))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Provider proxy completions require an authenticated mTLS peer tunnel identity.");
        }

        await using var userScope = await AcquireUserScopeAsync(ctx, ct).ConfigureAwait(false);
        var secrets = userScope.Services.GetRequiredService<ISecretStore>();
        var hostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(secrets);

        // 1. Controller-side policy check.
        if (!IsControllerProxyPolicyEnabled(userScope.Services, hostKey))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Controller provider proxy is disabled by policy.");
        }

        if (userScope.Services.GetService<IPeerPairingService>() is { } pairingService)
        {
            var devices = await pairingService.ListDevicesAsync(ct).ConfigureAwait(false);
            if (devices.Count > 0)
            {
                var matchedDevice = devices.FirstOrDefault(d =>
                    string.Equals(d.RemoteDeviceId, verifiedWorkerId, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(verifiedDeviceId) && string.Equals(d.DeviceId, verifiedDeviceId, StringComparison.Ordinal)));
                if (matchedDevice is null || !matchedDevice.ConnectionDesired)
                {
                    return PeerDeviceEndpoints.Problem(
                        ctx,
                        StatusCodes.Status403Forbidden,
                        PeerErrorCodes.PolicyDenied,
                        $"Worker device '{verifiedWorkerId}' is not an active paired peer on this controller.");
                }
            }
        }

        if (userScope.Services.GetService<IPeerPolicyService>() is { } policyService)
        {
            var grants = await policyService.ListGrantsAsync(ct).ConfigureAwait(false);
            var workerGrant = grants.FirstOrDefault(g =>
                string.Equals(g.ControllerDeviceId, verifiedWorkerId, StringComparison.Ordinal));
            // If a mutually-paired grant exists and was revoked or explicitly configured to deny provider proxy:
            if (workerGrant is not null)
            {
                if (workerGrant.RevokedAt is not null)
                {
                    return PeerDeviceEndpoints.Problem(
                        ctx,
                        StatusCodes.Status403Forbidden,
                        PeerErrorCodes.PolicyDenied,
                        $"Peer grant for '{verifiedWorkerId}' has been revoked.");
                }
                if (!workerGrant.Policy.AllowProviderProxy && !string.Equals(workerGrant.Policy.Revision, "1", StringComparison.Ordinal))
                {
                    return PeerDeviceEndpoints.Problem(
                        ctx,
                        StatusCodes.Status403Forbidden,
                        PeerErrorCodes.PolicyDenied,
                        $"Controller peer policy for '{verifiedWorkerId}' denies allow_provider_proxy.");
                }
            }
        }

        // 2. Short-lived proxy_session_token validation & rate/count consumption.
        if (!PeerProviderProxyHostRegistry.TryValidateAndConsumeToken(
                hostKey,
                rawToken,
                verifiedWorkerId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                out _,
                out var denyReason))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                denyReason);
        }

        // 3. Parse incoming OpenAI-compatible request JSON (never trusting incoming auth headers).
        JsonDocument incomingDoc;
        try
        {
            incomingDoc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "Provider proxy completion body must be valid JSON.");
        }

        using (incomingDoc)
        {
            if (incomingDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return PeerDeviceEndpoints.Problem(
                    ctx,
                    StatusCodes.Status400BadRequest,
                    "invalid_request",
                    "Provider proxy completion body must be a JSON object.");
            }

            // 4. Resolve Controller's local configured model provider + API Key from ISecretStore.
            var resolved = await ResolveControllerUpstreamProviderAsync(ctx, userScope.Services, ct).ConfigureAwait(false);
            if (!resolved.IsAvailable
                || string.IsNullOrWhiteSpace(resolved.BaseUrl)
                || string.IsNullOrWhiteSpace(resolved.ApiKey))
            {
                return PeerDeviceEndpoints.Problem(
                    ctx,
                    StatusCodes.Status503ServiceUnavailable,
                    "model_not_configured",
                    PeerProviderProxySecretRedactor.Redact(
                        resolved.Error ?? "Controller has no configured upstream API model provider with a valid API key."));
            }

            var controllerApiKey = resolved.ApiKey!;
            var upstreamModel = string.IsNullOrWhiteSpace(resolved.Model) ? "gpt-4o-mini" : resolved.Model!.Trim();
            var rewrittenPayload = RewriteModelInPayload(incomingDoc.RootElement, upstreamModel, out var wantsStream);
            var candidateUrls = BuildUpstreamChatCompletionUrls(resolved.BaseUrl!);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.RequestAborted, lifetime.ApplicationStopping);
            var callToken = linkedCts.Token;

            HttpResponseMessage? upstreamResponse = null;
            try
            {
                for (var i = 0; i < candidateUrls.Count; i++)
                {
                    using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, candidateUrls[i])
                    {
                        Content = new ByteArrayContent(rewrittenPayload)
                    };
                    upstreamReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", controllerApiKey);
                    upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(wantsStream ? "text/event-stream" : "application/json"));

                    var attemptRes = await UpstreamHttpClient.SendAsync(
                        upstreamReq,
                        HttpCompletionOption.ResponseHeadersRead,
                        callToken).ConfigureAwait(false);

                    if (attemptRes.StatusCode == HttpStatusCode.NotFound && i + 1 < candidateUrls.Count)
                    {
                        attemptRes.Dispose();
                        continue;
                    }

                    upstreamResponse = attemptRes;
                    break;
                }

                if (upstreamResponse is null)
                {
                    return PeerDeviceEndpoints.Problem(
                        ctx,
                        StatusCodes.Status502BadGateway,
                        PeerErrorCodes.Unreachable,
                        "Controller upstream model provider returned 404.");
                }

                if (!upstreamResponse.IsSuccessStatusCode)
                {
                    var rawErrBody = await upstreamResponse.Content.ReadAsStringAsync(callToken).ConfigureAwait(false);
                    var sanitizedErr = PeerProviderProxySecretRedactor.Redact(rawErrBody, controllerApiKey);
                    if (sanitizedErr.Length > 500) sanitizedErr = sanitizedErr[..500];
                    return PeerDeviceEndpoints.Problem(
                        ctx,
                        StatusCodes.Status502BadGateway,
                        "upstream_provider_error",
                        string.IsNullOrWhiteSpace(sanitizedErr)
                            ? $"Controller upstream provider failed with HTTP {(int)upstreamResponse.StatusCode}."
                            : $"Controller upstream provider failed with HTTP {(int)upstreamResponse.StatusCode}: {sanitizedErr}");
                }

                var upstreamMediaType = upstreamResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (upstreamMediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
                    ctx.Response.Headers.CacheControl = "no-cache, no-store";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";
                    await ctx.Response.StartAsync(callToken).ConfigureAwait(false);

                    await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(callToken).ConfigureAwait(false);
                    using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync(callToken).ConfigureAwait(false)) is not null)
                    {
                        // Guarantee zero canary secret leakage even inside streamed SSE frames.
                        var safeLine = PeerProviderProxySecretRedactor.Redact(line, controllerApiKey);
                        await ctx.Response.WriteAsync(safeLine + "\n", callToken).ConfigureAwait(false);
                        if (line.Length == 0)
                        {
                            await ctx.Response.Body.FlushAsync(callToken).ConfigureAwait(false);
                        }
                    }
                    await ctx.Response.Body.FlushAsync(callToken).ConfigureAwait(false);
                    return Results.Empty;
                }
                else
                {
                    var jsonBody = await upstreamResponse.Content.ReadAsStringAsync(callToken).ConfigureAwait(false);
                    var safeJson = PeerProviderProxySecretRedactor.Redact(jsonBody, controllerApiKey);
                    return Results.Content(safeJson, "application/json; charset=utf-8", Encoding.UTF8, StatusCodes.Status200OK);
                }
            }
            catch (OperationCanceledException) when (callToken.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (Exception ex)
            {
                var safeEx = PeerProviderProxySecretRedactor.Redact(ex.Message, controllerApiKey);
                if (!ctx.Response.HasStarted)
                {
                    return PeerDeviceEndpoints.Problem(
                        ctx,
                        StatusCodes.Status502BadGateway,
                        PeerErrorCodes.Unreachable,
                        $"Controller upstream model provider request failed: {safeEx}");
                }
                return Results.Empty;
            }
            finally
            {
                upstreamResponse?.Dispose();
            }
        }
    }

    private static async Task<ChatResolution> ResolveControllerUpstreamProviderAsync(
        HttpContext ctx,
        IServiceProvider userServices,
        CancellationToken ct)
    {
        var fromUser = await ResolveScopeProviderAsync(userServices, ct).ConfigureAwait(false);
        if (fromUser.IsAvailable) return fromUser;

        if (ctx.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            foreach (var scope in registry.List().Where(s => s.ScopeKind == "project"))
            {
                try
                {
                    await using var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                    var fromProject = await ResolveScopeProviderAsync(lease.Services, ct).ConfigureAwait(false);
                    if (fromProject.IsAvailable) return fromProject;
                }
                catch
                {
                    // Skip unavailable project scopes.
                }
            }
        }

        return fromUser;
    }

    private static async Task<ChatResolution> ResolveScopeProviderAsync(IServiceProvider services, CancellationToken ct)
    {
        if (services.GetService<IChatResolver>() is { } resolver)
        {
            var chatResolved = await resolver.ResolveChatAsync("chat", ct).ConfigureAwait(false);
            if (chatResolved.IsAvailable
                && ChatProtocols.Normalize(chatResolved.Protocol) != ChatProtocols.PeerProxy
                && !ChatProtocols.IsProcessTransport(chatResolved.Protocol)
                && !string.IsNullOrWhiteSpace(chatResolved.BaseUrl)
                && !string.IsNullOrWhiteSpace(chatResolved.ApiKey))
            {
                return chatResolved;
            }
        }

        // Fallback: pick the most recently updated enabled HTTP API provider that has a stored API key.
        var dbFactory = services.GetService<IDbContextFactory<ModelControlDbContext>>();
        var contentStore = services.GetService<IContentStore>();
        var secretStore = services.GetService<ISecretStore>();
        if (dbFactory is null || contentStore is null || secretStore is null)
        {
            return new ChatResolution { IsAvailable = false, Error = "Controller model stores are unavailable." };
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var providers = (await db.Providers.AsNoTracking()
            .Where(p => p.Enabled && p.DeletedAt == null && p.SecretReference != null && p.SecretReference != "")
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .OrderByDescending(p => p.Revision)
            .ThenByDescending(p => p.UpdatedAt)
            .ToList();

        foreach (var provider in providers)
        {
            var apiKey = await secretStore.GetAsync(provider.SecretReference!, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey)) continue;

            var pv = await db.ProviderVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.Id == provider.CurrentVersionId, ct)
                .ConfigureAwait(false);
            if (pv is null) continue;

            string configJson;
            try
            {
                await using var stream = await contentStore.OpenReadAsync(
                    new ContentReference(pv.ContentReference, pv.ContentHash, pv.ContentLength, "application/json"),
                    ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                configJson = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            using var doc = JsonDocument.Parse(configJson);
            string? Prop(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var protocol = HarnessCatalog.ResolveProtocol(Prop("protocol"), provider.Driver, Prop("channel"));
            if (ChatProtocols.IsProcessTransport(protocol) || protocol == ChatProtocols.OpencodeServe || protocol == ChatProtocols.PeerProxy)
                continue;

            var baseUrl = Prop("base_url");
            if (string.IsNullOrWhiteSpace(baseUrl)) continue;

            var model = Prop("model");
            if (string.IsNullOrWhiteSpace(model)
                && doc.RootElement.TryGetProperty("models", out var modelsArr)
                && modelsArr.ValueKind == JsonValueKind.Array)
            {
                model = modelsArr.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            }

            return new ChatResolution
            {
                IsAvailable = true,
                BaseUrl = baseUrl.Trim(),
                ApiKey = apiKey,
                Model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model!.Trim(),
                Protocol = protocol,
                ProviderInstanceId = provider.Id,
                ProviderVersionId = pv.Id
            };
        }

        return new ChatResolution { IsAvailable = false, Error = "No enabled API model provider with a stored API key was found on controller." };
    }

    private static IReadOnlyList<string> BuildUpstreamChatCompletionUrls(string rawBaseUrl)
    {
        var trimmed = rawBaseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return [trimmed];
        }
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            && (string.IsNullOrEmpty(parsed.AbsolutePath) || parsed.AbsolutePath == "/"))
        {
            // Support both OpenAI standard `/v1/chat/completions` and root `/chat/completions` mock servers.
            return [$"{trimmed}/v1/chat/completions", $"{trimmed}/chat/completions"];
        }
        return [$"{trimmed}/chat/completions"];
    }

    private static byte[] RewriteModelInPayload(JsonElement root, string controllerModel, out bool stream)
    {
        stream = root.TryGetProperty("stream", out var sEl) && sEl.ValueKind == JsonValueKind.True;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
        using var writer = new Utf8JsonWriter(buffer, RewriteJsonOptions);

        writer.WriteStartObject();
        var wroteModel = false;
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "model", StringComparison.Ordinal))
            {
                wroteModel = true;
                var requested = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()?.Trim() : null;
                var effectiveModel = string.IsNullOrWhiteSpace(requested)
                    || string.Equals(requested, ChatProtocols.PeerProxy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(requested, "peer-proxy", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(requested, "default", StringComparison.OrdinalIgnoreCase)
                    ? controllerModel
                    : requested!;
                writer.WriteString("model", effectiveModel);
                continue;
            }
            prop.WriteTo(writer);
        }

        if (!wroteModel)
        {
            writer.WriteString("model", controllerModel);
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static bool IsControllerProxyPolicyEnabled(IServiceProvider services, int hostKey)
    {
        if (!PeerProviderProxyHostRegistry.IsControllerProxyAllowed(hostKey))
            return false;
        var cfg = services.GetService<IConfiguration>();
        var configured = cfg?["TinadecPeers:AllowProviderProxy"];
        if (!string.IsNullOrWhiteSpace(configured) && bool.TryParse(configured.Trim(), out var parsed))
            return parsed;
        return true;
    }

    private static async ValueTask<object?> EnforceInternalProviderProxyLoopbackAsync(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        if (!TinadecMcpServerEndpoints.IsLoopbackOnlyRequest(ctx))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Internal provider-proxy endpoints only accept local loopback connections.");
        }

        var controllerHeader = ctx.Request.Headers[PeerDeviceEndpoints.PeerControllerIdHeader].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(controllerHeader))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Proxied peer requests are strictly forbidden on internal provider-proxy endpoints.");
        }

        var peerRole = ctx.Request.Headers[PeerDeviceEndpoints.PeerRoleHeader].FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(peerRole))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Proxied peer roles are strictly forbidden on internal provider-proxy endpoints.");
        }

        var principal = ctx.Request.Headers[PeerDeviceEndpoints.PrincipalHeader].FirstOrDefault()?.Trim();
        if (!string.Equals(principal, PeerDeviceEndpoints.GatewayInternalPrincipal, StringComparison.Ordinal))
        {
            return PeerDeviceEndpoints.Problem(
                ctx,
                StatusCodes.Status403Forbidden,
                PeerErrorCodes.PolicyDenied,
                "Internal provider-proxy endpoints require x-tinadec-principal: gateway-internal.");
        }

        return await next(invocation).ConfigureAwait(false);
    }

    private readonly record struct ScopedServicesLease(IServiceProvider Services, StorageRuntimeLease? Lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private static async ValueTask<ScopedServicesLease> AcquireUserScopeAsync(HttpContext context, CancellationToken ct)
    {
        if (context.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            var lease = await registry.AcquireAsync("user", ct).ConfigureAwait(false);
            return new ScopedServicesLease(lease.Services, lease);
        }

        return new ScopedServicesLease(context.RequestServices, null);
    }
}
