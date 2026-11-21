using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class ModelProfileTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("model-profile-tests");
    private Factory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new Factory(_root, managed: false);
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
    public async Task Providers_PersistAndPreserveModelProfiles_AlongsideModelParameters()
    {
        using var client = _factory!.CreateClient();

        // Create without model_profiles -> backward-compatible empty map
        var minimalResp = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Minimal Provider",
            connection_kind = "api-key",
            base_url = "https://api.example.com/v1",
            model = "gpt-4o-mini"
        });
        Assert.Equal(HttpStatusCode.OK, minimalResp.StatusCode);
        var minimal = await minimalResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(minimal.TryGetProperty("model_profiles", out var emptyProfiles));
        Assert.Equal(JsonValueKind.Object, emptyProfiles.ValueKind);
        Assert.Empty(emptyProfiles.EnumerateObject().ToArray());

        // Create with both model_parameters and model_profiles -> neither interferes with ModelParameters strict check
        var createResp = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Profiled Provider",
            connection_kind = "api-key",
            base_url = "https://api.example.com/v1",
            model = "gpt-4o",
            models = new[] { "gpt-4o", "gpt-4o-mini" },
            model_parameters = new Dictionary<string, object>
            {
                ["gpt-4o"] = new { temperature = 0.25, reasoning_effort = "medium" }
            },
            model_profiles = new Dictionary<string, object>
            {
                ["gpt-4o"] = new
                {
                    specialties = new[] { "reasoning", "code", "Code" },
                    tier = "DEEP",
                    notes = "  High accuracy planner model  ",
                    context_window = 128000,
                    cost_tier = "HIGH"
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        Assert.NotNull(createResp.Headers.ETag);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var rev = created.GetProperty("revision").GetInt64();

        var profile = created.GetProperty("model_profiles").GetProperty("gpt-4o");
        Assert.Equal("deep", profile.GetProperty("tier").GetString());
        Assert.Equal("High accuracy planner model", profile.GetProperty("notes").GetString());
        Assert.Equal(128000, profile.GetProperty("context_window").GetInt32());
        Assert.Equal("high", profile.GetProperty("cost_tier").GetString());
        var specialties = profile.GetProperty("specialties").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "reasoning", "code" }, specialties);
        Assert.Equal(0.25d, created.GetProperty("model_parameters").GetProperty("gpt-4o").GetProperty("temperature").GetDouble(), 2);

        // GET list returns persisted model_profiles
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/model-providers");
        var fetched = listed.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal("deep", fetched.GetProperty("model_profiles").GetProperty("gpt-4o").GetProperty("tier").GetString());

        // Partial PUT without model_profiles preserves existing profiles
        var partialPut = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/model-providers/{id}")
        {
            Content = JsonContent.Create(new { display_name = "Renamed Profiled Provider" })
        };
        partialPut.Headers.TryAddWithoutValidation("If-Match", $"\"{rev}\"");
        var partialResp = await client.SendAsync(partialPut);
        Assert.Equal(HttpStatusCode.OK, partialResp.StatusCode);
        var partialBody = await partialResp.Content.ReadFromJsonAsync<JsonElement>();
        var rev2 = partialBody.GetProperty("revision").GetInt64();
        Assert.Equal("Renamed Profiled Provider", partialBody.GetProperty("display_name").GetString());
        Assert.Equal("deep", partialBody.GetProperty("model_profiles").GetProperty("gpt-4o").GetProperty("tier").GetString());

        // Explicit model_profiles: null clears stored profiles
        var clearPut = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/model-providers/{id}")
        {
            Content = JsonContent.Create(new Dictionary<string, object?> { ["model_profiles"] = null })
        };
        clearPut.Headers.TryAddWithoutValidation("If-Match", $"\"{rev2}\"");
        var clearResp = await client.SendAsync(clearPut);
        Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);
        var cleared = await clearResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(cleared.GetProperty("model_profiles").EnumerateObject().ToArray());
    }

    [Fact]
    public async Task Providers_RejectInvalidModelProfiles_AndKeepModelParametersStrict()
    {
        using var client = _factory!.CreateClient();

        // Invalid tier
        var badTier = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Bad Tier",
            connection_kind = "api-key",
            model_profiles = new { gpt4 = new { tier = "ultra" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, badTier.StatusCode);
        Assert.Equal("invalid_model_profile", (await badTier.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Notes exceeding 500 chars
        var badNotes = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Bad Notes",
            connection_kind = "api-key",
            model_profiles = new { gpt4 = new { notes = new string('a', 501) } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, badNotes.StatusCode);
        Assert.Equal("invalid_model_profile", (await badNotes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Non-positive context_window
        var badWindow = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Bad Window",
            connection_kind = "api-key",
            model_profiles = new { gpt4 = new { context_window = 0 } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, badWindow.StatusCode);

        // Unknown field inside model_profiles
        var unknownField = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Bad Unknown",
            connection_kind = "api-key",
            model_profiles = new { gpt4 = new { api_key = "secret" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);

        // Placing model_profiles inside model_parameters must fail ModelParameters strict check
        var insideParams = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Bad Nested",
            connection_kind = "api-key",
            model_parameters = new
            {
                gpt4 = new { model_profiles = new { tier = "fast" } }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, insideParams.StatusCode);
    }

    [Fact]
    public async Task ModelProfileCatalogService_ReturnsSanitizedCatalog_AndUpdatesProfileWithRevisionGuard()
    {
        using var client = _factory!.CreateClient();
        const string secretValue = "sk-top-secret-model-profile-test-999";

        var enabledResp = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Enabled Primary",
            connection_kind = "api-key",
            base_url = "https://api.example.com/v1",
            model = "gpt-4o-mini",
            models = new[] { "gpt-4o-mini", "o3-mini" },
            api_key = secretValue,
            enabled = true,
            model_parameters = new Dictionary<string, object>
            {
                ["gpt-4o-mini"] = new { temperature = 0.1 }
            },
            model_profiles = new Dictionary<string, object>
            {
                ["o3-mini"] = new { tier = "deep", specialties = new[] { "math" } }
            }
        });
        Assert.Equal(HttpStatusCode.OK, enabledResp.StatusCode);
        var enabledJson = await enabledResp.Content.ReadFromJsonAsync<JsonElement>();
        var enabledId = enabledJson.GetProperty("id").GetGuid();
        var enabledRev = enabledJson.GetProperty("revision").GetInt64();

        var disabledResp = await client.PostAsJsonAsync("/api/v1/model-providers", new
        {
            driver = "openai",
            display_name = "Disabled Secondary",
            connection_kind = "api-key",
            base_url = "https://api.example.com/v1",
            model = "gpt-disabled",
            enabled = false
        });
        Assert.Equal(HttpStatusCode.OK, disabledResp.StatusCode);
        var disabledId = (await disabledResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Add route and cooling health row
        var routePut = new HttpRequestMessage(HttpMethod.Put, "/api/v1/model-routes/execution")
        {
            Content = JsonContent.Create(new
            {
                candidates = new[] { new { provider_instance_id = enabledId, model = "gpt-4o-mini" } }
            })
        };
        var routeResp = await client.SendAsync(routePut);
        if (routeResp.StatusCode == (HttpStatusCode)428)
        {
            // Retry with current revision if execution route already exists in DevSeed
            var routes = await client.GetFromJsonAsync<JsonElement>("/api/v1/model-routes");
            var execRev = routes.EnumerateArray().First(r => r.GetProperty("purpose").GetString() == "execution").GetProperty("revision").GetInt64();
            var retry = new HttpRequestMessage(HttpMethod.Put, "/api/v1/model-routes/execution")
            {
                Content = JsonContent.Create(new
                {
                    candidates = new[] { new { provider_instance_id = enabledId, model = "gpt-4o-mini" } }
                })
            };
            retry.Headers.TryAddWithoutValidation("If-Match", $"\"{execRev}\"");
            routeResp = await client.SendAsync(retry);
        }
        Assert.Equal(HttpStatusCode.OK, routeResp.StatusCode);

        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var modelDbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ModelControlDbContext>>();
        await using (var db = await modelDbFactory.CreateDbContextAsync())
        {
            db.Health.Add(new ModelHealthRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId ?? Guid.Empty,
                ProviderInstanceId = enabledId,
                Model = "gpt-4o-mini",
                ConsecutiveFailures = 3,
                CoolingUntil = DateTimeOffset.UtcNow.AddMinutes(5),
                LastCategory = "rate_limit",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var catalogService = _factory.Services.GetRequiredService<IModelProfileCatalogService>();
        var catalog = await catalogService.GetSanitizedCatalogAsync(includeDisabled: false);
        Assert.Contains(catalog.Providers, p => p.Id == enabledId && p.HasApiKey);
        Assert.DoesNotContain(catalog.Providers, p => p.Id == disabledId);
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(catalog), StringComparison.Ordinal);

        var enabledProvider = catalog.Providers.Single(p => p.Id == enabledId);
        Assert.Equal("deep", enabledProvider.ModelProfiles["o3-mini"].Tier);
        Assert.Single(enabledProvider.ModelParameters);
        Assert.Contains(catalog.Health, h => h.ProviderInstanceId == enabledId && h.Model == "gpt-4o-mini" && h.IsCooling);

        var fullCatalog = await catalogService.GetSanitizedCatalogAsync(includeDisabled: true);
        Assert.Contains(fullCatalog.Providers, p => p.Id == disabledId);

        // Stale revision must throw 412 without mutating
        var ex = await Assert.ThrowsAsync<ModelProfileException>(() =>
            catalogService.UpdateModelProfileAsync(
                enabledId,
                "gpt-4o-mini",
                new ModelProfileDto { Tier = "fast", CostTier = "low", Specialties = ["triage"] },
                expectedRevision: enabledRev + 99));
        Assert.Equal(412, ex.StatusCode);

        // Valid update writes profile, keeps secret & other profiles intact, increments revision
        var updated = await catalogService.UpdateModelProfileAsync(
            enabledId,
            "gpt-4o-mini",
            new ModelProfileDto
            {
                Tier = "fast",
                CostTier = "low",
                ContextWindow = 64000,
                Specialties = ["triage", "summary"],
                Notes = "Quick worker default"
            },
            expectedRevision: enabledRev);

        Assert.Equal(enabledRev + 1, updated.Revision);
        Assert.Equal("fast", updated.Profile!.Tier);
        Assert.True(updated.ModelProfiles.ContainsKey("o3-mini"));
        Assert.True(updated.ModelProfiles.ContainsKey("gpt-4o-mini"));

        // Ensure secret remained untouched in ISecretStore and provider config
        var afterCatalog = await catalogService.GetSanitizedCatalogAsync();
        var afterProvider = afterCatalog.Providers.Single(p => p.Id == enabledId);
        Assert.True(afterProvider.HasApiKey);
        await using (var db = await modelDbFactory.CreateDbContextAsync())
        {
            var providerRow = await db.Providers.SingleAsync(p => p.Id == enabledId);
            var secrets = _factory.Services.GetRequiredService<ISecretStore>();
            Assert.Equal(secretValue, await secrets.GetAsync(providerRow.SecretReference!));
        }

        // Passing null removes just that model's profile
        var removed = await catalogService.UpdateModelProfileAsync(enabledId, "gpt-4o-mini", null, expectedRevision: updated.Revision);
        Assert.False(removed.ModelProfiles.ContainsKey("gpt-4o-mini"));
        Assert.True(removed.ModelProfiles.ContainsKey("o3-mini"));
    }

    [Fact]
    public async Task ManagedStorage_ProjectsModelProfilesIntoModelsToml_WithoutSecrets()
    {
        var managedRoot = ApiTestStorage.CreateRoot("model-profile-managed");
        Directory.CreateDirectory(managedRoot);
        try
        {
            using var managedFactory = new Factory(managedRoot, managed: true);
            using var client = managedFactory.CreateClient();
            const string secretValue = "sk-managed-toml-secret-000";

            var resp = await client.PostAsJsonAsync("/api/v1/model-providers", new
            {
                driver = "anthropic",
                display_name = "TOML Projected Provider",
                connection_kind = "api-key",
                base_url = "https://api.anthropic.com/v1",
                model = "claude-sonnet-4-6",
                models = new[] { "claude-sonnet-4-6" },
                api_key = secretValue,
                model_profiles = new Dictionary<string, object>
                {
                    ["claude-sonnet-4-6"] = new
                    {
                        specialties = new[] { "architecture", "review" },
                        tier = "deep",
                        context_window = 200000,
                        cost_tier = "medium"
                    }
                }
            });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            var tomlFiles = Directory.GetFiles(managedRoot, "models.toml", SearchOption.AllDirectories);
            Assert.NotEmpty(tomlFiles);
            var tomlContent = await File.ReadAllTextAsync(tomlFiles[0]);
            Assert.Contains("model_profiles", tomlContent, StringComparison.Ordinal);
            Assert.Contains("architecture", tomlContent, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, tomlContent, StringComparison.Ordinal);

            var catalogService = managedFactory.Services.GetRequiredService<IModelProfileCatalogService>();
            var catalog = await catalogService.GetSanitizedCatalogAsync();
            var provider = catalog.Providers.Single(p => p.Id == id);
            Assert.Equal("deep", provider.ModelProfiles["claude-sonnet-4-6"].Tier);
            Assert.Equal(200000, provider.ModelProfiles["claude-sonnet-4-6"].ContextWindow);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(managedRoot)) Directory.Delete(managedRoot, true);
        }
    }

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;
        private readonly bool _managed;

        public Factory(string root, bool managed)
        {
            _root = root;
            _managed = managed;
        }

        protected override bool UsesManagedStorage => _managed;
        protected override string? ManagedUserRoot => _managed ? _root : null;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            var settings = new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Warning" };
            if (_managed)
            {
                settings["TinadecStorage:Enabled"] = "true";
                settings["TinadecStorage:UserRoot"] = _root;
                settings["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(_root, "workspace");
            }
            else
            {
                settings["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db");
                settings["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data");
            }
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
        }
    }
}
