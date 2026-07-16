using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>Runs in CI when a real PostgreSQL service is explicitly configured.</summary>
public sealed class PostgreSqlStorageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-core-postgres-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("TINADEC_TEST_POSTGRES") != "1") return Task.CompletedTask;
        Directory.CreateDirectory(_root);
        _factory = new PostgreSqlFactory(_root, Environment.GetEnvironmentVariable("TINADEC_TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("TINADEC_TEST_POSTGRES_CONNECTION is required for PostgreSQL storage tests."));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProviderSpecificMigrations_SupportTheStorageContract()
    {
        if (_factory is null) return;

        var client = _factory.CreateClient();
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "PostgreSQL storage", path = Path.Combine(_root, "workspace") });
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "PostgreSQL session" });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var messageResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{session.GetProperty("id").GetGuid()}/messages", new { content = "PostgreSQL history" });
        Assert.Equal(HttpStatusCode.Created, messageResponse.StatusCode);
    }

    private sealed class PostgreSqlFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly string _connectionString;

        public PostgreSqlFactory(string root, string connectionString)
        {
            _root = root;
            _connectionString = connectionString;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Provider"] = "PostgreSql",
                ["TinadecPersistence:ApplyMigrationsOnStartup"] = "true",
                ["ConnectionStrings:TinadecCore"] = _connectionString,
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
