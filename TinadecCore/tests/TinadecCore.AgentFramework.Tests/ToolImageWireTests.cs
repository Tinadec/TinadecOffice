using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Models.Harness;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The native tool-result image channel, end to end at the wire. Three acceptances live here:
/// (1) a Responses request built through the production factory shape + rewrite handler carries
/// the image inside the function_call_output as a native content-part array; (2) a Tool message
/// that still contains a DataContent is refused with <see cref="ToolImageUnrepresentableException"/>
/// instead of being silently dropped by the SDK adapter; (3) requests whose tool results carry no
/// image are byte-for-byte identical with and without the rewrite handler in the pipeline.
/// </summary>
public sealed class ToolImageWireTests
{
    // ── the rewriter itself ─────────────────────────────────────────────────────

    [Fact]
    public void Rewriter_LeavesImagelessBodiesUntouched()
    {
        Assert.Null(ToolImageOutputRewriter.TryRewrite("""{"model":"m","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}]}"""));
        Assert.Null(ToolImageOutputRewriter.TryRewrite("""{"model":"m","input":[{"type":"function_call_output","call_id":"c1","output":"{\"ok\":true}"}]}"""));
        Assert.Null(ToolImageOutputRewriter.TryRewrite("not json at all"));
        // an output JSON with an image object but no path → nothing to deliver, no rewrite
        Assert.Null(ToolImageOutputRewriter.TryRewrite("""{"input":[{"type":"function_call_output","call_id":"c1","output":"{\"image\":{\"media_type\":\"image/png\"}}"}]}"""));
    }

