using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinadecCore.AspNetCore.Auth;
using TinadecCore.AspNetCore.Endpoints;

namespace TinadecCore.Api.Tests;

public sealed class WebAuthAndBrowseTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("web-auth-browse");
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FilesystemBrowse_ListsDirectoriesRootsAndHandlesFiltersAndMissingPath()
    {
        var sampleDir = Path.Combine(_root, "workspace-sample");
        var subAlpha = Path.Combine(sampleDir, "alpha-repo");
        var subBeta = Path.Combine(sampleDir, "beta-dir");
        Directory.CreateDirectory(Path.Combine(subAlpha, ".git"));
        Directory.CreateDirectory(Path.Combine(subBeta, "nested"));
        await File.WriteAllTextAsync(Path.Combine(sampleDir, "readme.txt"), "hello");

        using var client = _factory!.CreateClient();

        // Default directory listing hides files and annotates git repo + children
        var dirResp = await client.GetAsync($"/api/v1/fs/browse?path={Uri.EscapeDataString(sampleDir)}");
        Assert.Equal(HttpStatusCode.OK, dirResp.StatusCode);
        var dirBody = await dirResp.Content.ReadFromJsonAsync<FilesystemBrowseResponseDto>();
        Assert.NotNull(dirBody);
        Assert.Equal(Path.GetFullPath(sampleDir), dirBody!.CurrentPath);
        Assert.NotEmpty(dirBody.Roots);
        Assert.Equal(2, dirBody.Entries.Count);
        Assert.Equal("alpha-repo", dirBody.Entries[0].Name);
        Assert.True(dirBody.Entries[0].IsGitRepo);
        Assert.True(dirBody.Entries[0].HasChildren);
        Assert.Equal("beta-dir", dirBody.Entries[1].Name);
        Assert.False(dirBody.Entries[1].IsGitRepo);

        // include_files=true returns files after directories
        var filesResp = await client.GetAsync($"/api/v1/fs/browse?path={Uri.EscapeDataString(sampleDir)}&include_files=true&limit=2");
        Assert.Equal(HttpStatusCode.OK, filesResp.StatusCode);
        var filesBody = await filesResp.Content.ReadFromJsonAsync<FilesystemBrowseResponseDto>();
        Assert.NotNull(filesBody);
        Assert.True(filesBody!.Truncated);
        Assert.Equal(2, filesBody.Entries.Count);

        // __roots__ sentinel lists logical drives/roots cleanly
        var rootsResp = await client.GetAsync("/api/v1/fs/browse?path=__roots__");
        Assert.Equal(HttpStatusCode.OK, rootsResp.StatusCode);
        var rootsBody = await rootsResp.Content.ReadFromJsonAsync<FilesystemBrowseResponseDto>();
        Assert.NotNull(rootsBody);
        Assert.Equal(string.Empty, rootsBody!.CurrentPath);
        Assert.NotEmpty(rootsBody.Roots);

        // Missing path returns 404 directory_not_found
        var missingResp = await client.GetAsync($"/api/v1/fs/browse?path={Uri.EscapeDataString(Path.Combine(_root, "does-not-exist"))}");
        Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);
    }

    [Fact]
    public async Task FilesystemMkdirAndMedia_EnforcesTraversalCollisionAndStrictMediaAllowlist()
    {
        var baseDir = Path.Combine(_root, "fs-picker-wave1");
        Directory.CreateDirectory(baseDir);

        // Minimal 1x1 PNG header/bytes + disallowed SVG/HTML files
        var pngPath = Path.Combine(baseDir, "wallpaper.png");
        await File.WriteAllBytesAsync(pngPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var svgPath = Path.Combine(baseDir, "vector.svg");
        await File.WriteAllTextAsync(svgPath, "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        var htmlPath = Path.Combine(baseDir, "page.html");
        await File.WriteAllTextAsync(htmlPath, "<html></html>");

        using var client = _factory!.CreateClient();

        // 1. POST /api/v1/fs/mkdir: 200 creation, 409 collision, 400 traversal/separator rejection, 404 missing parent
        var mkdirOk = await client.PostAsJsonAsync("/api/v1/fs/mkdir", new { parent_path = baseDir, name = "new-workspace" });
        Assert.Equal(HttpStatusCode.OK, mkdirOk.StatusCode);
        var mkdirBody = await mkdirOk.Content.ReadFromJsonAsync<CreateHostDirectoryResponseDto>();
        Assert.NotNull(mkdirBody);
        Assert.Equal(Path.Combine(baseDir, "new-workspace"), mkdirBody!.CreatedPath);
        Assert.True(Directory.Exists(mkdirBody.CreatedPath));
        Assert.True(mkdirBody.Entry.IsDir);

        var mkdirConflict = await client.PostAsJsonAsync("/api/v1/fs/mkdir", new { parent_path = baseDir, name = "new-workspace" });
        Assert.Equal(HttpStatusCode.Conflict, mkdirConflict.StatusCode);

        foreach (var badName in new[] { "..", ".", "nested/escape", "nested\\escape", "   " })
        {
            var badResp = await client.PostAsJsonAsync("/api/v1/fs/mkdir", new { parent_path = baseDir, name = badName });
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
        }

        var missingParent = await client.PostAsJsonAsync("/api/v1/fs/mkdir", new { parent_path = Path.Combine(_root, "missing-parent"), name = "child" });
        Assert.Equal(HttpStatusCode.NotFound, missingParent.StatusCode);

        // 2. GET /api/v1/fs/media: 200 on .png with nosniff + range header, 403 on .svg/.html, 400 on malformed token, 404 on missing file
        static string EncodeToken(string path) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var pngResp = await client.GetAsync($"/api/v1/fs/media?token={EncodeToken(pngPath)}");
        Assert.Equal(HttpStatusCode.OK, pngResp.StatusCode);
        Assert.Equal("image/png", pngResp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", pngResp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("bytes", pngResp.Headers.AcceptRanges);

        var svgResp = await client.GetAsync($"/api/v1/fs/media?token={EncodeToken(svgPath)}");
        Assert.Equal(HttpStatusCode.Forbidden, svgResp.StatusCode);

        var htmlResp = await client.GetAsync($"/api/v1/fs/media?token={EncodeToken(htmlPath)}");
        Assert.Equal(HttpStatusCode.Forbidden, htmlResp.StatusCode);

        var badTokenResp = await client.GetAsync("/api/v1/fs/media?token=%%%not-base64%%%");
        Assert.Equal(HttpStatusCode.BadRequest, badTokenResp.StatusCode);

        var relTokenResp = await client.GetAsync($"/api/v1/fs/media?token={EncodeToken("relative/wallpaper.png")}");
        Assert.Equal(HttpStatusCode.BadRequest, relTokenResp.StatusCode);

        var missingMediaResp = await client.GetAsync($"/api/v1/fs/media?token={EncodeToken(Path.Combine(baseDir, "missing.png"))}");
        Assert.Equal(HttpStatusCode.NotFound, missingMediaResp.StatusCode);
    }

    [Fact]
    public async Task AuthEndpoints_SupportForwardedPrincipalHeadersAndWebOidcAllowlists()
    {
        using var client = _factory!.CreateClient();

        // Unauthenticated when neither X-Tinadec-Session nor X-User-Id is sent
        var unauthMe = await client.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthMe.StatusCode);
        var unauthPrincipal = await client.GetAsync("/auth/principal");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthPrincipal.StatusCode);

        // Forwarded BFF principal headers resolve /auth/principal and /auth/me
        using var req = new HttpRequestMessage(HttpMethod.Get, "/auth/principal");
        req.Headers.Add(AuthEndpoints.UserIdHeader, "owner-123");
        req.Headers.Add(AuthEndpoints.TenantIdHeader, "tenant-home");
        req.Headers.Add(AuthEndpoints.RolesHeader, "owner,admin");
        req.Headers.Add(AuthEndpoints.AuthMethodHeader, "local_password");

        var principalResp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, principalResp.StatusCode);
        var principalJson = await principalResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(principalJson.GetProperty("authenticated").GetBoolean());
        Assert.Equal("owner-123", principalJson.GetProperty("user_id").GetString());
        Assert.Equal("tenant-home", principalJson.GetProperty("tenant_id").GetString());
        Assert.Equal("local_password", principalJson.GetProperty("auth_method").GetString());

        using var meReq = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        meReq.Headers.Add(AuthEndpoints.UserIdHeader, "owner-123");
        var meResp = await client.SendAsync(meReq);
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var meJson = await meResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("owner-123", meJson.GetProperty("subject").GetString());

        // Web OIDC redirect_uri override + allowlist binding + pending state metadata
        var store = _factory.Services.GetRequiredService<OidcSessionStore>();
        var (state, nonce) = store.BeginLogin(
            redirectUri: "https://chat.example.com/api/v1/web-auth/oidc/callback",
            returnTo: "/settings",
            mode: "web",
            allowedSubjects: ["sub-owner"],
            allowedEmails: ["owner@example.com"]);
        var pending = store.ConsumePendingLogin(state);
        Assert.NotNull(pending);
        Assert.Equal(nonce, pending!.Nonce);
        Assert.Equal("https://chat.example.com/api/v1/web-auth/oidc/callback", pending.RedirectUri);
        Assert.Equal("/settings", pending.ReturnTo);
        Assert.Equal("web", pending.Mode);

        var opts = new TinadecOidcOptions
        {
            Authority = "https://auth.example.com/oidc",
            ClientId = "cid",
            ClientSecret = "sec",
            RedirectUri = "http://127.0.0.1:48730/auth/callback",
            AllowedSubjects = ["sub-owner"],
            AllowedEmails = ["Owner@Example.com"]
        };
        Assert.True(opts.IsAllowedIdentity(new OidcIdentity("sub-owner", "Owner", null, "other@example.com")));
        Assert.True(opts.IsAllowedIdentity(new OidcIdentity("sub-other", "Owner", null, "owner@example.com")));
        Assert.False(opts.IsAllowedIdentity(new OidcIdentity("sub-stranger", "Stranger", null, "stranger@example.com")));

        // Verify OidcClient honors dynamic redirectUriOverride in authorization URL
        using var discoveryHttp = new HttpClient(new FakeDiscoveryHandler());
        var oidcClient = new OidcClient(Options.Create(opts), discoveryHttp);
        var authUrl = await oidcClient.BuildAuthorizationUrlAsync(
            "st-1",
            "nonce-1",
            "https://chat.example.com/auth/callback",
            CancellationToken.None);
        Assert.Contains(Uri.EscapeDataString("https://chat.example.com/auth/callback"), authUrl);
    }

    private sealed class FakeDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"authorization_endpoint":"https://auth.example.com/oidc/auth","token_endpoint":"https://auth.example.com/oidc/token","userinfo_endpoint":"https://auth.example.com/oidc/me","jwks_uri":"https://auth.example.com/oidc/jwks"}""",
                    Encoding.UTF8,
                    "application/json")
            });
    }

    private sealed class Factory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
