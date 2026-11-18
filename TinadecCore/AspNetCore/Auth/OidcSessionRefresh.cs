namespace TinadecCore.AspNetCore.Auth;

/// <summary>
/// Keeps a restored session's access token usable.
///
/// The identity session is local, but anything that talks to Logto – writing the user's profile
/// – needs an OP token that lives about an hour. This is the one place that decides whether to
/// trade the refresh token for a new one, and it draws the line the whole login UX depends on:
/// only an explicit rejection from Logto (`invalid_grant`) signs the user out; a network failure
/// or a 5xx keeps the session and tries again next launch.
/// </summary>
public static class OidcSessionRefresh
{
    /// <summary>Refresh slightly early so a token cannot expire between the check and the call.</summary>
    private static readonly TimeSpan EarlyRefresh = TimeSpan.FromMinutes(1);

    /// <summary>
    /// One gate per session. Two windows starting together both call /auth/me; without this they
    /// would present the same refresh token twice, Logto's rotation would reject the second as
    /// reuse, and the user would be signed out by their own app opening.
    ///
    /// Taking and returning a gate happen under one lock. A reference count with a separate
    /// concurrent removal would let a newcomer take the same gate between the decrement to zero
    /// and the removal, and the removal would then delete a gate that is in use — exactly the
    /// double presentation this exists to prevent. The dictionary is bounded by the number of
    /// refreshes in flight, not by the number of tokens ever seen.
    /// </summary>
    private static readonly object GateLock = new();
    private static readonly Dictionary<string, Gate> RefreshGates = new(StringComparer.Ordinal);

    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    internal static int RefreshGateCount
    {
        get { lock (GateLock) return RefreshGates.Count; }
    }

    private static Gate AcquireGate(string sessionToken)
    {
        lock (GateLock)
        {
            if (!RefreshGates.TryGetValue(sessionToken, out var gate)) RefreshGates[sessionToken] = gate = new Gate();
            gate.References++;
            return gate;
        }
    }

    private static void ReleaseGate(string sessionToken, Gate gate, bool acquired)
    {
        // Release outside the lock so a waiter never queues behind dictionary work.
        if (acquired) gate.Semaphore.Release();
        lock (GateLock)
        {
            if (--gate.References == 0) RefreshGates.Remove(sessionToken);
        }
    }

    public static async Task<OidcActiveSession?> EnsureFreshAsync(
        OidcSessionStore store,
        OidcClient client,
        string sessionToken,
        CancellationToken ct)
    {
        await store.EnsureLoadedAsync().ConfigureAwait(false);
        var session = store.GetSession(sessionToken);
        if (session is null) return null;
        if (!NeedsRefresh(session) || string.IsNullOrWhiteSpace(session.RefreshToken)) return session;

        var gate = AcquireGate(sessionToken);
        var acquired = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            // Whoever held the gate before us may already have rotated the tokens.
            session = store.GetSession(sessionToken);
            if (session is null) return null;
            if (!NeedsRefresh(session) || string.IsNullOrWhiteSpace(session.RefreshToken)) return session;

            OidcTokenSet? refreshed;
            try
            {
                refreshed = await client.RefreshAsync(session.RefreshToken, ct).ConfigureAwait(false);
            }
            catch
            {
                // Logto unreachable or answered 5xx. The identity is still valid locally, so serve
                // it and leave the stored tokens alone; a real rejection is the only sign-out.
                return session;
            }

            if (refreshed is null)
            {
                // invalid_grant: the refresh chain is broken (reuse of a rotated token, revoke, or
                // expiry). This is the only outcome that drops the session.
                await store.RevokeAsync(sessionToken).ConfigureAwait(false);
                return null;
            }

            await store.UpdateTokensAsync(sessionToken, refreshed).ConfigureAwait(false);
            return session with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? session.RefreshToken,
                AccessTokenExpiresAt = refreshed.ExpiresAt,
            };
        }
        finally
        {
            ReleaseGate(sessionToken, gate, acquired);
        }
    }

    private static bool NeedsRefresh(OidcActiveSession session) =>
        session.AccessTokenExpiresAt <= DateTimeOffset.UtcNow + EarlyRefresh;
}
