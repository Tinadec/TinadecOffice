using TinadecTools.Tools.Search;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class FileSearchTests : IDisposable
{
    private readonly string? _rgPath;
    private readonly string? _originalRgEnv;
    private readonly List<string> _temporaryDirectories = new();

    public FileSearchTests()
    {
        _originalRgEnv = Environment.GetEnvironmentVariable(RipgrepRunner.RgPathEnvVar);
        _rgPath = FindRgOnPath();
        if (_rgPath is not null)
            Environment.SetEnvironmentVariable(RipgrepRunner.RgPathEnvVar, _rgPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RipgrepRunner.RgPathEnvVar, _originalRgEnv);
        foreach (var directory in _temporaryDirectories)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // ── 基础搜索 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task BasicMatch_ReturnsMatchLineAndFileHash()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "a.txt", "hello world\nsecond line\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "hello", Path = dir },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        var match = Assert.Single(resp.Lines);
        Assert.True(match.IsMatch);
        Assert.Equal(1, match.LineNumber);
        Assert.Contains("hello", match.Content);
        Assert.NotEmpty(resp.FileHashes);
    }

    [Fact]
    public async Task Submatches_ContainMatchedText()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "b.txt", "foo bar baz\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "bar", Path = dir },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        var match = Assert.Single(resp.Lines);
        Assert.NotNull(match.Submatches);
        Assert.Contains(match.Submatches!, s => s.Text == "bar");
    }

    // ── Glob / Type 过滤 ──────────────────────────────────────────────────────

    [Fact]
    public async Task GlobFilter_OnlyMatchesSpecifiedExtension()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "keep.cs",    "target content\n");
        WriteFile(dir, "ignore.txt", "target content\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "target", Path = dir, Glob = "*.cs" },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        Assert.All(resp.Lines, l => Assert.EndsWith(".cs", l.FilePath));
    }

    [Fact]
    public async Task TypeFilter_OnlyMatchesCsharpFiles()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "code.cs",   "needle here\n");
        WriteFile(dir, "notes.txt", "needle here\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "needle", Path = dir, Type = "cs" },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        Assert.All(resp.Lines, l => Assert.EndsWith(".cs", l.FilePath));
    }

    // ── 大小写 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CaseSensitiveFalse_MatchesUpperAndLower()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "c.txt", "HELLO\nhello\nHeLLo\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "hello", Path = dir, CaseSensitive = false },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        Assert.Equal(3, resp.Lines.Count(l => l.IsMatch));
    }

    [Fact]
    public async Task CaseSensitiveTrue_RequiresExactCase()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "d.txt", "HELLO\nhello\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "hello", Path = dir, CaseSensitive = true },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        Assert.Equal(1, resp.Lines.Count(l => l.IsMatch));
        Assert.Equal("hello", resp.Lines.First(l => l.IsMatch).Content);
    }

    // ── FixedStrings ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FixedStrings_TreatsRegexCharsAsLiteral()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "e.txt", "foo.bar\nfooXbar\n");

        // FixedStrings=true：'.' 是字面量，只应匹配 "foo.bar"
        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "foo.bar", Path = dir, FixedStrings = true },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        Assert.Equal(1, resp.Lines.Count(l => l.IsMatch));
        Assert.Contains("foo.bar", resp.Lines.First(l => l.IsMatch).Content);
    }

    // ── ContextLines ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ContextLines_IncludesAdjacentLinesWithLineNumbers()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "f.txt", "before\ntarget\nafter\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "target", Path = dir, ContextLines = 1 },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        var matchLine = Assert.Single(resp.Lines, l => l.IsMatch);
        Assert.Equal(2, matchLine.LineNumber);

        var ctxLines = resp.Lines.Where(l => !l.IsMatch).ToList();
        Assert.Contains(ctxLines, l => l.LineNumber == 1 && l.Content == "before");
        Assert.Contains(ctxLines, l => l.LineNumber == 3 && l.Content == "after");
    }

    // ── MaxResults 截断 ───────────────────────────────────────────────────────

    [Fact]
    public async Task MaxResults_TruncatesResultsAndSetsTruncatedFlag()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        // 写10行都能命中
        WriteFile(dir, "g.txt", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"match {i}")) + "\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "match", Path = dir, MaxResults = 3 },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        Assert.Equal(3, resp.Lines.Count(l => l.IsMatch));
        Assert.True(resp.Truncated);
    }

    // ── 无命中 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoMatch_ReturnsSuccessWithEmptyLines()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "h.txt", "nothing relevant here\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "zzznomatch", Path = dir },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        Assert.Empty(resp.Lines);
        Assert.False(resp.Truncated);
    }

    // ── rg 找不到 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RgNotFound_ReturnsFailureWithHelpfulError()
    {
        Environment.SetEnvironmentVariable(RipgrepRunner.RgPathEnvVar,
            Path.Combine(Path.GetTempPath(), $"rg-fake-{Guid.NewGuid():N}"));
        try
        {
            var resp = await FileSearch.HandleAsync(
                new FileSearchParams { Pattern = "x", Path = "." },
                CancellationToken.None);

            Assert.False(resp.Success);
            Assert.NotNull(resp.Error);
            // The failure must be actionable, not just true: a caller has to learn that
            // ripgrep is missing AND how to supply it, without reading the source.
            Assert.Contains("no ripgrep binary was found", resp.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(RipgrepRunner.RgPathEnvVar, resp.Error, StringComparison.Ordinal);
            Assert.Contains("PATH", resp.Error, StringComparison.Ordinal);
            Assert.Contains("Every other tool works without ripgrep", resp.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RipgrepRunner.RgPathEnvVar, _rgPath);
        }
    }

    // ── 空 pattern ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyPattern_ReturnsFailure()
    {
        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "" },
            CancellationToken.None);

        Assert.False(resp.Success);
        Assert.NotNull(resp.Error);
    }

    // ── 超长行（压缩文件） ────────────────────────────────────────────────────

    [Fact]
    public async Task MinifiedLine_IsClippedToAWindowAroundTheFirstMatch()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        // One 200 KB line with the needle deep inside, like a minified JSON metadata file.
        var line = new string('x', 120_000) + "NEEDLE" + new string('y', 80_000);
        WriteFile(dir, "minified.json", line + "\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "NEEDLE", Path = dir },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        var match = Assert.Single(resp.Lines);
        Assert.True(match.ContentTruncated);
        Assert.True(match.Content.Length <= RipgrepRunner.MaxLineChars);
        Assert.Contains("NEEDLE", match.Content);
        Assert.Equal(line.Length, match.LineLength);
        Assert.Equal(line.IndexOf("NEEDLE", StringComparison.Ordinal) - match.ContentStart, match.Content.IndexOf("NEEDLE", StringComparison.Ordinal));
        Assert.Equal("NEEDLE", Assert.Single(match.Submatches!).Text);
    }

    [Fact]
    public async Task ManyMatchesOnOneLine_KeepABoundedSubmatchList()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "dense.txt", string.Concat(Enumerable.Repeat("id,", 5_000)) + "\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "id", Path = dir },
            CancellationToken.None);

        Assert.True(resp.Success, resp.Error);
        var match = Assert.Single(resp.Lines);
        Assert.Equal(RipgrepRunner.MaxSubmatchesPerLine, match.Submatches!.Count);
        Assert.True(match.Content.Length <= RipgrepRunner.MaxLineChars);
    }

    [Fact]
    public async Task ShortLine_IsReturnedWhole_WithoutTruncationFields()
    {
        if (_rgPath is null) return;
        var dir = CreateTempDir();
        WriteFile(dir, "short.txt", "plain hello line\n");

        var resp = await FileSearch.HandleAsync(
            new FileSearchParams { Pattern = "hello", Path = dir },
            CancellationToken.None);

        var match = Assert.Single(resp.Lines);
        Assert.Equal("plain hello line", match.Content);
        Assert.False(match.ContentTruncated);
        Assert.Null(match.ContentStart);
        Assert.Null(match.LineLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(999)]
    public void ClipWindow_KeepsTheAnchorAndNeverSplitsASurrogatePair(int anchor)
    {
        // Every character here is a surrogate pair, so any odd boundary would split one.
        var line = string.Concat(Enumerable.Repeat("😀", 500));
        var (start, length) = RipgrepRunner.ClipWindow(line, anchor, 101);

        Assert.True(length <= 101);
        Assert.False(char.IsLowSurrogate(line[start]));
        Assert.False(char.IsHighSurrogate(line[start + length - 1]));
        Assert.InRange(anchor, start, start + length);
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private string CreateTempDir()
    {
        var path = Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    private static void WriteFile(string dir, string name, string content) =>
        File.WriteAllText(Path.Combine(dir, name), content);

    private static string? FindRgOnPath()
    {
        var exe = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return paths.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }
}
