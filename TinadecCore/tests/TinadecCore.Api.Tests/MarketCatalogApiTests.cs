using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Skills;

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

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Two of the tests below hand the workspace to a real tool process, and a child keeps its
        // working directory held for a moment after the host stops. Same retry the other
        // real-process tests use (ToolChainEndpointTests) — a leftover temp directory is not worth
        // a failure that names neither the market nor the behaviour under test.
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
        }
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
        var kinds = body.GetProperty("supported_kinds").EnumerateArray()
            .Select(x => x.GetString()!).ToArray();
        Assert.Equal(["mcp_registry", "skill_repository"], kinds);
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
        // M4 is the kind with no adapter yet. The point of the answer is that a picker is told
        // which kinds exist *now*, so the example has to be one this build genuinely cannot read.
        var response = await Client.PostAsJsonAsync("/api/v1/market/sources", new
        {
            name = "clis",
            kind = "cli_runtime",
            location = "https://example.com/runtimes",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("unsupported_market_source_kind", body.GetProperty("code").GetString());
        Assert.Contains("mcp_registry", body.GetProperty("message").GetString());
        Assert.Contains("skill_repository", body.GetProperty("message").GetString());
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
        // The catalog row is a claim, and the claim's shape decides whether an install can be
        // offered at all. A row with a package is offerable; the flag is derived, not stored twice.
        Assert.True(item.GetProperty("installable").GetBoolean());
        Assert.False(item.TryGetProperty("install_blocker", out var blocker)
            && blocker.ValueKind == JsonValueKind.String);
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

    // ─────────────────────────────────────────────────────────────────────────────
    // Installation: a proposal, then one governed write a human approves
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APreviewNamesTheExactVersionItPins_AndTheFileItWouldWrite()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/files", "1.2.3", "@ac/files-server"));
        var project = await CreateProjectAsync("install-target");

        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = project });

        Assert.Equal("npx", proposal.GetProperty("command").GetString());
        var args = proposal.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal(new[] { "-y", "@ac/files-server@1.2.3" }, args);
        Assert.Equal("1.2.3", proposal.GetProperty("version").GetString());

        // The target is the provider's own answer, and it is absolute: a relative path would be a
        // second rule about where files go.
        var target = proposal.GetProperty("target_path").GetString()!;
        Assert.True(Path.IsPathRooted(target), $"target_path should be absolute, got '{target}'.");
        Assert.EndsWith("mcp_servers.json", target);
        Assert.Equal(Path.Combine(_root, "workspace"), Path.GetDirectoryName(target));

        // Nothing is conditioned on an overwrite until a config actually exists to overwrite.
        // Core's JSON omits a null rather than writing it, so "no precondition" is absence.
        Assert.False(proposal.TryGetProperty("expected_file_hash", out _),
            "a create must not claim an overwrite precondition");
        Assert.Contains("@ac/files-server@1.2.3", proposal.GetProperty("content").GetString()!);
        Assert.True(proposal.GetProperty("expires_at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [Fact]
    public async Task APinnedArgumentCarriesThePackageVersion_NotAWordForWhateverIsLatest()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/g", "2.0.0", "@ac/g-server"));

        var content = (await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("pin") })).GetProperty("content").GetString()!;

        Assert.DoesNotContain("latest", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{version}", content, StringComparison.Ordinal);
        Assert.Contains("2.0.0", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryWithNoPackageIsNotInstallable_AndSaysWhy_WithoutAProposal()
    {
        var source = await CreateSourceAsync("registry");
        // A remote-only server is a real server; it is simply not one this tool layer can start.
        var entry = await RefreshedEntryAsync(source, S("ac.remote/only", "1.0.0", transports: ["streamable-http"]));

        var listed = await CatalogAsync("q=ac.remote/only");
        var row = listed.GetProperty("items")[0];
        Assert.False(row.GetProperty("installable").GetBoolean());
        Assert.Contains("no package", row.GetProperty("install_blocker").GetString(), StringComparison.OrdinalIgnoreCase);

        var response = await Client.PostAsJsonAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("remote") });
        await _factory!.AssertStatusAsync(response, HttpStatusCode.Conflict, "preview of a remote-only entry");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("market_install_not_expressible", body.GetProperty("code").GetString());
    }

    [Theory]
    // Every one of these is text from outside that would reach a command line. The whitelist is
    // what makes an approved proposal mean something, so it is tested at the boundary it guards.
    [InlineData("pkg; rm -rf /")]
    [InlineData("pkg && curl evil")]
    [InlineData("pkg|tee")]
    [InlineData("pkg$(id)")]
    [InlineData(@"pkg\path")]
    [InlineData("pkg>out")]
    [InlineData("pkg\nname")]
    public async Task APackageNameThatCannotBeSafeOnACommandLineIsRefused(string identifier)
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/odd", "1.0.0", identifier));

        var listed = await CatalogAsync("q=@ac/odd");
        Assert.False(listed.GetProperty("items")[0].GetProperty("installable").GetBoolean());

        var response = await Client.PostAsJsonAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("unsafe") });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The stored row is still readable: refusing to install is not refusing to display.
        Assert.Equal("@ac/odd", listed.GetProperty("items")[0].GetProperty("extension_id").GetString());
    }

    [Fact]
    public async Task AProviderWhoseConfigLivesOutsideTheProjectIsRefused_NotWrittenSomewherePlausible()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/x", "1.0.0", "@ac/x-server"));
        var project = await CreateProjectAsync("outside");

        Provider.ConfigPath = Path.Combine(Path.GetTempPath(), "not-the-project", "mcp_servers.json");

        var response = await Client.PostAsJsonAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = project });
        await _factory!.AssertStatusAsync(response, HttpStatusCode.Conflict, "preview with an outside config path");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("market_install_target_unresolved", body.GetProperty("code").GetString());
        Assert.Contains("not-the-project", body.GetProperty("message").GetString());
        Provider.ConfigPath = null;
    }

    [Fact]
    public async Task ApplyingAProposalQueuesOneGovernedWrite_AndWritesNothingItself()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/q", "1.0.0", "@ac/q-server"));
        var project = await CreateProjectAsync("queued");
        var target = Path.Combine(_root, "workspace", "mcp_servers.json");

        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview", new { project_id = project });
        var applied = await PostAsync($"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply");

        var actionId = applied.GetProperty("install_action_id").GetString()!;
        Assert.NotEmpty(actionId);
        Assert.Equal("installing", applied.GetProperty("state").GetString());

        // The whole point of routing through the user action path: nothing has happened yet.
        var action = await GetJsonAsync("/api/v1/user/tool-actions/" + actionId);
        Assert.Equal("write_file", action.GetProperty("tool_id").GetString());
        Assert.True(action.GetProperty("requires_approval").GetBoolean());
        Assert.False(File.Exists(target), "apply must not write the file; a human has to approve it first.");

        // One proposal applies to one action, no matter how often the button is pressed.
        var applyPath = $"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply";
        var again = await PostAsync(applyPath);
        Assert.Equal(actionId, again.GetProperty("install_action_id").GetString());
    }

    [Fact]
    public async Task ReApplyingAConsumedProposalAnswersWithTheSameAction_NotASecondApproval()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/twice", "1.0.0", "@ac/twice-server"));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("twice") });
        var applyPath = $"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply";

        var first = await PostAsync(applyPath);
        Provider.Requests.Clear();

        // A client that retried because its response was lost gets the same answer, not an error
        // that would send it to look for an approval that was never duplicated.
        var second = await PostAsync(applyPath);
        Assert.Equal(first.GetProperty("install_action_id").GetString(), second.GetProperty("install_action_id").GetString());
        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.DoesNotContain(Provider.Requests, r => r.ToolId == "write_file");

        // That list route answers with a bare array, so "one decision waiting" is one element.
        // A governed write stops at the permission request before it stops at the approval
        // envelope, and a re-apply must not add a second row for either gate.
        var pending = await GetJsonAsync("/api/v1/user/tool-actions?status=awaiting_user");
        Assert.Single(pending.EnumerateArray());
    }

    [Fact]
    public async Task ARefreshThatRepublishesTheEntryInvalidatesAPendingProposal()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/moved", "1.0.0", "@ac/moved-server"));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("moved") });

        // The source changes its mind about this exact row while the proposal is pending.
        Provider.Replies.Enqueue(Wire.Ok(Listing(Pkg("@ac/moved", "1.1.0", "@ac/moved-server"))));
        await RefreshAsync(source);

        var response = await Client.PostAsync(
            $"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply", null);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.PreconditionFailed, "apply after the market moved");
        Assert.Contains("no longer listed",
            (JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement).GetProperty("message").GetString());
    }

    /// <summary>
    /// The one invariant M2 buys with a proposal: the bytes an approval will authorize are the bytes
    /// a human read. Everything else here is a preview detail. This test reaches into the store
    /// because no public route can produce the condition — which is exactly the point: a proposal is
    /// meant to be immutable once handed out, so the only way to break that promise is below the API.
    /// </summary>
    [Fact]
    public async Task AProposalWhoseFrozenBytesMovedIsRefused_NotAppliedOnTheReviewersBehalf()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/tampered", "1.0.0", "@ac/tampered-server"));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("tampered") });
        var proposalId = Guid.Parse(proposal.GetProperty("id").GetString()!);

        await using (var db = await ((Factory)_factory!).Services
                .GetRequiredService<IDbContextFactory<IntegrationDbContext>>()
                .CreateDbContextAsync())
        {
            var row = await db.InstallProposals.FirstAsync(x => x.Id == proposalId);
            var document = JsonNode.Parse(row.Content)!.AsObject();
            document["servers"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "extra",
                ["command"] = "an-executable-nobody-reviewed",
            });
            row.Content = document.ToJsonString();
            await db.SaveChangesAsync();
        }

        Provider.Requests.Clear();
        var response = await Client.PostAsync($"/api/v1/market/install-proposals/{proposalId:N}/apply", null);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.PreconditionFailed, "apply of edited bytes");
        var message = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("message").GetString();
        Assert.Contains("digest", message);
        Assert.DoesNotContain(Provider.Requests, r => r.ToolId == "write_file");

        // Written down, not just reported once: the row stays refused rather than becoming applyable
        // for a client that retries after the first answer was lost.
        var retry = await Client.PostAsync($"/api/v1/market/install-proposals/{proposalId:N}/apply", null);
        await _factory!.AssertStatusAsync(retry, HttpStatusCode.PreconditionFailed, "retry of an edited proposal");
    }

    [Fact]
    public async Task AnInstalledEntrySurvivesAListingThatDroppedIt_AndSaysItWasRetained()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/keep", "1.0.0", "@ac/keep-server"));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("keep") });
        await PostAsync($"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply");

        // The next listing no longer mentions it. An uninstalled row would be deleted; this one is
        // what a human approved by version, and the record of that has to outlive the listing.
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("other.server", "9.9.9"))));
        var refreshed = await RefreshAsync(source);
        Assert.Equal(0, refreshed.GetProperty("removed_rows").GetInt32());
        Assert.Equal(1, refreshed.GetProperty("retained_rows").GetInt32());

        var listed = await CatalogAsync("q=@ac/keep");
        var row = listed.GetProperty("items")[0];
        Assert.True(row.GetProperty("expires_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow,
            "a retained row must not keep claiming to be current");

        var deletion = await Client.DeleteAsync($"/api/v1/market/sources/{source}");
        await _factory!.AssertStatusAsync(deletion, HttpStatusCode.Conflict, "deleting a source that still backs an install");
        Assert.Equal("market_source_in_use",
            JsonDocument.Parse(await deletion.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        var installations = await GetJsonAsync("/api/v1/market/installations");
        Assert.Single(installations.GetProperty("installations").EnumerateArray());
    }

    [Fact]
    public async Task AnUninstallProposalTakesTheEntryBackOut_AndLeavesEveryOtherServerAlone()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/rm", "2.1.0", "@ac/rm-server"));
        var project = await CreateProjectAsync("remover");

        // The second entry is what Core's own install wrote: the config id is a slug of the
        // extension id, not the package name, and this is the config as it would read afterwards.
        var existing = """
            {"servers":[{"id":"someone-elses","name":"Kept","command":"docker","args":["x"],"custom":{"a":1}},{"id":"ac-rm","name":"@ac/rm","command":"npx","args":["-y","@ac/rm-server@2.1.0"]}]}
            """;
        Provider.QueueConfig(existing, "sha256:abc");
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview", new { project_id = project });
        var content = proposal.GetProperty("content").GetString()!;

        // The unknown key and the untouched entry both have to survive: Core edits a document, it
        // does not re-serialize the tool layer's config through its own idea of what belongs.
        Assert.Contains("someone-elses", content);
        Assert.Contains("custom", content);
        Assert.Contains(@"""a"": 1", content);
        Assert.Equal("sha256:abc", proposal.GetProperty("expected_file_hash").GetString());

        var applied = await PostAsync($"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply");
        var installationId = applied.GetProperty("id").GetString()!;

        Provider.QueueConfig(content, "sha256:after-install");
        var removal = await PreviewAsync($"/api/v1/market/installations/{installationId}/uninstall-preview", body: null);
        var removed = removal.GetProperty("content").GetString()!;
        Assert.DoesNotContain("\"id\": \"ac-rm\"", removed);
        Assert.Contains("someone-elses", removed);
        Assert.Equal("uninstall", removal.GetProperty("action").GetString());
    }

    [Fact]
    public async Task AnInstallForAProjectInTheWrongWorkspaceIsRefused()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/ws", "1.0.0", "@ac/ws-server"));

        var response = await Client.PostAsJsonAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = Guid.NewGuid().ToString("N") });
        await _factory!.AssertStatusAsync(response, HttpStatusCode.NotFound, "preview for an unknown project");
        Assert.Equal("market_install_project_not_found",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApplyingAProposalWithoutAPreviewIsRefused_AndAnUnknownProposalIdNamesItself()
    {
        var response = await Client.PostAsync($"/api/v1/market/install-proposals/{Guid.NewGuid():N}/apply", null);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.NotFound, "apply with no such proposal");
        Assert.Equal("market_install_proposal_not_found",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheOlderPackageSpellingStillProjects_AndStillPins()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Legacy(Pkg("@ac/old", "3.4.5", "@ac/old-server")));

        var listed = await CatalogAsync("q=@ac/old");
        var row = listed.GetProperty("items")[0];
        Assert.Equal("npm", row.GetProperty("registry_type").GetString());
        Assert.Contains("stdio", row.GetProperty("transports").EnumerateArray().Select(t => t.GetString()));
        Assert.True(row.GetProperty("installable").GetBoolean());

        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("legacy") });
        Assert.Equal(new[] { "-y", "@ac/old-server@3.4.5" },
            proposal.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());
    }

    [Fact]
    public async Task TheEnvListCarriesNamesAndFlags_AndNeverAValue()
    {
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/env", "1.0.0", "@ac/env-server"));

        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("env") });
        var environment = proposal.GetProperty("environment")[0];
        Assert.Equal("API_KEY", environment.GetProperty("name").GetString());
        Assert.True(environment.GetProperty("required").GetBoolean());
        Assert.True(environment.GetProperty("secret").GetBoolean());

        // The proposal is the last place a secret could be smuggled in from a listing, and the
        // bytes a human approves must not be the place where a value appears.
        Assert.DoesNotContain(@"""value""", proposal.GetProperty("content").GetString()!);
        Assert.Contains("API_KEY", string.Join(',', (proposal.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()).ToArray())));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Skill repositories: an index of names, one document each, and a write that lands
    // where the workspace's own skill loader looks.
    // ─────────────────────────────────────────────────────────────────────────────

    private const string SkillIndexLocation = "https://skills.example.com/catalog/index.json";
    private const string SkillSource = "https://skills.example.com/catalog/pdf-forms/SKILL.md";

    [Fact]
    public async Task ASkillIndexListsItsRows_AndReadsNoDocumentToDone()
    {
        Provider.Replies.Enqueue(Wire.Ok(SkillIndex(
            new SkillRow("pdf-forms", "1.2.0", Description: "Fill PDF forms"),
            new SkillRow("git-triage"))));

        var source = await CreateSkillSourceAsync();
        var result = await RefreshAsync(source);

        Assert.Equal(2, result.GetProperty("fetched_rows").GetInt32());
        Assert.Equal(0, result.GetProperty("refused_rows").GetInt32());

        // One request for the whole market. A 400-skill repository must not cost 401 fetches to
        // answer a list call, and the document is not needed to decide what is worth reading.
        Assert.Single(Provider.Urls);
        Assert.Equal(SkillIndexLocation, Provider.Urls[0]);

        var page = await CatalogAsync();
        var row = page.GetProperty("items").EnumerateArray()
            .First(x => x.GetProperty("extension_id").GetString() == "pdf-forms");

        Assert.Equal("skill", row.GetProperty("kind").GetString());
        Assert.Equal("1.2.0", row.GetProperty("version").GetString());
        Assert.True(row.GetProperty("installable").GetBoolean());

        // A row with no version is still a row: for a skill the pin is the document's bytes, not a
        // string in the index, so the listing has nothing to refuse.
        var bare = page.GetProperty("items").EnumerateArray()
            .First(x => x.GetProperty("extension_id").GetString() == "git-triage");
        Assert.Equal("unversioned", bare.GetProperty("version").GetString());
    }

    [Fact]
    public async Task ARowWhoseNameIsNotASkillNameIsCounted_AndNeverStored()
    {
        Provider.Replies.Enqueue(Wire.Ok(SkillIndex(
            new SkillRow("ok-skill"),
            new SkillRow("Bad_Name"),
            new SkillRow("has space"),
            new SkillRow("nested/dir"),
            new SkillRow(null))));

        var source = await CreateSkillSourceAsync();
        var result = await RefreshAsync(source);

        // Stored only if it could be a directory name, because that single rule is what makes the
        // identifier safe to put into a path and a URL. The rest is a count, not a silence.
        Assert.Equal(1, result.GetProperty("fetched_rows").GetInt32());
        Assert.Equal(4, result.GetProperty("refused_rows").GetInt32());

        var page = await CatalogAsync();
        Assert.Equal("ok-skill", page.GetProperty("items")[0].GetProperty("extension_id").GetString());
    }

    [Fact]
    public async Task AnIndexThatIsNotASkillIndexNamesTheShapeItExpected()
    {
        var source = await CreateSkillSourceAsync();

        // A registry answer handed to the skill adapter is a misconfigured source, and the reader
        // has to be able to tell that from a repository that genuinely holds nothing.
        Provider.Replies.Enqueue(Wire.Ok(Listing(S("io.github.a/ok", "1.0.0"))));
        var result = await RefreshAsync(source);

        Assert.Equal("unavailable", result.GetProperty("outcome").GetString());
        Assert.Contains("'skills'", result.GetProperty("reason").GetString());
        Assert.Empty((await CatalogAsync()).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PreviewingASkillFetchesTheDocumentOnce_AndWritesWhereTheLoaderLooks()
    {
        var source = await CreateSkillSourceAsync();
        var entry = await RefreshedSkillEntryAsync(source, new SkillRow("pdf-forms", "1.2.0",
            // An address the listing offers. Core reads it as display text and dials nothing but
            // the path it composes itself, so a hostile index cannot point a market read at a host
            // the operator never approved.
            DeclaredUrl: "https://evil.example.com/pdf-forms.md"));

        Provider.Replies.Enqueue(Wire.Ok(SkillDocument("pdf-forms")));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("skill") });

        Assert.Equal(2, Provider.Urls.Count);
        Assert.Equal(SkillSource, Provider.Urls[1]);
        Assert.DoesNotContain(Provider.Urls, u => u.Contains("evil.example.com", StringComparison.Ordinal));

        // The provider is not asked where its config lives: a skill's target is decided by this
        // process, because the loader that advertises skills is in here.
        Assert.DoesNotContain(Provider.Requests, r => r.ToolId == "mcp_list");

        var target = proposal.GetProperty("target_path").GetString()!;
        Assert.Equal(
            Path.Combine(_root, "workspace", "skills", "pdf-forms", "SKILL.md"),
            Path.GetFullPath(target));
        Assert.Equal("skill", proposal.GetProperty("kind").GetString());
        Assert.Contains("name: pdf-forms", proposal.GetProperty("content").GetString());

        // A skill has no package host to mistrust, so the package warning would be a lie here.
        var warnings = string.Join(',', proposal.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()).ToArray());
        Assert.Contains("fetched once", warnings, StringComparison.Ordinal);
        Assert.DoesNotContain("package host", warnings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyingASkillProposalQueuesTheFrozenBytes_AndGoesBackToTheNetworkNever()
    {
        var source = await CreateSkillSourceAsync();
        var entry = await RefreshedSkillEntryAsync(source, new SkillRow("pdf-forms"));
        Provider.Replies.Enqueue(Wire.Ok(SkillDocument("pdf-forms")));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("freeze") });
        var target = Path.GetFullPath(proposal.GetProperty("target_path").GetString()!);

        Provider.Urls.Clear();
        var applied = await PostAsync(
            $"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply");

        // What was reviewed is what will be written — the source could have republished the file by
        // now, and this install does not care, because the bytes are already in the proposal.
        Assert.Empty(Provider.Urls);

        var action = await GetJsonAsync(
            "/api/v1/user/tool-actions/" + applied.GetProperty("install_action_id").GetString());
        Assert.Equal("write_file", action.GetProperty("tool_id").GetString());
        Assert.False(File.Exists(target), "an approval has to happen before anything is on disk.");
    }

    [Theory]
    // Each of these is a document that would sit in the workspace and be refused by the loader on
    // every turn — an install that "succeeds" and advertises nothing is the failure this can least
    // afford, so the check runs at preview, against the rule the workspace itself applies.
    [InlineData("---\nname: other-skill\ndescription: d\n---\n", "does not match its directory")]
    [InlineData("---\nname: pdf-forms\ndescription: d\ndisabled: true\n---\n", "marked off by its own")]
    [InlineData("# no frontmatter here\n", "no YAML frontmatter")]
    [InlineData("---\nname: pdf-forms\n---\n", "no 'description'")]
    public async Task ASkillDocumentThatWouldNotAdvertiseItselfIsRefused(string body, string because)
    {
        var source = await CreateSkillSourceAsync();
        var entry = await RefreshedSkillEntryAsync(source, new SkillRow("pdf-forms"));
        Provider.Replies.Enqueue(Wire.Ok(body));

        var response = await Client.PostAsJsonAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("refused") });

        await _factory!.AssertStatusAsync(response, HttpStatusCode.Conflict, "preview of an unusable skill");
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("market_install_not_expressible", problem.GetProperty("code").GetString());
        Assert.Contains(because, problem.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ASkillAlreadyInTheWayIsReplacedOnItsOwnTerms_NotByGuess()
    {
        var source = await CreateSkillSourceAsync();
        var entry = await RefreshedSkillEntryAsync(source, new SkillRow("pdf-forms"));

        // Somebody hand-wrote a skill at the same path. The bytes on disk are shown to the reviewer
        // as what is being replaced, and the write is conditioned on them, so a change underneath
        // makes the tool refuse rather than win.
        Provider.QueueConfig("# my own notes about pdf\n", "sha256:mine");
        Provider.Replies.Enqueue(Wire.Ok(SkillDocument("pdf-forms")));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("replace") });

        Assert.Equal("sha256:mine", proposal.GetProperty("expected_file_hash").GetString());
        Assert.Contains("name: pdf-forms", proposal.GetProperty("content").GetString());
        Assert.Contains("already exists", string.Join(',', proposal.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()).ToArray()));
    }

    [Fact]
    public async Task ADocumentsLargerThanAWorkspaceWouldReadIsRefusedWhole()
    {
        var source = await CreateSkillSourceAsync();
        var entry = await RefreshedSkillEntryAsync(source, new SkillRow("pdf-forms"));

        Provider.Replies.Enqueue(Wire.Ok(SkillDocument("pdf-forms")
            + new string('x', (int)MarketInstallPolicy.MaxSkillBodyBytes)));

        var response = await Client.PostAsJsonAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("huge") });

        await _factory!.AssertStatusAsync(response, HttpStatusCode.Conflict, "preview of an oversized skill");
        var message = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("message").GetString();
        Assert.Contains("more than the", message);
        // Both ceilings are named because a document past one is unreadable to the loader and a
        // document past the other cannot be frozen into a proposal; a source author can only fix
        // what the answer tells them which of the two broke.
        Assert.Contains(MarketInstallPolicy.MaxSkillBodyBytes.ToString(), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingASkillIsRefusedWithTheSwitchThatActuallyExists()
    {
        var source = await CreateSkillSourceAsync();
        var entry = await RefreshedSkillEntryAsync(source, new SkillRow("pdf-forms"));
        Provider.Replies.Enqueue(Wire.Ok(SkillDocument("pdf-forms")));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = await CreateProjectAsync("remove") });
        await PostAsync($"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply");

        var installation = Assert.Single((await GetJsonAsync("/api/v1/market/installations"))
            .GetProperty("installations").EnumerateArray());
        Assert.Equal("skill", installation.GetProperty("kind").GetString());

        var response = await Client.PostAsync(
            $"/api/v1/market/installations/{installation.GetProperty("id").GetString()}/uninstall-preview",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        await _factory!.AssertStatusAsync(response, HttpStatusCode.Conflict, "uninstall of a skill");
        var message = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("message").GetString();
        Assert.Contains("no delete", message);
        Assert.Contains("disabled: true", message, StringComparison.Ordinal);
        Assert.Contains("skills/pdf-forms/SKILL.md", message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Against the real tool process
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two gates a human has to pass an approved write through, in the order the durable state
    /// puts them in: the permission envelope first, then the action's own approval. The only other
    /// place this sequence is walked is <c>ApprovalFlowTests</c>.
    /// </summary>
    private async Task<string> ApproveAsync(string actionId)
    {
        var queued = await GetJsonAsync("/api/v1/user/tool-actions/" + actionId);
        Assert.Equal("awaiting_user", queued.GetProperty("status").GetString());

        var permission = await Client.PostAsJsonAsync(
            $"/api/v1/governance/permission-requests/{queued.GetProperty("permission_request_id")}/decision",
            new { approve = true, reason = "Reviewer confirmed the market install." });
        await _factory!.AssertStatusAsync(permission, HttpStatusCode.Accepted, "permission decision");

        var gated = await GetJsonAsync("/api/v1/user/tool-actions/" + actionId);
        Assert.Equal("awaiting_approval", gated.GetProperty("status").GetString());

        var decided = await Client.PostAsJsonAsync(
            $"/api/v1/approvals/{gated.GetProperty("action_approval_id")}/decision",
            new { decision = "approved", reason = "Reviewer approved the write." });
        await _factory!.AssertStatusAsync(decided, HttpStatusCode.OK, "approval decision");

        return (await GetJsonAsync("/api/v1/user/tool-actions/" + actionId)).GetProperty("status").GetString()!;
    }

    [RequiresTinadecToolsFact]
    public async Task AnApprovedServerInstallLandsInTheFileTheProviderItselfReads()
    {
        Provider.Real = _factory!.Services.GetRequiredService<IToolProcessManager>();
        var source = await CreateSourceAsync("registry");
        var entry = await RefreshedEntryAsync(source, Pkg("@ac/live", "2.3.4", "@ac/live-server"));
        var project = await CreateProjectAsync("live");
        var root = Path.Combine(_root, "workspace");
        var target = Path.Combine(root, "mcp_servers.json");

        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = project });

        // The path came out of the child process's own mcp_list, not out of a constant in here, so
        // "the same path" is the strongest thing a test can say about it before anything is written.
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(proposal.GetProperty("target_path").GetString()!));
        Assert.False(File.Exists(target), "Nothing is written before a human decides.");
        Assert.True(!proposal.TryGetProperty("expected_file_hash", out var firstHash)
            || string.IsNullOrEmpty(firstHash.GetString()), "There was no file for the first write to be conditioned on.");

        var applied = await PostAsync($"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply");
        Assert.Equal("completed", await ApproveAsync(applied.GetProperty("install_action_id").GetString()!));

        var written = JsonDocument.Parse(await File.ReadAllTextAsync(target)).RootElement;
        var server = Assert.Single(written.GetProperty("servers").EnumerateArray());
        Assert.Equal(proposal.GetProperty("server_id").GetString(), server.GetProperty("id").GetString());
        Assert.Equal("npx", server.GetProperty("command").GetString());
        Assert.Contains("@ac/live-server@2.3.4", server.GetProperty("args").EnumerateArray()
            .Select(arg => arg.GetString()).ToArray());

        // The closed loop, and the reason this test exists: a second preview has to describe the
        // entry that is now in the file, and it can only do that by reading it back through the same
        // process that wrote it. The `-y` is the tell — that argument is in the proposal only as one
        // element of a list, so a joined command line can only have come out of the bytes on disk.
        var again = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = project });
        Assert.Equal("npx -y @ac/live-server@2.3.4", again.GetProperty("replaces_command").GetString());
        Assert.Equal(
            await ProviderFileHashAsync(root, target),
            again.GetProperty("expected_file_hash").GetString());

        var row = Assert.Single((await GetJsonAsync("/api/v1/market/installations"))
            .GetProperty("installations").EnumerateArray());
        Assert.Equal("completed", row.GetProperty("action_status").GetString());

        // The way out has to be a real edit of the same file rather than a rewrite of it: this
        // server disappears and the document around it survives. Then the ledger stops carrying a
        // row for something that is no longer installed, which is the only reason "installed" is an
        // observable rather than a claim.
        var removal = await PostAsync($"/api/v1/market/installations/{row.GetProperty("id").GetString()}/uninstall-preview");
        await PostAsync($"/api/v1/market/install-proposals/{removal.GetProperty("id").GetString()}/apply");

        var removing = Assert.Single((await GetJsonAsync("/api/v1/market/installations"))
            .GetProperty("installations").EnumerateArray());
        Assert.Equal("removing", removing.GetProperty("state").GetString());
        Assert.Equal("completed", await ApproveAsync(removing.GetProperty("uninstall_action_id").GetString()!));

        Assert.Empty(JsonDocument.Parse(await File.ReadAllTextAsync(target)).RootElement
            .GetProperty("servers").EnumerateArray());
        Assert.Empty((await GetJsonAsync("/api/v1/market/installations"))
            .GetProperty("installations").EnumerateArray());
    }

    [RequiresTinadecToolsFact]
    public async Task AnApprovedSkillInstallCreatesTheFolderTheLoaderLooksIn()
    {
        Provider.Real = _factory!.Services.GetRequiredService<IToolProcessManager>();
        var source = await CreateSkillSourceAsync();
        var entry = await RefreshedSkillEntryAsync(source, new SkillRow("pdf-forms"));
        var project = await CreateProjectAsync("live-skill");
        var root = Path.Combine(_root, "workspace");
        var target = Path.Combine(root, "skills", "pdf-forms", "SKILL.md");

        Provider.Replies.Enqueue(Wire.Ok(SkillDocument("pdf-forms")));
        var proposal = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = project });
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(proposal.GetProperty("target_path").GetString()!));

        // The workspace has never held a skill, so `skills/` does not exist and the approved write
        // has to create it. This is the step a fake provider cannot vouch for: the tool used to
        // answer "the parent directory must already exist", and an install needing two approvals
        // for one change is not an install.
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)!));

        var applied = await PostAsync($"/api/v1/market/install-proposals/{proposal.GetProperty("id").GetString()}/apply");
        Assert.Equal("completed", await ApproveAsync(applied.GetProperty("install_action_id").GetString()!));

        var body = await File.ReadAllTextAsync(target);
        Assert.Contains("name: pdf-forms", body, StringComparison.Ordinal);
        Assert.Contains("description: Reads a PDF form and fills it.", body, StringComparison.Ordinal);

        Provider.Replies.Enqueue(Wire.Ok(SkillDocument("pdf-forms")));
        var again = await PreviewAsync($"/api/v1/market/catalog/{entry}/install-preview",
            new { project_id = project });
        // Both halves of this answer are read back out of the file the child process just created:
        // the warning names a path Core only knows because read_file succeeded, and the hash is the
        // tool's own value for those bytes — the shape of it is the tool's business, not Core's.
        Assert.Contains("already exists", string.Join(',', again.GetProperty("warnings").EnumerateArray()
            .Select(warning => warning.GetString()).ToArray()));
        Assert.Equal(
            await ProviderFileHashAsync(root, target),
            again.GetProperty("expected_file_hash").GetString());
    }

    /// <summary>
    /// Reads one file through the real tool process from the test side, so a proposal's conditional
    /// hash can be compared against the number the tool itself reports rather than against a guess
    /// at how it renders one.
    /// </summary>
    private async Task<string> ProviderFileHashAsync(string workspaceRoot, string path)
    {
        var response = await Provider.Real!.CallAsync(workspaceRoot, new ToolWireRequestDto
        {
            ToolId = "read_file",
            Approved = true,
            Params = JsonDocument.Parse(new JsonObject { ["filepath"] = path }.ToJsonString()).RootElement,
        });

        Assert.True(response.IsSuccess, $"The tool process could not read {path}: {response.Error}");
        return response.Result?.GetProperty("file_hash").GetString() ?? string.Empty;
    }

    private static string SkillDocument(string name) =>
        $"---\nname: {name}\ndescription: Reads a PDF form and fills it.\n---\n\n"
        + $"# {name}\n\nOpen the referenced script before answering.\n";

    private async Task<string> CreateSkillSourceAsync(string name = "skills")
    {
        var response = await Client.PostAsJsonAsync("/api/v1/market/sources", new
        {
            name,
            kind = "skill_repository",
            location = SkillIndexLocation,
            enabled = true,
        });

        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, $"POST skill source ({name})");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Refreshes an index and returns the catalog id of its first row. The refresh is part of the
    /// helper rather than the caller because a row that never got stored is otherwise discovered
    /// three assertions later, as an empty collection with no reason attached to it.
    /// </summary>
    private async Task<string> RefreshedSkillEntryAsync(string sourceId, params SkillRow[] rows)
    {
        Provider.Replies.Enqueue(Wire.Ok(SkillIndex(rows)));
        var result = await RefreshAsync(sourceId);
        Assert.Equal(rows.Length, result.GetProperty("fetched_rows").GetInt32());

        var page = await CatalogAsync($"q={Uri.EscapeDataString(rows[0].Name!)}");
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        return item.GetProperty("catalog_id").GetString()!;
    }

    /// <summary>
    /// One row of a skill index, built as a document rather than a string so the escaping is the
    /// JSON writer's. <paramref name="declaredUrl"/> is the address the listing offers and Core
    /// ignores when it decides what to fetch.
    /// </summary>
    private sealed record SkillRow(
        string? Name,
        string? Version = null,
        string? Title = null,
        string? Description = null,
        string? DeclaredUrl = null);

    private static string SkillIndex(params SkillRow[] rows)
    {
        var items = new JsonArray();
        foreach (var row in rows)
        {
            var item = new JsonObject();
            if (row.Name is not null) item["name"] = row.Name;
            if (row.Version is not null) item["version"] = row.Version;
            if (row.Title is not null) item["title"] = row.Title;
            if (row.Description is not null) item["description"] = row.Description;
            if (row.DeclaredUrl is not null) item["url"] = row.DeclaredUrl;
            items.Add(item);
        }

        return new JsonObject { ["skills"] = items }.ToJsonString();
    }

    /// <summary>Refreshes one listing and returns the catalog id of its first stored row.</summary>
    private async Task<string> RefreshedEntryAsync(string sourceId, Server server)
    {
        Provider.Replies.Enqueue(Wire.Ok(Listing(server)));
        var result = await RefreshAsync(sourceId);
        Assert.Equal(1, result.GetProperty("fetched_rows").GetInt32());

        var page = await CatalogAsync($"q={Uri.EscapeDataString(server.Name)}");
        return page.GetProperty("items")[0].GetProperty("catalog_id").GetString()!;
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        // The governed write path snapshots the workspace before it queues anything, so the project
        // root has to be a real directory rather than a string.
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        var response = await Client.PostAsJsonAsync("/api/v1/projects",
            new { name, path = Path.Combine(_root, "workspace") });
        await _factory!.AssertStatusAsync(response, HttpStatusCode.Created, $"create project {name}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("id").ToString()!;
    }

    private async Task<JsonElement> PreviewAsync(string path, object? body) =>
        await PostAsync(path, body);

    private async Task<JsonElement> PostAsync(string path, object? body = null)
    {
        var response = body is null
            ? await Client.PostAsync(path, new StringContent(string.Empty, Encoding.UTF8, "application/json"))
            : await Client.PostAsJsonAsync(path, body);
        await _factory!.AssertStatusAsync(response, HttpStatusCode.OK, $"POST {path}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

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

    /// <summary>
    /// One row of a registry listing, as the shape rather than as the wire bytes. <c>Legacy</c>
    /// switches the package member spelling to the older one the registry has also used, and
    /// <c>RawIdentifier</c> carries a package identifier verbatim so a row can say something Core
    /// must refuse.
    /// </summary>
    private sealed record Server(
        string? Name,
        string? Version,
        string? Title = null,
        string? Description = null,
        string? Homepage = null,
        string? RegistryType = null,
        string? PackageName = null,
        string[]? Transports = null,
        bool Legacy = false,
        string? EnvironmentName = null,
        string? PackageVersion = null,
        string? RuntimeHint = null,
        string? RuntimeArgument = null,
        bool NoTransport = false);

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

    /// <summary>
    /// A packaged server in the spelling the live registry uses today: camelCase members, the
    /// transport nested under <c>transport</c>, runtime arguments as objects, and environment
    /// requests carrying <c>isRequired</c> and <c>isSecret</c>. This is copied from a real
    /// <c>/v0/servers</c> answer rather than written from memory, because the point of the shape is
    /// that Core does not invent it.
    /// </summary>
    private static Server Pkg(string name, string version, string identifier, string registryType = "npm") =>
        new(name, version, Title: name, RegistryType: registryType, PackageName: identifier,
            Transports: ["stdio"], RuntimeHint: registryType == "npm" ? "npx" : "uvx",
            RuntimeArgument: "-y", EnvironmentName: "API_KEY", PackageVersion: version);

    private static Server Legacy(Server server) => server with { Legacy = true };

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
                var transport = server.NoTransport
                    ? null
                    : server.Transports is { Length: > 0 } ? server.Transports[0] : "stdio";
                var package = new JsonObject
                {
                    [server.Legacy ? "registry_type" : "registryType"] = server.RegistryType,
                    [server.Legacy ? "name" : "identifier"] = server.PackageName,
                    ["version"] = server.PackageVersion ?? server.Version,
                };

                if (server.Legacy)
                {
                    if (transport is not null)
                        package["transport_type"] = transport;
                    if (server.RuntimeArgument is not null)
                        package["packageArguments"] = new JsonObject { [server.RuntimeArgument.TrimStart('-')] = "true" };
                }
                else
                {
                    if (transport is not null)
                        package["transport"] = new JsonObject { ["type"] = transport };
                    if (server.RuntimeHint is not null)
                        package["runtimeHint"] = server.RuntimeHint;
                    if (server.RuntimeArgument is not null)
                    {
                        package["runtimeArguments"] = new JsonArray((JsonNode)new JsonObject
                        {
                            ["value"] = server.RuntimeArgument,
                            ["type"] = "positional",
                        });
                    }
                }

                if (server.EnvironmentName is not null)
                {
                    package[server.Legacy ? "environment" : "environmentVariables"] = new JsonArray(
                        (JsonNode)new JsonObject
                        {
                            ["name"] = server.EnvironmentName,
                            ["isRequired"] = true,
                            ["isSecret"] = true,
                            ["description"] = "A key. The value is not here and never is.",
                        });
                }

                descriptor["packages"] = new JsonArray(package);
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

        /// <summary>Overrides where <c>mcp_list</c> says the config lives. Null means the workspace root.</summary>
        internal string? ConfigPath { get; set; }

        /// <summary>Queued <c>read_file> answers, in order. Empty means "no such file".</summary>
        internal Queue<ToolWireResponseDto> ConfigReads { get; } = new();

        internal List<string> ConfigPathsSeen { get; } = [];
        internal List<string?> FileHashesSeen { get; } = [];

        /// <summary>
        /// The real tool process, for the tests that ask what happened on disk rather than what came
        /// back over the wire. Only <c>#fetch</c> stays simulated: it is the one call that would need
        /// the network, and the provider blocks loopback addresses on purpose, so no local stand-in
        /// server is reachable by design. Every other call — <c>mcp_list</c>, <c>read_file</c>,
        /// <c>write_file</c> — goes to the child process, which means a test holding this has a real
        /// file written by the same binary that will later read it back.
        /// <para>
        /// Forwarding happens before the bounding assertions below, because the executor that runs an
        /// approved action calls with no timeout of its own and the test would fail on the harness
        /// rather than on the behaviour it is checking.
        /// </para>
        /// </summary>
        internal IToolProvider? Real { get; set; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Real is { } backend ? backend.EnsureStartedAsync(workspaceRoot, cancellationToken) : Manifest();

        /// <summary>
        /// A manifest that advertises <c>write_file</c> and hashes to itself: a user tool action
        /// refuses to bind to a provider whose manifest identity does not compute, so the fake has
        /// to be as careful as the real process.
        /// </summary>
        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Real is { } backend ? backend.GetManifestAsync(workspaceRoot, cancellationToken) : Manifest();

        private static Task<ToolManifestDto> Manifest()
        {
            var tools = new List<ToolManifestEntryDto>
            {
                new()
                {
                    Id = "write_file",
                    Description = "In-process fake write probe",
                    RequiresApproval = true,
                    Risk = "high",
                    MutatesWorkspace = true,
                    InputSchema = JsonDocument.Parse(
                        """{"type":"object","properties":{"filepath":{"type":"string"},"content":{"type":"string"},"file_hash":{"type":"string"}},"additionalProperties":true}""").RootElement.Clone(),
                },
            };

            return Task.FromResult(new ToolManifestDto
            {
                ProtocolVersion = 2,
                ManifestHash = ToolManifestHasher.Compute(tools),
                Tools = tools,
            });
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Throw is { } failure) throw failure;

            if (request.ToolId != "#fetch" && Real is { } backend)
                return backend.CallAsync(workspaceRoot, request, timeout, cancellationToken);

            Assert.NotNull(timeout);
            Assert.True(timeout > TimeSpan.Zero, "Every market call reaches a network or a disk; it must be bounded.");

            switch (request.ToolId)
            {
                case "#fetch":
                {
                    var url = request.Params is { } p && p.TryGetProperty("url", out var named)
                        ? named.GetString() ?? string.Empty
                        : string.Empty;
                    Urls.Add(url);
                    Assert.True(Replies.Count > 0, $"The test queued no answer for {url}.");
                    return Task.FromResult(Replies.Dequeue().ToWire(url));
                }

                // The install surface asks the provider where its own config file is, rather than
                // resolving a path of its own. Queueing the answer is how a test makes that answer
                // lie on purpose.
                case "mcp_list":
                {
                    ConfigPathsSeen.Add(workspaceRoot);
                    var path = ConfigPath ?? Path.Combine(workspaceRoot, "mcp_servers.json");
                    return Task.FromResult(Ok(new JsonObject
                    {
                        ["config_path"] = path,
                        ["servers"] = new JsonArray(),
                    }));
                }

                case "read_file":
                {
                    if (request.Params is { } params1
                        && params1.TryGetProperty("filepath", out var filepath))
                    {
                        ConfigPathsSeen.Add(filepath.GetString());
                    }

                    if (ConfigReads.Count > 0)
                        return Task.FromResult(ConfigReads.Dequeue());

                    return Task.FromResult(Ok(new JsonObject
                    {
                        ["success"] = false,
                        ["error"] = "Could not find file.",
                        ["file_hash"] = string.Empty,
                        ["all_contents"] = new JsonArray(),
                    }));
                }

                default:
                    throw new InvalidOperationException(
                        $"The market surface called an unexpected tool '{request.ToolId}'.");
            }
        }

        /// <summary>What <c>read_file</c> answers for a config that exists, in the tool's own wire shape.</summary>
        internal void QueueConfig(string json, string fileHash = "sha256:existing")
        {
            var lines = new JsonArray();
            var number = 0;
            foreach (var line in json.Split('\n'))
            {
                number++;
                lines.Add((JsonNode)new JsonObject
                {
                    // LineContent is a positional record with no JSON naming, which is exactly why
                    // the reader under test does not assume a casing.
                    ["content"] = new JsonObject
                    {
                        ["Content"] = line,
                        ["LineNumber"] = number,
                        ["StartOffset"] = 0,
                        ["EndOffset"] = line.Length,
                    },
                    ["line_hash"] = $"{number}#hash",
                });
            }

            ConfigReads.Enqueue(Ok(new JsonObject
            {
                ["success"] = true,
                ["file_hash"] = fileHash,
                ["all_contents"] = lines,
            }));
        }
    }

    private static ToolWireResponseDto Ok(JsonObject payload) => new()
    {
        IsSuccess = true,
        Result = JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone(),
    };
}
