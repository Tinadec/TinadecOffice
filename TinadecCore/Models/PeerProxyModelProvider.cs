using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness;

namespace TinadecCore.Models;

/// <summary>
/// Redacts upstream model provider secrets (Authorization Bearer tokens, <c>sk-...</c> API keys,
/// <c>x-api-key</c> values, and any caller-supplied secret literal) before any error message,
/// stream payload, or diagnostic crosses process or device boundaries.
/// </summary>
public static partial class PeerProviderProxySecretRedactor
{
    private const string RedactedPlaceholder = "[REDACTED]";

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9._~+/=-]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\b(?:sk|rk|pk|ak|ant|gsk|xai)-[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex VendorApiKeyRegex();

    [GeneratedRegex(@"(?i)([""']?(?:api[_-]?key|x-api-key|authorization|secret)[""']?\s*[:=]\s*[""']?)([^""'\s,}]{6,})", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    public static string Redact(string? input, params string?[] explicitSecrets)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var scrubbed = input;
        if (explicitSecrets is { Length: > 0 })
        {
            foreach (var secret in explicitSecrets)
            {
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    var trimmed = secret.Trim();
                    if (trimmed.Length >= 4)
                    {
                        scrubbed = scrubbed.Replace(trimmed, RedactedPlaceholder, StringComparison.Ordinal);
                    }
                }
            }
        }

        scrubbed = BearerTokenRegex().Replace(scrubbed, $"$1{RedactedPlaceholder}");
        scrubbed = VendorApiKeyRegex().Replace(scrubbed, RedactedPlaceholder);
        scrubbed = KeyValueSecretRegex().Replace(scrubbed, $"$1{RedactedPlaceholder}");
        return scrubbed;
    }
}

