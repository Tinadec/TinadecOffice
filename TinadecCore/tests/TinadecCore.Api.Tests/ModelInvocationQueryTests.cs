using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Read path for <c>/api/v1/model-invocations</c> — the page the desktop's run usage view sums into
/// "what did this run cost". Two things are pinned here that no other gate covers: the response body
/// being typed in the OpenAPI document (a renamed field used to drift through the snapshot gate
/// unnoticed), and the cursor returning every row exactly once (a page boundary that drops rows makes
/// the usage totals silently wrong rather than visibly empty).
/// </summary>
public sealed class ModelInvocationQueryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Provider = Guid.Parse("7c1f3c00-0000-4000-8000-000000000001");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-model-invocation-tests", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task OpenApi_TypesTheInvocationPageBody_SoFieldRenamesReachTheDriftGate()
    {
        var client = _factory!.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/core.json"));
        var response = doc.RootElement
            .GetProperty("paths").GetProperty("/api/v1/model-invocations").GetProperty("get")
            .GetProperty("responses").GetProperty("200");

        Assert.True(response.TryGetProperty("content", out var content),
            "A 200 with no content is what let this route's fields drift unnoticed; the body must stay typed.");
        var schema = content.GetProperty("application/json").GetProperty("schema");
        Assert.Equal("#/components/schemas/ModelInvocationPageDto", schema.GetProperty("$ref").GetString());

        var components = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(components.GetProperty("ModelInvocationPageDto").GetProperty("properties").TryGetProperty("items", out _));
        var item = components.GetProperty("ModelInvocationDto").GetProperty("properties");
        foreach (var name in new[] { "run_id", "model", "provider_instance_id", "input_tokens", "output_tokens", "total_tokens", "started_at" })
        {
            // The usage surface sums the token trio and groups on model + provider; "prompt_tokens"
            // and "completion_tokens" are the names the desktop interface invented, and it read
            // undefined for every row until the contract was checked against this list.
            Assert.True(item.TryGetProperty(name, out _), $"ModelInvocationDto.{name} must stay in the contract.");
        }

        Assert.False(item.TryGetProperty("prompt_tokens", out _), "prompt_tokens is not a Core field; do not reintroduce it.");
        Assert.False(item.TryGetProperty("completion_tokens", out _), "completion_tokens is not a Core field; do not reintroduce it.");
    }

    [Fact]
    public async Task Page_FiltersByRun_AndOmitsNextCursorWhenNothingIsTruncated()
    {
        var runId = Guid.NewGuid();
        var otherRun = Guid.NewGuid();
        var seeded = await SeedAsync(runId, 3);
        await SeedAsync(otherRun, 2);

        var page = await PageAsync($"&run_id={runId}");

        var items = page.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(seeded.OrderBy(x => x).ToArray(), EnumerateIds(items).OrderBy(x => x).ToArray());
        Assert.All(items.EnumerateArray(), item => Assert.Equal(runId, item.GetProperty("run_id").GetGuid()));

        var stamps = items.EnumerateArray().Select(x => x.GetProperty("started_at").GetDateTimeOffset()).ToArray();
        Assert.Equal(stamps.OrderByDescending(x => x).ToArray(), stamps);

        Assert.False(page.TryGetProperty("next_cursor", out _),
            "Core omits null keys, so an absent next_cursor is the contract's only 'last page' signal.");
    }

    [Fact]
    public async Task Page_FiltersByStartedAtWindow()
    {
        // SQLite keeps these timestamps as TEXT and EF cannot translate a range comparison against a
        // parameter; this is the second date-shaped filter on the route, and it 400'd for the same
        // reason the cursor did.
        var runId = Guid.NewGuid();
        await SeedAsync(runId, 5);

        var from = Uri.EscapeDataString(Base.AddSeconds(2).ToString("O"));
        var to = Uri.EscapeDataString(Base.AddSeconds(3).ToString("O"));
        var page = await PageAsync($"&run_id={runId}&from={from}&to={to}");

        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Page_UnmeasuredUsageStaysAbsent_RatherThanArrivingAsNull()
    {
        // The desktop must be able to tell "the provider reported nothing" from "this call cost 0".
        var runId = Guid.NewGuid();
        await SeedAsync(runId, 1, withUsage: false, model: null);

        var page = await PageAsync($"&run_id={runId}");
        var item = page.GetProperty("items")[0];

        Assert.False(item.TryGetProperty("total_tokens", out _));
        Assert.False(item.TryGetProperty("input_tokens", out _));
        Assert.False(item.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task Page_CursorReturnsEveryRowExactlyOnce_WhenTimestampsTie()
    {
        // A run writes its invocations second by second at most, so ties are normal, and this route
        // orders by (started_at, id) while the cursor used to carry only started_at: the second half
        // of a tied page was skipped by the next page and never reached the usage totals.
        var runId = Guid.NewGuid();
        var seeded = await SeedAsync(runId, 5, at: Base);

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10 && seen.Count < seeded.Count; guard++)
        {
            var page = await PageAsync($"&run_id={runId}&limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
            seen.AddRange(EnumerateIds(page.GetProperty("items")));
            cursor = page.TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
            if (cursor is null) break;
        }

        Assert.Equal(seeded.Count, seen.Distinct().Count());
        Assert.Equal(0, seen.Count - seen.Distinct().Count());
        Assert.Null(cursor);
    }

    [Fact]
    public async Task Page_ClampsAnOversizedLimitInsteadOfStreamingTheWholeRun()
    {
        var runId = Guid.NewGuid();
        await SeedAsync(runId, 205);

        var page = await PageAsync($"&run_id={runId}&limit=100000");

        Assert.Equal(200, page.GetProperty("items").GetArrayLength());
        Assert.True(page.TryGetProperty("next_cursor", out _), "A clamped page must still hand back a cursor.");
    }

    [Fact]
    public async Task Page_RejectsMalformedFiltersAndCursorsWithMachineCodes()
    {
        var client = _factory!.CreateClient();

        var badRun = await client.GetAsync("/api/v1/model-invocations?run_id=not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, badRun.StatusCode);
        Assert.Equal("invalid_query", (await badRun.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var badCursor = await client.GetAsync("/api/v1/model-invocations?cursor=%2A%2A%2A");
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        // These two codes are the gateway's allow-list for this route: a code Core stops sending
        // would be rewritten to "conflict" on the way to the desktop.
        Assert.Equal("invalid_cursor", (await badCursor.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var badStatus = await client.GetAsync("/api/v1/model-invocations?status=exploded");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal("invalid_query", (await badStatus.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private async Task<IReadOnlyList<Guid>> SeedAsync(
        Guid runId,
        int count,
        DateTimeOffset? at = null,
        string? model = "gpt-test",
        bool withUsage = true)
    {
        var scope = _factory!.Services.GetRequiredService<ITenantContextAccessor>().Current;
        await using var db = await _factory.Services
            .GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        var ids = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.ModelInvocations.Add(new ModelInvocationRecord
            {
                Id = id,
                CallId = Guid.NewGuid(),
                Attempt = index,
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                SessionId = Guid.NewGuid(),
                RunId = runId,
                AgentDefinitionId = Guid.NewGuid(),
                AgentVersionId = Guid.NewGuid(),
                ModeVersionId = Guid.NewGuid(),
                StrategySource = "test",
                ProviderInstanceId = Provider,
                ProviderVersionId = Guid.NewGuid(),
                Model = model,
                Protocol = "openai-chat",
                Status = "succeeded",
                InputTokens = withUsage ? 10 : null,
                OutputTokens = withUsage ? 5 : null,
                TotalTokens = withUsage ? 15 : null,
                StartedAt = at ?? Base.AddSeconds(index)
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<JsonElement> PageAsync(string queryWithAmpersand)
    {
        var client = _factory!.CreateClient();
        var response = await client.GetAsync($"/api/v1/model-invocations?{queryWithAmpersand[1..]}");
        await ServerFailureReports.AssertStatusAsync(_factory, response, HttpStatusCode.OK, "model-invocations page");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<Guid> EnumerateIds(JsonElement items) =>
        items.EnumerateArray().Select(x => x.GetProperty("id").GetGuid());

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
