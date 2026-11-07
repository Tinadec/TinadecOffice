using System.Text.Json;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Tool-result image delivery: the image object a capture tool leaves in its result JSON must
/// reach the model as a user-role carrier next to the tool result (never inside the Tool message —
/// both OpenAI adapters drop that part on the wire), and every image that is not delivered must
/// leave a note in the result string saying so and what to do instead.
/// </summary>
public sealed class ToolResultImageTests
{
    // ── parse ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_ReadsTheImageObject()
    {
        var reference = ToolResultImages.Find(
            """{"ok":true,"path":"C:/shots/full.png","image":{"path":"C:/shots/ctx.png","media_type":"image/png","width":1568,"height":982,"bytes":123456}}""");
        Assert.NotNull(reference);
        Assert.Equal("C:/shots/ctx.png", reference!.Path);
        Assert.Equal("image/png", reference.MediaType);
        Assert.Equal(1568, reference.Width);
        Assert.Equal(982, reference.Height);
        Assert.Equal(123456, reference.Bytes);
    }

    [Fact]
    public void Find_DefaultsMediaTypeAndReturnsNullForMalformedInput()
    {
        Assert.Null(ToolResultImages.Find("""{"ok":true}"""));
        Assert.Null(ToolResultImages.Find("""{"image":"C:/x.png"}"""));
        Assert.Null(ToolResultImages.Find("""{"image":{"media_type":"image/png"}}"""));
        Assert.Null(ToolResultImages.Find("not json at all"));
        var bare = ToolResultImages.Find("""{"image":{"path":"C:/x.png","bytes":10}}""");
        Assert.NotNull(bare);
        Assert.Equal("image/png", bare!.MediaType);
        Assert.Null(bare.Width);
        Assert.Equal(10, bare.Bytes);
    }

    // ── delivery through the worker conversation ────────────────────────────────

    [Fact]
    public async Task Delivered_RidesTheUserCarrierNextToTheToolResult()
    {
        var (png, pngPath) = TempPng();
        var client = new CapturingClient();
        await RunAgentAsync(client, supportsImages: true, ImageTurn("shot-1", pngPath));

        var messages = client.Messages!;
        var toolIndex = messages.FindIndex(m => m.Role == ChatRole.Tool);
        var toolMessage = messages[toolIndex];
        var result = Assert.Single(toolMessage.Contents.OfType<FunctionResultContent>());
        Assert.DoesNotContain("未附送", result.Result?.ToString(), StringComparison.Ordinal);

        var carrier = messages[toolIndex + 1];
        Assert.Equal(ChatRole.User, carrier.Role);
        var caption = Assert.Single(carrier.Contents.OfType<TextContent>());
        Assert.StartsWith("[tool-image shot-1 desktop_screenshot]", caption.Text, StringComparison.Ordinal);
        var image = Assert.Single(carrier.Contents.OfType<DataContent>());
        Assert.Equal("image/png", image.MediaType);
        Assert.True(image.Data.ToArray().AsSpan().SequenceEqual(png), "carrier bytes differ from the file on disk");
    }

    [Fact]
    public async Task UnsupportedRoute_LeavesGuidanceInTheResultAndNoImageAnywhere()
    {
        var (_, pngPath) = TempPng();
        var client = new CapturingClient();
        await RunAgentAsync(client, supportsImages: false, ImageTurn("shot-1", pngPath));

        var result = ToolResultOf(client, "shot-1");
        Assert.Contains("当前模型路由不接受图像输入", result, StringComparison.Ordinal);
        Assert.Contains("desktop_read_text", result, StringComparison.Ordinal);
        Assert.Empty(client.Messages!.SelectMany(m => m.Contents).OfType<DataContent>());
    }

