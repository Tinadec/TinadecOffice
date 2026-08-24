using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

public sealed class Maf18RuntimeAdapterTests
{
    [Fact]
    public void FrameworkPackageFamily_IsLockedTo118()
    {
        Maf18RuntimeAdapter.EnsureCompatible();

        Assert.Equal(
            [
                "Microsoft.Agents.AI",
                "Microsoft.Agents.AI.Abstractions",
                "Microsoft.Agents.AI.OpenAI",
                "Microsoft.Agents.AI.Workflows"
            ],
            Maf18RuntimeAdapter.FrameworkVersions.Keys.Order(StringComparer.Ordinal));
        Assert.All(Maf18RuntimeAdapter.FrameworkVersions.Values, version =>
        {
            Assert.Equal(1, version.Major);
            Assert.Equal(18, version.Minor);
        });
    }

    [Fact]
    public void GovernanceAgent_HasStableIdentityAndCoreOwnedToolBoundary()
    {
        using var agent = Maf18RuntimeAdapter.CreateGovernanceAgent(
            new RecordingChatClient(),
            "operation.meeting",
            "meeting",
            "User-facing governance agent.",
            new ChatOptions { Instructions = "Reply from governed evidence." });

        Assert.Equal("operation.meeting", agent.Id);
        Assert.Equal("meeting", agent.Name);
        Assert.False(agent.EnableSensitiveData);

        var options = agent.GetService<ChatClientAgentOptions>();
        Assert.NotNull(options);
        Assert.False(options.AllowConcurrentInvocation);
        Assert.False(options.DisableApprovalResponseBinding);

        var invoker = agent.GetService<IChatClient>()?.GetService<FunctionInvokingChatClient>();
        Assert.NotNull(invoker);
        Assert.False(invoker.AllowConcurrentInvocation);

        var withTool = new ChatOptions
        {
            Tools = [AIFunctionFactory.CreateDeclaration(
                "write_file",
                "Writes a file",
                JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}"))]
        };
        Assert.Throws<InvalidOperationException>(() => Maf18RuntimeAdapter.CreateGovernanceAgent(
            new RecordingChatClient(), "operation.supervisor", "supervisor", "Reviews evidence.", withTool));
    }

    [Fact]
    public void Usage_IsNormalizedAndAggregatedWithoutMafTypes()
    {
        var first = Maf18RuntimeAdapter.NormalizeUsage(new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 4,
            TotalTokenCount = 14,
            CachedInputTokenCount = 3,
            AdditionalCounts = new() { ["accepted_prediction_tokens"] = 2 }
        });
        var second = Maf18RuntimeAdapter.NormalizeUsage(new UsageDetails
        {
            InputTokenCount = 7,
            OutputTokenCount = 5,
            TotalTokenCount = 12,
            ReasoningTokenCount = 2,
            AdditionalCounts = new() { ["accepted_prediction_tokens"] = 1 }
        });

        var total = Maf18RuntimeAdapter.AddUsage(first, second);

        Assert.NotNull(total);
        Assert.Equal(17, total.InputTokens);
        Assert.Equal(9, total.OutputTokens);
        Assert.Equal(26, total.TotalTokens);
        Assert.Equal(3, total.CachedInputTokens);
        Assert.Equal(2, total.ReasoningTokens);
        Assert.Equal(3, total.AdditionalCounts?["accepted_prediction_tokens"]);
        Assert.DoesNotContain("Microsoft.Agents", typeof(ModelUsage).AssemblyQualifiedName);
    }

    [Fact]
    public async Task WorkerCompaction_KeepsToolCallsAndResultsAtomic()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 5; i++)
        {
            var callId = $"call-{i}";
            history.Add(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(callId, "search", new Dictionary<string, object?> { ["query"] = i })
            ]));
            history.Add(new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent(callId, new { value = i })
            ]));
        }

        var compacted = await Maf18RuntimeAdapter.CompactWorkerConversationAsync(
            new ChatMessage(ChatRole.User, "goal"), history, maxHistoryMessages: 5, CancellationToken.None);

        Assert.Equal("goal", compacted[0].Text);
        Assert.True(compacted.Count <= 5);
        var calls = compacted.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(content => content.CallId).ToHashSet(StringComparer.Ordinal);
        var results = compacted.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(content => content.CallId).ToHashSet(StringComparer.Ordinal);
        Assert.True(calls.SetEquals(results), "Compaction must not split a tool call from its result.");
    }

    [Fact]
    public void ToolRounds_RemainACoreOwnedLimit()
    {
        ToolRuntimePolicy.Validate(new ToolRuntimePolicy("provider", true, true, 120, ToolRuntimePolicy.MaximumRounds));

        var error = Assert.Throws<InvalidDataException>(() => ToolRuntimePolicy.Validate(
            new ToolRuntimePolicy("provider", true, true, 120, ToolRuntimePolicy.MaximumRounds + 1)));

        Assert.Contains("Core safety ceiling", error.Message, StringComparison.Ordinal);
        Assert.NotEqual(ToolApprovalAgent.DefaultMaxAutoApprovalIterations, ToolRuntimePolicy.MaximumRounds);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
    }
}
