using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Tools;
using System.Text.Json;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Git governance topology. Core ships no built-in roster any more: the shipped
/// shape lives in the bootstrap fixture pack (installed through the regular pack
/// pipeline), so these tests read that manifest instead of the retired TOML
/// directory.
/// </summary>
public sealed class GitTopologyTests
{
    [Fact]
    public void BootstrapDirectoryIncludesGitGovernanceAndWorkerRoles()
    {
        var (agents, modes) = LoadBootstrapDirectory();

        var steward = agents["git_steward"];
        Assert.Equal("operation", steward.GetProperty("layer").GetString());
        Assert.Equal("git_steward", steward.GetProperty("role").GetString());
        Assert.Empty(steward.GetProperty("tool_scope").EnumerateArray());
        var stewardCapabilities = steward.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Contains("git.review", stewardCapabilities);
        Assert.Contains("git.commit_plan", stewardCapabilities);
        Assert.Contains("approval.request", stewardCapabilities);

        var worker = agents["worker.git"];
        Assert.Equal("execution", worker.GetProperty("layer").GetString());
        Assert.Equal("git_specialist", worker.GetProperty("role").GetString());
        var workerTools = worker.GetProperty("tool_scope").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Contains("git_commit", workerTools);
        Assert.Contains("git_push", workerTools);
        Assert.Contains("git_worktree_create", workerTools);
        Assert.Contains("git_conflict_resolve", workerTools);

        var defaultMode = modes.Single(mode => mode.GetProperty("resource_key").GetString() == "default-mode");
        var roster = defaultMode.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("agent_ref").GetString()!.Replace("agent:", string.Empty, StringComparison.Ordinal))
            .ToArray();
        Assert.Contains("git_steward", roster);
        Assert.Contains("worker.git", roster);
    }

    [Fact]
    public void GitWorkerManifestIntersectionDoesNotGrantUnlistedTools()
    {
        var (agents, _) = LoadBootstrapDirectory();
        var workerTools = agents["worker.git"].GetProperty("tool_scope").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var manifest = new[] { "git_status", "git_diff", "git_commit", "git_push", "read_file" };

        var effective = workerTools
            .Intersect(manifest, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            ["git_status", "git_diff", "git_commit", "git_push"],
            effective);
        Assert.DoesNotContain("read_file", effective, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FormalModeToolManifestResolverIntersectsGitWorkerGrant()
    {
        var sessionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "tinadec-git-topology-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var tools = new[]
            {
                new ToolManifestEntryDto { Id = "git_status", Description = "Git status" },
                new ToolManifestEntryDto { Id = "git_commit", Description = "Git commit", MutatesWorkspace = true, RequiresApproval = true, Risk = "high" },
                new ToolManifestEntryDto { Id = "read_file", Description = "Read a file" }
            };
            var provider = new StubToolProvider(new ToolManifestDto
            {
                ProtocolVersion = 2,
                ManifestHash = ToolManifestHasher.Compute(tools),
                Tools = tools
            });
            var sessions = new StubSessionLocator(new SessionReference(sessionId, projectId, tenantId, workspaceId), new ProjectReference(projectId, tenantId, workspaceId, root));
            var formal = new StubFormalModeResolver(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git_status", "git_commit" });
            using var services = new ServiceCollection().AddSingleton<IFormalModeResolver>(formal).BuildServiceProvider();
            var resolver = new ToolManifestSnapshotResolver(sessions, provider, services);

            var snapshot = await resolver.ResolveAsync(new ToolManifestSnapshotRequest(sessionId, [], AllowAllTools: true));

            Assert.Equal(["git_status", "git_commit"], snapshot.AuthorizedTools.Select(tool => tool.Id));
            Assert.DoesNotContain(snapshot.AuthorizedTools, tool => tool.Id == "read_file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectlessSessionFreezesCoreCreateWorkspaceToolOnly()
    {
        var sessionId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        // A free-conversation session carries no project; no provider is ever contacted.
        var sessions = new StubSessionLocator(new SessionReference(sessionId, null, tenantId, workspaceId), null);
        var provider = new StubToolProvider(new ToolManifestDto { ProtocolVersion = 2, ManifestHash = string.Empty, Tools = [] });
        var resolver = new ToolManifestSnapshotResolver(sessions, provider);

        var snapshot = await resolver.ResolveAsync(new ToolManifestSnapshotRequest(sessionId, ["create_workspace"], AllowAllTools: false));

        Assert.Equal(2, snapshot.ProtocolVersion);
        var tool = Assert.Single(snapshot.AuthorizedTools);
        Assert.Equal("create_workspace", tool.Id);
        Assert.True(tool.RequiresApproval);
        Assert.True(tool.MutatesWorkspace);
        Assert.Equal("high", tool.Risk);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ManifestHash));
    }

    /// <summary>
    /// The shipped roster shape, read from the bootstrap fixture manifest that the
    /// Api test host installs. Upsearch from the test output directory so the file
    /// is read where it lives rather than copied into every test project.
    /// </summary>
    private static (Dictionary<string, JsonElement> Agents, List<JsonElement> Modes) LoadBootstrapDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "TinadecCore", "tests", "TinadecCore.Api.Tests", "Fixtures", "bootstrap-pack.json");
            if (!File.Exists(candidate)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(candidate));
            var resources = document.RootElement.GetProperty("resources");
            var agents = resources.GetProperty("agents").EnumerateArray()
                .ToDictionary(agent => agent.GetProperty("resource_key").GetString()!, agent => agent.Clone(), StringComparer.Ordinal);
            var modes = resources.GetProperty("modes").EnumerateArray().Select(mode => mode.Clone()).ToList();
            return (agents, modes);
        }
        throw new FileNotFoundException(
            "The bootstrap fixture manifest (TinadecCore/tests/TinadecCore.Api.Tests/Fixtures/bootstrap-pack.json) was not found from the test output directory.");
    }

    private sealed class StubSessionLocator(SessionReference session, ProjectReference? project) : ISessionLocator
    {
        public Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionReference?>(session.SessionId == sessionId ? session : null);

        public Task<ProjectReference?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectReference?>(project is not null && project.ProjectId == projectId ? project : null);
    }

    private sealed class StubToolProvider(ToolManifestDto manifest) : IToolProvider
    {
        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) => Task.FromResult(manifest);

        public Task<ToolWireResponseDto> CallAsync(string workspaceRoot, ToolWireRequestDto request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default) => Task.FromResult(manifest);

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubFormalModeResolver(HashSet<string> tools) : IFormalModeResolver
    {
        public Task<HashSet<string>?> GetEffectiveToolsForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<HashSet<string>?>(tools);

        public Task<HashSet<string>?> GetEffectiveToolsForModeAsync(Guid sessionId, Guid modeVersionId, CancellationToken cancellationToken = default) => Task.FromResult<HashSet<string>?>(tools);

        public Task<ChatResolution?> TryResolveFormalChatAsync(Guid sessionId, string layer, Guid runId, Guid turnId, CancellationToken cancellationToken = default) => Task.FromResult<ChatResolution?>(null);

        public Task<FormalModeRoster?> ResolveRosterAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<FormalModeRoster?>(null);

        public Task<FormalModeRoster?> ResolveRosterForModeAsync(Guid sessionId, Guid modeVersionId, CancellationToken cancellationToken = default) => Task.FromResult<FormalModeRoster?>(null);
    }
}
