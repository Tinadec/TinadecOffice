using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The tool host refuses an entire execution context when any read root is not a fully qualified
/// path ("read_roots paths must be absolute"), so a single malformed entry fails every tool call in
/// the project rather than just that resource.
///
/// A builtin Skill ships inside the tool host and has no filesystem root:
/// ToolSkillResourceService reports its RootPath as an empty string. That empty string must never
/// reach read_roots. It did, which made user tool actions fail with tool_error and turned
/// ApprovalFlowTests red; this pins the contract at the API that exposes the resolved context.
/// </summary>
public sealed class ToolReadRootsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-tool-read-roots", Guid.NewGuid().ToString("N"));
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
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EffectiveToolContextOnlyGrantsAbsoluteReadRoots()
    {
        var client = _factory!.CreateClient();
        var projectPath = Path.Combine(_root, "read-roots-project");
        Directory.CreateDirectory(projectPath);
        var created = await client.PostAsJsonAsync("/api/v1/projects", new { name = "Read roots", path = projectPath }, Json);
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var project = await created.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/api/v1/tools/settings/effective?project_id={projectId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var context = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(context.TryGetProperty("read_roots", out var readRoots), "the effective context must expose read_roots");
        Assert.Equal(JsonValueKind.Array, readRoots.ValueKind);

        var offenders = new List<string>();
        foreach (var root in readRoots.EnumerateArray())
        {
            var path = root.TryGetProperty("path", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            // Empty is the builtin-Skill shape and relative means a producer bug; both are what the
            // tool host rejects, so neither may appear here.
            if (path.Length == 0 || !Path.IsPathFullyQualified(path)) offenders.Add($"'{path}'");
        }
        Assert.Empty(offenders);

        // A builtin Skill must never contribute a read root: it has no filesystem root, and the
        // tool host rejects the whole context on one empty path. This route does not admit Skills
        // for a plain project, so the presence check is conditional; the end-to-end guard for the
        // builtin case is ApprovalFlowTests, which used to fail with
        // "read_roots paths must be absolute".
        var skills = context.GetProperty("skill_resources");
        var builtinRoots = skills.EnumerateArray()
            .Where(x => (x.TryGetProperty("source_kind", out var snake) ? snake.GetString() : null) == "builtin"
                || (x.TryGetProperty("sourceKind", out var camel) ? camel.GetString() : null) == "builtin")
            .Select(x => x.GetProperty("rootPath").GetString() ?? string.Empty)
            .ToArray();
        Assert.All(builtinRoots, root => Assert.Equal(string.Empty, root));
    }

    private sealed class Factory : IsolatedApiFactory
    {
        private readonly string _root;
        public Factory(string root) => _root = root;
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
            ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
}
