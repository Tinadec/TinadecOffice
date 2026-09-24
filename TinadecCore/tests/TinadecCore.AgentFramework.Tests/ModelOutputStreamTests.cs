using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using StreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

public sealed class ModelOutputStreamTests
{
    [Fact]
    public async Task PublishesBeforeProviderCompletes_AndPreservesCallsAndUsage()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<ModelOutputFrame>();
        var client = new StreamingClient(async () => await release.Task);
        var pending = ModelOutputStream.ReadAsync(client, [], null, Guid.NewGuid(), (frame, _) =>
        {
            frames.Add(frame);
            if (frame.Channel == "reasoning") first.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.DoesNotContain(frames, frame => frame.Kind == "completed");
        }
        finally { release.TrySetResult(); }
        var response = await pending;
        Assert.Equal("Answer", response.Text);
        Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal(7, response.Usage?.TotalTokenCount);
        Assert.DoesNotContain(frames, frame => (frame.Delta ?? "").Contains("protected-secret"));
        Assert.Equal("completed", frames[^1].Kind);
    }

    [Fact]
    public async Task CancelledPartialOutputIsMarkedFailed_NotCompleted()
    {
        var frames = new List<ModelOutputFrame>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModelOutputStream.ReadAsync(
            new StreamingClient(() => throw new OperationCanceledException()), [], null, Guid.NewGuid(),
            (frame, _) => { frames.Add(frame); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal("failed", frames[^1].Kind);
        Assert.DoesNotContain(frames, frame => frame.Kind == "completed");
    }

    [Theory]
    [InlineData("<think>", "</think>")]
    [InlineData("<thinking data-test='1'>", "</thinking >")]
    public void SplitTagsNeverLeakIntoTheAnswer(string open, string close)
    {
        var splitter = new ModelTextSplitter();
        var parts = new List<(string Channel, string Text)>();
        foreach (var c in $"{open}inspect file{close}Answer < 3") parts.AddRange(splitter.Push(c.ToString()));
        parts.AddRange(splitter.Push("", final: true));
        Assert.Equal("inspect file", string.Concat(parts.Where(p => p.Channel == "reasoning").Select(p => p.Text)));
        Assert.Equal("Answer < 3", string.Concat(parts.Where(p => p.Channel == "text").Select(p => p.Text)));
    }

    [Fact]
    public void CompatibleReasoningIsReadFromTheSdkExtension_NotArbitraryMetadata()
    {
        var raw = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString("""
            {"id":"response-1","object":"chat.completion.chunk","created":1,"model":"compatible",
             "choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Inspect the file","secret":"not displayed"},"finish_reason":null}]}
            """));
        var update = new ChatResponseUpdate { RawRepresentation = raw };
        Assert.Equal("Inspect the file", ModelOutputStream.CompatibleReasoning(update));
    }

    [Fact]
    public async Task RealChatSdkRetainsCompatibleReasoningOnStreamingUpdates()
    {
        using var http = new HttpClient(new SseHandler());
        var sdk = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("test-only"),
            new OpenAI.OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http) });
        using var client = sdk.GetChatClient("test-model").AsIChatClient();
        var frames = new List<ModelOutputFrame>();
        await ModelOutputStream.ReadAsync(client, [new ChatMessage(ChatRole.User, "hello")], null, Guid.NewGuid(),
            (frame, _) => { frames.Add(frame); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Contains(frames, frame => frame.Channel == "reasoning" && frame.Delta == "Inspect file");
        Assert.Contains(frames, frame => frame.Channel == "text" && frame.Delta == "Answer");
    }

    private sealed class SseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"r","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Inspect file"},"finish_reason":null}]}

                    data: {"id":"r","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"content":"Answer"},"finish_reason":"stop"}]}

                    data: [DONE]


                    """, System.Text.Encoding.UTF8, "text/event-stream")
            });
    }

    private sealed class StreamingClient(Func<Task> wait) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new TextReasoningContent("Inspect file") { ProtectedData = "protected-secret" }]);
            await wait();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Answer");
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())]);
            yield return new ChatResponseUpdate(null, [new UsageContent(new UsageDetails { TotalTokenCount = 7 })]);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
