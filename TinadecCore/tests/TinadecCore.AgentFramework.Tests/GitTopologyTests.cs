using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Tools;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.AgentFramework.Tests;

public sealed class GitTopologyTests
{
    [Fact]
    public void BootstrapDirectoryIncludesGitGovernanceAndWorkerRoles()
    {
        var (agents, modes) = LoadBootstrapDirectory();

        var steward = agents["git_steward"];
        Assert.Equal("operation", (string)steward["layer"]);
        Assert.Equal("git_steward", (string)steward["role"]);
        Assert.Empty((TomlArray)steward["tools"]);
        var stewardCapabilities = ((TomlArray)steward["capabilities"]).Cast<string>().ToArray();
        Assert.Contains("git.review", stewardCapabilities);
        Assert.Contains("git.commit_plan", stewardCapabilities);
        Assert.Contains("approval.request", stewardCapabilities);

        var worker = agents["worker.git"];
        Assert.Equal("execution", (string)worker["layer"]);
        Assert.Equal("git_specialist", (string)worker["role"]);
        var workerTools = ((TomlArray)worker["tools"]).Cast<string>().ToArray();
        Assert.Contains("git_commit", workerTools);
        Assert.Contains("git_push", workerTools);
        Assert.Contains("git_worktree_create", workerTools);
        Assert.Contains("git_conflict_resolve", workerTools);

        var defaultMode = modes.Single(mode => (string)mode["key"] == "default-mode");
        var roster = ((TomlArray)defaultMode["agents"]).Cast<string>().ToArray();
        Assert.Contains("git_steward", roster);
        Assert.Contains("worker.git", roster);
    }

    [Fact]
    public void GitWorkerManifestIntersectionDoesNotGrantUnlistedTools()
    {
        var (agents, _) = LoadBootstrapDirectory();
        var workerTools = ((TomlArray)agents["worker.git"]["tools"]).Cast<string>().ToArray();
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

    private static (Dictionary<string, TomlTable> Agents, List<TomlTable> Modes) LoadBootstrapDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "bootstrap-agent-directory.toml");
        Assert.True(File.Exists(path), $"The bootstrap agent directory was not copied to '{path}'.");
        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path));
        var agents = new Dictionary<string, TomlTable>(StringComparer.Ordinal);
        if (model.TryGetValue("agents", out var agentsValue) && agentsValue is TomlTableArray agentRows)
            foreach (var table in agentRows.Cast<TomlTable>())
                agents[(string)table["key"]] = table;
        var modes = new List<TomlTable>();
        if (model.TryGetValue("modes", out var modesValue) && modesValue is TomlTableArray modeRows)
            modes.AddRange(modeRows.Cast<TomlTable>());
        return (agents, modes);
    }

    private sealed class StubSessionLocator(SessionReference session, ProjectReference project) : ISessionLocator
    {
        public Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionReference?>(session.SessionId == sessionId ? session : null);

        public Task<ProjectReference?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectReference?>(project.ProjectId == projectId ? project : null);
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

        public Task<ChatResolution?> TryResolveFormalChatAsync(Guid sessionId, string layer, Guid runId, Guid turnId, CancellationToken cancellationToken = default) => Task.FromResult<ChatResolution?>(null);

        public Task<FormalModeRoster?> ResolveRosterAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<FormalModeRoster?>(null);
    }
}