/// <summary>
/// Worker-side <see cref="IChatClient"/> and <see cref="IModelProvider"/> adapter for Wave 3
/// Peer Provider Proxy (<see cref="ChatProtocols.PeerProxy"/>).
/// <para>
/// Security invariants:
/// <list type="bullet">
///   <item>Holds ZERO upstream provider API keys — never reads <c>ISecretStore</c> or <c>ChatResolution.ApiKey</c>.</item>
///   <item>Connects strictly to the local Gateway loopback callback (<c>http://127.0.0.1:&lt;gwPort&gt;/api/v1/peers/internal/provider-proxy/completions</c>)
///         carrying only the short-lived <c>x-tinadec-peer-proxy-token</c> and target <c>controller_device_id</c>.</item>
///   <item>Supports full SSE delta streaming AND multi-turn <see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/> tool call loops.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PeerProxyModelProvider : IChatClient, IModelProvider, IDisposable
{
    public const string CompletionsPath = "/api/v1/peers/internal/provider-proxy/completions";
    public const string ProxyTokenHeader = "x-tinadec-peer-proxy-token";
    public const string TargetControllerHeader = "x-tinadec-peer-controller-target";
    public const string PrincipalHeader = "x-tinadec-principal";
    public const string HostControlHeader = "x-tinadec-host-control";
    public const string GatewayInternalPrincipal = "gateway-internal";
    public const string DefaultLoopbackGatewayUrl = "http://127.0.0.1:48730";

    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseCookies = false,
        AllowAutoRedirect = false
    };

    private static readonly HttpClient DefaultHttpClient = new(SharedHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly JsonWriterOptions WireWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _gatewayBaseUrl;
    private readonly string _controllerDeviceId;
    private readonly string _proxySessionToken;
    private readonly string _model;
    private readonly string? _hostControlToken;
    private int _disposed;

    public PeerProxyModelProvider(
        string gatewayBaseUrl,
        string controllerDeviceId,
        string proxySessionToken,
        string? model = null,
        HttpClient? httpClient = null,
        string? hostControlToken = null)
    {
        var resolvedBaseUrl = ResolveValidatedLoopbackUrl(gatewayBaseUrl);
        if (string.IsNullOrWhiteSpace(controllerDeviceId))
            throw new PeerException(403, PeerErrorCodes.PolicyDenied, "Peer provider proxy requires a target controller_device_id.");
        if (string.IsNullOrWhiteSpace(proxySessionToken))
            throw new PeerException(403, PeerErrorCodes.PolicyDenied, "Peer provider proxy requires a valid short-lived proxy_session_token.");

        _gatewayBaseUrl = resolvedBaseUrl;
        _controllerDeviceId = controllerDeviceId.Trim();
        _proxySessionToken = proxySessionToken.Trim();
        _model = string.IsNullOrWhiteSpace(model) ? ChatProtocols.PeerProxy : model.Trim();
        _http = httpClient ?? DefaultHttpClient;
        _ownsHttp = false;
        _hostControlToken = string.IsNullOrWhiteSpace(hostControlToken)
            ? Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN")?.Trim()
            : hostControlToken.Trim();
    }

    public static PeerProxyModelProvider FromResolution(ChatResolution resolution, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var baseUrl = !string.IsNullOrWhiteSpace(resolution.BaseUrl)
            ? resolution.BaseUrl!
            : ResolveEnvironmentGatewayUrl();
        var controllerId = resolution.ProxyControllerDeviceId ?? string.Empty;
        var proxyToken = resolution.ProxySessionToken ?? resolution.Token ?? string.Empty;
        return new PeerProxyModelProvider(
            baseUrl,
            controllerId,
            proxyToken,
            resolution.Model,
            httpClient);
    }

    public static string ResolveEnvironmentGatewayUrl(string? fallbackLoopbackUrl = null)
    {
        if (PeerProviderProxyHostRegistry.TryNormalizeLoopbackBaseUrl(fallbackLoopbackUrl, out var normalizedFallback))
            return normalizedFallback;

        var envUrl = Environment.GetEnvironmentVariable("TINADEC_GATEWAY_URL");
        if (PeerProviderProxyHostRegistry.TryNormalizeLoopbackBaseUrl(envUrl, out var normalizedEnv))
            return normalizedEnv;

        var envPort = Environment.GetEnvironmentVariable("TINADEC_GATEWAY_PORT")
            ?? Environment.GetEnvironmentVariable("PORT");
        if (int.TryParse(envPort?.Trim(), out var port) && port is > 0 and <= 65535)
            return $"http://127.0.0.1:{port}";

        return DefaultLoopbackGatewayUrl;
    }

    internal static string ResolveValidatedLoopbackUrl(string? candidateUrl)
    {
        var effective = string.IsNullOrWhiteSpace(candidateUrl)
            ? ResolveEnvironmentGatewayUrl()
            : candidateUrl.Trim();
        if (!PeerProviderProxyHostRegistry.TryNormalizeLoopbackBaseUrl(effective, out var normalized))
        {
            throw new InvalidOperationException(
                $"PeerProxyModelProvider only permits strict local loopback Gateway endpoints (127.0.0.1 / ::1 / localhost); rejected '{PeerProviderProxySecretRedactor.Redact(effective)}'.");
        }
        return normalized;
    }

    public string GatewayBaseUrl => _gatewayBaseUrl;
    public string ControllerDeviceId => _controllerDeviceId;
    public string Model => _model;

    public Task<IChatClient?> GetChatClientAsync(string? routeId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IChatClient?>(this);

    public Task<ModelReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModelReadiness
        {
            IsReady = !string.IsNullOrWhiteSpace(_controllerDeviceId) && !string.IsNullOrWhiteSpace(_proxySessionToken),
            StatusMessage = $"Peer provider proxy bound to controller '{_controllerDeviceId}'.",
            Warnings = []
        });

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        if (updates.Count == 0)
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(messages);

        var requestPayloadBytes = BuildOpenAiCompletionRequestBytes(messages, options, _model, stream: true);
        var endpoint = $"{_gatewayBaseUrl}{CompletionsPath}";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(requestPayloadBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        request.Headers.TryAddWithoutValidation(PrincipalHeader, GatewayInternalPrincipal);
        request.Headers.TryAddWithoutValidation(TargetControllerHeader, _controllerDeviceId);
        request.Headers.TryAddWithoutValidation(ProxyTokenHeader, _proxySessionToken);
        request.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(_hostControlToken))
        {
            request.Headers.TryAddWithoutValidation(HostControlHeader, _hostControlToken);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var rawError = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateSanitizedHttpException(response.StatusCode, rawError);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var jsonBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            foreach (var singleUpdate in ParseNonStreamingCompletionUpdates(jsonBody))
            {
                yield return singleUpdate;
            }
            yield break;
        }

        var responseId = "chatcmpl_" + Guid.NewGuid().ToString("N")[..16];
        var messageId = "msg_" + Guid.NewGuid().ToString("N")[..16];
        var toolAccumulators = new SortedDictionary<int, ToolCallAccumulator>();
        ChatFinishReason? finishReason = null;
        UsageDetails? finalUsage = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var sse in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(sse.Data))
                continue;

            var trimmedData = sse.Data.Trim();
            if (string.Equals(trimmedData, "[DONE]", StringComparison.Ordinal))
                break;

            using var doc = JsonDocument.Parse(trimmedData);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw CreateSanitizedHttpException(HttpStatusCode.BadGateway, errEl.GetRawText());
            }

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString()))
            {
                responseId = idEl.GetString()!;
            }

            string? updateModelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                ? modelEl.GetString()
                : _model;

            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                finalUsage = ParseUsageDetails(usageEl);
            }

            if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array || choicesEl.GetArrayLength() == 0)
                continue;

            var choice = choicesEl[0];
            if (choice.TryGetProperty("finish_reason", out var finishEl) && finishEl.ValueKind == JsonValueKind.String)
            {
                finishReason = MapFinishReason(finishEl.GetString());
            }

            if (!choice.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Object)
            {
                // Fallback if a server sent `message` instead of `delta` inside an SSE frame.
                if (choice.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
                {
                    deltaEl = msgEl;
                }
                else
                {
                    continue;
                }
            }

            foreach (var reasoningKey in (string[])["reasoning_content", "reasoning"])
            {
                if (deltaEl.TryGetProperty(reasoningKey, out var rEl) && rEl.ValueKind == JsonValueKind.String && rEl.GetString() is { Length: > 0 } reasoningText)
                {
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        ResponseId = responseId,
                        MessageId = messageId,
                        ModelId = updateModelId,
                        Contents = [new TextReasoningContent(PeerProviderProxySecretRedactor.Redact(reasoningText))]
                    };
                    break;
                }
            }

            if (deltaEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                var chunkText = contentEl.GetString();
                if (!string.IsNullOrEmpty(chunkText))
                {
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        ResponseId = responseId,
                        MessageId = messageId,
                        ModelId = updateModelId,
                        Contents = [new TextContent(PeerProviderProxySecretRedactor.Redact(chunkText))]
                    };
                }
            }

            if (deltaEl.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
            {
                var fallbackIdx = 0;
                foreach (var tc in toolCallsEl.EnumerateArray())
                {
                    if (tc.ValueKind != JsonValueKind.Object) continue;
                    var idx = tc.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var parsedIdx)
                        ? parsedIdx
                        : fallbackIdx++;
                    if (!toolAccumulators.TryGetValue(idx, out var acc))
                    {
                        acc = new ToolCallAccumulator();
                        toolAccumulators[idx] = acc;
                    }
                    if (tc.TryGetProperty("id", out var tcIdEl) && tcIdEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tcIdEl.GetString()))
                    {
                        acc.Id.Append(tcIdEl.GetString());
                    }
                    if (tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                    {
                        if (fnEl.TryGetProperty("name", out var fnNameEl) && fnNameEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fnNameEl.GetString()))
                        {
                            acc.Name.Append(fnNameEl.GetString());
                        }
                        if (fnEl.TryGetProperty("arguments", out var fnArgsEl) && fnArgsEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fnArgsEl.GetString()))
                        {
                            acc.Arguments.Append(fnArgsEl.GetString());
                        }
                    }
                }
            }
        }

        if (toolAccumulators.Count > 0)
        {
            var toolContents = new List<AIContent>(toolAccumulators.Count);
            foreach (var (_, acc) in toolAccumulators)
            {
                var callId = acc.Id.Length > 0 ? acc.Id.ToString().Trim() : $"call_{Guid.NewGuid():N}"[..17];
                var toolName = acc.Name.ToString().Trim();
                if (string.IsNullOrEmpty(toolName))
                    continue;
                var argsDict = ParseToolArguments(acc.Arguments.ToString());
                toolContents.Add(new FunctionCallContent(callId, toolName, argsDict));
            }
            if (toolContents.Count > 0)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    ResponseId = responseId,
                    MessageId = messageId,
                    ModelId = _model,
                    FinishReason = finishReason ?? ChatFinishReason.ToolCalls,
                    Contents = toolContents
                };
            }
        }

        if (finalUsage is not null || finishReason is not null)
        {
            var tailContents = new List<AIContent>();
            if (finalUsage is not null)
            {
                tailContents.Add(new UsageContent(finalUsage));
            }
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ResponseId = responseId,
                MessageId = messageId,
                ModelId = _model,
                FinishReason = finishReason ?? (toolAccumulators.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop),
                Contents = tailContents
            };
        }
    }

    public TService? GetService<TService>(object? key = null) where TService : class =>
        typeof(TService) == typeof(IChatClient) || typeof(TService) == typeof(PeerProxyModelProvider)
            ? (TService)(object)this
            : null;

    public object? GetService(Type serviceType, object? key = null) =>
        serviceType == typeof(IChatClient) || serviceType == typeof(PeerProxyModelProvider)
            ? this
            : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    internal static byte[] BuildOpenAiCompletionRequestBytes(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string model,
        bool stream)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
        using var writer = new Utf8JsonWriter(buffer, WireWriterOptions);

        writer.WriteStartObject();
        writer.WriteString("model", string.IsNullOrWhiteSpace(options?.ModelId) ? model : options!.ModelId!.Trim());
        writer.WriteBoolean("stream", stream);
        if (stream)
        {
            writer.WritePropertyName("stream_options");
            writer.WriteStartObject();
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
        }

        if (options?.Temperature is { } temp)
            writer.WriteNumber("temperature", temp);
        if (options?.TopP is { } topP)
            writer.WriteNumber("top_p", topP);
        if (options?.MaxOutputTokens is { } maxTokens && maxTokens > 0)
            writer.WriteNumber("max_tokens", maxTokens);
        if (options?.StopSequences is { Count: > 0 } stops)
        {
            writer.WritePropertyName("stop");
            writer.WriteStartArray();
            foreach (var stop in stops)
            {
                if (!string.IsNullOrEmpty(stop))
                    writer.WriteStringValue(stop);
            }
            writer.WriteEndArray();
        }

        writer.WritePropertyName("messages");
        writer.WriteStartArray();

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", options!.Instructions);
            writer.WriteEndObject();
        }

        foreach (var msg in messages)
        {
            WriteWireMessages(writer, msg);
        }

        writer.WriteEndArray();

        if (options?.Tools is { Count: > 0 } tools)
        {
            var declarations = tools.OfType<AIFunctionDeclaration>().ToArray();
            if (declarations.Length > 0)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var decl in declarations)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", decl.Name);
                    if (!string.IsNullOrWhiteSpace(decl.Description))
                        writer.WriteString("description", decl.Description);
                    writer.WritePropertyName("parameters");
                    if (decl.JsonSchema.ValueKind == JsonValueKind.Object)
                    {
                        decl.JsonSchema.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "object");
                        writer.WritePropertyName("properties");
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                if (options.ToolMode is RequiredChatToolMode requiredMode)
                {
                    if (!string.IsNullOrWhiteSpace(requiredMode.RequiredFunctionName))
                    {
                        writer.WritePropertyName("tool_choice");
                        writer.WriteStartObject();
                        writer.WriteString("type", "function");
                        writer.WritePropertyName("function");
                        writer.WriteStartObject();
                        writer.WriteString("name", requiredMode.RequiredFunctionName);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteString("tool_choice", "required");
                    }
                }
                else if (options.ToolMode is NoneChatToolMode)
                {
                    writer.WriteString("tool_choice", "none");
                }
                else
                {
                    writer.WriteString("tool_choice", "auto");
                }
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteWireMessages(Utf8JsonWriter writer, ChatMessage message)
    {
        if (message.Role == ChatRole.Tool)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (results.Length > 0)
            {
                foreach (var frc in results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", frc.CallId);
                    writer.WriteString("content", FormatFunctionResult(frc.Result));
                    writer.WriteEndObject();
                }
                return;
            }
        }

        if (message.Role == ChatRole.Assistant)
        {
            var calls = message.Contents.OfType<FunctionCallContent>().Where(c => !c.InformationalOnly).ToArray();
            var assistantText = CollectText(message);
            writer.WriteStartObject();
            writer.WriteString("role", "assistant");
            if (!string.IsNullOrEmpty(assistantText))
                writer.WriteString("content", assistantText);
            else if (calls.Length > 0)
                writer.WriteNull("content");
            else
                writer.WriteString("content", string.Empty);

            if (calls.Length > 0)
            {
                writer.WritePropertyName("tool_calls");
                writer.WriteStartArray();
                foreach (var call in calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", string.IsNullOrWhiteSpace(call.CallId) ? $"call_{Guid.NewGuid():N}"[..17] : call.CallId);
                    writer.WriteString("type", "function");
                    writer.WritePropertyName("function");
                    writer.WriteStartObject();
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", SerializeToolArguments(call.Arguments));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            return;
        }

        var roleName = message.Role == ChatRole.System
            ? "system"
            : message.Role == ChatRole.User
                ? "user"
                : message.Role.Value;

        var dataParts = message.Contents.OfType<DataContent>()
            .Where(d => d.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        if (dataParts.Length > 0 && roleName == "user")
        {
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var content in message.Contents)
            {
                if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", tc.Text);
                    writer.WriteEndObject();
                }
                else if (content is DataContent dc && dc.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var base64 = Convert.ToBase64String(dc.Data.Span);
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WritePropertyName("image_url");
                    writer.WriteStartObject();
                    writer.WriteString("url", $"data:{dc.MediaType};base64,{base64}");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", roleName);
        writer.WriteString("content", CollectText(message));
        writer.WriteEndObject();
    }

    private static IReadOnlyList<ChatResponseUpdate> ParseNonStreamingCompletionUpdates(string jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody))
            return [];

        using var doc = JsonDocument.Parse(jsonBody);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw CreateSanitizedHttpException(HttpStatusCode.BadGateway, errEl.GetRawText());
        }

        var responseId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString())
            ? idEl.GetString()!
            : "chatcmpl_" + Guid.NewGuid().ToString("N")[..16];
        var messageId = "msg_" + Guid.NewGuid().ToString("N")[..16];
        var modelId = root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
        var contents = new List<AIContent>();
        ChatFinishReason? finishReason = null;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finEl) && finEl.ValueKind == JsonValueKind.String)
                finishReason = MapFinishReason(finEl.GetString());

            if (choice.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                if (msg.TryGetProperty("reasoning_content", out var rcEl) && rcEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rcEl.GetString()))
                {
                    contents.Add(new TextReasoningContent(PeerProviderProxySecretRedactor.Redact(rcEl.GetString())));
                }
                if (msg.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cEl.GetString()))
                {
                    contents.Add(new TextContent(PeerProviderProxySecretRedactor.Redact(cEl.GetString())));
                }
                if (msg.TryGetProperty("tool_calls", out var tcsEl) && tcsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcsEl.EnumerateArray())
                    {
                        if (tc.ValueKind != JsonValueKind.Object) continue;
                        var callId = tc.TryGetProperty("id", out var tcId) && tcId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tcId.GetString())
                            ? tcId.GetString()!
                            : $"call_{Guid.NewGuid():N}"[..17];
                        if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
                        var fnName = fn.TryGetProperty("name", out var fnN) && fnN.ValueKind == JsonValueKind.String ? fnN.GetString() : null;
                        if (string.IsNullOrWhiteSpace(fnName)) continue;
                        var fnArgs = fn.TryGetProperty("arguments", out var fnA) && fnA.ValueKind == JsonValueKind.String ? fnA.GetString() : "{}";
                        contents.Add(new FunctionCallContent(callId, fnName!, ParseToolArguments(fnArgs)));
                    }
                }
            }
        }

        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var usage = ParseUsageDetails(usageEl);
            if (usage is not null) contents.Add(new UsageContent(usage));
        }

        return
        [
            new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ResponseId = responseId,
                MessageId = messageId,
                ModelId = modelId,
                FinishReason = finishReason ?? ChatFinishReason.Stop,
                Contents = contents
            }
        ];
    }

    private static HttpRequestException CreateSanitizedHttpException(HttpStatusCode status, string? rawBody)
    {
        string code = status == HttpStatusCode.Forbidden ? PeerErrorCodes.PolicyDenied : "provider_proxy_error";
        string detail = $"Peer provider proxy failed with HTTP {(int)status}.";

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            var scrubbedBody = PeerProviderProxySecretRedactor.Redact(rawBody);
            try
            {
                using var doc = JsonDocument.Parse(scrubbedBody);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("code", out var cEl) && cEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cEl.GetString()))
                        code = cEl.GetString()!;
                    else if (root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tEl.GetString()))
                        code = tEl.GetString()!;

                    if (root.TryGetProperty("detail", out var dEl) && dEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dEl.GetString()))
                        detail = dEl.GetString()!;
                    else if (root.TryGetProperty("message", out var mEl) && mEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mEl.GetString()))
                        detail = mEl.GetString()!;
                    else if (root.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object
                        && errObj.TryGetProperty("message", out var errMsg) && errMsg.ValueKind == JsonValueKind.String)
                        detail = errMsg.GetString()!;
                }
            }
            catch (JsonException)
            {
                detail = scrubbedBody.Length <= 300 ? scrubbedBody : scrubbedBody[..300];
            }
        }

        detail = PeerProviderProxySecretRedactor.Redact(detail);
        return new HttpRequestException($"{code}: {detail}", null, status);
    }

    private static string CollectText(ChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.Text))
            return message.Text;
        var parts = message.Contents.OfType<TextContent>().Select(c => c.Text).Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", parts);
    }

    private static string FormatFunctionResult(object? result)
    {
        if (result is null) return string.Empty;
        if (result is string s) return s;
        if (result is JsonElement el) return el.GetRawText();
        return JsonSerializer.Serialize(result);
    }

    private static string SerializeToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "{}";
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer, WireWriterOptions);
        writer.WriteStartObject();
        foreach (var (key, value) in arguments)
        {
            writer.WritePropertyName(key);
            if (value is null) writer.WriteNullValue();
            else if (value is JsonElement je) je.WriteTo(writer);
            else JsonSerializer.Serialize(writer, value);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static IDictionary<string, object?> ParseToolArguments(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }
            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static UsageDetails? ParseUsageDetails(JsonElement usageEl)
    {
        long? input = usageEl.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pv)
            ? pv
            : usageEl.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var itv)
                ? itv
                : null;
        long? output = usageEl.TryGetProperty("completion_tokens", out var c) && c.TryGetInt64(out var cv)
            ? cv
            : usageEl.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var otv)
                ? otv
                : null;
        long? total = usageEl.TryGetProperty("total_tokens", out var t) && t.TryGetInt64(out var tv)
            ? tv
            : (input is not null || output is not null ? (input ?? 0) + (output ?? 0) : null);

        if (input is null && output is null && total is null)
            return null;

        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = total
        };
    }

    private static ChatFinishReason? MapFinishReason(string? reason) => reason?.Trim().ToLowerInvariant() switch
    {
        "stop" or "end_turn" => ChatFinishReason.Stop,
        "tool_calls" or "function_call" or "tool_use" => ChatFinishReason.ToolCalls,
        "length" or "max_tokens" => ChatFinishReason.Length,
        "content_filter" => ChatFinishReason.ContentFilter,
        null or "" => null,
        _ => new ChatFinishReason(reason)
    };

    private sealed class ToolCallAccumulator
    {
        public StringBuilder Id { get; } = new();
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}

