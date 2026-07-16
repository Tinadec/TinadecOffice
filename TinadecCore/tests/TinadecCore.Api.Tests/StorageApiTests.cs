using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed class StorageApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-core-storage-tests", Guid.NewGuid().ToString("N"));
    private StorageFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new StorageFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProjectSessionAndHistory_AreStoredWithSnakeCaseCompatibility()
    {
        var client = _factory!.CreateClient();
        var rootPath = Path.Combine(_root, "workspace");
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Storage test", path = rootPath });
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        using var projectJson = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync());
        Assert.True(projectJson.RootElement.TryGetProperty("path", out _));
        var projectId = projectJson.RootElement.GetProperty("id").GetGuid();

        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Session" });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        Assert.True(sessionJson.RootElement.TryGetProperty("project_id", out _));
        var sessionId = sessionJson.RootElement.GetProperty("id").GetGuid();

        var messageResponse = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Hello history" });
        Assert.Equal(HttpStatusCode.Created, messageResponse.StatusCode);
        var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        Assert.Single(messages!);
        Assert.Equal("Hello history", messages![0].GetProperty("content").GetString());
        Assert.True(File.Exists(Path.Combine(_root, "data", "sessions", sessionId + ".json")));

        var duplicate = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Duplicate", path = rootPath + Path.DirectorySeparatorChar });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task RunEvents_AreIndexedReplayedAndRemainBelowDataRoot()
    {
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Event test", path = Path.Combine(_root, "workspace-events") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "Event session" })).Content.ReadFromJsonAsync<JsonElement>();
        var message = await (await client.PostAsJsonAsync($"/api/v1/sessions/{session.GetProperty("id").GetGuid()}/messages", new { content = "Trigger" })).Content.ReadFromJsonAsync<JsonElement>();

        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(session.GetProperty("id").GetGuid(), message.GetProperty("id").GetGuid());
        await lifecycle.AppendEventAsync(run.Id, "run.started", new { source = "test" }, "Run started");
        var events = await lifecycle.ReplayEventsAsync(session.GetProperty("id").GetGuid(), 0);

        Assert.Single(events);
        Assert.Equal("run.started", events[0].EventType);
        Assert.True(File.Exists(Path.Combine(_root, "data", "tasks", run.Id + ".tasks.json")));
        Assert.True(File.Exists(Path.Combine(_root, "data", "events", run.Id + ".events.jsonl")));
    }

    private sealed class StorageFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public StorageFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
