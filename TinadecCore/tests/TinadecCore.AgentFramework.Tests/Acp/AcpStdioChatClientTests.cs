using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Acp;
using TinadecCore.Persistence;

namespace TinadecCore.AgentFramework.Tests.Acp;

/// <summary>
/// The harness-as-chat-model seam: a real <see cref="AcpSessionHost"/> and a real
/// <see cref="AcpSession"/> driven through <see cref="IChatClient"/>, so what Core's run engine sees
/// is what an agent would see — streamed text, one usage record, and a harness it can actually stop.
/// </summary>
public sealed class AcpStdioChatClientTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "tinadec-acp-chat-client", Guid.NewGuid().ToString("N"));

    public AcpStdioChatClientTests() => Directory.CreateDirectory(_contentRoot);

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            try
            {
                Directory.Delete(_contentRoot, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private (AcpSessionHost Host, FakeAcpGenerationPool Pool) CreateHost()
    {
        var paths = new StoragePaths(_contentRoot, Options.Create(new TinadecPersistenceOptions { DataRoot = "data" }));
        var pool = new FakeAcpGenerationPool();
        var host = new AcpSessionHost(AcpSessionTestSupport.FastOptions(), new HarnessWorkspaceRoots(paths), new RefusingAcpInteractionRouterFactory(),
            NullLogger<AcpSessionHost>.Instance, pool.Create);
        return (host, pool);
    }

    private static AcpSessionRequest Request(IAcpSessionHost host, Guid providerInstanceId) => new(
        providerInstanceId,
        "opencode",
        "fake-harness-binary",
        ["acp"],
        host.ScratchDirectoryFor(providerInstanceId));

    private static ChatMessage[] Ask(string text) => [new ChatMessage(ChatRole.User, text)];

    [Fact]
    public void PromptBlocks_MapLocalImageDataWithoutGuessingTheMediaType()
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent("look at this"),
            new DataContent(new byte[] { 1, 2, 3, 4 }, "image/png") { Name = "shot.png" }
        ]);

        var blocks = AcpStdioChatClient.PromptBlocks([message]);

        Assert.Equal("text", blocks[0].Type);
        Assert.Equal("look at this", blocks[0].Text);
        Assert.Equal("image", blocks[1].Type);
        Assert.Equal("image/png", blocks[1].MimeType);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), blocks[1].Data);
    }

    [Fact]
    public async Task GetResponseAsync_ReturnsTheAnswerTheAgentStreamed()
    {
        var (host, pool) = CreateHost();
        pool.Configure = agent => agent.OnPrompt = StreamAsync("Hello", " world");
        var provider = Guid.NewGuid();

        var response = await new AcpStdioChatClient(host, Request(host, provider))
            .GetResponseAsync(Ask("hi"), cancellationToken: CancellationToken.None);

        Assert.Equal("Hello world", response.Text);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Streaming_EmitsOneUpdatePerChunkAndEndsWithUsage()
    {
        var (host, pool) = CreateHost();
        pool.Configure = agent => agent.OnPrompt = async (a, _) =>
        {
            await a.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Hel" } });
            await a.SendSessionUpdateAsync(new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "(private)" } });
            await a.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "lo" } });
            await a.SendSessionUpdateAsync(new { sessionUpdate = "usage_update", used = 91_000, size = 200_000 });
            return new { stopReason = "end_turn" };
        };
        var provider = Guid.NewGuid();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in new AcpStdioChatClient(host, Request(host, provider))
            .GetStreamingResponseAsync(Ask("hi"), cancellationToken: CancellationToken.None))
        {
            updates.Add(update);
        }

        // A thought chunk is not an answer: it reaches the observer, never the chat stream.
        Assert.Equal(["Hel", "lo"], updates.Where(u => !string.IsNullOrEmpty(u.Text)).Select(u => u.Text).ToArray());
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>());
        Assert.Equal(91_000, usage.Details.TotalTokenCount);
        await host.DisposeAsync();
    }

    /// <summary>
    /// The lease is what makes a harness usable as a shared chat backend: a second run against the
    /// same provider queues instead of hitting the protocol's one-turn rule and failing with
    /// "already has a turn in flight" while the first is still thinking.
    /// </summary>
    [Fact]
    public async Task ConcurrentRuns_OnOneProviderInstance_QueueInsteadOfFaulting()
    {
        var (host, pool) = CreateHost();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.Configure = agent => agent.OnPrompt = async (a, text) =>
        {
            await release.Task;
            await a.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "answer:" + text } });
            return new { stopReason = "end_turn" };
        };
        var provider = Guid.NewGuid();

        var first = new AcpStdioChatClient(host, Request(host, provider))
            .GetResponseAsync(Ask("first"), cancellationToken: CancellationToken.None);
        await AcpTestHarness.WaitForAsync(() => pool.Latest.Prompts == 1);

        var second = new AcpStdioChatClient(host, Request(host, provider))
            .GetResponseAsync(Ask("second"), cancellationToken: CancellationToken.None);
        await Task.Delay(200);

        // Queued, not rejected: the harness has still only seen one prompt.
        Assert.Equal(1, pool.Latest.Prompts);

        release.SetResult();
        var both = await Task.WhenAll(first, second);

        Assert.Equal(2, pool.Latest.Prompts);
        Assert.Equal(["answer:first", "answer:second"], both.Select(response => response.Text).ToArray());
        await host.DisposeAsync();
    }

    /// <summary>
    /// A chat client is built per model invocation, so if the scratch directory were minted per
    /// client the host would refuse the second turn ("already has a session rooted at …"). Stable
    /// per provider is what lets one harness keep the conversation across runs.
    /// </summary>
    [Fact]
    public async Task SequentialInvocations_ReuseOneHarnessProcess_AndTheSameScratchRoot()
    {
        var (host, pool) = CreateHost();
        pool.Configure = agent => agent.OnPrompt = StreamAsync("ok");
        var provider = Guid.NewGuid();
        var scratch = host.ScratchDirectoryFor(provider);

        await new AcpStdioChatClient(host, Request(host, provider)).GetResponseAsync(Ask("one"), cancellationToken: CancellationToken.None);
        await new AcpStdioChatClient(host, Request(host, provider)).GetResponseAsync(Ask("two"), cancellationToken: CancellationToken.None);

        Assert.Equal(1, pool.Count);
        Assert.Equal(2, pool.Latest.Prompts);
        Assert.Equal(scratch, host.ScratchDirectoryFor(provider));
        // The scratch root lives under Temp: StoragePaths.HarnessWorkspace moved it off
        // "acp-sessions" so harness scratch files stay out of the backed-up data tree. Ask
        // StoragePaths for the root rather than pinning a location the product may move again.
        var paths = new StoragePaths(_contentRoot, Options.Create(new TinadecPersistenceOptions { DataRoot = "data" }));
        Assert.StartsWith(Path.GetFullPath(paths.Locations.Temp), scratch, StringComparison.Ordinal);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_ReachesTheHarnessAsANotification()
    {
        var (host, pool) = CreateHost();
        pool.Configure = agent => agent.SuspendPrompts = true;
        var provider = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var turn = new AcpStdioChatClient(host, Request(host, provider))
            .GetResponseAsync(Ask("never answered"), cancellationToken: cts.Token);
        await AcpTestHarness.WaitForAsync(() => pool.Latest.ReceivedFrames.Any(frame => frame.Contains("session/prompt", StringComparison.Ordinal)));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        await AcpTestHarness.WaitForAsync(() => pool.Latest.Cancels >= 1);
        // `session/cancel` is a notification: the frame that carries it must not carry an id.
        var cancelFrame = pool.Latest.ReceivedFrames.Single(frame => frame.Contains("session/cancel", StringComparison.Ordinal));
        Assert.DoesNotContain("\"id\"", cancelFrame, StringComparison.Ordinal);
        await host.DisposeAsync();
    }

    private static Func<FakeAcpAgent, string, Task<object?>> StreamAsync(params string[] chunks) => async (agent, _) =>
    {
        foreach (var chunk in chunks)
        {
            await agent.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = chunk } });
        }

        return new { stopReason = "end_turn" };
    };
}