/// <summary>
/// Issues short-lived single-session <c>proxy_session_token</c> grants in the host-partitioned
/// in-memory registry so <c>PeerDelegationService</c> and <c>PeerProviderProxyEndpoints</c>
/// share tokens across <c>user</c> and <c>project</c> storage scopes without persisting secrets.
/// </summary>
internal sealed class PeerProviderProxyTokenIssuerService : IPeerProviderProxyTokenIssuer
{
    private readonly TinadecCore.Persistence.ISecretStore _secrets;
    private readonly IPeerIdentityService? _peerIdentity;

    public PeerProviderProxyTokenIssuerService(
        TinadecCore.Persistence.ISecretStore secrets,
        IPeerIdentityService? peerIdentity = null)
    {
        _secrets = secrets;
        _peerIdentity = peerIdentity;
    }

    public async Task<PeerProviderProxyTokenGrantDto> IssueTokenAsync(
        PeerProviderProxyTokenIssueRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hostKey = PeerProviderProxyHostRegistry.ResolveHostPartitionKey(_secrets);
        if (!PeerProviderProxyHostRegistry.IsControllerProxyAllowed(hostKey))
        {
            throw new PeerException(403, PeerErrorCodes.PolicyDenied, "Controller peer provider proxy is disabled.");
        }

        var controllerId = request.ControllerDeviceId?.Trim();
        if (string.IsNullOrWhiteSpace(controllerId) && _peerIdentity is not null)
        {
            try
            {
                var local = await _peerIdentity.EnsureLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
                controllerId = local.LocalDeviceId;
            }
            catch
            {
                // Fallback when local peer identity is not initialized yet.
            }
        }

        var issued = PeerProviderProxyHostRegistry.IssueToken(
            hostKey,
            controllerId ?? string.Empty,
            request.WorkerDeviceId,
            request.DelegationId,
            request.RemoteSessionId,
            request.ExpiresAtMs > 0 ? request.ExpiresAtMs : null,
            request.MaxRequests > 0 ? request.MaxRequests : null);

        return new PeerProviderProxyTokenGrantDto
        {
            ProxySessionToken = issued.Token,
            ExpiresAtMs = issued.ExpiresAtMs,
            MaxRequests = issued.MaxRequests
        };
    }
}

