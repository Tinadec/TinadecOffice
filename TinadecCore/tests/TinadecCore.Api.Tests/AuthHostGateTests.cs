using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// /auth/* used to sit outside the host gate entirely, so any local web page could read the
/// login state and resolve an identity cross-origin. The gate now covers it, with one exemption:
/// the browser's callback cannot carry a private header and validates a single-use state instead.
///
/// The gate only runs when storage scopes are enabled, so this fixture must use managed storage —
/// the default isolated factory does not register the middleware at all.
/// </summary>
public sealed class AuthHostGateTests : IAsyncLifetime
{
    private const string Token = "auth-gate-test-token-0123456789abcdef0123456789abcdef";
    private readonly string _root = ApiTestStorage.CreateRoot("auth-gate");
    private string? _previousToken;
    private Factory? _factory;

    public Task InitializeAsync()
    {
        _previousToken = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN");
        Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", Token);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", _previousToken);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    public static TheoryData<string, string> GatedRoutes => new()
    {
        { "GET", "/auth/begin" },
        { "GET", "/auth/login" },
        { "GET", "/auth/check?state=unknown" },
        { "GET", "/auth/me" },
        { "POST", "/auth/logout" },
        { "PATCH", "/auth/profile" },
    };

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task WithoutTheHostCredential_EveryAuthRouteIsRefused(string method, string path)
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");

        var response = await SendAsync(client, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("host_authorization_required", body.GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task WithAWrongHostCredential_EveryAuthRouteIsRefused(string method, string path)
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");
        client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", new string('x', Token.Length));

        var response = await SendAsync(client, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("host_authorization_required", body.GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task WithTheHostCredential_TheRouteAnswersWithItsOwnSemantics(string method, string path)
    {
        using var client = _factory!.CreateClient();

        var response = await SendAsync(client, method, path);

        // Not 403: the gate let it through. The route's own answer depends on OIDC being
        // disabled here, which is asserted separately below rather than pinned per route.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithTheHostCredential_BeginReportsThatOidcIsNotConfigured()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync("/auth/begin");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("oidc_not_configured", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheBrowserCallbackStaysReachableWithoutTheCredential()
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");

        var response = await client.GetAsync("/auth/callback");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_callback", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/api/v1/host-challenge")]
    public async Task ThePublicApiEndpointsStayPublic(string path)
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");

        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path) => method switch
    {
        "GET" => client.GetAsync(path),
        "POST" => client.PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json")),
        "PATCH" => client.PatchAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json")),
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override bool UsesManagedStorage => true;
        protected override string? ManagedUserRoot => Path.Combine(root, "user");
        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", Token);
        }
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Enabled is computed from these four fields, so setting "Enabled=false" does
                // nothing. Clearing them is what actually keeps every test off the real OP.
                ["TinadecOidc:Authority"] = "",
                ["TinadecOidc:ClientId"] = "",
                ["TinadecOidc:ClientSecret"] = "",
                ["TinadecOidc:RedirectUri"] = "",
                ["Logging:LogLevel:Default"] = "Warning",
            }));
    }
}