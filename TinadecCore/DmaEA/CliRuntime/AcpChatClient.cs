using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TinadecCore.DmaEA.CliRuntime;

/// <summary>
/// Chat-only Agent Client Protocol (ACP) client: hosts the CLI agent's local JSON-RPC/SSE server
/// and maps <c>message/delta</c> text events onto <see cref="ChatResponseUpdate"/> deltas.
/// Tool calls and permission requests are deliberately unsupported — the workbench's tool
/// governance stays with TinadecTools — so a permission request fails the run instead of
/// pretending to grant it. One session per client; the chat engine is sequential per agent.
/// </summary>
internal sealed class AcpChatClient : IChatClient, IAsyncDisposable
{
    private const string ClientName = "tinadec-core";
    private const string ProtocolVersion = "0.1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly MediaTypeHeaderValue JsonMediaType = MediaTypeHeaderValue.Parse("application/json");

    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly string? _token;
    private readonly ILogger<AcpChatClient> _logger;
    private readonly Channel<JsonElement> _notifications = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private CancellationTokenSource? _subscriptionCts;
    private Task? _subscriptionTask;
    private string? _sessionId;
    private int _disposed;

    public AcpChatClient(string serverUrl, string? token, ILogger<AcpChatClient> logger)
    {
        _serverUrl = serverUrl;
        _token = token;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(serverUrl) };
        if (!string.IsNullOrWhiteSpace(token)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text)) text.Append(update.Text);
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var sessionId = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var messageId = await SendMessageAsync(sessionId, messageList, cancellationToken).ConfigureAwait(false);

        while (await _notifications.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var notification = await _notifications.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var method = notification.TryGetProperty("method", out var m) ? m.GetString() : null;
            var parameters = notification.TryGetProperty("params", out var p) ? p : default;

            switch (method)
            {
                case "message.delta" when Matches(parameters, messageId) && parameters.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && delta.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text.GetString()!);
                    break;

                case "message.done" when Matches(parameters, messageId):
                    yield break;

                case "message.error" when Matches(parameters, messageId):
                    throw new InvalidOperationException($"ACP message failed: {ErrorText(parameters)}");

                case "session.error" when MatchesSession(parameters, sessionId):
                    throw new InvalidOperationException($"ACP session failed: {ErrorText(parameters)}");

                case "permission.request":
                    throw new InvalidOperationException("ACP agent requested a permission the workbench never grants; tool governance stays with TinadecTools.");
            }
        }

        throw new InvalidOperationException($"ACP event stream ended before message {messageId} completed.");
    }

    public TService? GetService<TService>(object? key = null) where TService : class
        => typeof(TService) == typeof(IChatClient) ? (TService)(object)this : null;

    public object? GetService(Type serviceType, object? key = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose() => _ = DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subscriptionCts?.Cancel();
        _subscriptionCts?.Dispose();
        if (_sessionId is not null)
        {
            try
            {
                await PostAsync("session/cancel", new { sessionId = _sessionId }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ACP session cancel failed on dispose.");
            }
        }
        _sessionGate.Dispose();
        _http.Dispose();
    }

    private async Task<string> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is not null) return _sessionId;
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId is not null) return _sessionId;
            await PostAsync("initialize", new { client = new { name = ClientName, version = "0.1.0" }, protocolVersion = ProtocolVersion, capabilities = new { message = new { streaming = true } } }, cancellationToken).ConfigureAwait(false);
            var newSession = await PostAsync("session/new", new { }, cancellationToken).ConfigureAwait(false);
            _sessionId = newSession.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(_sessionId)) throw new InvalidOperationException("ACP session/new returned no sessionId.");
            await PostAsync("session/event", new { sessionId = _sessionId }, cancellationToken).ConfigureAwait(false);
            StartSubscription();
            return _sessionId;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private void StartSubscription()
    {
        _subscriptionCts = new CancellationTokenSource();
        _subscriptionTask = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _subscriptionCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await foreach (var sse in SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(_subscriptionCts.Token).ConfigureAwait(false), _subscriptionCts.Token).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(sse.Data)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(sse.Data);
                        await _notifications.Writer.WriteAsync(document.RootElement.Clone(), _subscriptionCts.Token).ConfigureAwait(false);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogDebug(ex, "Ignoring non-JSON ACP notification: {Data}", sse.Data);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on dispose/cancel
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ACP event stream for {ServerUrl} ended unexpectedly.", _serverUrl);
                _notifications.Writer.TryComplete(ex);
            }
            finally
            {
                _notifications.Writer.TryComplete();
            }
        });
    }

    private async Task<string> SendMessageAsync(string sessionId, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var request = new
        {
            sessionId,
            message = ToAcpMessage(messages),
            tools = Array.Empty<object>(),
            agents = Array.Empty<object>()
        };
        var result = await PostAsync("message/create", request, cancellationToken).ConfigureAwait(false);
        var messageId = result.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var id)
            ? id.GetString()
            : result.TryGetProperty("messageId", out var messageIdProp) && messageIdProp.ValueKind == JsonValueKind.String ? messageIdProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(messageId)) throw new InvalidOperationException("ACP message/create returned no message id.");
        return messageId;
    }

    private async Task<JsonElement> PostAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var body = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, parameters }, Json), Encoding.UTF8, JsonMediaType);
        using var response = await _http.PostAsync("/", body, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            throw new InvalidOperationException($"ACP {method} failed: {error.GetProperty("message").GetString() ?? error.ToString()}");
        if (!root.TryGetProperty("result", out var result))
            throw new InvalidOperationException($"ACP {method} returned no result: {text[..Math.Min(text.Length, 200)]}");
        return result;
    }

    private static object ToAcpMessage(IList<ChatMessage> messages)
    {
        var text = string.Join("\n\n", messages.Select(ToText).Where(s => s.Length > 0));
        return new { role = LastRole(messages), content = new object[] { new { type = "text", text } } };

        static string ToText(ChatMessage message)
        {
            var parts = message.Contents.OfType<TextContent>().Select(c => c.Text).Where(s => s.Length > 0);
            return string.Join("\n", parts);
        }

        static string LastRole(IList<ChatMessage> all)
        {
            for (var i = all.Count - 1; i >= 0; i--)
            {
                var role = all[i].Role.ToString();
                if (role != "System") return role == "Assistant" ? "assistant" : "user";
            }
            return "user";
        }
    }

    private static bool Matches(JsonElement parameters, string messageId)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return false;
        var value = parameters.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        return value == messageId;
    }

    private static bool MatchesSession(JsonElement parameters, string sessionId)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return false;
        var value = parameters.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        return value == sessionId;
    }

    private static string ErrorText(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("error", out var error)) return parameters.ToString();
        return error.ValueKind == JsonValueKind.String ? error.GetString()! : error.ToString();
    }
}
