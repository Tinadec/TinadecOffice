using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class FileWriterTests
{
    [Fact]
    public async Task ReplaceLines_RejectsMismatchedLineHash()
    {
        var path = CreateTempFile("first\nsecond\n");

        var response = await FileWriter.ReplaceLinesAsync(new ReplaceLinesParams
        {
            FilePath = path,
            StartRow = 1,
            EndRow = 1,
            Content = [new HashedLineContent { Content = "updated", Hash = "1#ZZ" }]
        }, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("hash mismatch", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("first\nsecond\n", ReadSharedText(path));
    }

    [Fact]
    public async Task InsertLine_ChecksFileHashAndUpdatesContent()
    {
        var path = CreateTempFile("first\nsecond\n");
        var read = await FileReader.HandleAsync(new NormalFileReadParams { FilePath = path }, CancellationToken.None);

        var response = await FileWriter.InsertLineAsync(new InsertLineParams
        {
            FilePath = path,
            FileHash = read.FileHash,
            LineNumber = 1,
            Position = "after",
            Content = ["inserted"]
        }, CancellationToken.None);

        Assert.True(response.Success, response.Error);
        Assert.Equal("first\ninserted\nsecond\n", ReadSharedText(path));
        Assert.NotEqual(read.FileHash, response.FileHash);
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tinadec-tools-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }
}
