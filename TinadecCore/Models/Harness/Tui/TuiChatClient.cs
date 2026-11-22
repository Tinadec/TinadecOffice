using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Models.Harness.Tui;

internal sealed class TuiChatClient : IChatClient
{
    private readonly string _driver;
    private readonly string _fileName;
    private readonly string _workingDirectory;
    private readonly IReadOnlyDictionary<string, string?>? _environment;
    private readonly IReadOnlyList<string> _arguments;
    private readonly ITuiHarnessRunner _runner;

    internal string WorkingDirectory => _workingDirectory;

    internal IReadOnlyDictionary<string, string?>? Environment => _environment;

    public TuiChatClient(
        string driver,
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        ITuiHarnessRunner runner)
    {
        _driver = driver;
        _fileName = fileName;
        _workingDirectory = workingDirectory;
        _arguments = arguments;
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
        var result = await RunAsync(PromptText(messages), null, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Answer));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deltas = Channel.CreateUnbounded<string>();
        var turn = RunAsync(PromptText(messages), deltas.Writer, cancellationToken);
        _ = turn.ContinueWith(
            task =>
            {
                if (task.IsFaulted) deltas.Writer.TryComplete(task.Exception!.GetBaseException());
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
        await turn.ConfigureAwait(false);
    }

    public TService? GetService<TService>(object? key = null) where TService : class
        => typeof(TService) == typeof(IChatClient) ? (TService)(object)this : null;

    public object? GetService(Type serviceType, object? key = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose() { }

    private async Task<TuiTurnOutcome> RunAsync(string prompt, ChannelWriter<string>? deltas, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException($"'{_driver}' cannot start a TUI turn without prompt text.");

        return await _runner.RunAsync(
            new TuiTurnRequest(_fileName, _arguments, _workingDirectory, _environment),
            prompt,
            deltas is null ? null : text => deltas.TryWrite(text),
            cancellationToken).ConfigureAwait(false);
    }

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
}
