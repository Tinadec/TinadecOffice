using System.Security.Cryptography;
using System.Text.Json;
using TinadecCore.Persistence;

namespace TinadecCore.AspNetCore.Auth;

/// <summary>The identity Core hands back to the Desktop after a Logto login.</summary>
public sealed record OidcIdentity(string Subject, string Nickname, string? AvatarUrl, string? Email);

/// <summary>One token response: what Logto handed back, and when the access token stops working.</summary>
public sealed record OidcTokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>A live session as the auth endpoints see it: who it is, and what can still be done with it.</summary>
public sealed record OidcActiveSession(
    OidcIdentity Identity,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset AccessTokenExpiresAt);

/// <summary>On-disk shape. Token, identity and OP token travel together inside one encrypted blob.</summary>
internal sealed record OidcStoredSession(
    string Token,
    string Subject,
    string Nickname,
    string? AvatarUrl,
    string? Email,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string? RefreshToken = null,
    DateTimeOffset AccessTokenExpiresAt = default);

/// <summary>
/// Housekeeping for the OIDC login flow: pending logins (state → nonce) and active
/// sessions (opaque token → identity).
///
/// Sessions outlive the process. A Desktop launch spawns a fresh Core, so a process-scoped
/// session meant every launch asked the user to sign in again; the records are now written
/// through <see cref="ISecretStore"/> (DPAPI on Windows, an AES-GCM file on POSIX) and read
/// back on the first request. Pending logins stay in memory: a half-finished browser dance
/// is over in minutes and must not be resumable after a restart.
/// </summary>
public sealed class OidcSessionStore
{
    /// <summary>Only one blob: a desktop has one signed-in user, so enumeration beats a list API.</summary>
    private const string SecretReference = "oauth.sessions";

    /// <summary>
    /// Identity lifetime. There is no refresh token yet (the authorization request asks for
    /// `openid profile email`, not `offline_access`), so this is the longest a session can be
    /// honoured without sending the user back to the browser; 12 hours made that a daily chore.
    /// </summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Backstop for a caller that starts logins without ever completing them. Far above any
    /// real human concurrency; hitting it means the host is looping, so it is told to wait
    /// rather than being handed an unbounded dictionary.
    /// </summary>
    private const int MaxPendingLogins = 256;

    private sealed record PendingLogin(string Nonce, DateTimeOffset ExpiresAt);
    private sealed record CompletedLogin(string SessionToken, DateTimeOffset ExpiresAt);
    /// <summary>
    /// <paramref name="AccessToken"/> is Logto's opaque OP token, kept so the host can
    /// write the user's own profile through the Account API (<c>PATCH /api/my-account</c>).
    /// It never leaves the main process, and it is persisted only inside the encrypted store.
    /// <paramref name="RefreshToken"/> is what lets the identity session outlive the OP token.
    /// </summary>
    private sealed record ActiveSession(
        OidcIdentity Identity,
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset AccessTokenExpiresAt,
        DateTimeOffset ExpiresAt);

    private readonly ISecretStore? _secrets;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly Dictionary<string, PendingLogin> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompletedLogin> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveSession> _sessions = new(StringComparer.Ordinal);
    private Task? _loadTask;

    /// <param name="secrets">
    /// Null keeps the store memory-only, which is what tests and hosts without a writable
    /// secret store get: the flow still works, it just does not survive a restart.
    /// </param>
    /// <param name="clock">Injected so expiry can be exercised without waiting ten minutes.</param>
    public OidcSessionStore(ISecretStore? secrets = null, TimeProvider? clock = null)
    {
        _secrets = secrets;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads persisted sessions once. Every endpoint awaits this before touching the maps;
    /// after the first call it is a completed task, so the cost is a single dictionary check.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        lock (_sync) _loadTask ??= LoadAsync();
        return _loadTask;
    }

