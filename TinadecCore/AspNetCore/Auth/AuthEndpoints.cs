using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TinadecCore.AspNetCore.Auth;

/// <summary>
/// The Logto login surface mounted under <c>/auth/*</c>. These routes sit outside
/// <c>/api/v1</c> because the login window completes the OIDC dance before any session
/// exists — but they are still inside the host-control gate. That gate checks the host
/// credential, which the Desktop main process holds from launch, not the session token
/// that only exists after login. <c>/auth/callback</c> is the one exemption: the system
/// browser makes that request and cannot carry a private header, so it validates a
/// single-use state instead. The session token arrives over <c>X-Tinadec-Session</c>,
/// which only the Electron main process injects.
/// </summary>
public static class AuthEndpoints
{
    private const string SessionHeader = "X-Tinadec-Session";

    private const string SuccessPage =
        "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>登录成功</title><body style=\"font-family:system-ui;display:grid;place-items:center;height:100vh;margin:0;color:#e6edf3;background:#0d1117\"><p>登录成功，可以关闭此页面并返回 TinadecOffice。</p></body></html>";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/begin", BeginAsync).WithSummary("Begin Logto login").WithDescription("Returns the authorization URL and state; the system browser completes the flow.");
        app.MapGet("/auth/login", LoginAsync).WithSummary("Start Logto login").WithDescription("302 redirect to the Logto authorization endpoint.");
        app.MapGet("/auth/callback", CallbackAsync).WithSummary("OIDC callback").WithDescription("Exchanges the authorization code and records the session for polling.");
        app.MapGet("/auth/check", CheckAsync).WithSummary("Poll login result").WithDescription("Returns the session token once the authorization callback has completed.");
        app.MapGet("/auth/me", MeAsync).WithSummary("Current identity").WithDescription("Resolves X-Tinadec-Session to the logged-in identity.");
        app.MapPost("/auth/logout", LogoutAsync).WithSummary("Revoke a session").WithDescription("Revokes the X-Tinadec-Session token.");
        app.MapPatch("/auth/profile", UpdateProfileAsync).WithSummary("Update the account profile").WithDescription("Writes nickname/avatar to Logto's Account API and refreshes the session identity.");
        return app;
    }

    private static async Task<IResult> BeginAsync(HttpContext context, OidcClient client, OidcSessionStore store, IOptions<TinadecOidcOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return Results.Json(new { code = "oidc_not_configured", message = "Logto OIDC login is not configured on this host." }, statusCode: 503);
        (string State, string Nonce) login;
        try
        {
            login = store.BeginLogin();
        }
        catch (OidcLoginCapacityException ex)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Json(new { code = "too_many_logins", message = ex.Message }, statusCode: 429);
        }
        var url = await client.BuildAuthorizationUrlAsync(login.State, login.Nonce, ct).ConfigureAwait(false);
        return Results.Json(new { authorizeUrl = url, state = login.State });
    }

    private static async Task<IResult> LoginAsync(HttpContext context, OidcClient client, OidcSessionStore store, IOptions<TinadecOidcOptions> options, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return Results.Json(new { code = "oidc_not_configured", message = "Logto OIDC login is not configured on this host." }, statusCode: 503);
        (string State, string Nonce) login;
        try
        {
            login = store.BeginLogin();
        }
        catch (OidcLoginCapacityException ex)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.Json(new { code = "too_many_logins", message = ex.Message }, statusCode: 429);
        }
        var url = await client.BuildAuthorizationUrlAsync(login.State, login.Nonce, ct).ConfigureAwait(false);
        return Results.Redirect(url);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext context, OidcClient client, OidcSessionStore store, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TinadecOidc");
        var query = context.Request.Query;
        var code = query["code"].ToString();
        var state = query["state"].ToString();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return Results.Json(new { code = "invalid_callback", message = "The authorization callback is missing code or state." }, statusCode: 400);
        var nonce = store.ConsumeNonce(state);
        if (nonce is null)
            return Results.Json(new { code = "invalid_state", message = "The login attempt has expired or was never started." }, statusCode: 400);

        (OidcIdentity Identity, OidcTokenSet Tokens)? result = null;
        try { result = await client.ExchangeAsync(code, nonce, ct).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogError(ex, "OIDC authorization-code exchange failed: {Message}", ex.Message); result = null; }

        if (result is null)
        {
            logger.LogWarning("OIDC exchange returned no identity (code rejected or id_token failed validation).");
            return Results.Json(new { code = "oidc_exchange_failed", message = "Logto rejected the authorization code or the id_token failed validation." }, statusCode: 401);
        }

        await store.EnsureLoadedAsync().ConfigureAwait(false);
        var session = await store.CreateSessionAsync(result.Value.Identity, result.Value.Tokens).ConfigureAwait(false);
        store.CompleteLogin(state, session);
        return Results.Content(SuccessPage, "text/html; charset=utf-8");
    }

    private static IResult CheckAsync(HttpContext context, OidcSessionStore store)
    {
        var session = store.CheckLogin(context.Request.Query["state"].ToString());
        return session is null
            ? Results.Json(new { status = "pending" })
            : Results.Json(new { status = "complete", session });
    }

    private static async Task<IResult> MeAsync(
        HttpContext context, OidcSessionStore store, OidcClient client, CancellationToken ct)
    {
        // A Desktop launch spawns a fresh Core: the session this request carries may only exist
        // on disk right now. Restoring also renews an expired OP token, which is what keeps the
        // refresh chain alive (Logto rotates it on every token request).
        var session = await OidcSessionRefresh
            .EnsureFreshAsync(store, client, context.Request.Headers[SessionHeader].ToString(), ct)
            .ConfigureAwait(false);
        if (session is null)
            return Results.Json(new { code = "unauthorized", message = "No active login session." }, statusCode: 401);
        return Results.Ok(new
        {
            session.Identity.Subject,
            session.Identity.Nickname,
            AvatarUrl = session.Identity.AvatarUrl,
            session.Identity.Email,
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
}