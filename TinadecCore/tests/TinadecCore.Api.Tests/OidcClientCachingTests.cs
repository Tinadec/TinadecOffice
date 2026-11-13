using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TinadecCore.AspNetCore.Auth;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Every id_token verification used to re-fetch the JWKS, and discovery was cached forever, so
/// an OP that rotated its endpoints was never picked up. These tests count the actual HTTP
/// requests against a local fake issuer — no real OP is contacted.
/// </summary>
public sealed class OidcClientCachingTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeIssuer : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        public string Kid { get; set; } = "kid-1";
        public int DiscoveryCalls;
        public int JwksCalls;
        public bool DiscoveryUnavailable { get; set; }

        public string Jwks()
        {
            var parameters = _key.ExportParameters(false);
            return JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new { kty = "EC", crv = "P-384", kid = Kid, x = B64(parameters.Q.X!), y = B64(parameters.Q.Y!) }
                }
            });
        }

        public string IdToken(string nonce, string? kid = null)
        {
            var header = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "ES384", typ = "JWT", kid = kid ?? Kid })));
            var now = DateTimeOffset.UtcNow;
            var payload = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                iss = "https://issuer.test/oidc",
                aud = "cid",
                sub = "sub-1",
                nonce,
                iat = now.ToUnixTimeSeconds(),
                exp = now.AddHours(1).ToUnixTimeSeconds()
            })));
            var signature = B64(_key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA384));
            return $"{header}.{payload}.{signature}";
        }

        public void Dispose() => _key.Dispose();

        private static string B64(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class IssuerHandler(FakeIssuer issuer) : HttpMessageHandler
    {
        public string Nonce { get; set; } = "nonce-1";
        public string? TokenKid { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref issuer.DiscoveryCalls);
                if (issuer.DiscoveryUnavailable)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Json("""
                    {"authorization_endpoint":"https://issuer.test/oidc/auth",
                     "token_endpoint":"https://issuer.test/oidc/token",
                     "userinfo_endpoint":"https://issuer.test/oidc/me",
                     "jwks_uri":"https://issuer.test/oidc/jwks"}
                    """);
            }
            if (url.EndsWith("/oidc/jwks", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref issuer.JwksCalls);
                return Json(issuer.Jwks());
            }
            if (url.EndsWith("/oidc/token", StringComparison.Ordinal))
                return Json(JsonSerializer.Serialize(new
                {
                    access_token = "op-token",
                    refresh_token = "refresh-token",
                    expires_in = 3600,
                    token_type = "Bearer",
                    id_token = issuer.IdToken(Nonce, TokenKid)
                }));
            return Json("{}");
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private static OidcClient Client(IssuerHandler handler, ManualClock clock) => new(
        Options.Create(new TinadecOidcOptions
        {
            Authority = "https://issuer.test/oidc",
            ClientId = "cid",
            ClientSecret = "secret",
            RedirectUri = "http://127.0.0.1:48730/auth/callback",
        }),
        new HttpClient(handler),
        clock);

    [Fact]
    public async Task Repeated_verifications_fetch_the_JWKS_once()
    {
        using var issuer = new FakeIssuer();
        var handler = new IssuerHandler(issuer);
        var clock = new ManualClock();
        var client = Client(handler, clock);

        for (var i = 0; i < 3; i++)
        {
            var result = await client.ExchangeAsync("code", "nonce-1", CancellationToken.None);
            Assert.NotNull(result);
        }

        Assert.Equal(1, issuer.JwksCalls);
        Assert.Equal(1, issuer.DiscoveryCalls);
    }

    [Fact]
    public async Task An_unknown_kid_forces_one_refresh_and_then_rejects()
    {
        using var issuer = new FakeIssuer();
        var handler = new IssuerHandler(issuer);
        var client = Client(handler, new ManualClock());

        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        Assert.Equal(1, issuer.JwksCalls);

        handler.TokenKid = "rotated-away";
        Assert.Null(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        Assert.Equal(2, issuer.JwksCalls);
    }

    [Fact]
    public async Task Unknown_kids_cannot_drive_unlimited_JWKS_fetches()
    {
        using var issuer = new FakeIssuer();
        var handler = new IssuerHandler(issuer);
        var client = Client(handler, new ManualClock());

        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        handler.TokenKid = "unknown";
        for (var i = 0; i < 5; i++)
            Assert.Null(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));

        Assert.Equal(2, issuer.JwksCalls);
    }

    [Fact]
    public async Task A_rotated_key_is_picked_up_after_the_rate_limit_window()
    {
        using var issuer = new FakeIssuer();
        var handler = new IssuerHandler(issuer);
        var clock = new ManualClock();
        var client = Client(handler, clock);

        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        handler.TokenKid = "kid-2";
        Assert.Null(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(31));
        issuer.Kid = "kid-2";
        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        Assert.Equal(3, issuer.JwksCalls);
    }

    [Fact]
    public async Task Discovery_is_refetched_once_its_TTL_expires()
    {
        using var issuer = new FakeIssuer();
        var handler = new IssuerHandler(issuer);
        var clock = new ManualClock();
        var client = Client(handler, clock);

        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        Assert.Equal(1, issuer.DiscoveryCalls);

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        Assert.Equal(2, issuer.DiscoveryCalls);
    }

    [Fact]
    public async Task An_unreachable_OP_keeps_serving_the_last_discovery()
    {
        using var issuer = new FakeIssuer();
        var handler = new IssuerHandler(issuer);
        var clock = new ManualClock();
        var client = Client(handler, clock);

        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(61));
        issuer.DiscoveryUnavailable = true;

        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
    }

    [Fact]
    public async Task The_JWKS_cache_expires_on_its_own_TTL()
    {
        using var issuer = new FakeIssuer();
        var handler = new IssuerHandler(issuer);
        var clock = new ManualClock();
        var client = Client(handler, clock);

        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.NotNull(await client.ExchangeAsync("code", "nonce-1", CancellationToken.None));

        Assert.Equal(2, issuer.JwksCalls);
    }
}