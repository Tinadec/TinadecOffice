using System.Text;
using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Search;

namespace TinadecTools.Tests;

public sealed class FileSystemToolsTests : IDisposable
{
    private readonly WorkspaceTestDirectory _workspace = new();

    [Fact]
    public async Task ListAsync_IncludesHiddenEntriesAndUsesNaturalNameOrder()
    {
        _workspace.CreateFile("file10.txt", "ten");
        _workspace.CreateFile("file2.txt", "two");
        _workspace.CreateFile(".dotfile", "dot");
        var hidden = _workspace.CreateFile("hidden.txt", "hidden");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var response = await FileSystemTools.ListAsync(new ListDirectoryParams { Path = _workspace.RelativePath }, CancellationToken.None);
        var defaultResponse = await FileSystemTools.ListAsync(new ListDirectoryParams(), CancellationToken.None);

        Assert.True(response.Success, response.Error);
        Assert.True(defaultResponse.Success, defaultResponse.Error);
        Assert.Contains(response.Entries, entry => entry.Name == ".dotfile");
        Assert.Equal([".dotfile", "file2.txt", "file10.txt", "hidden.txt"], response.Entries.Select(entry => entry.Name));
        Assert.Equal(OperatingSystem.IsWindows(), response.Entries.Single(entry => entry.Name == "hidden.txt").IsHidden);
    }

    [Fact]
    public async Task ListAsync_PaginatesWithoutDuplicatesOrOmissions()
    {
        foreach (var name in new[] { "file1", "file2", "file3", "file4", "file5" })
            _workspace.CreateFile(name, name);

        var collected = new List<string>();
        string? cursor = null;
        do
        {
            var response = await FileSystemTools.ListAsync(new ListDirectoryParams
            {
                Path = _workspace.RelativePath,
                Limit = 2,
                Cursor = cursor
            }, CancellationToken.None);

            Assert.True(response.Success, response.Error);
            collected.AddRange(response.Entries.Select(entry => entry.Name));
            cursor = response.NextCursor;
        } while (cursor is not null);

        Assert.Equal(["file1", "file2", "file3", "file4", "file5"], collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ListAsync_ReturnsAnEmptyDirectory()
    {
        var emptyDirectory = Path.Combine(_workspace.Path, "empty");
        Directory.CreateDirectory(emptyDirectory);

        var response = await FileSystemTools.ListAsync(new ListDirectoryParams { Path = emptyDirectory }, CancellationToken.None);

        Assert.True(response.Success, response.Error);
        Assert.Empty(response.Entries);
        Assert.False(response.HasMore);
        Assert.Null(response.NextCursor);
    }

    [Fact]
    public async Task StatAsync_ReturnsFileAndDirectoryMetadata()
    {
        var file = _workspace.CreateFile("nested/item.txt", "content");
        var directory = Path.GetDirectoryName(file)!;

        var fileResponse = await FileSystemTools.StatAsync(new StatPathParams { Path = file }, CancellationToken.None);
        var directoryResponse = await FileSystemTools.StatAsync(new StatPathParams { Path = directory }, CancellationToken.None);

        Assert.True(fileResponse.Success, fileResponse.Error);
        Assert.Equal("file", fileResponse.Entry!.Type);
        Assert.Equal(7, fileResponse.Entry.Size);
        Assert.True(directoryResponse.Success, directoryResponse.Error);
        Assert.Equal("directory", directoryResponse.Entry!.Type);

        var missingResponse = await FileSystemTools.StatAsync(
            new StatPathParams { Path = Path.Combine(_workspace.Path, "missing.txt") }, CancellationToken.None);
        Assert.False(missingResponse.Success);
    }

    [Fact]
    public async Task WriteAsync_CreatesUtf8LfFileAndOverwritesWithMatchingHash()
    {
        var path = Path.Combine(_workspace.Path, "created.txt");
        var create = await FileSystemTools.WriteAsync(new WriteFileParams
        {
            FilePath = path,
            Content = "first\r\nsecond\r"
        }, CancellationToken.None);

        Assert.True(create.Success, create.Error);
        Assert.Equal("first\nsecond\n", ReadSharedText(path));
        Assert.False(ReadSharedBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble));

        var overwrite = await FileSystemTools.WriteAsync(new WriteFileParams
        {
            FilePath = path,
            Content = "replaced",
            FileHash = create.FileHash
        }, CancellationToken.None);

        Assert.True(overwrite.Success, overwrite.Error);
        Assert.Equal("replaced", ReadSharedText(path));
    }

    [Fact]
    public async Task WriteAsync_RejectsAnUnsafeOverwriteOfAnExistingFile()
    {
        var path = _workspace.CreateFile("existing.txt", "original");

        var noHash = await FileSystemTools.WriteAsync(new WriteFileParams { FilePath = path, Content = "replacement" }, CancellationToken.None);
        var wrongHash = await FileSystemTools.WriteAsync(new WriteFileParams { FilePath = path, Content = "replacement", FileHash = "bad" }, CancellationToken.None);

        Assert.False(noHash.Success);
        Assert.Contains("file_hash is required", noHash.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(wrongHash.Success);
        Assert.Contains("file_hash mismatch", wrongHash.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", ReadSharedText(path));
    }

    /// <summary>
    /// An install is one file in a directory that does not exist yet — a workspace that never held a
    /// skill has no `skills/` either — so `write_file` creates the missing parents rather than the
    /// caller needing a second approval for a directory. This test is the boundary of that: inside the
    /// workspace it may build a tree, and nowhere else may it.
    /// </summary>
    [Fact]
    public async Task WriteAsync_CreatesMissingParentsInsideTheWorkspace_AndRefusesEveryWhereElse()
    {
        var skill = Path.Combine(_workspace.Path, "skills", "release-notes", "SKILL.md");
        var created = await FileSystemTools.WriteAsync(new WriteFileParams
        {
            FilePath = skill,
            Content = "---\nname: release-notes\ndescription: Group commits into a note.\n---\n",
        }, CancellationToken.None);

        Assert.True(created.Success, created.Error);
        Assert.True(File.Exists(skill));
        Assert.Equal(
            "---\nname: release-notes\ndescription: Group commits into a note.\n---\n",
            ReadSharedText(skill));

        // A path element that is already a file is not a directory, and creating "under" it would
        // have to replace it — which is the overwrite case, and that one needs a file_hash.
        var blocker = _workspace.CreateFile("blocker.txt", "not a directory");
        var throughFile = await FileSystemTools.WriteAsync(new WriteFileParams
        {
            FilePath = Path.Combine(blocker, "child.md"),
            Content = "child",
        }, CancellationToken.None);

        Assert.False(throughFile.Success);
        Assert.Equal("not a directory", ReadSharedText(blocker));

        // The interesting refusal: a nonexistent path outside the workspace used to fail because its
        // parent was missing. Now that the tool can build parents, it must still fail because the
        // path is not in the workspace at all.
        var outside = Path.Combine(Path.GetTempPath(), $"tinadec-mkdir-{Guid.NewGuid():N}", "deep", "new.txt");
        var escaped = await FileSystemTools.WriteAsync(new WriteFileParams
        {
            FilePath = outside,
            Content = "escaped",
        }, CancellationToken.None);

        Assert.False(escaped.Success);
        Assert.False(Directory.Exists(Path.GetDirectoryName(outside)!));
    }

    [Fact]
    public async Task ExistingMutationTools_RejectMissingFiles()
    {
        var response = await FileWriter.InsertBytesAsync(new FileHashMutationParams
        {
            FilePath = Path.Combine(_workspace.Path, "missing.txt"),
            FileHash = "unused",
            Content = "content"
        }, CancellationToken.None);

        Assert.False(response.Success);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "missing.txt")));
    }

