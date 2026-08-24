using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Tools;

namespace TinadecCore.AgentFramework.Tests;

public sealed class GitTopologyTests
{
    [Fact]
    public void DefaultFullDuplexProfileIncludesGitGovernanceAndWorkerRoles()
    {
        var snapshot = LoadDefaultSnapshot();

        var steward = Assert.IsType<RuntimeAgentDefinition>(snapshot.Agents["git_steward"]);
        Assert.Equal("operation", steward.Layer);
        Assert.Equal("git_steward", steward.Role);
        Assert.Empty(steward.AllowedTools);
        Assert.Contains("git.review", steward.Capabilities);
        Assert.Contains("git.commit_plan", steward.Capabilities);
        Assert.Contains("approval.request", steward.Capabilities);

        var worker = Assert.IsType<RuntimeAgentDefinition>(snapshot.Agents["worker.git"]);
        Assert.Equal("execution", worker.Layer);
        Assert.Equal("git_specialist", worker.Role);
        Assert.Contains("git_commit", worker.AllowedTools);
        Assert.Contains("git_push", worker.AllowedTools);
        Assert.Contains("git_worktree_create", worker.AllowedTools);
        Assert.Contains("git_conflict_resolve", worker.AllowedTools);

        var profile = snapshot.Profiles["space.full_duplex"];
        Assert.Contains("git_steward", profile.OperationAgents);
        Assert.Contains("worker.git", profile.ExecutionAgents);
    }

    [Fact]
    public void GitWorkerManifestIntersectionDoesNotGrantUnlistedTools()
    {
        var worker = LoadDefaultSnapshot().Agents["worker.git"];
        var manifest = new[] { "git_status", "git_diff", "git_commit", "git_push", "read_file" };

        var effective = worker.AllowedTools
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

    private static AgentRuntimeConfigurationSnapshot LoadDefaultSnapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "default-agent-runtime.toml");
        Assert.True(File.Exists(path), $"The runtime baseline was not copied to '{path}'.");
        return AgentRuntimeConfigurationStore.LoadSnapshot(path, 1);
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
