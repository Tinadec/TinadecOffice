using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The process key hashes the context's grant boundary, and MaterializeForCallAsync
/// rewrites the primary root's Path when a cwd falls outside every frozen source
/// (ToolConfigurationResolver.cs:183-185). A terminal control call that re-derived its
/// context from the session's workspace root could therefore select a different Tools
/// child — one that has never heard of the session. These tests pin that the control
/// call lands on the child the creating call started, even under that drift.
/// Only read-only <c>ls</c> calls are made; no terminal session is created or killed.
/// </summary>
public sealed class TerminalProcessKeyTests : IDisposable
{
    private static readonly string ExecutablePath = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "TinadecTools.exe" : "TinadecTools");

    private readonly string _root;
    private readonly string _workspaceRoot;
    private readonly string _nested;
    private readonly TinadecToolsProcessManager _manager;

    public TerminalProcessKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tinadec-terminal-key-tests", Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_root, "workspace");
        _nested = Path.Combine(_workspaceRoot, "nested");
        Directory.CreateDirectory(_nested);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecTools:ExecutablePath"] = ExecutablePath,
                ["TinadecTools:StartupTimeoutSeconds"] = "30",
                ["TinadecTools:DefaultTimeoutSeconds"] = "60",
                ["TinadecStorage:UserRoot"] = Path.Combine(_root, "user")
            })
            .Build();
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TinadecToolsProcessManager>();
        _manager = new TinadecToolsProcessManager(configuration, logger);
        Assert.True(File.Exists(ExecutablePath), $"TinadecTools apphost missing at {ExecutablePath}.");
    }

    public void Dispose()
    {
        _manager.ShutdownAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ControlCall_UnderContextDrift_StaysOnTheChildThatCreatedTheSession()
    {
        var runId = Guid.NewGuid().ToString();
        var created = Context(runId, _workspaceRoot);
        var drifted = Context(runId, _nested);

        // The hazard is real: the two contexts select different children. Without this
        // assertion the reuse test below could pass on a key that never drifts at all.
        Assert.NotEqual(
            TinadecToolsProcessManager.ComputeProcessKey(_workspaceRoot, created),
            TinadecToolsProcessManager.ComputeProcessKey(_workspaceRoot, drifted));

        var creating = await _manager.CallAsync(_workspaceRoot, Request(created));
        Assert.True(creating.IsSuccess, creating.Error);

        var ownedKey = _manager.ResolveOwnedProcessKey(runId);
        Assert.NotNull(ownedKey);
        Assert.Equal(new[] { ownedKey! }, _manager.OwnedProcessKeysForTests(runId));

        // The child may well reject a context whose primary root no longer matches its workspace;
        // what this pins is which child the call was routed to, and that is decided and recorded
        // before the wire call is sent.
        await _manager.CallForRunAsync(runId, _workspaceRoot, Request(drifted));

        // The user-facing invariant: creation and control resolve to the same process key.
        Assert.Equal(new[] { ownedKey! }, _manager.OwnedProcessKeysForTests(runId));
        Assert.Equal(ownedKey, _manager.ResolveOwnedProcessKey(runId));
        Assert.Equal(TinadecToolsProcessManager.ComputeProcessKey(_workspaceRoot, created), ownedKey);
    }

    [Fact]
    public async Task ControlCall_WhenTheRunOwnsNoChild_KeepsTheOrdinaryContextResolution()
    {
        // A host restart clears the owned-key map, and the authoritative sessions live in
        // the child, so they are gone with it. Resolution must fall back, not throw.
        var runId = Guid.NewGuid().ToString();
        Assert.Null(_manager.ResolveOwnedProcessKey(runId));
        Assert.Empty(_manager.OwnedProcessKeysForTests(runId));

        var context = Context(runId, _workspaceRoot);
        var response = await _manager.CallForRunAsync(runId, _workspaceRoot, Request(context));

        Assert.True(response.IsSuccess, response.Error);
        Assert.Equal(
            new[] { TinadecToolsProcessManager.ComputeProcessKey(_workspaceRoot, context) },
            _manager.OwnedProcessKeysForTests(runId));
    }

    private ToolExecutionContextDto Context(string runId, string primaryPath) => new()
    {
        StorageId = "terminal-key-test",
        StorageRoot = Path.Combine(_root, "storage"),
        ProjectRoot = _workspaceRoot,
        RunId = runId,
        WorkingDirectory = primaryPath,
        PrimaryRootId = "primary",
        Settings = JsonDocument.Parse("{}").RootElement.Clone(),
        AllowedToolIds = ["ls"],
        WorkspaceRoots = [new ToolWorkspaceRootDto { Id = "primary", Path = primaryPath }]
    };

    private static ToolWireRequestDto Request(ToolExecutionContextDto context) => new()
    {
        ToolId = "ls",
        SessionId = context.RunId!,
        Approved = true,
        Params = JsonDocument.Parse("""{"path":".","limit":10}""").RootElement.Clone(),
        ExecutionContext = context
    };
}