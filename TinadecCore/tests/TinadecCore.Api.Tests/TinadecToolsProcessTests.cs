using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Real-process tests against the TinadecTools apphost copied into the test
/// output directory by the project reference. Covers the v2 manifest handshake,
/// wire calls, approval gating at the registry, and process shutdown.
/// </summary>
public sealed class TinadecToolsProcessTests : IDisposable
{
    private static readonly string ExecutablePath = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "TinadecTools.exe" : "TinadecTools");

    private readonly string _workspaceRoot;
    private readonly TinadecToolsProcessManager _manager;
    private readonly ToolManifestDto _manifest;

    public TinadecToolsProcessTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "tinadec-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecTools:ExecutablePath"] = ExecutablePath,
                ["TinadecTools:StartupTimeoutSeconds"] = "30",
                ["TinadecTools:DefaultTimeoutSeconds"] = "120"
            })
            .Build();
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TinadecToolsProcessManager>();
        _manager = new TinadecToolsProcessManager(configuration, logger);
        Assert.True(File.Exists(ExecutablePath), $"TinadecTools apphost missing at {ExecutablePath}.");
        _manifest = _manager.EnsureStartedAsync(_workspaceRoot).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _manager.ShutdownAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_workspaceRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ManifestHandshake_ReturnsV2WithValidHash()
    {
        Assert.Equal(2, _manifest.ProtocolVersion);
        Assert.NotEmpty(_manifest.Tools);
        Assert.Contains(_manifest.Tools, tool => tool.Id == "ls");
        Assert.Equal(_manifest.ManifestHash, ToolManifestHasher.Compute(_manifest.Tools));
        Assert.Contains(_manifest.Tools, tool => tool.Id == "write_file" && tool.RequiresApproval && tool.MutatesWorkspace);
    }

    [Fact]
    public async Task CallAsync_ReadOnlyTool_ReturnsSuccess()
    {
        var response = await _manager.CallAsync(_workspaceRoot, new ToolWireRequestDto
        {
            ToolId = "ls",
            SessionId = "test",
            ToolCallId = 1,
            Approved = true,
            Params = JsonElementFrom("""{"path":".","limit":100}""")
        });

        Assert.True(response.IsSuccess, response.Error);
        Assert.NotNull(response.Result);
        Assert.True(response.Result!.Value.TryGetProperty("success", out var success) && success.GetBoolean());
    }

    [Fact]
    public async Task CallAsync_UnknownTool_ReturnsFailure()
    {
        var response = await _manager.CallAsync(_workspaceRoot, new ToolWireRequestDto
        {
            ToolId = "does_not_exist",
            SessionId = "test",
            ToolCallId = 2,
            Approved = true,
            Params = JsonElementFrom("{}")
        });

        Assert.False(response.IsSuccess);
        Assert.Contains("Unknown tool", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallAsync_WriteToolWithoutApproval_IsRejectedByRegistry()
    {
        var response = await _manager.CallAsync(_workspaceRoot, new ToolWireRequestDto
        {
            ToolId = "write_file",
            SessionId = "test",
            ToolCallId = 3,
            Approved = false,
            Params = JsonElementFrom("""{"path":"probe.txt","content":"x"}""")
        });

        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task Shutdown_ThenRestart_Succeeds()
    {
        await _manager.ShutdownAsync();
        var manifest = await _manager.EnsureStartedAsync(_workspaceRoot);
        Assert.Equal(2, manifest.ProtocolVersion);
        Assert.NotEmpty(manifest.Tools);
    }

    private static JsonElement JsonElementFrom(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}