    [Fact]
    public async Task Budget_KeepsOnlyTheNewestTwoImageTurns()
    {
        var (_, p1) = TempPng();
        var (_, p2) = TempPng();
        var (_, p3) = TempPng();
        var client = new CapturingClient();
        await RunAgentAsync(client, supportsImages: true, ImageTurn("old", p1), ImageTurn("mid", p2), ImageTurn("new", p3));

        Assert.Equal(2, client.Messages!.SelectMany(m => m.Contents).OfType<DataContent>().Count());
        Assert.Contains("仅最近 2 轮", ToolResultOf(client, "old"), StringComparison.Ordinal);
        Assert.DoesNotContain("未附送", ToolResultOf(client, "mid"), StringComparison.Ordinal);
        Assert.DoesNotContain("未附送", ToolResultOf(client, "new"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedImage_IsNotDelivered()
    {
        var (_, pngPath) = TempPng();
        var client = new CapturingClient();
        await RunAgentAsync(client, supportsImages: true, ImageTurn("big", pngPath, declaredBytes: 5_000_000));

        Assert.Empty(client.Messages!.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Contains("超过单图上限", ToolResultOf(client, "big"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_DegradesToANote()
    {
        var client = new CapturingClient();
        var gone = Path.Combine(Path.GetTempPath(), "tinadec-tool-image-tests", $"gone-{Guid.NewGuid():N}.png");
        await RunAgentAsync(client, supportsImages: true, ImageTurn("gone", gone, declaredBytes: 100));

        Assert.Empty(client.Messages!.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Contains("读取失败", ToolResultOf(client, "gone"), StringComparison.Ordinal);
    }

    // ── online-loop budget demotion ─────────────────────────────────────────────

    [Fact]
    public void DemoteBeyondBudget_StripsTheOldestCarrierImages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "goal"),
            Carrier("c1", 1),
            new(ChatRole.Assistant, "step 1 done"),
            Carrier("c2", 2),
            Carrier("c3", 3),
        };

        ToolResultImages.DemoteBeyondBudget(messages);

        // the oldest carrier lost its image but kept the caption and a pointer to the file
        Assert.Empty(messages[1].Contents.OfType<DataContent>());
        var demoted = Assert.Single(messages[1].Contents.OfType<TextContent>());
        Assert.StartsWith("[tool-image c1", demoted.Text, StringComparison.Ordinal);
        Assert.Contains("移出上下文", demoted.Text, StringComparison.Ordinal);
        // the two newest carriers are untouched
        Assert.Single(messages[3].Contents.OfType<DataContent>());
        Assert.Single(messages[4].Contents.OfType<DataContent>());
        // a non-carrier user message is never demoted
        Assert.IsType<TextContent>(Assert.Single(messages[0].Contents));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static WorkerToolTurn ImageTurn(string callId, string pngPath, long? declaredBytes = null) => new()
    {
        CallId = callId,
        ToolId = "desktop_screenshot",
        ArgumentsJson = "{}",
        ResultJson = ImageResultJson(pngPath, declaredBytes ?? new FileInfo(pngPath).Length),
    };

    private static string ImageResultJson(string pngPath, long bytes) =>
        "{\"ok\":true,\"path\":\"C:/shots/full.png\",\"width\":2560,\"height\":1600,"
        + $"\"image\":{{\"path\":\"{pngPath.Replace("\\", "/")}\",\"media_type\":\"image/png\",\"width\":1568,\"height\":980,\"bytes\":{bytes}}}}}";

    private static async Task RunAgentAsync(CapturingClient client, bool supportsImages, params WorkerToolTurn[] history)
    {
        await new ExecutionAgent(new Factory(client, supportsImages)).GetNextTurnAsync(
            new DmaeaRunContext { RunId = Guid.NewGuid(), SessionId = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), TriggerMessageId = Guid.NewGuid(), UserGoal = "goal" },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "worker", Layer = "execution", AgentType = "worker", Enabled = true },
            new PlannedTask { Title = "t", SuccessCriteria = ["done"] },
            history, [], null, 64, CancellationToken.None);
    }

    private static string ToolResultOf(CapturingClient client, string callId) =>
        client.Messages!.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Single(r => r.CallId == callId).Result?.ToString() ?? string.Empty;

    private static ChatMessage Carrier(string callId, int seed) => new(ChatRole.User,
    [
        new TextContent($"[tool-image {callId} desktop_screenshot] caption {seed}"),
        new DataContent(new byte[] { (byte)seed, (byte)(seed + 1) }, "image/png"),
    ]);

    private static (byte[] Bytes, string Path) TempPng()
    {
        // a minimal but valid PNG header followed by payload: the delivery path never decodes it,
        // it only has to exist and match byte-for-byte on the other side.
        var bytes = Convert.FromHexString(
            "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c6300010000050001" +
            "0d0a2db40000000049454e44ae426082");
        var directory = Path.Combine(Path.GetTempPath(), "tinadec-tool-image-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"shot-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, bytes);
        return (bytes, path);
    }

    private sealed class Factory(IChatClient client, bool supportsImages) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, Model = "fake", ModelId = "openai/fake", SupportsImageInput = supportsImages });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) => Task.FromResult(client);
    }

    private sealed class CapturingClient : IChatClient
    {
        public List<ChatMessage>? Messages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