    private async Task LoadAsync()
    {
        if (_secrets is null) return;
        string? json;
        try
        {
            json = await _secrets.GetAsync(SecretReference).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An unreadable store must not break sign-in; it just starts this process empty.
            Console.Error.WriteLine($"[auth] could not read persisted sessions: {ex.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(json)) return;

        OidcStoredSession[]? stored;
        try { stored = JsonSerializer.Deserialize<OidcStoredSession[]>(json); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[auth] persisted sessions are unreadable: {ex.Message}");
            return;
        }
        if (stored is null) return;

        var now = _clock.GetUtcNow();
        lock (_sync)
        {
            foreach (var row in stored)
            {
                if (string.IsNullOrEmpty(row.Token) || row.ExpiresAt <= now) continue;
                _sessions[row.Token] = new ActiveSession(
                    new OidcIdentity(
                        row.Subject ?? string.Empty,
                        row.Nickname ?? string.Empty,
                        row.AvatarUrl,
                        row.Email),
                    row.AccessToken ?? string.Empty,
                    string.IsNullOrWhiteSpace(row.RefreshToken) ? null : row.RefreshToken,
                    row.AccessTokenExpiresAt,
                    row.ExpiresAt);
            }
        }
    }

    public (string State, string Nonce) BeginLogin()
    {
        var state = RandomToken();
        var nonce = RandomToken();
        lock (_sync)
        {
            SweepExpiredLocked(_clock.GetUtcNow());
            if (_pending.Count >= MaxPendingLogins) throw new OidcLoginCapacityException();
            _pending[state] = new PendingLogin(nonce, _clock.GetUtcNow() + LoginLifetime);
        }
        return (state, nonce);
    }

    public string? ConsumeNonce(string state)
    {
        lock (_sync)
        {
            var nonce = _pending.Remove(state, out var pending) && pending.ExpiresAt > _clock.GetUtcNow()
                ? pending.Nonce
                : null;
            SweepExpiredLocked(_clock.GetUtcNow());
            return nonce;
        }
    }

    /// <summary>
    /// Caller holds <see cref="_sync"/>. Both maps are written only by the login flow, which a
    /// human drives, so sweeping on every write covers each leak without a timer or a background
    /// job whose lifetime would have to be managed for a DI singleton.
    /// </summary>
    private void SweepExpiredLocked(DateTimeOffset now)
    {
        foreach (var key in _pending.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList())
            _pending.Remove(key);
        foreach (var key in _completed.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList())
            _completed.Remove(key);
    }

    internal int PendingLoginCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    internal int CompletedLoginCount
    {
        get { lock (_sync) return _completed.Count; }
    }

    public async Task<string> CreateSessionAsync(OidcIdentity identity, OidcTokenSet tokens)
    {
        var token = RandomToken();
        lock (_sync)
        {
            _sessions[token] = new ActiveSession(
                identity,
                tokens.AccessToken,
                tokens.RefreshToken,
                tokens.ExpiresAt,
                _clock.GetUtcNow() + SessionLifetime);
        }
        await PersistAsync().ConfigureAwait(false);
        return token;
    }

    /// <summary>
    /// Records a session token as the result of a login, keyed by the OIDC
    /// <c>state</c> an in-browser flow started with. The system browser cannot
    /// hand the token to Electron directly, so the desktop polls it back.
    /// </summary>
    public void CompleteLogin(string state, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(sessionToken)) return;
        lock (_sync)
        {
            SweepExpiredLocked(_clock.GetUtcNow());
            _completed[state] = new CompletedLogin(sessionToken, _clock.GetUtcNow() + LoginLifetime);
        }
    }

    /// <summary>One-time read: the first successful poll removes the entry to prevent replay.</summary>
    public string? CheckLogin(string state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        lock (_sync)
        {
            if (!_completed.Remove(state, out var completed)) return null;
            if (completed.ExpiresAt <= _clock.GetUtcNow()) return null;
            return completed.SessionToken;
        }
    }

    public OidcIdentity? GetIdentity(string token) => GetSession(token)?.Identity;

    /// <summary>The session's identity plus the Logto tokens needed to write it back.</summary>
    public OidcActiveSession? GetSession(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(token, out var session)) return null;
            if (session.ExpiresAt <= _clock.GetUtcNow())
            {
                _sessions.Remove(token);
                return null;
            }
            return new OidcActiveSession(session.Identity, session.AccessToken, session.RefreshToken, session.AccessTokenExpiresAt);
        }
    }

    /// <summary>
    /// Replaces the tokens after a successful refresh and slides the identity session forward:
    /// the 30-day window is "30 days without opening the app", not "30 days since the first
    /// login", otherwise a usable refresh token would still expire the user out.
    /// </summary>
    public async Task UpdateTokensAsync(string token, OidcTokenSet tokens)
    {
        var changed = false;
        lock (_sync)
        {
            if (_sessions.TryGetValue(token, out var session))
            {
                _sessions[token] = session with
                {
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken ?? session.RefreshToken,
                    AccessTokenExpiresAt = tokens.ExpiresAt,
                    ExpiresAt = _clock.GetUtcNow() + SessionLifetime,
                };
                changed = true;
            }
        }
        if (changed) await PersistAsync().ConfigureAwait(false);
    }

    /// <summary>Replaces the stored identity after a profile write, keeping the session alive.</summary>
    public async Task UpdateIdentityAsync(string token, OidcIdentity identity)
    {
        var changed = false;
        lock (_sync)
        {
            if (_sessions.TryGetValue(token, out var session))
            {
                _sessions[token] = session with { Identity = identity };
                changed = true;
            }
        }
        if (changed) await PersistAsync().ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(string token)
    {
        bool removed;
        lock (_sync) removed = !string.IsNullOrWhiteSpace(token) && _sessions.Remove(token);
        if (removed) await PersistAsync().ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Writes the live sessions through the encrypted store. Serialised so two logins cannot
    /// interleave a stale snapshot over a newer one; a failed write is logged and dropped —
    /// the session still works for this process, it just will not survive a restart.
    /// </summary>
    private async Task PersistAsync()
    {
        if (_secrets is null) return;
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            OidcStoredSession[] snapshot;
            lock (_sync)
            {
                var now = _clock.GetUtcNow();
                snapshot = _sessions
                    .Where(pair => pair.Value.ExpiresAt > now)
                    .Select(pair => new OidcStoredSession(
                        pair.Key,
                        pair.Value.Identity.Subject,
                        pair.Value.Identity.Nickname,
                        pair.Value.Identity.AvatarUrl,
                        pair.Value.Identity.Email,
                        pair.Value.AccessToken,
                        pair.Value.ExpiresAt,
                        pair.Value.RefreshToken,
                        pair.Value.AccessTokenExpiresAt))
                    .ToArray();
            }
            await _secrets.PutAsync(SecretReference, JsonSerializer.Serialize(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[auth] could not persist sessions: {ex.Message}");
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Too many sign-in attempts are outstanding. The host maps this to 429 with Retry-After:
/// it is a caller pacing problem, not a server fault.
/// </summary>
public sealed class OidcLoginCapacityException()
    : Exception("Too many sign-in attempts are in progress. Try again shortly.");
