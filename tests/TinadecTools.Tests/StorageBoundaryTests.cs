using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Windows;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

[Collection("CommandSandbox")]
public sealed class StorageBoundaryTests
{
    public StorageBoundaryTests() { GeneratedToolRegistry.RegisterAll(); ToolHostControls.Register(); }

    private static JsonElement Context(string root, bool writable = false, string id = "scope-a", string[]? protectedRoots = null, string[]? readRoots = null) =>
        JsonSerializer.SerializeToElement(new { schema_version = 1, storage_id = id, storage_root = root,
            project_storage_write = writable, protected_storage_roots = protectedRoots ?? [], settings_hash = "frozen", settings = new { },
            allowed_tool_ids = new[] { "read_file", "write_file", "ls", "stat" }, read_roots = (readRoots ?? []).Select(path => new { path }).ToArray() });
    private static ValueTask<ToolCallResponse<JsonElement>> Call(string id, object parameters, JsonElement context) => ToolRegistry.DispatchAsync(new()
    { ToolId = id, ToolCallId = 98101, SessionId = "storage-boundary", Approved = true, Params = JsonSerializer.SerializeToElement(parameters), ExecutionContext = context });

    [Fact]
    public async Task RealFileWritesAllowSourcesAndRejectEveryInternalCategory()
    {
        using var workspace = new WorkspaceTestDirectory();
        var root = Path.Combine(workspace.Path, ".tinadec");
        var context = Context(root);
        foreach (var name in new[] { "code.cs", ".tinadec/config/tools.toml", ".tinadec/skills/example/SKILL.md" })
        {
            var path = Path.Combine(workspace.Path, name.Replace('/', Path.DirectorySeparatorChar));
            var write = await Call("write_file", new { filepath = path, content = "approved source" }, context);
            Assert.True(write.Response.GetProperty("success").GetBoolean(), write.Response.ToString());
            Assert.Equal("approved source", await File.ReadAllTextAsync(path));
        }
        foreach (var category in WorkspaceStoragePolicy.InternalDirectories)
        {
            var path = workspace.CreateFile($".tinadec/{category}/private.txt", "kept bytes");
            var denied = await Call("write_file", new { filepath = path, content = "changed", project_storage_write = true }, context);
            Assert.False(denied.Response.GetProperty("success").GetBoolean());
            Assert.Equal("kept bytes", await File.ReadAllTextAsync(path));
            var read = await Call("read_file", new { filepath = path }, context);
            Assert.False(read.Response.GetProperty("success").GetBoolean());
        }
        var list = await Call("ls", new { path = root }, context);
        var names = list.Response.GetProperty("entries").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray();
        Assert.Contains("config", names); Assert.Contains("skills", names); Assert.DoesNotContain("data", names);
    }

    [Fact]
    public async Task HostFrozenWriteGrantCannotOpenAnotherScopeAndSelectedPackagesStayReadable()
    {
        using var workspace = new WorkspaceTestDirectory();
        var root = Path.Combine(workspace.Path, ".tinadec");
        var other = Path.Combine(workspace.Path, "other-scope");
        var otherFile = workspace.CreateFile("other-scope/data/private.txt", "other scope");
        var path = Path.Combine(root, "data", "host-granted.txt");
        var granted = Context(root, true, protectedRoots: [other]);
        var result = await Call("write_file", new { filepath = path, content = "host permitted" }, granted);
        Assert.True(result.Response.GetProperty("success").GetBoolean());
        Assert.Equal("host permitted", await File.ReadAllTextAsync(path));
        var denied = await Call("read_file", new { filepath = otherFile }, granted);
        Assert.False(denied.Response.GetProperty("success").GetBoolean());
        var package = workspace.CreateFile(".tinadec/packages/skills/version/example/SKILL.md", "frozen body");
        var selected = Context(root, readRoots: [Path.GetDirectoryName(package)!]);
        var read = await Call("read_file", new { filepath = package }, selected);
        Assert.True(read.Response.GetProperty("success").GetBoolean());
        var write = await Call("write_file", new { filepath = package, content = "changed" }, selected);
        Assert.False(write.Response.GetProperty("success").GetBoolean());
        Assert.Equal("frozen body", await File.ReadAllTextAsync(package));
    }

