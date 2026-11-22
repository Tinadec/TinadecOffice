using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TinadecCore.AspNetCore.Auth;

/// <summary>
/// Standardized authenticated principal forwarded by Gateway BFF or resolved from an
/// OIDC session (<c>X-User-Id</c>, <c>X-Tenant-Id</c>, <c>X-Roles</c>).
/// </summary>
public sealed record HttpForwardedPrincipal(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("roles")] string[] Roles,
    [property: JsonPropertyName("auth_method")] string AuthMethod);

/// <summary>
/// The Logto / OIDC login surface mounted under <c>/auth/*</c>. These routes sit outside
/// <c>/api/v1</c> because the login flow completes before a workspace session exists, while
/// staying inside the <c>X-Tinadec-Host-Control</c> host-control gate. <c>/auth/callback</c>
/// is the sole exemption because the browser reaches it directly with a single-use <c>state</c>.
/// Supports both Electron desktop (<c>X-Tinadec-Session</c> polling) and Web Gateway BFF
/// redirect/exchange with owner allowlist binding and <c>X-User-Id</c>/<c>X-Tenant-Id</c>/<c>X-Roles</c>.
/// </summary>
public static class AuthEndpoints
{
    private const string SessionHeader = "X-Tinadec-Session";
    public const string UserIdHeader = "X-User-Id";
    public const string TenantIdHeader = "X-Tenant-Id";
    public const string RolesHeader = "X-Roles";
    public const string AuthMethodHeader = "X-Auth-Method";

