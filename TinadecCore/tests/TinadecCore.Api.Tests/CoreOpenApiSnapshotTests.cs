using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// OpenAPI snapshot drift gate — Core side.
/// CI: dotnet test TinadecCore/TinadecCore.slnx --no-build
///     git diff --exit-code -- TinadecGateway/tests/__snapshots__/openapi.external.json TinadecCore/tests/__snapshots__/openapi.core.json
/// Non-zero exit = contract drifted. Review diff, then git add the snapshot(s) if intentional.
/// </summary>
public sealed class CoreOpenApiSnapshotTests
{
    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string ResolveSnapshotPath()
    {
        // Prefer repo-level path: TinadecCore/tests/__snapshots__/openapi.core.json
        // Fallback to project-local: TinadecCore/tests/TinadecCore.Api.Tests/__snapshots__/openapi.core.json
        // Probe upward from test assembly location to find repo root (contains .git or TinadecCore.slnx).
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(probe, "..", "tests", "__snapshots__", "openapi.core.json"));
            // heuristic: repo root contains TinadecCore directory
            var repoRoot = Path.GetFullPath(Path.Combine(probe, "..", "..", "..", "..", ".."));
            var repoLevel = Path.Combine(repoRoot, "TinadecCore", "tests", "__snapshots__", "openapi.core.json");
            if (File.Exists(repoLevel) || Directory.Exists(Path.Combine(repoRoot, "TinadecCore"))) return repoLevel;
            if (File.Exists(candidate)) return candidate;
            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }
        // default when first run: repo-level path
        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TinadecCore", "tests", "__snapshots__", "openapi.core.json"));
        // If that doesn't look like repo, fallback to local project snapshot dir
        if (!fallback.Contains("TinadecCore")) fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "__snapshots__", "openapi.core.json"));
        return fallback;
    }

    private static object? Normalize(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => Normalize(p.Value), StringComparer.Ordinal) as object,
            JsonValueKind.Array => el.EnumerateArray().Select(Normalize).ToList() as object,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.GetRawText()
        };
    }

    private sealed class SnapshotFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-openapi-snap", Guid.NewGuid().ToString("N"));
        public SnapshotFactory() => Directory.CreateDirectory(_root);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CoreOpenApi_Snapshot_TitleContainsCore_AndFileBaseline()
    {
        using var factory = new SnapshotFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/openapi/core.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("openapi", out var ver) && ver.ValueKind == JsonValueKind.String, "openapi field must exist");
        Assert.True(root.TryGetProperty("info", out var info), "info must exist");
        Assert.True(info.TryGetProperty("title", out var titleEl), "info.title must exist");
        var title = titleEl.GetString() ?? "";
        Assert.Contains("TinadecCore", title, StringComparison.OrdinalIgnoreCase);
        Assert.True(root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object, "paths must be an object");
        Assert.True(paths.EnumerateObject().Any(), "openapi.paths must be non-empty");
        Assert.DoesNotContain("\"nonce\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nonce_secret_reference", body, StringComparison.OrdinalIgnoreCase);

        var snapshotPath = ResolveSnapshotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);

        var normalized = Normalize(root);
        var serialized = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });

        if (File.Exists(snapshotPath))
        {
            var expectedText = await File.ReadAllTextAsync(snapshotPath);
            using var expectedDoc = JsonDocument.Parse(expectedText);
            var expectedNorm = Normalize(expectedDoc.RootElement);
            var expectedJson = JsonSerializer.Serialize(expectedNorm, new JsonSerializerOptions { WriteIndented = true });
            Assert.True(serialized == expectedJson, $"OpenAPI snapshot drift at {snapshotPath}. Run dotnet test then git diff --exit-code to gate CI; git add the snapshot if intentional.");
        }

        await File.WriteAllTextAsync(snapshotPath, serialized + Environment.NewLine);

        // Also ensure sibling local path stays in sync if repo-level was used (optional, no-op if same)
        var altPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "__snapshots__", "openapi.core.json"));
        if (!string.Equals(Path.GetFullPath(snapshotPath), Path.GetFullPath(altPath), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.GetDirectoryName(altPath)!))
        {
            // don't overwrite silently if alt exists and differs — leave drift to be caught via git diff
        }
    }
}
