using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TinadecCore.DmaEA.CliRuntime;

/// <summary>
/// Chat-only client for the opencode <c>serve</c> HTTP/SSE surface. Creates a session, streams a
/// message via the POST response SSE stream, and maps <c>message.part.updated</c> text deltas onto
/// <see cref="ChatResponseUpdate"/>. The CLI owns model selection and its own tool use; the
/// workbench treats it as a chat backend only.
/// </summary>
internal sealed class OpenCodeChatClient : IChatClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly ILogger<OpenCodeChatClient> _logger;
    private string? _sessionId;
    private int _disposed;

    public OpenCodeChatClient(string serverUrl, string? token, ILogger<OpenCodeChatClient> logger)
    {
        _serverUrl = serverUrl;
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
        var sessionId = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var content = string.Join("\n\n", messages.Select(ToText).Where(s => s.Length > 0));
        var messageId = Guid.NewGuid().ToString("N");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/session/{Uri.EscapeDataString(sessionId)}/message")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { content, messageId }, Json), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"opencode message failed with HTTP {(int)response.StatusCode}: {error[..Math.Min(error.Length, 200)]}");
        }

        await foreach (var sse in SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(sse.Data)) continue;
            using var document = JsonDocument.Parse(sse.Data);
            var data = document.RootElement;
            switch (sse.EventName)
            {
                case "message.part.updated":
                    if (data.TryGetProperty("part", out var part) && part.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text.GetString()!);
                    }
                    break;

                case "message.completed":
                    yield break;

                case "message.error":
                    throw new InvalidOperationException($"opencode message failed: {ErrorText(data)}");
            }
        }
    }

    public TService? GetService<TService>(object? key = null) where TService : class
        => typeof(TService) == typeof(IChatClient) ? (TService)(object)this : null;

    public object? GetService(Type serviceType, object? key = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_sessionId is not null)
        {
            try
            {
                _http.PostAsync($"/session/{Uri.EscapeDataString(_sessionId)}/abort", content: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "opencode session abort failed on dispose.");
            }
        }
        _http.Dispose();
    }

    private async Task<string> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is not null) return _sessionId;
        using var response = await _http.PostAsync("/session", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"opencode session create failed with HTTP {(int)response.StatusCode}: {error[..Math.Min(error.Length, 200)]}");
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidOperationException("opencode session create returned no id.");
        return _sessionId = id.GetString()!;
    }

    private static string ToText(ChatMessage message)
    {
        var parts = message.Contents.OfType<TextContent>().Select(c => c.Text).Where(s => s.Length > 0);
        return string.Join("\n", parts);
    }

    private static string ErrorText(JsonElement data)
    {
        if (data.TryGetProperty("error", out var error)) return error.ValueKind == JsonValueKind.String ? error.GetString()! : error.ToString();
        return data.ToString();
    }
}