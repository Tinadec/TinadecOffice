using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

public sealed class CliDiscoveryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-cli-discovery", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;
    private string _searchBin = "";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _searchBin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_searchBin);
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
    public async Task DiscoverCliRuntimes_ReturnsAllKnownClisWithAStatus()
    {
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverCliRuntimes(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var clis = payload.GetProperty("cli_runtimes").EnumerateArray().ToList();
        Assert.Equal(4, clis.Count);
        foreach (var cli in clis)
        {
            var status = cli.GetProperty("status").GetString();
            Assert.NotNull(cli.GetProperty("driver").GetString());
            Assert.Contains(status, new[] { "found", "missing", "configured" });
        }
    }

    [Fact]
    public async Task DiscoverCliRuntimes_MarksDetectedExecutableAsFoundWithResolvedPath()
    {
        var binary = WriteRunnableStub("claude");
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverCliRuntimes(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var claude = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "claude-cli");
        Assert.Equal("found", claude.GetProperty("status").GetString());
        Assert.Equal(Path.GetFullPath(binary), Path.GetFullPath(claude.GetProperty("binary_path").GetString()!));
    }

    [Fact]
    public async Task DiscoverCliRuntimes_MarksNonRunnableExecutableAsMissing()
    {
        WriteRunnableStub("claude");
        var dead = Path.Combine(_searchBin, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        File.WriteAllText(dead, "");
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverCliRuntimes(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var codex = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "codex-cli");
        Assert.Equal("missing", codex.GetProperty("status").GetString());
    }

    /// <summary>Writes a stub the --version probe actually passes: a .cmd shim on Windows, an
    /// executable shell script elsewhere.</summary>
    private string WriteRunnableStub(string name)
    {
        var binary = Path.Combine(_searchBin, OperatingSystem.IsWindows() ? $"{name}.cmd" : name);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(binary, "@echo ok\r\n");
        }
        else
        {
            File.WriteAllText(binary, "#!/bin/sh\necho ok\n");
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return binary;
    }

    [Fact]
    public async Task DiscoverCliRuntimes_MarksConfiguredDriverAsConfigured()
    {
        var client = _factory!.CreateClient();
        var create = await client.PostAsync("/api/v1/model-providers", JsonContent(new
        {
            driver = "codex-cli",
            display_name = "Codex CLI",
            connection_kind = "cli",
            binary_path = _searchBin,
            enabled = true
        }));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());

        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverCliRuntimes(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var codex = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "codex-cli");
        Assert.Equal("configured", codex.GetProperty("status").GetString());
    }

    private static async Task<JsonElement> ReadBodyAsync(IResult result, IServiceProvider services)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

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