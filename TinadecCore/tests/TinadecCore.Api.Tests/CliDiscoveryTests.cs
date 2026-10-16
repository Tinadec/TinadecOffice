using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
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
    public async Task DiscoverHarnesses_ReturnsEveryCatalogHarnessWithAStatus()
    {
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var clis = payload.GetProperty("cli_runtimes").EnumerateArray().ToList();
        // Counted against the catalog rather than a number copied into the test: the row set is a
        // fact about HarnessCatalog, and a literal here goes stale the moment a harness is added.
        Assert.Equal(HarnessCatalog.All.Count, clis.Count);
        Assert.Equal(HarnessCatalog.All.Select(spec => spec.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            clis.Select(cli => cli.GetProperty("driver").GetString()!).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        foreach (var cli in clis)
        {
            var status = cli.GetProperty("status").GetString();
            Assert.NotNull(cli.GetProperty("driver").GetString());
            Assert.Contains(status, new[] { "found", "missing", "configured" });
        }
    }

    /// <summary>
    /// The channel list is what the model center renders as buttons, so two things have to be true in
    /// the payload: a harness is never offered on a channel it does not speak, and a channel this
    /// build cannot drive is stated as undrivable with a reason instead of being silently present.
    /// </summary>
    [Fact]
    public async Task DiscoverHarnesses_StatesEachChannelAndWhatThisBuildCanDrive()
    {
        var binary = WriteRunnableStub("dsh");
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var dsh = payload.GetProperty("cli_runtimes").EnumerateArray()
            .First(item => item.GetProperty("driver").GetString() == "dsh");
        Assert.Equal("found", dsh.GetProperty("status").GetString());
        Assert.Equal(Path.GetFullPath(binary), Path.GetFullPath(dsh.GetProperty("binary_path").GetString()!));

        var channels = dsh.GetProperty("channels").EnumerateArray().ToList();
        Assert.Equal(new[] { "acp", "cli", "tui" }, channels.Select(c => c.GetProperty("channel").GetString()).ToArray());
        Assert.True(channels.Single(c => c.GetProperty("channel").GetString() == "acp").GetProperty("drivable").GetBoolean());
        foreach (var undrivable in channels.Where(c => !c.GetProperty("drivable").GetBoolean()))
        {
            Assert.False(string.IsNullOrWhiteSpace(undrivable.GetProperty("reason").GetString()),
                $"{undrivable.GetProperty("channel").GetString()} is offered as unavailable without saying why");
        }

        // claude-code is the case the old table got wrong: it has no ACP endpoint, so the channel must
        // not appear at all rather than appear and fail to connect.
        var claude = payload.GetProperty("cli_runtimes").EnumerateArray()
            .First(item => item.GetProperty("driver").GetString() == "claude-code");
        Assert.DoesNotContain("acp", claude.GetProperty("channels").EnumerateArray()
            .Select(c => c.GetProperty("channel").GetString()));
    }

    [Fact]
    public async Task DiscoverHarnesses_MarksDetectedExecutableAsFoundWithResolvedPath()
    {
        var binary = WriteRunnableStub("claude");
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var claude = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "claude-code");
        Assert.Equal("found", claude.GetProperty("status").GetString());
        Assert.Equal(Path.GetFullPath(binary), Path.GetFullPath(claude.GetProperty("binary_path").GetString()!));
    }

    [Fact]
    public async Task DiscoverHarnesses_MarksNonRunnableExecutableAsMissing()
    {
        WriteRunnableStub("claude");
        var dead = Path.Combine(_searchBin, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        File.WriteAllText(dead, "");
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var codex = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "codex");
        Assert.Equal("missing", codex.GetProperty("status").GetString());
    }

    /// <summary>
    /// The probe used to wait on the child while its own stdout stayed unread. A harness printing more
    /// than the pipe buffer blocks on its write, the wait expires, and discovery reports an installed
    /// binary as "not detected" — the failure class this round exists to end, so it needs a stub that
    /// is deliberately chatty rather than one that echoes a single line.
    /// </summary>
    [Fact]
    public async Task DiscoverHarnesses_ChattyBinaryIsStillFound()
    {
        WriteChattyStub("claude");
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var claude = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "claude-code");
        Assert.Equal("found", claude.GetProperty("status").GetString());
        // The API boundary omits nulls, so "no note" is either an absent key or a JSON null.
        Assert.True(!claude.TryGetProperty("probe_note", out var note) || note.ValueKind == JsonValueKind.Null,
            "a passing probe should not leave a note");
    }

    [Fact]
    public async Task DiscoverHarnesses_FailingProbeSaysWhichCommandItRan()
    {
        WriteFailingStub("claude", 3);
        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        var claude = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "claude-code");
        Assert.Equal("missing", claude.GetProperty("status").GetString());
        // "missing" alone reads as "not installed", which is a different claim from "it answered with
        // an error", so the row has to carry the difference for the user to act on it.
        var note = claude.GetProperty("probe_note").GetString();
        Assert.NotNull(note);
        Assert.Contains("3", note);
        // The same fact for a client that words it itself.
        Assert.Equal("exit_code", claude.GetProperty("probe_note_code").GetString());
        Assert.Equal("3", claude.GetProperty("probe_note_data").GetProperty("exit_code").GetString());
    }

    /// <summary>Writes a stub the --version probe actually passes: a .cmd shim on Windows, an
    /// executable shell script elsewhere.</summary>
    private string WriteRunnableStub(string name)
    {
        return WriteStub(name, OperatingSystem.IsWindows() ? "@echo ok\r\n" : "#!/bin/sh\necho ok\n");
    }

    /// <summary>A stub printing roughly 200 KB before exiting, which is what overruns the pipe buffer.</summary>
    private string WriteChattyStub(string name)
    {
        var line = new string('x', 50);
        return WriteStub(name, OperatingSystem.IsWindows()
            ? $"@echo off\r\nfor /l %%i in (1,1,4000) do @echo {line}\r\n"
            : $"#!/bin/sh\nfor i in $(seq 1 4000); do echo {line}; done\n");
    }

    private string WriteFailingStub(string name, int exitCode)
    {
        return WriteStub(name, OperatingSystem.IsWindows()
            ? $"@exit /b {exitCode}\r\n"
            : $"#!/bin/sh\nexit {exitCode}\n");
    }

    private string WriteStub(string name, string content)
    {
        var binary = Path.Combine(_searchBin, OperatingSystem.IsWindows() ? $"{name}.cmd" : name);
        File.WriteAllText(binary, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return binary;
    }

    [Fact]
    public async Task DiscoverHarnesses_MarksConfiguredDriverAsConfigured()
    {
        var binary = WriteRunnableStub("codex");
        var client = _factory!.CreateClient();
        var create = await client.PostAsync("/api/v1/model-providers", JsonContent(new
        {
            driver = "codex",
            display_name = "Codex",
            connection_kind = "cli",
            protocol = "acp",
            binary_path = binary,
            enabled = true
        }));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());

        using var scope = _factory!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var payload = await ReadBodyAsync((IResult)await service.DiscoverHarnesses(CancellationToken.None, new[] { _searchBin }), scope.ServiceProvider);

        // Matched by the catalog id: the row is configured because a provider with *that harness id*
        // exists, which is also why the pre-catalog driver names ("codex-cli") no longer appear here.
        var codex = payload.GetProperty("cli_runtimes").EnumerateArray().First(item => item.GetProperty("driver").GetString() == "codex");
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