    private const string SuccessPage =
        "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>登录成功</title><body style=\"font-family:system-ui;display:grid;place-items:center;height:100vh;margin:0;color:#e6edf3;background:#0d1117\"><p>登录成功，可以关闭此页面并返回 TinadecOffice。</p></body></html>";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/begin", BeginAsync).WithSummary("Begin OIDC login").WithDescription("Returns the authorization URL and state; supports optional redirect_uri, return_to, mode, and owner allowlists.");
        app.MapGet("/auth/login", LoginAsync).WithSummary("Start OIDC login").WithDescription("302 redirect to the OIDC authorization endpoint.");
        app.MapGet("/auth/callback", CallbackAsync).WithSummary("OIDC callback").WithDescription("Exchanges the authorization code, verifies owner allowlists, and either redirects to return_to (web mode) or renders completion HTML.");
        app.MapPost("/auth/exchange", ExchangeAsync).WithSummary("Exchange OIDC code for Gateway BFF").WithDescription("Host-gated authorization-code exchange returning session, identity, and standardized principal.");
        app.MapGet("/auth/check", CheckAsync).WithSummary("Poll login result").WithDescription("Returns the session token, identity, and principal once the authorization callback has completed.");
        app.MapGet("/auth/me", MeAsync).WithSummary("Current identity").WithDescription("Resolves X-Tinadec-Session or trusted X-User-Id/X-Tenant-Id/X-Roles headers to the active identity.");
        app.MapGet("/auth/principal", PrincipalAsync).WithSummary("Resolved request principal").WithDescription("Resolves the authenticated principal from X-Tinadec-Session or forwarded BFF principal headers.");
        app.MapPost("/auth/logout", LogoutAsync).WithSummary("Revoke a session").WithDescription("Revokes the X-Tinadec-Session token.");
        app.MapPatch("/auth/profile", UpdateProfileAsync).WithSummary("Update the account profile").WithDescription("Writes nickname/avatar to Logto's Account API and refreshes the session identity.");
        return app;
    }

    public static HttpForwardedPrincipal? TryReadForwardedPrincipal(HttpRequest request)
    {
        var userId = request.Headers[UserIdHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var tenantId = request.Headers[TenantIdHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(tenantId)) tenantId = "default";
        var rawRoles = request.Headers[RolesHeader].ToString();
        var roles = string.IsNullOrWhiteSpace(rawRoles)
            ? new[] { "owner" }
            : rawRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (roles.Length == 0) roles = ["owner"];
        var authMethod = request.Headers[AuthMethodHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(authMethod)) authMethod = "bff_forwarded";
        return new HttpForwardedPrincipal(userId, tenantId, roles, authMethod);
    }

    private static async Task<IResult> BeginAsync(HttpContext context, OidcClient client, OidcSessionStore store, IOptions<TinadecOidcOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return Results.Json(new { code = "oidc_not_configured", message = "Logto OIDC login is not configured on this host." }, statusCode: 503);

        var (redirectUri, returnTo, mode, allowedSubjects, allowedEmails) = ReadLoginQuery(context.Request.Query);
        (string State, string Nonce) login;
        try
        {
            login = store.BeginLogin(redirectUri, returnTo, mode, allowedSubjects, allowedEmails);
        }
        catch (OidcLoginCapacityException ex)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Json(new { code = "too_many_logins", message = ex.Message }, statusCode: 429);
        }
        var url = await client.BuildAuthorizationUrlAsync(login.State, login.Nonce, redirectUri, ct).ConfigureAwait(false);
        return Results.Json(new { authorizeUrl = url, state = login.State });
    }

    private static async Task<IResult> LoginAsync(HttpContext context, OidcClient client, OidcSessionStore store, IOptions<TinadecOidcOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return Results.Json(new { code = "oidc_not_configured", message = "Logto OIDC login is not configured on this host." }, statusCode: 503);

        var (redirectUri, returnTo, mode, allowedSubjects, allowedEmails) = ReadLoginQuery(context.Request.Query);
        (string State, string Nonce) login;
        try
        {
            login = store.BeginLogin(redirectUri, returnTo, mode, allowedSubjects, allowedEmails);
        }
        catch (OidcLoginCapacityException ex)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Json(new { code = "too_many_logins", message = ex.Message }, statusCode: 429);
        }
        var url = await client.BuildAuthorizationUrlAsync(login.State, login.Nonce, redirectUri, ct).ConfigureAwait(false);
        return Results.Redirect(url);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext context, OidcClient client, OidcSessionStore store, IOptions<TinadecOidcOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TinadecOidc");
        var query = context.Request.Query;
        var code = query["code"].ToString();
        var state = query["state"].ToString();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return Results.Json(new { code = "invalid_callback", message = "The authorization callback is missing code or state." }, statusCode: 400);
        var pending = store.ConsumePendingLogin(state);
        if (pending is null)
            return Results.Json(new { code = "invalid_state", message = "The login attempt has expired or was never started." }, statusCode: 400);

        (OidcIdentity Identity, OidcTokenSet Tokens)? result = null;
        try { result = await client.ExchangeAsync(code, pending.Nonce, pending.RedirectUri, ct).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogError(ex, "OIDC authorization-code exchange failed: {Message}", ex.Message); result = null; }

        if (result is null)
        {
            logger.LogWarning("OIDC exchange returned no identity (code rejected or id_token failed validation).");
            return Results.Json(new { code = "oidc_exchange_failed", message = "Logto rejected the authorization code or the id_token failed validation." }, statusCode: 401);
        }

        if (!options.Value.IsAllowedIdentity(result.Value.Identity, pending.AllowedSubjects, pending.AllowedEmails))
        {
            logger.LogWarning("OIDC subject '{Subject}' rejected by owner allowlist.", result.Value.Identity.Subject);
            return Results.Json(new { code = "oidc_subject_forbidden", message = "This OIDC account is not authorized for host owner access." }, statusCode: 403);
        }

        await store.EnsureLoadedAsync().ConfigureAwait(false);
        var session = await store.CreateSessionAsync(result.Value.Identity, result.Value.Tokens).ConfigureAwait(false);
        store.CompleteLogin(state, session);

        if (string.Equals(pending.Mode, "web", StringComparison.OrdinalIgnoreCase)
            && TryNormalizeRelativeReturnTo(pending.ReturnTo, out var safeReturnTo))
        {
            return Results.Redirect(AppendStateQuery(safeReturnTo, state));
        }
        return Results.Content(SuccessPage, "text/html; charset=utf-8");
    }

    private sealed record ExchangeRequest(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("redirect_uri")] string? RedirectUri = null,
        [property: JsonPropertyName("allowed_subjects")] string[]? AllowedSubjects = null,
        [property: JsonPropertyName("allowed_emails")] string[]? AllowedEmails = null);

    private static async Task<IResult> ExchangeAsync(
        HttpContext context, OidcClient client, OidcSessionStore store, IOptions<TinadecOidcOptions> options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return Results.Json(new { code = "oidc_not_configured", message = "Logto OIDC login is not configured on this host." }, statusCode: 503);

        ExchangeRequest? body;
        try { body = await context.Request.ReadFromJsonAsync<ExchangeRequest>(ct).ConfigureAwait(false); }
        catch (JsonException) { body = null; }
        if (body is null || string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.State))
            return Results.Json(new { code = "invalid_request", message = "Expected { code, state, redirect_uri? }." }, statusCode: 400);

        var pending = store.ConsumePendingLogin(body.State);
        if (pending is null)
            return Results.Json(new { code = "invalid_state", message = "The login attempt has expired or was never started." }, statusCode: 400);

        var effectiveRedirect = !string.IsNullOrWhiteSpace(body.RedirectUri) ? body.RedirectUri.Trim() : pending.RedirectUri;
        (OidcIdentity Identity, OidcTokenSet Tokens)? result = null;
        try { result = await client.ExchangeAsync(body.Code, pending.Nonce, effectiveRedirect, ct).ConfigureAwait(false); }
        catch (Exception ex) { loggerFactory.CreateLogger("TinadecOidc").LogError(ex, "OIDC code exchange failed: {Message}", ex.Message); result = null; }

        if (result is null)
            return Results.Json(new { code = "oidc_exchange_failed", message = "Logto rejected the authorization code or the id_token failed validation." }, statusCode: 401);

        var extraSubjects = CombineLists(pending.AllowedSubjects, body.AllowedSubjects);
        var extraEmails = CombineLists(pending.AllowedEmails, body.AllowedEmails);
        if (!options.Value.IsAllowedIdentity(result.Value.Identity, extraSubjects, extraEmails))
            return Results.Json(new { code = "oidc_subject_forbidden", message = "This OIDC account is not authorized for host owner access." }, statusCode: 403);

        await store.EnsureLoadedAsync().ConfigureAwait(false);
        var session = await store.CreateSessionAsync(result.Value.Identity, result.Value.Tokens).ConfigureAwait(false);
        var principal = BuildOidcPrincipal(result.Value.Identity);
        return Results.Ok(new
        {
            session,
            identity = ProjectIdentity(result.Value.Identity),
            principal
        });
    }

    private static IResult CheckAsync(HttpContext context, OidcSessionStore store)
    {
        var session = store.CheckLogin(context.Request.Query["state"].ToString());
        if (session is null)
            return Results.Json(new { status = "pending" });

        var identity = store.GetIdentity(session);
        return Results.Json(new
        {
            status = "complete",
            session,
            identity = identity is null ? null : ProjectIdentity(identity),
            principal = identity is null ? null : BuildOidcPrincipal(identity)
        });
    }

    private static async Task<IResult> MeAsync(
        HttpContext context, OidcSessionStore store, OidcClient client, CancellationToken ct)
    {
        // A Desktop launch spawns a fresh Core: the session this request carries may only exist
        // on disk right now. Restoring also renews an expired OP token, which is what keeps the
        // refresh chain alive (Logto rotates it on every token request).
        var sessionToken = context.Request.Headers[SessionHeader].ToString();
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            var session = await OidcSessionRefresh
                .EnsureFreshAsync(store, client, sessionToken, ct)
                .ConfigureAwait(false);
            if (session is null)
                return Results.Json(new { code = "unauthorized", message = "No active login session." }, statusCode: 401);
            return Results.Ok(new
            {
                session.Identity.Subject,
                session.Identity.Nickname,
                AvatarUrl = session.Identity.AvatarUrl,
                session.Identity.Email,
                Principal = BuildOidcPrincipal(session.Identity)
            });
        }

        var forwarded = TryReadForwardedPrincipal(context.Request);
        if (forwarded is null)
            return Results.Json(new { code = "unauthorized", message = "No active login session." }, statusCode: 401);

        return Results.Ok(new
        {
            Subject = forwarded.UserId,
            Nickname = forwarded.UserId,
            AvatarUrl = (string?)null,
            Email = (string?)null,
            Principal = forwarded
        });
    }

    private static async Task<IResult> PrincipalAsync(
        HttpContext context, OidcSessionStore store, OidcClient client, CancellationToken ct)
    {
        var sessionToken = context.Request.Headers[SessionHeader].ToString();
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            var session = await OidcSessionRefresh
                .EnsureFreshAsync(store, client, sessionToken, ct)
                .ConfigureAwait(false);
            if (session is not null)
            {
                var p = BuildOidcPrincipal(session.Identity);
                return Results.Ok(new
                {
                    authenticated = true,
                    user_id = p.UserId,
                    tenant_id = p.TenantId,
                    roles = p.Roles,
                    auth_method = p.AuthMethod,
                    identity = ProjectIdentity(session.Identity)
                });
            }
        }

        var forwarded = TryReadForwardedPrincipal(context.Request);
        if (forwarded is null)
            return Results.Json(new { code = "unauthorized", message = "No active authenticated principal." }, statusCode: 401);

        return Results.Ok(new
        {
            authenticated = true,
            user_id = forwarded.UserId,
            tenant_id = forwarded.TenantId,
            roles = forwarded.Roles,
            auth_method = forwarded.AuthMethod,
            identity = (object?)null
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, OidcSessionStore store)
    {
        await store.EnsureLoadedAsync().ConfigureAwait(false);
        await store.RevokeAsync(context.Request.Headers[SessionHeader].ToString()).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>Body of <c>PATCH /auth/profile</c>; both fields are optional.</summary>
    private sealed record ProfileUpdate(string? Nickname, string? AvatarUrl);

    private static async Task<IResult> UpdateProfileAsync(
        HttpContext context, OidcClient client, OidcSessionStore store, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        // A profile write is the one thing that must reach Logto, so it renews the OP token first:
        // an hour after login the stored one is already dead.
        var session = await OidcSessionRefresh
            .EnsureFreshAsync(store, client, context.Request.Headers[SessionHeader].ToString(), ct)
            .ConfigureAwait(false);
        if (session is null)
            return Results.Json(new { code = "unauthorized", message = "No active login session." }, statusCode: 401);

        ProfileUpdate? update;
        try { update = await context.Request.ReadFromJsonAsync<ProfileUpdate>(ct).ConfigureAwait(false); }
        catch { update = null; }
        if (update is null)
            return Results.Json(new { code = "invalid_body", message = "Expected { nickname?, avatar_url? }." }, statusCode: 400);

        var current = session.Identity;
        var nickname = string.IsNullOrWhiteSpace(update.Nickname) ? current.Nickname : update.Nickname.Trim();
        // Absent means "leave the avatar alone"; empty string means "clear it".
        var avatar = update.AvatarUrl is null ? null : (update.AvatarUrl.Trim() is { Length: > 0 } value ? value : "");

        OidcIdentity updated;
        try
        {
            updated = await client.UpdateProfileAsync(session.AccessToken, current.Subject, nickname, avatar, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TinadecOidc").LogError(ex, "Logto Account API update failed: {Message}", ex.Message);
            return Results.Json(new { code = "profile_update_failed", message = ex.Message }, statusCode: 502);
        }

        var sessionToken = context.Request.Headers[SessionHeader].ToString();
        await store.UpdateIdentityAsync(sessionToken, updated).ConfigureAwait(false);
        return Results.Ok(new
        {
            updated.Subject,
            updated.Nickname,
            AvatarUrl = updated.AvatarUrl,
            updated.Email,
        });
    }

    private static (string? RedirectUri, string? ReturnTo, string? Mode, string[]? AllowedSubjects, string[]? AllowedEmails) ReadLoginQuery(IQueryCollection query)
    {
        var redirectUri = query["redirect_uri"].ToString().Trim();
        var returnTo = query["return_to"].ToString().Trim();
        var mode = query["mode"].ToString().Trim();
        var allowedSubjects = SplitCsv(query["allowed_subjects"].ToString());
        var allowedEmails = SplitCsv(query["allowed_emails"].ToString());
        return (
            string.IsNullOrWhiteSpace(redirectUri) ? null : redirectUri,
            TryNormalizeRelativeReturnTo(returnTo, out var safeReturn) ? safeReturn : null,
            string.IsNullOrWhiteSpace(mode) ? null : mode,
            allowedSubjects,
            allowedEmails);
    }

    private static string[]? SplitCsv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static string[]? CombineLists(string[]? first, string[]? second)
    {
        if ((first is null || first.Length == 0) && (second is null || second.Length == 0)) return null;
        return (first ?? []).Concat(second ?? []).ToArray();
    }

    private static bool TryNormalizeRelativeReturnTo(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (trimmed.Contains('\r') || trimmed.Contains('\n'))
            return false;
        normalized = trimmed;
        return true;
    }

    private static string AppendStateQuery(string relativePath, string state)
    {
        var separator = relativePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{relativePath}{separator}state={Uri.EscapeDataString(state)}";
    }

    private static HttpForwardedPrincipal BuildOidcPrincipal(OidcIdentity identity) =>
        new(identity.Subject, "default", ["owner"], "official_oidc");

    private static object ProjectIdentity(OidcIdentity identity) => new
    {
        subject = identity.Subject,
        nickname = identity.Nickname,
        avatar_url = identity.AvatarUrl,
        email = identity.Email
    };
}