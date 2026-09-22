using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The market read surface. Before this, <c>GET /market/sources</c> and <c>GET /market/catalog</c>
/// were stubs answering <c>[]</c>, and the desktop rendered that as "the market has nothing" on
/// every machine — the same phantom <see cref="McpInventoryApiTests"/> closed for MCP, except that
/// here the facts existed nowhere and had to be built.
///
/// Every assertion below is chosen around one question: when a read comes back short or empty, can
/// a reader tell what actually happened? A refused row, a blocked fetch, a half-fetched listing,
/// and an empty market are four different facts, and the old stubs collapsed all four into
/// <c>[]</c>.
/// </summary>
public sealed class MarketCatalogApiTests : IAsyncLifetime
{
    private const string RegistryLocation = "https://registry.example.com/v0/servers";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tinadec-market-tests", Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program>? _factory;

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

    private MarketProvider Provider => ((Factory)_factory!).Provider;

    // ─────────────────────────────────────────────────────────────────────────────
    // Sources
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sources_AnswerWithAnEnvelope_AndNameTheKindsThisBuildCanRead()
    {
        // The stub answered a bare []. It is an envelope now because supported_kinds has to travel
        // with the list: a picker that guesses which kinds work will offer one that 400s.
        var body = await GetJsonAsync("/api/v1/market/sources");

        Assert.Equal(JsonValueKind.Array, body.GetProperty("sources").ValueKind);
        Assert.Empty(body.GetProperty("sources").EnumerateArray());
        Assert.Equal("mcp_registry", Assert.Single(body.GetProperty("supported_kinds").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("http://registry.example.com/v0/servers")]
    [InlineData("https://registry.example.com/v0/servers?page=2")]
    [InlineData("https://registry.example.com/v0/servers#frag")]
    [InlineData("https://user:pass@registry.example.com/v0/servers")]
    [InlineData("file:///c:/temp/market.json")]
    public async Task CreatingASource_RefusesALocationCoreCouldNotBuildASafeRequestFrom(string location)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/market/sources", new
        {
            name = "bad-" + Math.Abs(location.GetHashCode()),
            kind = "mcp_registry",
            location,
        });

        // Each of these would put something in the request that Core's own builder did not
        // compose: plaintext transport, a preset query, a fragment, a credential, or a local file.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
        Assert.Contains("location", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreatingASource_RefusesAKindWithNoAdapter_AndListsWhatWorks()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/market/sources", new
        {
            name = "skills",
            kind = "skill_repository",
            location = "https://example.com/skills",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("unsupported_market_source_kind", body.GetProperty("code").GetString());
        Assert.Contains("mcp_registry", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TwoSourcesWithOneNameAreRefused_BecauseTheNameIsTheHandle()
    {
        await CreateSourceAsync("once");

        var again = await Client.PostAsJsonAsync("/api/v1/market/sources", new
        {
            name = "once",
            kind = "mcp_registry",
            location = RegistryLocation,
        });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("market_source_exists", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DisablingASourceBlocksRefresh_AndReEnablingClearsIt()
    {
        var offId = await CreateSourceAsync("off", enabled: false);

        var refused = await Client.PostAsync($"/api/v1/market/sources/{offId}/refresh", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var code = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement
            .GetProperty("code").GetString();
        Assert.Equal("market_source_disabled", code);

        // Refused before the egress decision, not after: a disabled source must not reach out.
        Assert.Empty(Provider.Urls);

        var patch = await Client.PatchAsJsonAsync($"/api/v1/market/sources/{offId}", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.True(JsonDocument.Parse(await patch.Content.ReadAsStringAsync()).RootElement
            .GetProperty("enabled").GetBoolean());

        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/b", "1.0.0"))));
        var refreshed = await Client.PostAsync($"/api/v1/market/sources/{offId}/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Single(Provider.Urls);
    }

    [Fact]
    public async Task TogglingWithoutTheFieldDoesNotSwitchTheSourceOff()
    {
        var id = await CreateSourceAsync("toggle");

        var response = await Client.PatchAsJsonAsync($"/api/v1/market/sources/{id}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var sources = await GetJsonAsync("/api/v1/market/sources");
        Assert.True(sources.GetProperty("sources")[0].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task DeletingASourceTakesItsRows_WithoutLeavingAnOrphanPage()
    {
        var id = await CreateSourceAsync("doomed");
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            S("io.github.a/ok", "1.0.0"), S("io.github.c/ok", "1.0.0"))));
        await RefreshAsync(id);

        Assert.Equal(2, (await CatalogAsync()).GetProperty("total_available").GetInt32());

        var deleted = await Client.DeleteAsync($"/api/v1/market/sources/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(0, (await CatalogAsync()).GetProperty("total_available").GetInt32());
        Assert.Empty((await GetJsonAsync("/api/v1/market/sources")).GetProperty("sources").EnumerateArray());

        // A second delete is 404 rather than a quiet success, so a client cannot read a typo as
        // "already gone" when nothing of theirs was ever here.
        var again = await Client.DeleteAsync($"/api/v1/market/sources/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Refresh
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_FetchesThroughTheProviderAndStoresWhatItSaw()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            S("io.github.foo/mcp", "1.2.3", "Foo", "Searches foo."),
            S("io.github.bar/mcp", "0.1.0", "Bar"))));

        var outcome = await RefreshAsync(id);
        Assert.Equal("fetched", outcome.GetProperty("outcome").GetString());
        Assert.Equal(2, outcome.GetProperty("fetched_rows").GetInt32());
        Assert.Equal(0, outcome.GetProperty("refused_rows").GetInt32());
        Assert.Equal(1, outcome.GetProperty("pages_fetched").GetInt32());
        Assert.False(outcome.GetProperty("truncated_pages").GetBoolean());
        Assert.False(outcome.TryGetProperty("reason", out _));
        Assert.True(outcome.TryGetProperty("refreshed_at", out _));

        // The fetch is a control-tool call: no run owns it, and no approval is claimed for it.
        var url = Assert.Single(Provider.Urls);
        Assert.Equal($"{RegistryLocation}?limit=100", url);
        var call = Assert.Single(Provider.Requests);
        Assert.Equal("#fetch", call.ToolId);
        Assert.False(call.Approved);

        var sources = await GetJsonAsync("/api/v1/market/sources");
        var row = sources.GetProperty("sources")[0];
        Assert.Equal(2, row.GetProperty("entry_count").GetInt32());
        Assert.True(row.TryGetProperty("last_refreshed_at", out _));
        Assert.False(row.TryGetProperty("last_error", out _));

        var catalog = await CatalogAsync();
        Assert.Equal(2, catalog.GetProperty("total_available").GetInt32());
        Assert.True(catalog.TryGetProperty("as_of", out _));
        var first = catalog.GetProperty("items")[0];
        Assert.Equal("io.github.bar/mcp", first.GetProperty("extension_id").GetString());
        Assert.Equal("mcp-server", first.GetProperty("kind").GetString());
        Assert.Equal("Bar", first.GetProperty("display_name").GetString());
        Assert.Equal(id, first.GetProperty("source_id").GetString());
        Assert.Equal("registry", first.GetProperty("source_name").GetString());
        Assert.Equal(64, first.GetProperty("manifest_hash").GetString()!.Length);
    }

    [Fact]
    public async Task ARowWithoutAVersionIsRefusedAndCounted_NotStoredAndNotMentioned()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            S("io.github.pin/ok", "1.0.0"), S("io.github.noversion/ok", null), S(null, "1.0.0"))));

        var outcome = await RefreshAsync(id);

        // "Install this, version unpinned" is not an actionable claim, and a nameless row cannot
        // be clicked. Both are refused — but counted, because fetched_rows alone would read as a
        // market of one rather than a listing of three with two rejects.
        Assert.Equal(1, outcome.GetProperty("fetched_rows").GetInt32());
        Assert.Equal(2, outcome.GetProperty("refused_rows").GetInt32());

        var catalog = await CatalogAsync();
        Assert.Equal(1, catalog.GetProperty("total_available").GetInt32());
        Assert.Equal("io.github.pin/ok", catalog.GetProperty("items")[0].GetProperty("extension_id").GetString());
    }

    [Fact]
    public async Task Refresh_FollowsTheCursor_AndSaysWhenTheCeilingCutItShort()
    {
        var id = await CreateSourceAsync("registry");
        for (var page = 0; page < MarketRefreshPolicy.MaxListingPages + 3; page++)
            Provider.Replies.Enqueue(Wire.Ok(Listing([S($"io.github.p{page}/ok", "1.0.0")], $"cursor-{page}")));

        var outcome = await RefreshAsync(id);

        Assert.Equal(MarketRefreshPolicy.MaxListingPages, outcome.GetProperty("pages_fetched").GetInt32());
        Assert.True(outcome.GetProperty("truncated_pages").GetBoolean());
        Assert.Equal(MarketRefreshPolicy.MaxListingPages, outcome.GetProperty("fetched_rows").GetInt32());

        // The cursor is data Core passes through, not something it re-derives.
        Assert.Equal($"{RegistryLocation}?limit=100", Provider.Urls[0]);
        Assert.Equal($"{RegistryLocation}?limit=100&cursor=cursor-0", Provider.Urls[1]);
    }

    [Fact]
    public async Task APartialListingNeverDeletesRowsItDidNotSee()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            S("io.github.a/ok", "1.0.0"), S("io.github.b/ok", "1.0.0"), S("io.github.c/ok", "1.0.0"))));
        await RefreshAsync(id);

        // Second pass: the page ceiling is hit after one row. B and C are past the prefix, which is
        // not evidence they vanished — the registry simply was not read that far.
        for (var page = 0; page < MarketRefreshPolicy.MaxListingPages; page++)
            Provider.Replies.Enqueue(Wire.Ok(Listing([S("io.github.a/ok", "1.0.0")], "again")));

        var outcome = await RefreshAsync(id);
        Assert.True(outcome.GetProperty("truncated_pages").GetBoolean());
        Assert.Equal(0, outcome.GetProperty("removed_rows").GetInt32());

        Assert.Equal(3, (await CatalogAsync()).GetProperty("total_available").GetInt32());
    }

