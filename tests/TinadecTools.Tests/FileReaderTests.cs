using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class FileReaderTests
{
    [Fact]
    public async Task ReadFile_ReturnsOneBasedLineNumbersAndOffsets()
    {
        var path = CreateTempFile("alpha\nbeta\n");

        var response = await FileReader.HandleAsync(new NormalFileReadParams
        {
            FilePath = path,
            StartRow = 1,
            EndRow = 2
        }, CancellationToken.None);

        Assert.True(response.Success, response.Error);
        Assert.Equal(2, response.AllContents.Count);
        Assert.Equal(1, response.AllContents[0].Content.LineNumber);
        Assert.Equal("alpha", response.AllContents[0].Content.Content);
        Assert.Equal(0, response.AllContents[0].Content.StartOffset);
        Assert.Equal(6, response.AllContents[0].Content.EndOffset);
        Assert.Equal(2, response.AllContents[1].Content.LineNumber);
        Assert.Equal("beta", response.AllContents[1].Content.Content);
        Assert.Equal(6, response.AllContents[1].Content.StartOffset);
        Assert.Equal(11, response.AllContents[1].Content.EndOffset);
    }

    [Fact]
    public async Task ReadFile_ReturnsEmptyResultForEmptyFile()
    {
        var path = CreateTempFile(string.Empty);

        var response = await FileReader.HandleAsync(new NormalFileReadParams
        {
            FilePath = path,
            StartRow = 1,
            EndRow = 10
        }, CancellationToken.None);

        Assert.True(response.Success, response.Error);
        Assert.Empty(response.AllContents);
        Assert.NotEmpty(response.FileHash);
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tinadec-tools-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }
}