    [Fact]
    public void Rewriter_TurnsTheImageObjectIntoNativeParts()
    {
        var (png, pngPath) = TempPng();
        var resultJson = "{\"ok\":true,\"path\":\"C:/full.png\",\"image\":{\"path\":\"" + pngPath.Replace("\\", "/") + "\",\"media_type\":\"image/png\",\"width\":10,\"height\":10,\"bytes\":" + png.Length + "}}";
        var head = "{\"model\":\"m\",\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"goal\"}]},"
            + "{\"type\":\"function_call\",\"call_id\":\"c1\",\"name\":\"desktop_screenshot\",\"arguments\":\"{}\"},"
            + "{\"type\":\"function_call_output\",\"call_id\":\"c1\",\"output\":";
        var tail = "},{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"end\"}]}]}";
        var body = head + JsonSerializer.Serialize(resultJson) + tail;

        var rewritten = ToolImageOutputRewriter.TryRewrite(body);
        Assert.NotNull(rewritten);

        using var document = JsonDocument.Parse(rewritten!);
        var items = document.RootElement.GetProperty("input");
        // the user messages and the function_call survive untouched
        Assert.Equal("goal", items[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("function_call", items[1].GetProperty("type").GetString());
        Assert.Equal("end", items[3].GetProperty("content")[0].GetProperty("text").GetString());

        // the tool result is the native form: output is an array of content parts
        var output = items[2];
        Assert.Equal("function_call_output", output.GetProperty("type").GetString());
        Assert.Equal("c1", output.GetProperty("call_id").GetString());
        Assert.Equal(JsonValueKind.Array, output.GetProperty("output").ValueKind);
        var parts = output.GetProperty("output");
        Assert.Equal("input_text", parts[0].GetProperty("type").GetString());
        Assert.Equal(resultJson, parts[0].GetProperty("text").GetString());
        Assert.Equal("input_image", parts[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", parts[1].GetProperty("image_url").GetString());
        Assert.Equal(png, Convert.FromBase64String(parts[1].GetProperty("image_url").GetString()!["data:image/png;base64,".Length..]));
    }

    [Fact]
    public void Rewriter_MissingImageFileFailsLoudly()
    {
        var gone = Path.Combine(Path.GetTempPath(), "tinadec-tool-image-tests", $"wire-gone-{Guid.NewGuid():N}.png");
        var resultJson = "{\"ok\":true,\"image\":{\"path\":\"" + gone.Replace("\\", "/") + "\",\"media_type\":\"image/png\"}}";
        var body = "{\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"c1\",\"output\":" + JsonSerializer.Serialize(resultJson) + "}]}";
        Assert.Throws<IOException>(() => ToolImageOutputRewriter.TryRewrite(body));
    }

    // ── native mode at the conversation level ──────────────────────────────────

    [Fact]
    public async Task NativeMode_KeepsTheImageInResultJson_AndAddsNoCarrierMessage()
    {
        var (_, pngPath) = TempPng();
        var client = new CapturingClient();
        var history = new List<WorkerToolTurn> { ImageTurn("shot-1", pngPath) };

        await RunAgentAsync(client, protocol: ChatProtocols.OpenAiResponses, supportsImages: true, history);

        var messages = client.Messages!;
        // native: no DataContent anywhere, no carrier message — the image stays in the result JSON
        Assert.Empty(messages.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.DoesNotContain(messages, m => ToolResultImages.IsCarrier(m));
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("\"image\"", result.Result?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("未附送", result.Result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeMode_OutOfBudgetImageIsStrippedFromTheResultJson()
    {
        var (_, p1) = TempPng();
        var (_, p2) = TempPng();
        var (_, p3) = TempPng();
        var client = new CapturingClient();
        var history = new List<WorkerToolTurn> { ImageTurn("old", p1), ImageTurn("mid", p2), ImageTurn("new", p3) };

        await RunAgentAsync(client, protocol: ChatProtocols.OpenAiResponses, supportsImages: true, history);

        var results = client.Messages!.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .ToDictionary(r => r.CallId, r => r.Result?.ToString() ?? string.Empty);
        // the newest two keep their image object; the oldest is stripped so the HTTP rewrite
        // cannot pick it up, and the model reads why
        Assert.DoesNotContain("\"image\"", results["old"], StringComparison.Ordinal);
        Assert.Contains("未附送", results["old"], StringComparison.Ordinal);
        Assert.Contains("\"image\"", results["mid"], StringComparison.Ordinal);
        Assert.Contains("\"image\"", results["new"], StringComparison.Ordinal);
    }

    // ── acceptance 2: the hard stop that replaces the silent drop ──────────────

    [Fact]
    public void ToolMessageImageData_IsRefusedWithATypedError()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-9", "{\"ok\":true}"),
                new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
            ]),
        };
        var resolution = new ChatResolution { IsAvailable = true, Protocol = ChatProtocols.OpenAiChat, SupportsImageInput = true };

        var error = Assert.Throws<ToolImageUnrepresentableException>(
            () => ToolResultImages.AssertNoToolMessageImages(messages, resolution));
        Assert.Equal(ChatProtocols.OpenAiChat, error.Protocol);
        Assert.Equal("call-9", error.CallId);
    }

    // ── acceptance 1 + 3: the wire ──────────────────────────────────────────────

    [Fact]
    public async Task Wire_ResponsesRequestCarriesTheImageInsideTheToolResult()
    {
        var (png, pngPath) = TempPng();
        var handler = new CapturingHandler();
        var client = ResponsesClientWith(new ToolImageRewriteHandler(handler));
        var history = new List<WorkerToolTurn> { ImageTurn("shot-1", pngPath) };

        await RunAgentAsync(client, protocol: ChatProtocols.OpenAiResponses, supportsImages: true, history);

        var body = handler.LastBody ?? throw new InvalidOperationException("no request captured");
        // the image is on the wire, natively inside the function_call_output
        Assert.Contains("\"output\":[", body, StringComparison.Ordinal);
        Assert.Contains("\"input_image\"", body, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", body, StringComparison.Ordinal);
        Assert.Equal(png, Convert.FromBase64String(ExtractFirstImageUrl(body)));
    }

    [Fact]
    public async Task Wire_ImagelessRequestsAreByteForByteIdenticalWithAndWithoutTheHandler()
    {
        // acceptance 3: the rewrite handler must not change a single byte of requests whose
        // tool results carry no image — same conversation through both pipelines.
        var plain = new CapturingHandler();
        var rewritten = new CapturingHandler();
        var history = new List<WorkerToolTurn>
        {
            new() { CallId = "plain-1", ToolId = "desktop_read_text", ArgumentsJson = "{}", ResultJson = "{\"ok\":true,\"texts\":[]}" },
        };

        await RunAgentAsync(ResponsesClientWith(plain), ChatProtocols.OpenAiResponses, true, history);
        await RunAgentAsync(ResponsesClientWith(new ToolImageRewriteHandler(rewritten)), ChatProtocols.OpenAiResponses, true, history);

        Assert.NotNull(plain.LastBody);
        Assert.NotNull(rewritten.LastBody);
        Assert.Equal(plain.LastBody, rewritten.LastBody);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static async Task RunAgentAsync(IChatClient client, string protocol, bool supportsImages, IReadOnlyList<WorkerToolTurn> history)
    {
        await new ExecutionAgent(new Factory(client, protocol, supportsImages)).GetNextTurnAsync(
            new DmaeaRunContext { RunId = Guid.NewGuid(), SessionId = Guid.NewGuid(), TenantId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), TriggerMessageId = Guid.NewGuid(), UserGoal = "goal" },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "worker", Layer = "execution", AgentType = "worker", Enabled = true },
            new PlannedTask { Title = "t", SuccessCriteria = ["done"] },
            history, [], null, 64, CancellationToken.None);
    }

    private static WorkerToolTurn ImageTurn(string callId, string pngPath) => new()
    {
        CallId = callId,
        ToolId = "desktop_screenshot",
        ArgumentsJson = "{}",
        ResultJson = "{\"ok\":true,\"path\":\"C:/full.png\",\"image\":{\"path\":\"" + pngPath.Replace("\\", "/") + "\",\"media_type\":\"image/png\",\"width\":1568,\"height\":980,\"bytes\":1234}}",
    };

    /// <summary>The production factory shape for a Responses client, with a capturing handler under the rewriter.</summary>
    private static IChatClient ResponsesClientWith(HttpMessageHandler handler)
    {
#pragma warning disable OPENAI001
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://127.0.0.1:9/"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        return new OpenAIClient(new System.ClientModel.ApiKeyCredential("key"), options).GetResponsesClient().AsIChatClient("m");
#pragma warning restore OPENAI001
    }

    private static string ExtractFirstImageUrl(string body)
    {
        var marker = "data:image/png;base64,";
        var start = body.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = start;
        while (end < body.Length && (char.IsAsciiLetterOrDigit(body[end]) || body[end] is '+' or '/' or '=')) end++;
        return body[start..end];
    }

    private static (byte[] Bytes, string Path) TempPng()
    {
        var bytes = Convert.FromHexString(
            "89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c6300010000050001" +
            "0d0a2db40000000049454e44ae426082");
        var directory = Path.Combine(Path.GetTempPath(), "tinadec-tool-image-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"wire-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, bytes);
        return (bytes, path);
    }

    private sealed class Factory(IChatClient client, string protocol, bool supportsImages) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string routePurpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, Model = "m", ModelId = "openai/m", Protocol = protocol, SupportsImageInput = supportsImages });

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

    /// <summary>Returns a canned Responses API body; captures the request verbatim.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string ResponseJson =
            """
            {"id":"resp_1","object":"response","created_at":1730000000,"model":"m","status":"completed",
             "output":[{"type":"message","id":"msg_1","role":"assistant","status":"completed",
                        "content":[{"type":"output_text","text":"ok"}]}],
             "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
            """;

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