    [Fact]
    public async Task ACompleteListingThatDropsARowReportsTheRemoval()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            S("io.github.keep", "1.0.0"), S("io.github.drop", "1.0.0"))));
        await RefreshAsync(id);

        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.keep", "1.0.0"))));
        var outcome = await RefreshAsync(id);

        Assert.Equal(1, outcome.GetProperty("fetched_rows").GetInt32());
        Assert.Equal(1, outcome.GetProperty("removed_rows").GetInt32());
        Assert.Equal(1, (await CatalogAsync()).GetProperty("total_available").GetInt32());
    }

    [Fact]
    public async Task ABlockedFetchKeepsTheOldRows_AndStoresWhy()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/ok", "1.0.0"))));
        var first = await RefreshAsync(id);
        var firstStamp = first.GetProperty("refreshed_at").GetString();

        Provider.Replies.Enqueue(Wire.Blocked("the target address is not allowed"));
        var second = await RefreshAsync(id);

        Assert.Equal("blocked", second.GetProperty("outcome").GetString());
        Assert.Contains("not allowed", second.GetProperty("reason").GetString());
        Assert.Equal(0, second.GetProperty("fetched_rows").GetInt32());
        Assert.True(second.TryGetProperty("reason", out _));

        Assert.Equal(1, (await CatalogAsync()).GetProperty("total_available").GetInt32());

        // last_refreshed_at still holds the last time it actually worked, and the reason is on the
        // row: a later reader who missed the failure can still see the catalog is stale and why.
        var row = (await GetJsonAsync("/api/v1/market/sources")).GetProperty("sources")[0];
        Assert.Equal(firstStamp, row.GetProperty("last_refreshed_at").GetString());
        Assert.Contains("not allowed", row.GetProperty("last_error").GetString());
    }

    [Fact]
    public async Task AProviderThatNeverAnsweredIsUnavailable_NotAnEmptyMarket()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/ok", "1.0.0"))));
        await RefreshAsync(id);

        Provider.Throw = new InvalidOperationException("TinadecTools executable path is not configured.");
        var outcome = await RefreshAsync(id);

        Assert.Equal("unavailable", outcome.GetProperty("outcome").GetString());
        Assert.Contains("not configured", outcome.GetProperty("reason").GetString());
        Assert.Equal(1, (await CatalogAsync()).GetProperty("total_available").GetInt32());
    }

    [Fact]
    public async Task AGarbageBodyIsNotStoredAsAnEmptyMarket()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/ok", "1.0.0"))));
        await RefreshAsync(id);

        foreach (var body in new[] { "not json at all", """{"unexpected":true}""", "[]", """{"servers":"ten"}""" })
        {
            Provider.Replies.Enqueue(Wire.Ok(body));
            var outcome = await RefreshAsync(id);

            // A page that parses to nothing is indistinguishable from an empty market if the code
            // lets it be one, so each shape has to come back as its own named failure.
            Assert.Equal("unavailable", outcome.GetProperty("outcome").GetString());
            Assert.False(string.IsNullOrEmpty(outcome.GetProperty("reason").GetString()));
            Assert.Equal(1, (await CatalogAsync()).GetProperty("total_available").GetInt32());
        }
    }

    [Fact]
    public async Task ASuccessFlagFromTheProviderIsNotEnough_TheBodyCarriesTheOutcome()
    {
        var id = await CreateSourceAsync("registry");

        // #fetch answering success:false with no body is a failed fetch even though the wire call
        // itself succeeded; reading it as "fetched 0 rows" would report an emptied market.
        Provider.Replies.Enqueue(Wire.Failed("HTTP 503 from registry.example.com"));
        var outcome = await RefreshAsync(id);

        Assert.Equal("unavailable", outcome.GetProperty("outcome").GetString());
        Assert.Contains("503", outcome.GetProperty("reason").GetString());
        Assert.Equal(0, (await GetJsonAsync("/api/v1/market/sources")).GetProperty("sources")[0]
            .GetProperty("entry_count").GetInt32());
    }

    [Fact]
    public async Task RefreshingAnUnknownSourceIs404_AndNothingIsFetched()
    {
        var response = await Client.PostAsync($"/api/v1/market/sources/{Guid.NewGuid():N}/refresh", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("market_source_not_found",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Empty(Provider.Urls);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Identity, projection, paging
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASurvivingEntryKeepsItsRowIdAcrossRefreshes_AndItsHashTracksTheBody()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/ok", "1.0.0", "A", "first"))));
        await RefreshAsync(id);
        var before = Assert.Single((await CatalogAsync()).GetProperty("items").EnumerateArray()).Clone();
        var catalogId = before.GetProperty("catalog_id").GetString();
        var hash = before.GetProperty("manifest_hash").GetString();

        // Same fields, different document order, plus a member Core never reads. A metadata digest
        // that moved with key order would look like a new release every time the registry changed
        // its serializer — and M2 is supposed to pin versions on this value.
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            [S("io.github.a/ok", "1.0.0", "A", "first")], cursor: null, reordered: true)));
        await RefreshAsync(id);
        var same = Assert.Single((await CatalogAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(catalogId, same.GetProperty("catalog_id").GetString());
        Assert.Equal(hash, same.GetProperty("manifest_hash").GetString());

        // Now the content really changes: same row, different fingerprint.
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/ok", "1.0.0", "A", "second"))));
        await RefreshAsync(id);
        var changed = Assert.Single((await CatalogAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(catalogId, changed.GetProperty("catalog_id").GetString());
        Assert.NotEqual(hash, changed.GetProperty("manifest_hash").GetString());
    }

    [Fact]
    public async Task TheEntryProjectionCarriesOnlyWhatTheSourceActuallySaid()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            S("io.github.a/ok", "1.0.0", homepage: "https://github.com/a/ok",
                registryType: "npm", packageName: "@a/ok", transports: ["stdio", "streamable-http"]))));
        await RefreshAsync(id);

        var item = Assert.Single((await CatalogAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal("https://github.com/a/ok", item.GetProperty("homepage").GetString());
        Assert.Equal("npm", item.GetProperty("registry_type").GetString());
        Assert.Equal(["stdio", "streamable-http"], item.GetProperty("transports").EnumerateArray()
            .Select(x => x.GetString()).ToArray());

        // No publisher, capabilities, permissions, or install status: the registry publishes none
        // of those, and the desktop used to declare all four as if something did.
        foreach (var absent in new[] { "publisher", "capabilities", "permissions", "installed_extension_id", "status" })
            Assert.False(item.TryGetProperty(absent, out _), $"'{absent}' must not appear — nothing sources it.");

        // A description the source never gave stays absent rather than becoming "".
        Assert.False(item.TryGetProperty("description", out _));
    }

    [Fact]
    public async Task CatalogPagingIsHonoured_AndSaysThereAreMore()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(
            S("io.github.one/x", "1.0.0"), S("io.github.two/x", "1.0.0"), S("io.github.three/x", "1.0.0"))));
        await RefreshAsync(id);

        var page = await CatalogAsync("limit=2");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page.GetProperty("total_available").GetInt32());
        Assert.True(page.GetProperty("has_more").GetBoolean());

        var rest = await CatalogAsync("limit=2&offset=2");
        Assert.Equal(1, rest.GetProperty("items").GetArrayLength());
        Assert.False(rest.GetProperty("has_more").GetBoolean());

        // Ordering is by the source's own id, and the two pages must cover it exactly once.
        var both = page.GetProperty("items").EnumerateArray()
            .Concat(rest.GetProperty("items").EnumerateArray())
            .Select(x => x.GetProperty("extension_id").GetString())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["io.github.one/x", "io.github.three/x", "io.github.two/x"], both);
    }

    [Fact]
    public async Task WildcardsInAQueryAreData_NotAPattern()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("ab", "1.0.0"), S("a%b", "1.0.0"), S("axb", "1.0.0"))));
        await RefreshAsync(id);

        // Measured by mutation: with the escape applied, q="%" returns 1 row; with it removed,
        // the same request returned 3. (SQLite's LIKE treats "%" as "any run" and "_" as "any one
        // character", so an unescaped query widens instead of narrowing — the opposite of a search
        // box.) Only this one test turned red under that mutation; the other 28 stayed green.
        Assert.Equal(1, (await CatalogAsync("q=%25")).GetProperty("total_available").GetInt32());
        Assert.Equal(0, (await CatalogAsync("q=a%5Fb")).GetProperty("total_available").GetInt32());
        Assert.Equal(1, (await CatalogAsync("q=%25b")).GetProperty("total_available").GetInt32());
        Assert.Equal(3, (await CatalogAsync()).GetProperty("total_available").GetInt32());
        Assert.Equal(1, (await CatalogAsync("q=ax")).GetProperty("total_available").GetInt32());
    }

    [Fact]
    public async Task AFilterValueThatCannotMatchIsRejected_NotAnsweredWithAnEmptyPage()
    {
        await CreateSourceAsync("registry");

        var kind = await Client.GetAsync("/api/v1/market/catalog?kind=mcp");
        Assert.Equal(HttpStatusCode.BadRequest, kind.StatusCode);
        var body = JsonDocument.Parse(await kind.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
        Assert.Contains("mcp-server", body.GetProperty("message").GetString());

        foreach (var path in new[] { "?limit=0", "?limit=abc", "?offset=-1", "?offset=x", "?source_id=nope" })
        {
            var bad = await Client.GetAsync("/api/v1/market/catalog" + path);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("invalid_request",
                JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task OneEntryById_AndAnUnknownIdAnswersWithItsOwnCode()
    {
        var id = await CreateSourceAsync("registry");
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/ok", "1.0.0", "A", "reads things"))));
        await RefreshAsync(id);
        var catalogId = (await CatalogAsync()).GetProperty("items")[0].GetProperty("catalog_id").GetString();

        var found = await GetJsonAsync($"/api/v1/market/catalog/{catalogId}");
        Assert.Equal("io.github.a/ok", found.GetProperty("extension_id").GetString());
        Assert.Equal("reads things", found.GetProperty("description").GetString());
        Assert.Equal("registry", found.GetProperty("source_name").GetString());

        // A source from another tenant's view of the world: filtered out, and indistinguishable
        // from never having existed.
        var missing = await Client.GetAsync($"/api/v1/market/catalog/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("market_entry_not_found",
            JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        var malformed = await Client.GetAsync("/api/v1/market/catalog/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task CatalogCanNarrowToOneSource_AndAnUnusedSourceAnswersEmpty()
    {
        var mine = await CreateSourceAsync("mine");
        var other = await CreateSourceAsync("other");
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.mine/ok", "1.0.0"))));
        await RefreshAsync(mine);

        var filtered = await CatalogAsync($"source_id={other:N}");
        Assert.Equal(0, filtered.GetProperty("total_available").GetInt32());
        Assert.False(filtered.TryGetProperty("as_of", out _));

        Assert.Equal(1, (await CatalogAsync($"source_id={mine}")).GetProperty("total_available").GetInt32());
    }

    [Fact]
    public async Task TheInstallSurfaceIsStillRefused_NotPretendImplemented()
    {
        // M1 stores claims; it does not act on them. A 501 is the honest answer here, and this
        // test exists so that a future "quick" stub cannot make an empty 200 look like a market.
        foreach (var path in new[]
                 {
                     "/api/v1/extensions/install-preview",
                     "/api/v1/extensions/install",
                     "/api/v1/extensions/abc/enable",
                     "/api/v1/extensions/abc/disable",
                     "/api/v1/extensions/abc/update",
                 })
        {
            var response = await Client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        }

        var uninstall = await Client.DeleteAsync("/api/v1/extensions/abc");
        Assert.Equal(HttpStatusCode.NotImplemented, uninstall.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private HttpClient Client => _factory!.CreateClient();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Client.GetAsync(path);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, $"GET {path}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<string> CreateSourceAsync(string name, bool enabled = true)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/market/sources", new
        {
            name,
            kind = "mcp_registry",
            location = RegistryLocation,
            enabled,
        });

        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, $"POST /api/v1/market/sources ({name})");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> RefreshAsync(string sourceId)
    {
        var response = await Client.PostAsync($"/api/v1/market/sources/{sourceId}/refresh", null);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, $"POST refresh ({sourceId})");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<JsonElement> CatalogAsync(string query = "") =>
        await GetJsonAsync("/api/v1/market/catalog" + (query.Length == 0 ? string.Empty : "?" + query));

    private sealed record Server(
        string? Name,
        string? Version,
        string? Title = null,
        string? Description = null,
        string? Homepage = null,
        string? RegistryType = null,
        string? PackageName = null,
        string[]? Transports = null);

    private static Server S(
        string? name,
        string? version,
        string? title = null,
        string? description = null,
        string? homepage = null,
        string? registryType = null,
        string? packageName = null,
        string[]? transports = null) =>
        new(name, version, title, description, homepage, registryType, packageName, transports);

    private static string Listing(params Server[] servers) => Listing(servers, null, false);

    private static string Listing(
        Server[] servers,
        string? cursor = null,
        bool reordered = false,
        string uninterestingMember = "publisher_id")
    {
        // The registry v0 shape: every row wraps its descriptor in "server", and pagination rides
        // on metadata.nextCursor. Built through JsonNode rather than string interpolation, so the
        // escaping is the document's and not this test's guess at it.
        var rows = new JsonArray();
        foreach (var server in servers)
        {
            var descriptor = new JsonObject();
            void Add(string key, string? value)
            {
                if (value is not null)
                    descriptor[key] = value;
            }

            // Only the member order changes between the two passes; the content is identical.
            // Nothing obliges the registry to emit the same order twice, and a metadata digest that
            // tracked document order would look like a new release on every serializer change.
            Add(uninterestingMember, "never read by Core");
            if (reordered)
            {
                Add("description", server.Description);
                Add("version", server.Version);
                Add("title", server.Title);
                Add("name", server.Name);
            }
            else
            {
                Add("name", server.Name);
                Add("version", server.Version);
                Add("title", server.Title);
                Add("description", server.Description);
            }

            if (server.Homepage is not null)
                descriptor["repository"] = new JsonObject { ["url"] = server.Homepage };

            if (server.Transports is { Length: > 0 })
            {
                descriptor["remotes"] = new JsonArray(
                    server.Transports.Select(t => (JsonNode)new JsonObject
                    {
                        ["type"] = t,
                        ["url"] = "https://host.example/mcp",
                    }).ToArray());
            }

            if (server.RegistryType is not null)
            {
                descriptor["packages"] = new JsonArray(
                    (JsonNode)new JsonObject
                    {
                        ["registry_type"] = server.RegistryType,
                        ["name"] = server.PackageName,
                        ["version"] = server.Version,
                        ["transport_type"] = server.Transports is { Length: > 0 } ? server.Transports[0] : null,
                    });
            }

            rows.Add(new JsonObject { ["server"] = descriptor });
        }

        var root = new JsonObject { ["servers"] = rows };
        if (cursor is not null)
            root["metadata"] = new JsonObject { ["nextCursor"] = cursor, ["count"] = 100 };

        return root.ToJsonString();
    }

    /// <summary>
    /// What the provider puts on the wire for <c>#fetch</c>, keyed for keyed against
    /// <c>RawFetchResult</c> in the tool process — including the keys that carry nothing, because a
    /// fake that omitted <c>blocked</c> would let Core invent a friendlier answer than the one it
    /// actually has to read.
    /// </summary>
    private static class Wire
    {
        internal static Reply Ok(string body) => new()
        {
            Success = true,
            Body = body,
            Status = 200,
            MediaType = "application/json",
        };

        internal static Reply Blocked(string reason) => new()
        {
            Success = false,
            Blocked = true,
            Error = reason,
        };

        internal static Reply Failed(string reason) => new()
        {
            Success = false,
            Error = reason,
            Status = 503,
            MediaType = "application/json",
        };

        internal sealed record Reply
        {
            internal bool Success { get; init; }
            internal bool Blocked { get; init; }
            internal string? Error { get; init; }
            internal string? Body { get; init; }
            internal int Status { get; init; }
            internal string? MediaType { get; init; }

            internal ToolWireResponseDto ToWire(string url)
            {
                // #fetch answers at the wire level even when the fetch itself was refused: the
                // distinction lives inside the payload, exactly as it does for the real tool.
                var payload = new JsonObject
                {
                    ["success"] = Success,
                    ["error"] = Error,
                    ["blocked"] = Blocked,
                    ["url"] = url,
                    ["status_code"] = Status,
                    ["media_type"] = MediaType,
                    ["body"] = Body,
                    ["truncated"] = false,
                    ["byte_count"] = Body?.Length ?? 0,
                    ["declared_bytes"] = -1,
                };
                return new ToolWireResponseDto
                {
                    IsSuccess = true,
                    Result = JsonDocument.Parse(payload.ToJsonString()).RootElement,
                };
            }
        }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;

        public MarketProvider Provider { get; } = new();

        public Factory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(_root, "workspace"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(Provider);
            });
        }
    }

    /// <summary>
    /// Stands in for the tool process. Replies are queued per call, so a test that asks for three
    /// pages and queues two gets a failure rather than a silent repeat of the last answer — a
    /// provider that runs out of answers is not an empty market.
    /// </summary>
    private sealed class MarketProvider : IToolProvider
    {
        internal Queue<Wire.Reply> Replies { get; } = new();
        internal List<string> Urls { get; } = [];
        internal List<ToolWireRequestDto> Requests { get; } = [];
        internal Exception? Throw { get; set; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolManifestDto());

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolManifestDto());

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Throw is { } failure) throw failure;

            Assert.Equal("#fetch", request.ToolId);
            Assert.NotNull(timeout);
            Assert.True(timeout > TimeSpan.Zero, "A market refresh feeds an HTTP request; it must be bounded.");

            var url = request.Params is { } p && p.TryGetProperty("url", out var named)
                ? named.GetString() ?? string.Empty
                : string.Empty;
            Urls.Add(url);

            Assert.True(Replies.Count > 0, $"The test queued no answer for {url}.");
            return Task.FromResult(Replies.Dequeue().ToWire(url));
        }
    }
}
