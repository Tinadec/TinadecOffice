using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class ToolRegistryApprovalTests
{
    [Fact]
    public async Task DispatchAsync_RejectsUnapprovedMutationTool()
    {
        GeneratedToolRegistry.RegisterAll();
        var path = CreateTempFile("first\nsecond\n");
        var read = await FileReader.HandleAsync(new NormalFileReadParams { FilePath = path }, CancellationToken.None);
        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(new InsertLineParams
        {
            FilePath = path,
            FileHash = read.FileHash,
            LineNumber = 1,
            Position = "after",
            Content = ["inserted"]
        }, FileWriterJsonContext.Default.InsertLineParams));

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "insert_line",
            SessionId = "test",
            ToolCallId = 10,
            Approved = false,
            Params = parameters.RootElement.Clone()
        });

        Assert.False(response.IsSuccess);
        Assert.Equal(10, response.CallId);
        Assert.Equal(NotApprovedResponse.MESSAGE, response.Response.GetString());
        Assert.Equal("first\nsecond\n", ReadSharedText(path));
    }

    [Fact]
    public async Task DispatchAsync_AllowsUnapprovedReadTool()
    {
        GeneratedToolRegistry.RegisterAll();
        var path = CreateTempFile("first\nsecond\n");
        using var parameters = JsonDocument.Parse(JsonSerializer.Serialize(new NormalFileReadParams
        {
            FilePath = path,
            StartRow = 1,
            EndRow = 1
        }, FileReaderJsonContext.Default.NormalFileReadParams));

        var response = await ToolRegistry.DispatchAsync(new ToolCallRequest<JsonElement>
        {
            ToolId = "read_file",
            SessionId = "test",
            ToolCallId = 11,
            Approved = false,
            Params = parameters.RootElement.Clone()
        });

        Assert.True(response.IsSuccess);
        Assert.Equal(11, response.CallId);
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tinadec-tools-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
