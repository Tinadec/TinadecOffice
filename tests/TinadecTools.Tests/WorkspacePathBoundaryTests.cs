using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

/// <summary>
/// Workspace path contract: absolute paths inside the writable root are accepted,
/// relative paths resolve against it, declared read-only roots widen reads only,
/// and an out-of-bounds path is refused with a message that names the attempted
/// path and the allowed roots (the model has to be able to correct itself).
/// </summary>
public sealed class WorkspacePathBoundaryTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "tinadec-path-" + Guid.NewGuid().ToString("N"));
    private readonly string _writable;
    private readonly string _readOnly;

    public WorkspacePathBoundaryTests()
    {
        _writable = Directory.CreateDirectory(Path.Combine(_temp, "writable")).FullName;
        _readOnly = Directory.CreateDirectory(Path.Combine(_temp, "shared")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private WorkspaceRootSet Roots() => new(WorkspaceRootSet.Normalize(_writable), [WorkspaceRootSet.Normalize(_readOnly)]);

    [Fact]
    public void AbsolutePath_InsideWritableRoot_ResolvesForReadAndWrite()
    {
        var roots = Roots();
        var target = Path.Combine(_writable, "src", "app.ts");

        Assert.Equal(target, roots.Resolve(target, writable: false));
        Assert.Equal(target, roots.Resolve(target, writable: true));
    }

    [Fact]
    public void RelativePath_ResolvesAgainstTheWritableRoot()
    {
        var roots = Roots();
        Assert.Equal(
            Path.Combine(roots.WritableRoot, "src", "app.ts"),
            roots.Resolve(Path.Combine("src", "app.ts"), writable: false));
    }

    [Fact]
    public void ReadOnlyRoot_IsReadableButNeverWritable()
    {
        var roots = Roots();
        var target = Path.Combine(_readOnly, "notes.md");

        Assert.Equal(target, roots.Resolve(target, writable: false));
        Assert.True(roots.IsAllowed(target));

        var refusal = Assert.Throws<UnauthorizedAccessException>(() => roots.Resolve(target, writable: true));
        Assert.Contains(_readOnly, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(roots.WritableRoot, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("may be written", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PathOutsideEveryRoot_IsRefusedWithAttemptedPathAndAllowedRoots()
    {
        var roots = Roots();
        var outside = Path.Combine(_temp, "elsewhere", "secret.txt");

        var refusal = Assert.Throws<UnauthorizedAccessException>(() => roots.Resolve(outside, writable: false));
        Assert.Contains(outside, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(roots.WritableRoot, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(_readOnly, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("may be read", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwningRoot_IsTheRootThatContainsThePath()
    {
        var roots = Roots();
        Assert.Equal(roots.WritableRoot, roots.OwningRoot(Path.Combine(_writable, "a", "b")).Root);
        Assert.Equal(Path.Combine("a", "b"), roots.OwningRoot(Path.Combine(_writable, "a", "b")).Relative);
        Assert.Equal(roots.ReadOnlyRoots[0], roots.OwningRoot(Path.Combine(_readOnly, "c")).Root);
    }

    [Fact]
    public void ParseReadOnlyRoots_DropsCoveredEntriesAndSurvivesGarbage()
    {
        var writable = WorkspaceRootSet.Normalize(_writable);
        var nested = Path.Combine(_writable, "nested");
        var raw = string.Join(Path.PathSeparator, writable, nested, _readOnly, "   ");

        var roots = WorkspaceRootSet.ParseReadOnlyRoots(raw, writable);

        var parsed = Assert.Single(roots);
        Assert.Equal(WorkspaceRootSet.Normalize(_readOnly), parsed);
        Assert.Empty(WorkspaceRootSet.ParseReadOnlyRoots(null, writable));
        Assert.Empty(WorkspaceRootSet.ParseReadOnlyRoots("   ", writable));
        Assert.Empty(WorkspaceRootSet.ParseReadOnlyRoots(writable, writable));
    }

    [Fact]
    public void IsWithin_MatchesOnASegmentBoundary()
    {
        var root = WorkspaceRootSet.Normalize(_writable);
        Assert.True(WorkspaceRootSet.IsWithin(root, root));
        Assert.True(WorkspaceRootSet.IsWithin(root, Path.Combine(root, "child")));
        // A sibling that merely shares the prefix text is not inside.
        Assert.False(WorkspaceRootSet.IsWithin(root, root + "-sibling"));
    }

    /// <summary>The one rewrite macOS performs on a temp workspace, as a fixture so a Windows host
    /// exercises it: this process reads its root from Environment.CurrentDirectory, which the kernel
    /// hands back already resolved, while the host asks for the path it declared. Spelled without a
    /// leading "/" so it does not depend on how the host resolves absolute paths.</summary>
    private static string AliasForm(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        var alias = "ALIAS" + separator;
        return path.StartsWith(alias, StringComparison.Ordinal)
            ? string.Join(separator, new[] { "REAL" }.Concat(path.Split(separator).Skip(1)))
            : path;
    }

    private static string Sep(params string[] parts) => string.Join(Path.DirectorySeparatorChar, parts);

    [Fact]
    public void IsWithin_AcceptsEitherSpellingOfOneDirectory()
    {
        var root = Sep("REAL", "ws");
        Assert.True(WorkspaceRootSet.IsWithin(root, Sep("ALIAS", "ws", "mcp.json"), AliasForm));
        Assert.True(WorkspaceRootSet.IsWithin(root, Sep("REAL", "ws", "mcp.json"), AliasForm));
    }

    [Fact]
    public void IsWithin_StillRefusesAPathInNeitherSpelling()
    {
        // The added comparison must not turn into "everything is inside": these two differ from the
        // root in both spellings, and the CI failure this guards against is a refusal, not a grant.
        var root = Sep("REAL", "ws");
        Assert.False(WorkspaceRootSet.IsWithin(root, Sep("etc", "passwd"), AliasForm));
        Assert.False(WorkspaceRootSet.IsWithin(root, Sep("ALIAS", "elsewhere", "x.json"), AliasForm));
    }

    [Fact]
    public void IsWithin_NeverNarrowsWhatThePrefixTestAlreadyAllowed()
    {
        // A resolver that answers something unrelated has to cost no acceptance: the canonical
        // comparison only ever adds one, so fixing the macOS split cannot break a working workspace.
        var inside = Path.Combine(_writable, "src", "app.ts");
        Assert.True(WorkspaceRootSet.IsWithin(_writable, inside, _ => "/somewhere/else"));
        Assert.True(WorkspaceRootSet.IsWithin(_writable, _writable, _ => "/somewhere/else"));
    }

    [Fact]
    public void OwningRoot_ReturnsRootAndRelativeInOneSpelling()
    {
        // The pair is what link walking is done with. A relative computed across two spellings climbs
        // out of the workspace and back through the very symlink the policy refuses, which is how a
        // passing containment check still answered "Paths through symbolic links are not allowed".
        var (root, relative) = WorkspaceRootSet.OwningRootIn(
            Sep("ALIAS", "ws", "mcp.json"), [Sep("REAL", "ws")], AliasForm);

        Assert.Equal(Sep("REAL", "ws"), root);
        Assert.Equal("mcp.json", relative);
        Assert.DoesNotContain("..", relative.Split(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Git metadata is an execution surface: hooks run on commit, and .git/config names
    /// programs through core.fsmonitor, core.sshCommand, core.pager, filter.* and alias.*.
    /// The refusal is write-only — ls and stat resolve with writable:false and must keep
    /// working, and git itself writes .git as a subprocess that never passes through here.
    /// </summary>
    [Theory]
    [InlineData(".git")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".git/config")]
    [InlineData(".git/HEAD")]
    [InlineData(".git/objects/ab/cdef0123")]
    [InlineData("sub/.git/config")]
    [InlineData("nested/deep/.git/hooks/post-checkout")]
    public void GitMetadata_IsRefusedForWrite(string relative)
    {
        var roots = Roots();
        var target = Path.Combine(_writable, relative);
        Assert.Throws<UnauthorizedAccessException>(() => roots.Resolve(target, writable: true));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".git/config")]
    [InlineData("sub/.git/config")]
    public void GitMetadata_StaysReadable(string relative)
    {
        var roots = Roots();
        var target = Path.Combine(_writable, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(target, roots.Resolve(target, writable: false));
        Assert.True(roots.IsAllowed(target));
    }

    [Fact]
    public void OrdinaryWorkingTreeFile_IsStillReadableAndWritable()
    {
        var roots = Roots();
        var target = Path.Combine(_writable, "src", "app.ts");
        Assert.Equal(target, roots.Resolve(target, writable: false));
        Assert.Equal(target, roots.Resolve(target, writable: true));
    }

    [Fact]
    public void ANameThatMerelyStartsWithDotGit_IsNotGitMetadata()
    {
        var roots = Roots();
        // Segment equality, not a prefix test: .github and .gitignore are ordinary files.
        foreach (var relative in new[] { ".github/workflows/ci.yml", ".gitignore", ".gitattributes", "src/.gitkeep" })
        {
            var target = Path.Combine(_writable, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(target, roots.Resolve(target, writable: true));
        }
    }
}