    [Fact]
    public async Task ConcurrentScopesHaveSeparateScratchAndCannotInheritTheHostControlToken()
    {
        using var workspace = new WorkspaceTestDirectory();
        var a = Path.Combine(workspace.Path, "scope-a"); var b = Path.Combine(workspace.Path, "scope-b");
        var previous = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN");
        Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", "test-only-token");
        try
        {
            async Task<Dictionary<string, string>> Build(string root, string id)
            {
                using var context = ToolExecutionContext.Enter(Context(root, id: id));
                await Task.Yield();
                return SandboxEnvironment.Build(null, ["TINADEC_HOST_CONTROL_TOKEN"]);
            }
            var env = await Task.WhenAll(Build(a, "a"), Build(b, "b"));
            Assert.StartsWith(a, env[0]["NPM_CONFIG_CACHE"]); Assert.StartsWith(b, env[1]["NPM_CONFIG_CACHE"]);
            Assert.NotEqual(env[0]["TMPDIR"], env[1]["TMPDIR"]);
            Assert.All(env, item => Assert.False(item.ContainsKey("TINADEC_HOST_CONTROL_TOKEN")));
            Assert.True(Directory.Exists(env[0]["TMPDIR"])); Assert.True(Directory.Exists(env[1]["TMPDIR"]));
        }
        finally { Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", previous); }
    }

    [Fact]
    public void OnlyTheHostAssignedWorktreeIsASourceException()
    {
        var workspace = WorkspacePathResolver.WorkspaceRoot;
        var scope = Path.Combine(workspace, "..", "..");
        // The classifier uses a trusted call root; model params cannot introduce this exception.
        var root = Path.Combine(workspace, ".tinadec");
        var assigned = Path.Combine(root, "worktrees", "assigned");
        var ordinary = Context(root);
        using (ToolExecutionContext.Enter(ordinary))
            Assert.False(WorkspaceStoragePolicy.CanAccess(workspace, Path.Combine(assigned, "code.cs"), true, []));
        var frozen = JsonSerializer.SerializeToElement(new { schema_version = 1, storage_id = "a", storage_root = scope,
            working_directory = workspace, settings = new { }, allowed_tool_ids = Array.Empty<string>() });
        // A mismatched host root fails before any tool is executed.
        var mismatch = JsonSerializer.SerializeToElement(new { schema_version = 1, storage_id = "a", storage_root = root,
            working_directory = assigned, settings = new { }, allowed_tool_ids = Array.Empty<string>() });
        Assert.Throws<InvalidOperationException>(() => ToolExecutionContext.Enter(mismatch));
        using var valid = ToolExecutionContext.Enter(frozen);
        Assert.Equal(WorkspaceRootSet.Normalize(workspace), ToolExecutionContext.Current!.WorkingDirectory);
    }

    // The classification has to tell two similar-looking layouts apart, and getting the
    // containment direction backwards passes every check that only looks at one of them:
    // a project folder inside the storage root is project content, while the storage root
    // inside a project folder keeps its managed entries internal. Only the first direction
    // was tested when the nested-project fix landed, so widening it to the second shipped
    // green and opened data/state/logs/packages/worktrees for writing.
    [Fact]
    public void AProjectInsideTheStorageRootStaysWritableWhileManagedEntriesStayInternal()
    {
        using var workspace = new WorkspaceTestDirectory();
        var storage = Path.Combine(workspace.Path, ".tinadec");
        var project = Path.Combine(storage, "nested-project");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        using var scope = ToolExecutionContext.Enter(Context(storage));
        var source = Path.Combine(project, "src", "Program.cs");
        Assert.True(WorkspaceStoragePolicy.CanAccess(project, source, writable: true, explicitReadRoots: []));
        foreach (var category in WorkspaceStoragePolicy.InternalDirectories)
        {
            var managed = Path.Combine(storage, category, "private.txt");
            Assert.False(WorkspaceStoragePolicy.CanAccess(project, managed, writable: true, explicitReadRoots: []));
        }
        // The ordinary layout: the writable root contains the storage root rather than sitting in it.
        Assert.False(WorkspaceStoragePolicy.CanAccess(workspace.Path, Path.Combine(storage, "data", "facts.db"), writable: true, explicitReadRoots: []));
    }

    [Fact]
    public void WindowsIdentitySeparatesScopesAndPrivilegeTiers()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.NotEqual(SandboxAccountManager.IdentityFor("a", false), SandboxAccountManager.IdentityFor("b", false));
        Assert.NotEqual(SandboxAccountManager.IdentityFor("a", false), SandboxAccountManager.IdentityFor("a", true));
        Assert.Equal(20, SandboxAccountManager.IdentityFor("a", false).Length);
    }

    [Fact]
    public void WindowsRealAclDenialSurvivesAConcurrentLeaseAndReleasesAtTheEnd()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new WorkspaceTestDirectory();
        var folder = Path.Combine(workspace.Path, "acl-runtime"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "probe.txt");
        using var first = new AclManager(Environment.UserName);
        using var second = new AclManager(Environment.UserName);
        first.DenyWrite(folder); second.DenyWrite(folder);
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(path, "blocked"));
        first.RevokeAll();
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(path, "still blocked"));
        second.RevokeAll();
        File.WriteAllText(path, "released");
        Assert.Equal("released", File.ReadAllText(path));
    }
}
