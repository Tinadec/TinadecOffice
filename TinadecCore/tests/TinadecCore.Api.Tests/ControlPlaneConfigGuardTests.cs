using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models;
using TinadecCore.Persistence;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Regression tests for the model configuration chain: user-saved provider settings
/// must survive partial updates, keep secrets out of the content store, and refuse
/// blind writes (missing If-Match) and destructive deletes/disables of providers
/// still bound to a model route. These pin the 2026-09-04 configuration-drift fixes
/// (24 historical failed runs died on "missing provider instance" / "disabled").
/// </summary>
public sealed class ControlPlaneConfigGuardTests
{
    // ── If-Match enforcement ────────────────────────────────────────────────

    [Fact]
    public async Task PutProvider_WithoutIfMatch_IsRejected428()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, revision) = await CreateProviderAsync(client);

        var response = await PutProviderAsync(client, providerId, new { display_name = "Renamed" }, ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("precondition_required", body.GetProperty("code").GetString());

        // The blind write must not have landed.
        var current = await GetProviderAsync(client, providerId);
        Assert.Equal(revision, current.GetProperty("revision").GetInt64());
        Assert.Equal("guard-provider", current.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task PutProvider_WithStaleIfMatch_IsRejected412()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, revision) = await CreateProviderAsync(client);

        var stale = await PutProviderAsync(client, providerId, new { display_name = "First" }, revision);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var newRevision = (await GetProviderAsync(client, providerId)).GetProperty("revision").GetInt64();
        Assert.Equal(revision + 1, newRevision);

        var replay = await PutProviderAsync(client, providerId, new { display_name = "Second" }, revision);
        Assert.Equal(HttpStatusCode.PreconditionFailed, replay.StatusCode);
        Assert.Equal(newRevision, (await GetProviderAsync(client, providerId)).GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task DeleteRouteBoundProvider_WithoutIfMatch_IsRejected428()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, _) = await CreateProviderAsync(client);

        var response = await client.DeleteAsync($"/api/v1/model-providers/{providerId}");
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var providers = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/model-providers");
        Assert.Contains(providers!, p => p.GetProperty("id").GetGuid() == providerId);
    }

    [Fact]
    public async Task PutRoute_WithoutIfMatch_IsRejected428_WhenRouteExists()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, _) = await CreateProviderAsync(client);
        await BindChatRouteAsync(client, providerId, "guard-model");

        // Route exists (DevSeed or prior bind), so the PUT is a conditional update.
        var response = await PutRouteAsync(client, providerId, "guard-model-2", ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    // ── Partial-update merge semantics ──────────────────────────────────────

    [Fact]
    public async Task PutProvider_PartialBody_KeepsUnspecifiedConfigFields()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, _) = await CreateProviderAsync(client, withProtocol: true);

        var revision = (await GetProviderAsync(client, providerId)).GetProperty("revision").GetInt64();
        var response = await PutProviderAsync(client, providerId, new { models = new[] { "m-one", "m-two" }, model = "m-one" }, revision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var current = await GetProviderAsync(client, providerId);
        Assert.Equal("m-one", current.GetProperty("model").GetString());
        Assert.Equal(2, current.GetProperty("models").GetArrayLength());
        // Fields absent from the partial body must survive the update.
        Assert.Equal("http://127.0.0.1:59999/v1", current.GetProperty("base_url").GetString());
        Assert.Equal("openai-chat", current.GetProperty("protocol").GetString());
        Assert.Equal("guard.launch --args", current.GetProperty("launch_args").GetString());
        Assert.Equal("http://127.0.0.1:59999", current.GetProperty("server_url").GetString());
    }

    [Fact]
    public async Task PutProvider_ExplicitNull_ClearsOptionalField()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, _) = await CreateProviderAsync(client);

        var revision = (await GetProviderAsync(client, providerId)).GetProperty("revision").GetInt64();
        var payload = """
            { "model": null }
            """;
        var response = await PutProviderAsync(client, providerId, JsonDocument.Parse(payload).RootElement, revision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var current = await GetProviderAsync(client, providerId);
        Assert.True(!current.TryGetProperty("model", out var modelValue) || modelValue.ValueKind is JsonValueKind.Null,
            "explicit null must clear the model field");
        // Unspecified fields still survive.
        Assert.Equal("http://127.0.0.1:59999/v1", current.GetProperty("base_url").GetString());
    }

    // ── Secret never lands in the content store ─────────────────────────────

    [Fact]
    public async Task PutProvider_WithApiKey_StoresSecretOnlyInSecretStore()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, _) = await CreateProviderAsync(client, apiKey: "sk-guard-secret-123");

        var current = await GetProviderAsync(client, providerId);
        Assert.True(current.GetProperty("has_api_key").GetBoolean());

        // The persisted config blob must not contain the key (or its transport fields).
        var blob = await factory.ReadProviderConfigBlobAsync(providerId);
        Assert.DoesNotContain("sk-guard-secret-123", blob);
        Assert.DoesNotContain("api_key", blob);
        Assert.DoesNotContain("clear_api_key", blob);

        // SecretStore still holds the credential for runtime resolution.
        Assert.Equal("sk-guard-secret-123", await factory.SecretStore.GetAsync($"provider-{providerId:N}"));
    }

    [Fact]
    public async Task PutProvider_ClearApiKey_RemovesSecretButKeepsOtherFields()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, _) = await CreateProviderAsync(client, apiKey: "sk-clear-me");

        var revision = (await GetProviderAsync(client, providerId)).GetProperty("revision").GetInt64();
        var response = await PutProviderAsync(client, providerId, new { clear_api_key = true }, revision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var current = await GetProviderAsync(client, providerId);
        Assert.False(current.GetProperty("has_api_key").GetBoolean());
        Assert.Equal("http://127.0.0.1:59999/v1", current.GetProperty("base_url").GetString());
        // The provider row must no longer reference a secret.
        Assert.True(await factory.ProviderSecretReferenceIsNullAsync(providerId));
    }

    // ── provider_in_use reference guard ─────────────────────────────────────

    [Fact]
    public async Task DeleteProvider_ReferencedByChatRoute_Returns409_ThenForceSucceeds()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, revision) = await CreateProviderAsync(client);
        await BindChatRouteAsync(client, providerId, "guard-model");

        var blockedResponse = await client.DeleteAsync($"/api/v1/model-providers/{providerId}?force=false");
        Assert.Equal(HttpStatusCode.PreconditionRequired, blockedResponse.StatusCode);
        var conditional = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/model-providers/{providerId}");
        conditional.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        using var blocked = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("provider_in_use", body.GetProperty("code").GetString());
        Assert.Equal("chat", body.GetProperty("routes")[0].GetString());

        using var forced = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/model-providers/{providerId}?force=true");
        forced.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        using var forceResponse = await client.SendAsync(forced);
        Assert.Equal(HttpStatusCode.NoContent, forceResponse.StatusCode);

        // After a forced delete the runtime consequence is honest: readiness degrades.
        var readiness = await client.GetFromJsonAsync<JsonElement>("/api/v1/model-readiness");
        Assert.Equal("blocked", readiness.GetProperty("routes")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task DisableProvider_ReferencedByChatRoute_Returns409_ThenForceSucceeds()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, revision) = await CreateProviderAsync(client);
        await BindChatRouteAsync(client, providerId, "guard-model");

        using var blocked = await PutProviderAsync(client, providerId, new { enabled = false }, revision);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("provider_in_use", body.GetProperty("code").GetString());

        using var forced = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/model-providers/{providerId}?force=true")
        {
            Content = JsonContent.Create(new { enabled = false })
        };
        forced.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        using var forceResponse = await client.SendAsync(forced);
        Assert.Equal(HttpStatusCode.OK, forceResponse.StatusCode);
        Assert.False((await GetProviderAsync(client, providerId)).GetProperty("enabled").GetBoolean());

        var readiness = await client.GetFromJsonAsync<JsonElement>("/api/v1/model-readiness");
        Assert.Equal("blocked", readiness.GetProperty("routes")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeleteProvider_Unreferenced_SucceedsWithIfMatch()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, revision) = await CreateProviderAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/model-providers/{providerId}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── The happy path the whole fix exists for ──────────────────────────────

    [Fact]
    public async Task ProviderThenRouteBind_ThenReadiness_IsReady()
    {
        using var factory = new GuardFactory();
        using var client = factory.CreateClient();
        var (providerId, revision) = await CreateProviderAsync(client, apiKey: "sk-ready");

        var bound = await BindChatRouteAsync(client, providerId, "guard-model", revision: null);
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);

        var readiness = await client.GetFromJsonAsync<JsonElement>("/api/v1/model-readiness");
        Assert.Equal("ready", readiness.GetProperty("status").GetString());
        Assert.Equal("ready", readiness.GetProperty("routes")[0].GetProperty("status").GetString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<(Guid id, long revision)> CreateProviderAsync(
        HttpClient client, string? apiKey = null, bool withProtocol = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["driver"] = "openai",
            ["display_name"] = "guard-provider",
            ["connection_kind"] = "api-key",
            ["base_url"] = "http://127.0.0.1:59999/v1",
            ["model"] = "guard-model",
            ["server_url"] = "http://127.0.0.1:59999",
            ["launch_args"] = "guard.launch --args",
        };
        if (withProtocol) payload["protocol"] = "openai-chat";
        if (apiKey is not null) payload["api_key"] = apiKey;

        using var response = await client.PostAsJsonAsync("/api/v1/model-providers", payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"provider create failed: {response.StatusCode} {body}");
        var provider = JsonDocument.Parse(body).RootElement;
        return (provider.GetProperty("id").GetGuid(), provider.GetProperty("revision").GetInt64());
    }

    private static Task<HttpResponseMessage> PutProviderAsync(HttpClient client, Guid id, object payload, long? ifMatch)
        => PutProviderAsync(client, id, JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement, ifMatch);

    private static async Task<HttpResponseMessage> PutProviderAsync(HttpClient client, Guid id, JsonElement payload, long? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/model-providers/{id}")
        {
            Content = JsonContent.Create(payload)
        };
        if (ifMatch is { } revision) request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutRouteAsync(HttpClient client, Guid providerId, string model, long? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/model-routes/chat")
        {
            Content = JsonContent.Create(new { candidates = new[] { new { provider_instance_id = providerId, model } } })
        };
        if (ifMatch is { } revision) request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> BindChatRouteAsync(HttpClient client, Guid providerId, string model, long? revision = null)
    {
        // If the route already exists (DevSeed), resolve its current revision; a missing
        // If-Match on an existing route is exactly what the 428 guard rejects.
        long? ifMatch = revision;
        if (ifMatch is null)
        {
            var routes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/model-routes");
            var chat = routes?.FirstOrDefault(r => r.GetProperty("purpose").GetString() == "chat");
            if (chat is not null && chat.Value.ValueKind == JsonValueKind.Object) ifMatch = chat.Value.GetProperty("revision").GetInt64();
        }
        return await PutRouteAsync(client, providerId, model, ifMatch);
    }

    private static async Task<JsonElement> GetProviderAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/model-providers");
        response.EnsureSuccessStatusCode();
        var providers = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        return providers!.Single(p => p.GetProperty("id").GetGuid() == id);
    }

    /// <summary>Isolated host with a temp SQLite DB and an inspectable in-memory secret store.</summary>
    private sealed class GuardFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-config-guard-tests", Guid.NewGuid().ToString("N"));
        public TestModelSecretStore SecretStore { get; } = new();

        public GuardFactory() => Directory.CreateDirectory(_root);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(SecretStore);
            });
        }

        public async Task<string> ReadProviderConfigBlobAsync(Guid providerId)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ModelControlDbContext>>()
                .CreateDbContext();
            await using (db)
            {
                var row = await db.Providers.SingleAsync(p => p.Id == providerId);
                var version = await db.ProviderVersions.SingleAsync(v => v.Id == row.CurrentVersionId);
                var content = scope.ServiceProvider.GetRequiredService<IContentStore>();
                await using var stream = await content.OpenReadAsync(
                    new ContentReference(version.ContentReference, "", 0, "application/json"), default);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
        }

        public async Task<bool> ProviderSecretReferenceIsNullAsync(Guid providerId)
        {
            using var scope = Services.CreateScope();
            var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ModelControlDbContext>>()
                .CreateDbContextAsync();
            await using (db)
            {
                var row = await db.Providers.SingleAsync(p => p.Id == providerId);
                return row.SecretReference is null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