    [Fact]
    public async Task Tools_RejectPathsOutsideTheWorkspace()
    {
        var externalPath = Path.Combine(Path.GetTempPath(), $"tinadec-outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(externalPath, "outside");
        try
        {
            var read = await FileReader.HandleAsync(new NormalFileReadParams { FilePath = externalPath }, CancellationToken.None);
            var write = await FileSystemTools.WriteAsync(new WriteFileParams { FilePath = externalPath, Content = "replacement" }, CancellationToken.None);
            var stat = await FileSystemTools.StatAsync(new StatPathParams { Path = externalPath }, CancellationToken.None);
            var search = await FileSearch.HandleAsync(new FileSearchParams { Pattern = "outside", Path = Path.GetTempPath() }, CancellationToken.None);

            Assert.False(read.Success);
            Assert.False(write.Success);
            Assert.False(stat.Success);
            Assert.False(search.Success);
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    [Fact]
    public async Task LinksAreVisibleToStatButCannotBeTraversed()
    {
        var externalDirectory = Path.Combine(Path.GetTempPath(), $"tinadec-link-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(Path.Combine(externalDirectory, "secret.txt"), "secret");
        var link = Path.Combine(_workspace.Path, "external-link");
        try
        {
            if (!LinkPrerequisite.TryCreateDirectoryLink(link, externalDirectory)) return;

            var stat = await FileSystemTools.StatAsync(new StatPathParams { Path = link }, CancellationToken.None);
            var list = await FileSystemTools.ListAsync(new ListDirectoryParams { Path = _workspace.RelativePath }, CancellationToken.None);
            var read = await FileReader.HandleAsync(new NormalFileReadParams { FilePath = Path.Combine(link, "secret.txt") }, CancellationToken.None);
            var write = await FileSystemTools.WriteAsync(new WriteFileParams { FilePath = Path.Combine(link, "new.txt"), Content = "blocked" }, CancellationToken.None);
            var search = await FileSearch.HandleAsync(new FileSearchParams { Pattern = "secret", Path = link }, CancellationToken.None);

            Assert.True(stat.Success, stat.Error);
            Assert.Equal("link", stat.Entry!.Type);
            Assert.NotNull(stat.Entry.LinkTarget);
            var listedLink = Assert.Single(list.Entries, entry => entry.Name == "external-link");
            Assert.Equal("link", listedLink.Type);
            Assert.NotNull(listedLink.LinkTarget);
            Assert.False(read.Success);
            Assert.False(write.Success);
            Assert.False(search.Success);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(externalDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteFile_RequiresApprovalThroughRegistry()
    {
        GeneratedToolRegistry.RegisterAll();
        var path = Path.Combine(_workspace.Path, "approval.txt");
        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(new WriteFileParams
        {
            FilePath = path,
            Content = "not written"
        }, FileSystemToolsJsonContext.Default.WriteFileParams));

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "write_file",
            SessionId = "test",
            ToolCallId = 101,
            Approved = false,
            Params = parameters.RootElement.Clone()
        });

        Assert.False(response.IsSuccess);
        Assert.Equal(NotApprovedResponse.MESSAGE, response.Response.GetString());
        Assert.False(File.Exists(path));
    }

    public void Dispose() => _workspace.Dispose();

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
