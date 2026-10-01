using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The memory review queue as its reviewer sees it: what a candidate carries, what a
/// decision leaves behind, and what a filter answers when it cannot be honoured.
///
/// Nothing here re-tests the curator that writes candidates (that is the run-engine's own
/// coverage). Candidates are seeded through the same port the curator uses, so the write
/// path and the read path are exercised against the same stored blob rather than against a
/// shape this file invented.
///
/// The filters matter more than they look. A queue that answers "nothing to review" to a
/// misspelled status is indistinguishable, on screen, from a genuinely empty queue — and the
/// reviewer closes the tab on the second reading.
/// </summary>
public sealed class MemoryReviewApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-memory-review-tests", Guid.NewGuid().ToString("N"));
    private ReviewFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new ReviewFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private ILongTermMemoryService Memory => _factory!.Services.GetRequiredService<ILongTermMemoryService>();

    /// <summary>Seeds one candidate through the curator's own port, with the full proposal body.</summary>
    private async Task<MemoryCandidate> SeedAsync(
        string content,
        string scope = "workspace",
        string kind = "fact",
        Guid? runId = null,
        string? applicability = "when the workspace still builds with the .NET 10 SDK",
        string? expiry = "the global.json pins another band",
        string? evidence = "run log line 42 said the restore failed on sdk 9")
        => await Memory.CreateCandidateAsync(new MemoryCandidateProposal(
            runId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            scope,
            kind,
            content,
            0.82,
            Evidence: evidence,
            Applicability: applicability,
            ExpiryCondition: expiry));

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _factory!.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetResultAsync(string path)
    {
        var response = await _factory!.CreateClient().GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    [Fact(DisplayName = "A queued candidate reaches the reviewer with the constraint it carries")]
    public async Task ReviewEvidenceTravelsToTheReviewer()
    {
        await SeedAsync("This repo restores only under the pinned SDK band.");

        var rows = await GetJsonAsync("/api/v1/memory-candidates?status=proposed");
        var candidate = Assert.Single(rows.EnumerateArray());

        Assert.Equal("This repo restores only under the pinned SDK band.", candidate.GetProperty("content").GetString());
        Assert.Equal("when the workspace still builds with the .NET 10 SDK", candidate.GetProperty("applicability").GetString());
        Assert.Equal("the global.json pins another band", candidate.GetProperty("expiry_condition").GetString());
        Assert.Equal("run log line 42 said the restore failed on sdk 9", candidate.GetProperty("evidence").GetString());
        Assert.Equal("proposed", candidate.GetProperty("status").GetString());
    }

    [Fact(DisplayName = "A candidate stored without the optional body reads it back as no key at all")]
    public async Task AnAbsentConstraintIsAbsentRatherThanEmpty()
    {
        await Memory.CreateCandidateAsync(new MemoryCandidateProposal(
            Guid.NewGuid(), Guid.NewGuid(), "workspace", "fact", "Bare statement.", 0.5));

        var candidate = Assert.Single((await GetJsonAsync("/api/v1/memory-candidates?status=proposed")).EnumerateArray());

        // The API boundary writes no nulls, so "the curator recorded nothing here" arrives as
        // a missing key rather than a key holding null. A reader that requires the key would
        // call a well-formed answer a malformed one.
        Assert.False(candidate.TryGetProperty("applicability", out _));
        Assert.False(candidate.TryGetProperty("evidence", out _));
        Assert.Equal("Bare statement.", candidate.GetProperty("content").GetString());
    }

    [Fact(DisplayName = "A misspelled status is refused instead of answering with an empty queue")]
    public async Task AMisspelledFilterIsRefusedRatherThanAnsweredWithNothing()
    {
        await SeedAsync("Something worth reviewing.");

        var (status, body) = await GetResultAsync("/api/v1/memory-candidates?status=propose");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("code").GetString());
        Assert.Equal("status", body.GetProperty("field").GetString());
        Assert.Contains("proposed", body.GetProperty("message").GetString());
    }

    [Fact(DisplayName = "A scope outside the frozen vocabulary is refused with its own field name")]
    public async Task AnUnknownScopeIsRefused()
    {
        var (status, body) = await GetResultAsync("/api/v1/memory-candidates?scope=session");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("scope", body.GetProperty("field").GetString());
    }

    [Fact(DisplayName = "An id filter that is not an id says which field broke rather than returning nothing")]
    public async Task AFilterIdThatIsNotAnIdIsRefused()
    {
        var (status, body) = await GetResultAsync("/api/v1/memory-candidates?run_id=yesterday");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_RUN_ID", body.GetProperty("code").GetString());
        Assert.Equal("run_id", body.GetProperty("field").GetString());
    }

    [Fact(DisplayName = "A page size that is not a count is refused instead of silently unbounded")]
    public async Task AnUnusableLimitIsRefused()
    {
        var (status, body) = await GetResultAsync("/api/v1/memory-candidates?limit=many");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("limit", body.GetProperty("field").GetString());
    }

    [Fact(DisplayName = "Narrowing the queue by run and scope leaves only what that answer names")]
    public async Task TheQueueNarrowsByRunAndScope()
    {
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        await SeedAsync("From run A.", runId: runA);
        await SeedAsync("From run B.", runId: runB);
        await SeedAsync("A principal habit.", scope: "principal", runId: runA);

        var byRun = (await GetJsonAsync($"/api/v1/memory-candidates?run_id={runA:D}")).EnumerateArray()
            .Select(row => row.GetProperty("content").GetString()).ToArray();
        Assert.Equal(2, byRun.Length);
        Assert.DoesNotContain("From run B.", byRun);

        var byScope = (await GetJsonAsync("/api/v1/memory-candidates?scope=principal")).EnumerateArray()
            .Select(row => row.GetProperty("content").GetString()).ToArray();
        Assert.Equal(["A principal habit."], byScope);

        var byBoth = (await GetJsonAsync($"/api/v1/memory-candidates?scope=principal&run_id={runA:D}")).EnumerateArray();
        Assert.Equal("A principal habit.", Assert.Single(byBoth).GetProperty("content").GetString());
    }

    [Fact(DisplayName = "A page size bounds the queue and zero asks for nothing")]
    public async Task APageSizeBoundsTheQueue()
    {
        for (var i = 0; i < 3; i++) await SeedAsync($"Candidate {i}.");

        Assert.Equal(2, (await GetJsonAsync("/api/v1/memory-candidates?limit=2")).EnumerateArray().Count());
        Assert.Empty((await GetJsonAsync("/api/v1/memory-candidates?limit=0")).EnumerateArray());
        Assert.Equal(3, (await GetJsonAsync("/api/v1/memory-candidates")).EnumerateArray().Count());
    }

    [Fact(DisplayName = "Promoting a candidate opens an active memory that keeps its constraint")]
    public async Task APromotedCandidateBecomesActiveMemoryThatCarriesItsConstraint()
    {
        var candidate = await SeedAsync("Use the pinned SDK band.");

        var client = _factory!.CreateClient();
        var promoted = await client.PostAsJsonAsync(
            $"/api/v1/memory-candidates/{candidate.Id}/promote", new { reason = "confirmed by hand" });
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        var promotedBody = JsonDocument.Parse(await promoted.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("promoted", promotedBody.GetProperty("status").GetString());
        Assert.Equal("confirmed by hand", promotedBody.GetProperty("decision_reason").GetString());
        var itemId = promotedBody.GetProperty("promoted_memory_item_id").GetGuid();

        var active = Assert.Single((await GetJsonAsync("/api/v1/memory-items?status=active")).EnumerateArray());
        Assert.Equal(itemId, active.GetProperty("id").GetGuid());
        Assert.Equal("Use the pinned SDK band.", active.GetProperty("content").GetString());
        Assert.Equal("when the workspace still builds with the .NET 10 SDK", active.GetProperty("applicability").GetString());
        Assert.Equal(1, active.GetProperty("version").GetInt32());

        // The decided row stays in the queue's history under its own status, and the
        // proposed shelf no longer offers it for a second decision.
        Assert.Empty((await GetJsonAsync("/api/v1/memory-candidates?status=proposed")).EnumerateArray());
        Assert.Single((await GetJsonAsync("/api/v1/memory-candidates?status=promoted")).EnumerateArray());
    }

    [Fact(DisplayName = "Deciding twice is refused as already decided, not as a missing candidate")]
    public async Task DecidingTwiceIsAConflictAboutTheCandidate()
    {
        var candidate = await SeedAsync("A statement nobody will review twice.");
        var client = _factory!.CreateClient();
        await client.PostAsJsonAsync($"/api/v1/memory-candidates/{candidate.Id}/promote", new { reason = "first" });

        var second = await client.PostAsJsonAsync($"/api/v1/memory-candidates/{candidate.Id}/reject", new { reason = "second" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ALREADY_DECIDED", body.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "A revoked memory leaves the active shelf and stays readable as history")]
    public async Task ARevokedMemoryLeavesTheActiveShelfButStaysReadable()
    {
        var candidate = await SeedAsync("A habit the workspace outgrew.");
        var client = _factory!.CreateClient();
        var promoted = JsonDocument.Parse(await (await client.PostAsJsonAsync(
            $"/api/v1/memory-candidates/{candidate.Id}/promote", new { reason = "accepted" })).Content.ReadAsStringAsync()!).RootElement;
        var itemId = promoted.GetProperty("promoted_memory_item_id").GetGuid();

        // No reason is sent: a revocation has nowhere to be recorded, and a review surface
        // that collects an explanation Core throws away teaches the reviewer to type noise.
        var revoked = await client.PostAsJsonAsync($"/api/v1/memory-items/{itemId}/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var revokedBody = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("revoked", revokedBody.GetProperty("status").GetString());
        Assert.NotNull(revokedBody.GetProperty("revoked_at").GetString());
        Assert.Equal(itemId, revokedBody.GetProperty("id").GetGuid());
        Assert.Equal("when the workspace still builds with the .NET 10 SDK", revokedBody.GetProperty("applicability").GetString());

        Assert.Empty((await GetJsonAsync("/api/v1/memory-items?status=active")).EnumerateArray());
        var history = Assert.Single((await GetJsonAsync("/api/v1/memory-items?status=revoked")).EnumerateArray());
        Assert.Equal(itemId, history.GetProperty("id").GetGuid());
        // Forgetting is not deletion: the sentence is still readable in the history shelf.
        Assert.Equal("A habit the workspace outgrew.", history.GetProperty("content").GetString());
    }

    [Fact(DisplayName = "Revoking a memory twice reports the same revoked item instead of a conflict")]
    public async Task RevokingTwiceIsIdempotent()
    {
        var candidate = await SeedAsync("Forget this once, or twice.");
        var client = _factory!.CreateClient();
        var itemId = JsonDocument.Parse(await (await client.PostAsJsonAsync(
            $"/api/v1/memory-candidates/{candidate.Id}/promote", new { reason = "accepted" })).Content.ReadAsStringAsync()!)
            .RootElement.GetProperty("promoted_memory_item_id").GetGuid();

        await client.PostAsJsonAsync($"/api/v1/memory-items/{itemId}/revoke", new { });
        var second = await client.PostAsJsonAsync($"/api/v1/memory-items/{itemId}/revoke", new { });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var rows = (await GetJsonAsync("/api/v1/memory-items?status=revoked")).EnumerateArray().ToArray();
        Assert.Equal(itemId, Assert.Single(rows).GetProperty("id").GetGuid());
    }

    [Fact(DisplayName = "An unknown memory id is a not-found, and a bad filter is a bad request")]
    public async Task MissingRowsAndBadFiltersStayDistinct()
    {
        var client = _factory!.CreateClient();
        var missing = await client.PostAsJsonAsync($"/api/v1/memory-items/{Guid.NewGuid()}/revoke", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            "NOT_FOUND",
            JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        var badFilter = await GetResultAsync("/api/v1/memory-items?status=archived");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.Status);
        Assert.Equal("status", badFilter.Body.GetProperty("field").GetString());
    }

    [Fact(DisplayName = "A filter that mixes an item shelf and a page size answers with that page only")]
    public async Task ItemsNarrowByScopeAndLimit()
    {
        foreach (var scope in new[] { "workspace", "workspace", "principal" })
        {
            var candidate = await SeedAsync($"A {scope} memory.", scope: scope);
            await _factory!.CreateClient().PostAsJsonAsync($"/api/v1/memory-candidates/{candidate.Id}/promote", new { reason = (string?)null });
        }

        var workspace = (await GetJsonAsync("/api/v1/memory-items?scope=workspace")).EnumerateArray().ToArray();
        Assert.Equal(2, workspace.Length);
        var page = (await GetJsonAsync("/api/v1/memory-items?scope=workspace&limit=1")).EnumerateArray().ToArray();
        Assert.Single(page);
    }

    private sealed class ReviewFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public ReviewFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
