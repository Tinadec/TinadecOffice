using System.Diagnostics;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Posix;
using TinadecTools.Runtime.Sandbox.Windows;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

/// <summary>
/// The sandbox layer of the git-metadata refusal. Narrow on purpose: only .git/hooks and
/// .git/config are write-denied, so a person typing git commit inside the sandbox can still
/// write index, objects and refs. Reads stay open everywhere — git cannot work without its
/// own config.
/// </summary>
[Collection("CommandSandbox")]
public sealed class GitMetadataSandboxDenialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-git-sandbox-deny-" + Guid.NewGuid().ToString("N"));

    public GitMetadataSandboxDenialTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string WorkspaceWithGitMetadata()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, ".git", "hooks"));
        File.WriteAllText(Path.Combine(workspace, ".git", "config"), "[core]\n\trepositoryformatversion = 0\n");
        File.WriteAllText(Path.Combine(workspace, ".git", "hooks", "pre-commit"), "#!/bin/sh\nexit 0\n");
        return workspace;
    }

    [Fact]
    public void BuildPermissions_DeniesHooksAndConfig_ButNotTheGitRootItself()
    {
        var permissions = CommandSandboxRuntime.BuildPermissions(null, null, null);
        var root = WorkspacePathResolver.WorkspaceRoot;

        Assert.Contains(Path.Combine(root, ".git", "hooks"), permissions.WriteDeniedPaths);
        Assert.Contains(Path.Combine(root, ".git", "config"), permissions.WriteDeniedPaths);
        // Denying .git itself would break every git command a person runs in the sandbox.
        Assert.DoesNotContain(Path.Combine(root, ".git"), permissions.WriteDeniedPaths);
        Assert.DoesNotContain(root, permissions.WriteDeniedPaths);
    }

    [Fact]
    public void MergeWithPolicy_KeepsWriteDeniedPathsOnBothPaths()
    {
        var standalone = CommandSandboxRuntime.MergeWithPolicy(CommandSandboxRuntime.BuildPermissions(null, null, null));
        Assert.NotEmpty(standalone.WriteDeniedPaths);

        using var scope = TinadecTools.Runtime.ToolExecutionContext.Enter(
            System.Text.Json.JsonDocument.Parse(
                "{\"schema_version\":1,\"settings_hash\":\"t\",\"settings\":{},\"allowed_tool_ids\":[\"shell\"],\"read_roots\":[],\"mcp_servers\":[]}").RootElement,
            "shell");
        var governed = CommandSandboxRuntime.MergeWithPolicy(CommandSandboxRuntime.BuildPermissions(null, null, null));
        Assert.NotEmpty(governed.WriteDeniedPaths);
    }

    [Fact]
    public void SeatbeltProfile_WriteDeniedPathsAreWriteExcludedAndNeverReadDenied()
    {
        var workspace = WorkspaceWithGitMetadata();
        var hooks = Path.Combine(workspace, ".git", "hooks");
        var config = Path.Combine(workspace, ".git", "config");
        var storage = Path.Combine(_root, "storage");
        Directory.CreateDirectory(storage);

        var profile = SeatbeltProfile.Build([workspace], [storage], [], storage, [hooks, config]);

        var writeBlock = profile[..profile.IndexOf("(deny file-read*", StringComparison.Ordinal)];
        var readBlock = profile[profile.IndexOf("(deny file-read*", StringComparison.Ordinal)..];
        Assert.Contains($"(subpath {Quote(hooks)})", writeBlock);
        Assert.Contains($"(literal {Quote(config)})", writeBlock);
        Assert.DoesNotContain(Quote(hooks), readBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(Quote(config), readBlock, StringComparison.Ordinal);
        // The write exclusion must sit inside require-not, or it would be a grant instead.
        Assert.Contains("(require-not (require-any", writeBlock);
    }

    [Fact]
    public void BubblewrapMountView_ReadOnlyBindsHooksAndConfigWithoutMaskingThem()
    {
        var workspace = WorkspaceWithGitMetadata();
        var hooks = Path.Combine(workspace, ".git", "hooks");
        var config = Path.Combine(workspace, ".git", "config");
        var start = new ProcessStartInfo("bwrap");
        var permissions = new SandboxPermissions { WritePaths = [workspace], WriteDeniedPaths = [hooks, config] };

        BubblewrapLauncher.AddMountView(start, permissions, [workspace]);

        var argv = start.ArgumentList;
        Assert.Contains(hooks, argv);
        Assert.Contains(config, argv);
        Assert.Equal("--ro-bind", argv[argv.IndexOf(hooks) - 1]);
        Assert.Equal("--ro-bind", argv[argv.IndexOf(config) - 1]);
        // --tmpfs would hide the contents, and git cannot run without reading its config.
        Assert.DoesNotContain("--tmpfs", argv);
        Assert.Equal("--bind", argv[argv.IndexOf(workspace) - 1]);
    }

    /// <summary>
    /// The startup-existence boundary, made executable. Windows ACLs and Linux bind mounts both
    /// act on a path that exists when the sandbox starts, so a .git/hooks deleted before launch
    /// (or a repository initialised afterwards) is not protected. macOS matches path expressions
    /// and has no such gap.
    /// </summary>
    [Fact]
    public void BubblewrapMountView_ADeletedHooksDirectoryIsNoLongerProtected()
    {
        var workspace = WorkspaceWithGitMetadata();
        var hooks = Path.Combine(workspace, ".git", "hooks");
        var permissions = new SandboxPermissions { WritePaths = [workspace], WriteDeniedPaths = [hooks] };

        var present = new ProcessStartInfo("bwrap");
        BubblewrapLauncher.AddMountView(present, permissions, [workspace]);
        Assert.Contains(hooks, present.ArgumentList);

        Directory.Delete(hooks, recursive: true);
        var absent = new ProcessStartInfo("bwrap");
        BubblewrapLauncher.AddMountView(absent, permissions, [workspace]);

        Assert.DoesNotContain(hooks, absent.ArgumentList);
        // The derivation still names it: the gap is the backend's existence filter, not the list.
        Assert.Contains(hooks, permissions.WriteDeniedPaths);
    }

    [Fact]
    public void WindowsRealAcl_DenyWriteOnGitMetadata_KeepsItReadable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var workspace = WorkspaceWithGitMetadata();
        var hooks = Path.Combine(workspace, ".git", "hooks");
        var config = Path.Combine(workspace, ".git", "config");
        var hookFile = Path.Combine(hooks, "pre-commit");
        using var acl = new AclManager(Environment.UserName);

        acl.DenyWrite(hooks);
        acl.DenyWrite(config);

        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(hookFile, "#!/bin/sh\nexit 1\n"));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(hooks, "post-commit"), "x"));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(config, "[core]\n\tsshCommand = evil\n"));
        // The existing tests only asserted the refusal. Keeping the read is what lets git work.
        Assert.Equal("#!/bin/sh\nexit 0\n", File.ReadAllText(hookFile));
        Assert.Contains("repositoryformatversion", File.ReadAllText(config));
        // The rest of .git stays writable, so a sandboxed git commit can still write its index.
        File.WriteAllText(Path.Combine(workspace, ".git", "index-probe"), "x");

        acl.RevokeAll();
        File.WriteAllText(hookFile, "released");
        Assert.Equal("released", File.ReadAllText(hookFile));
    }

    /// <summary>
    /// Why the backends filter on existence instead of pre-granting: a deny ACE cannot be written
    /// onto a path that is not there. This is the mechanism behind the deleted-hooks gap above.
    /// </summary>
    [Fact]
    public void WindowsRealAcl_DenyingAMissingPathFails()
    {
        if (!OperatingSystem.IsWindows()) return;

        var workspace = WorkspaceWithGitMetadata();
        var hooks = Path.Combine(workspace, ".git", "hooks");
        Directory.Delete(hooks, recursive: true);
        using var acl = new AclManager(Environment.UserName);

        Assert.Throws<InvalidOperationException>(() => acl.DenyWrite(hooks));
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}