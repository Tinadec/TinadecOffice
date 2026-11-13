using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using TinadecCore.AspNetCore.Auth;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Two leaks that only show up after the app has been used for a while: the pending/completed
/// login maps were never swept (only the session map was), and every session token that ever
/// needed a refresh left a SemaphoreSlim behind forever. The gate tests also pin the race that
/// a reference count with a separate concurrent removal would have: a newcomer taking the gate
/// between the decrement to zero and the removal, then having that gate deleted underneath it.
/// </summary>
public sealed class OidcSessionLifecycleTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string> PutAsync(string secretReference, string value, CancellationToken cancellationToken = default)
        {
            Values[secretReference] = value;
            return Task.FromResult(secretReference);
        }

        public Task<bool> ExistsAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.ContainsKey(secretReference));

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(secretReference, out var value) ? value : null);

        public Task DeleteAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            Values.Remove(secretReference);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int TokenCalls;
        public Func<Task>? BeforeToken { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                return Json("""
                    {"authorization_endpoint":"https://issuer.test/oidc/auth",
                     "token_endpoint":"https://issuer.test/oidc/token",
                     "userinfo_endpoint":"https://issuer.test/oidc/me",
                     "jwks_uri":"https://issuer.test/oidc/jwks"}
                    """);
            Interlocked.Increment(ref TokenCalls);
            if (BeforeToken is { } gate) await gate().ConfigureAwait(false);
            return Json("""{"access_token":"new-at","refresh_token":"new-rt","expires_in":3600,"token_type":"Bearer"}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static readonly OidcIdentity Identity = new("sub-1", "Ada", "https://avatar", "ada@example.com");

    private static OidcClient Client(CountingHandler handler) => new(
        Options.Create(new TinadecOidcOptions
        {
            Authority = "https://issuer.test/oidc",
            ClientId = "cid",
            ClientSecret = "secret",
            RedirectUri = "http://127.0.0.1:48730/auth/callback",
        }),
        new HttpClient(handler));

    private static OidcTokenSet ExpiredTokens() =>
        new("old-at", "old-rt", DateTimeOffset.UtcNow.AddMinutes(-5));

    // ---- pending / completed sweep ----

    [Fact]
    public void Expired_pending_and_completed_logins_are_swept_on_the_next_write()
    {
        var clock = new ManualClock();
        var store = new OidcSessionStore(null, clock);
        var states = Enumerable.Range(0, 5).Select(_ => store.BeginLogin().State).ToArray();
        foreach (var state in states) store.CompleteLogin(state, "session-token");
        Assert.Equal(5, store.PendingLoginCount);
        Assert.Equal(5, store.CompletedLoginCount);

        clock.Advance(TimeSpan.FromMinutes(11));
        store.BeginLogin();

        Assert.Equal(1, store.PendingLoginCount);
        Assert.Equal(0, store.CompletedLoginCount);
        foreach (var state in states)
        {
            Assert.Null(store.ConsumeNonce(state));
            Assert.Null(store.CheckLogin(state));
        }
    }

    [Fact]
    public void Unexpired_logins_survive_the_sweep()
    {
        var clock = new ManualClock();
        var store = new OidcSessionStore(null, clock);
        var (state, nonce) = store.BeginLogin();
        store.CompleteLogin(state, "session-token");

        clock.Advance(TimeSpan.FromMinutes(9));
        store.BeginLogin();

        Assert.Equal(2, store.PendingLoginCount);
        Assert.Equal(1, store.CompletedLoginCount);
        Assert.Equal(nonce, store.ConsumeNonce(state));
        Assert.Equal("session-token", store.CheckLogin(state));
    }

    [Fact]
    public void An_entry_expiring_exactly_now_is_swept()
    {
        var clock = new ManualClock();
        var store = new OidcSessionStore(null, clock);
        var (state, _) = store.BeginLogin();
        store.CompleteLogin(state, "session-token");

        clock.Advance(TimeSpan.FromMinutes(10));
        store.BeginLogin();

        Assert.Equal(1, store.PendingLoginCount);
        Assert.Equal(0, store.CompletedLoginCount);
        Assert.Null(store.ConsumeNonce(state));
    }

    [Fact]
    public async Task Sweeping_logins_does_not_touch_the_identity_session()
    {
        var clock = new ManualClock();
        var store = new OidcSessionStore(null, clock);
        var token = await store.CreateSessionAsync(Identity, new OidcTokenSet("op", "refresh", clock.GetUtcNow().AddHours(1)));

        clock.Advance(TimeSpan.FromMinutes(11));
        store.BeginLogin();

        Assert.NotNull(store.GetSession(token));
    }

    [Fact]
    public void Pending_logins_are_capped_so_a_looping_caller_cannot_grow_the_map()
    {
        var store = new OidcSessionStore(null, new ManualClock());
        for (var i = 0; i < 256; i++) store.BeginLogin();
        Assert.Equal(256, store.PendingLoginCount);
        Assert.Throws<OidcLoginCapacityException>(() => store.BeginLogin());
    }

    [Fact]
    public void Expired_entries_free_capacity_before_the_cap_is_applied()
    {
        var clock = new ManualClock();
        var store = new OidcSessionStore(null, clock);
        for (var i = 0; i < 256; i++) store.BeginLogin();
        clock.Advance(TimeSpan.FromMinutes(11));
        store.BeginLogin();
        Assert.Equal(1, store.PendingLoginCount);
    }

    // ---- refresh gate lifecycle ----

    [Fact]
    public async Task Two_concurrent_refreshes_of_one_token_present_it_once()
    {
        var store = new OidcSessionStore(new MemorySecretStore());
        var token = await store.CreateSessionAsync(Identity, ExpiredTokens());
        var handler = new CountingHandler();
        var client = Client(handler);

        await Task.WhenAll(
            OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None),
            OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None));

        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(0, OidcSessionRefresh.RefreshGateCount);
    }

    [Fact]
    public async Task Gates_do_not_accumulate_across_repeated_refresh_rounds()
    {
        var store = new OidcSessionStore(new MemorySecretStore());
        var token = await store.CreateSessionAsync(Identity, ExpiredTokens());
        var handler = new CountingHandler();
        var client = Client(handler);

        // Local smoke only; the mutual-exclusion invariant is exercised at >=1000 rounds in CI.
        for (var round = 0; round < 100; round++)
        {
            await store.UpdateTokensAsync(token, ExpiredTokens());
            await Task.WhenAll(
                OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None),
                OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None));
            Assert.Equal(0, OidcSessionRefresh.RefreshGateCount);
        }
        Assert.Equal(100, handler.TokenCalls);
    }

    [Fact]
    public async Task A_thousand_distinct_tokens_leave_no_gate_behind()
    {
        var secrets = new MemorySecretStore();
        var store = new OidcSessionStore(secrets);
        var handler = new CountingHandler();
        var client = Client(handler);

        for (var i = 0; i < 1000; i++)
        {
            var token = await store.CreateSessionAsync(Identity, ExpiredTokens());
            await OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None);
        }

        Assert.Equal(0, OidcSessionRefresh.RefreshGateCount);
        Assert.Equal(1000, handler.TokenCalls);
    }

    [Fact]
    public async Task A_cancelled_wait_returns_the_gate_without_releasing_a_semaphore_it_never_took()
    {
        var store = new OidcSessionStore(new MemorySecretStore());
        var token = await store.CreateSessionAsync(Identity, ExpiredTokens());
        var handler = new CountingHandler();
        var client = Client(handler);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OidcSessionRefresh.EnsureFreshAsync(store, client, token, cancelled.Token));
        Assert.Equal(0, OidcSessionRefresh.RefreshGateCount);

        // If the cancelled path had released the semaphore it never acquired, the gate would
        // now admit two callers at once and Logto would see the refresh token presented twice.
        await store.UpdateTokensAsync(token, ExpiredTokens());
        await Task.WhenAll(
            OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None),
            OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None));
        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(0, OidcSessionRefresh.RefreshGateCount);
    }

    [Fact]
    public async Task A_waiter_that_arrives_while_the_gate_is_held_still_shares_it()
    {
        var store = new OidcSessionStore(new MemorySecretStore());
        var token = await store.CreateSessionAsync(Identity, ExpiredTokens());
        var handler = new CountingHandler();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        handler.BeforeToken = async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        var client = Client(handler);

        var first = OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = OidcSessionRefresh.EnsureFreshAsync(store, client, token, CancellationToken.None);
        Assert.Equal(1, OidcSessionRefresh.RefreshGateCount);

        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(0, OidcSessionRefresh.RefreshGateCount);
    }
}