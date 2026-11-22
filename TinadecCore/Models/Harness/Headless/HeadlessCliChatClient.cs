using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models.Harness.Headless;

/// <summary>
/// Runs one turn of a harness as a headless one-shot and returns its answer, so a local agent can be
/// the model behind an ordinary dispatch: the conversation text goes to the process, the answer comes
/// back as an assistant message, and the harness still picks its own model and runs its own tools —
/// inside the governed scratch directory it was spawned in.
/// <para>
/// This channel has no session. Each turn re-sends the whole message list as one prompt, so the files
/// a harness wrote carry over (same working directory every turn, see
/// <see cref="IHarnessWorkspaceRoots"/>) while its memory of the conversation does not. That is the
/// vendor's own shape for <c>-p</c>/<c>exec</c>, not a shortcut taken here.
/// </para>
/// <para>
/// Every way this build knows of losing an answer ends in an exception rather than an empty message:
/// a vendor-reported failure, a non-zero exit, or a stream that ended without its terminal frame.
/// Measured on this host, <c>claude -p &lt;prompt&gt; --input-format stream-json</c> exits 0 with zero
/// bytes of stdout, and <c>dsh</c> emits <c>{"type":"final","text":""}</c> while exiting 1 — either
/// would otherwise be recorded as a model that answered in silence.
/// </para>
/// </summary>
internal sealed class HeadlessCliChatClient : IChatClient
{
    private readonly string _driver;
    private readonly HarnessChannelSpec _channel;
    private readonly HarnessHeadlessEnvelopes _envelopeKind;
    private readonly string _fileName;
    private readonly string _workingDirectory;
    private readonly IReadOnlyDictionary<string, string?>? _environment;
    private readonly IHeadlessHarnessRunner _runner;

    internal string WorkingDirectory => _workingDirectory;

    internal IReadOnlyDictionary<string, string?>? Environment => _environment;

    public HeadlessCliChatClient(
        string driver,
        HarnessChannelSpec channel,
        HarnessHeadlessEnvelopes envelopeKind,
        string fileName,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        IHeadlessHarnessRunner runner)
    {
        _driver = driver;
        _channel = channel;
        _envelopeKind = envelopeKind;
        _fileName = fileName;
        _workingDirectory = workingDirectory;
        _environment = environment is null
            ? null
            : new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        _runner = runner;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var turn = await RunAsync(PromptText(messages), null, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, turn.Answer))
        {
            Usage = turn.Usage
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deltas = Channel.CreateUnbounded<string>();
        var turn = RunAsync(PromptText(messages), deltas.Writer, cancellationToken);

        // Same reason as the ACP client: the channel closing is the only thing that can end this loop,
        // and reading the exception here marks a faulted harness task as observed.
        _ = turn.ContinueWith(
            task =>
            {
                if (task.IsFaulted) deltas.Writer.TryComplete(task!.Exception!.GetBaseException());
                else if (task.IsCanceled) deltas.Writer.TryComplete(new OperationCanceledException());
                else deltas.Writer.TryComplete();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        await foreach (var text in deltas.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (text.Length > 0) yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        var result = await turn.ConfigureAwait(false);
        if (result.Usage is { } usage) yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
    }

    public TService? GetService<TService>(object? key = null) where TService : class
        => typeof(TService) == typeof(IChatClient) ? (TService)(object)this : null;

    public object? GetService(Type serviceType, object? key = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
    }

    private async Task<(string Answer, UsageDetails? Usage)> RunAsync(
        string prompt,
        ChannelWriter<string>? deltas,
        CancellationToken cancellationToken)
    {
        // Fresh per turn: an envelope that already answered once would hand back its previous answer.
        var envelope = HeadlessEnvelopes.For(_envelopeKind)!;
        var request = new HarnessTurnRequest(
            _fileName,
            HarnessCatalog.MaterializeChannelArgv(_driver, AgentChannels.Cli, prompt),
            _workingDirectory,
            _environment,
            StdinPayload(prompt));

        var outcome = await _runner.RunAsync(
            request,
            envelope,
            deltas is null ? null : text => deltas.TryWrite(text),
            cancellationToken).ConfigureAwait(false);

        if (envelope.Failure is { } failure)
        {
            throw new InvalidOperationException(
                $"'{_driver}' answered its headless turn with a failure: {failure}"
                + DescribeTail(outcome));
        }

        if (outcome.ExitCode is not null and not 0)
        {
            throw new InvalidOperationException(
                $"'{_driver}' exited its headless turn with code {outcome.ExitCode}" + DescribeTail(outcome));
        }

        if (envelope.Answer is null)
        {
            throw new InvalidOperationException(
                $"'{_driver}' finished a headless turn without emitting {envelope.TerminalFrame}, so Core has no answer to record. " +
                "Reading its stdout with a different envelope would be a guess." + DescribeTail(outcome));
        }

        return (envelope.Answer, envelope.Usage);
    }

    /// <summary>
    /// Where the prompt goes, decided by the catalog row rather than here — and framed as the vendor
    /// expects, because the measured behaviour of an argv prompt under <c>--input-format stream-json</c>
    /// is silence with a zero exit code.
    /// </summary>
    private string? StdinPayload(string prompt) => _channel.PromptDelivery switch
    {
        HarnessPromptDeliveries.Stdin => prompt + "\n",
        HarnessPromptDeliveries.StdinMessage => JsonSerializer.Serialize(new UserMessageFrame
        {
            Message = new UserMessageContent { Content = [new UserTextContent { Text = prompt }] }
        }) + "\n",
        _ => null
    };

    private static string DescribeTail(HarnessTurnOutcome outcome) => string.IsNullOrWhiteSpace(outcome.Stderr)
        ? string.Empty
        : $". Last stderr: {outcome.Stderr.Trim()}";

    /// <summary>
    /// Flattens the conversation into the one prompt a one-shot harness gets, the same way
    /// <c>AcpStdioChatClient</c> does: text blocks joined, non-text content dropped. No role labels are
    /// inserted — the measured call that produced a real answer carried the user's text verbatim, and a
    /// prefix invented here would be part of every prompt the harness sees.
    /// </summary>
    private static string PromptText(IEnumerable<ChatMessage> messages)
    {
        var parts = new StringBuilder();
        foreach (var message in messages)
        {
            var text = string.Join('\n', message.Contents.OfType<TextContent>()
                .Where(content => content.Text.Length > 0)
                .Select(content => content.Text));
            if (text.Length == 0) continue;
            if (parts.Length > 0) parts.Append("\n\n");
            parts.Append(text);
        }
        return parts.ToString();
    }

    /// <summary>
    /// The <c>type:"user"</c> frame the Claude-family harnesses take on stdin once
    /// <c>--input-format stream-json</c> is selected. Property names are lowercase because the vendor's
    /// parser is case-sensitive; the defaults below keep the frame to exactly the fields measured.
    /// </summary>
    private sealed class UserMessageFrame
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "user";

        [JsonPropertyName("message")]
        public UserMessageContent Message { get; init; } = new();
    }

    private sealed class UserMessageContent
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = "user";

        [JsonPropertyName("content")]
        public List<UserTextContent> Content { get; init; } = [];
    }

    private sealed class UserTextContent
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "text";

        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;
    }
}
