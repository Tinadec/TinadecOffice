using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NLog;

namespace TinadecTools.Tools.Browser;

internal interface IBrowserBridge
{
    ValueTask<BrowserBridgeResponse> SendAsync(BrowserBridgeCall call, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP client for the Electron browser bridge. The endpoint and bearer token are read from
/// `TINADEC_HOME/state/agent-browser/bridge.json`; they are never placed in the process
/// environment because tool children (git, shell) inherit it. The file is re-read after a 401 or
/// a connection failure so an app restart does not strand the run.
/// </summary>
internal sealed class BrowserBridgeClient : IBrowserBridge
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan EndpointTtl = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly Func<string?> _homeProvider;
    private readonly string? _bridgeFileOverride;
    private Endpoint? _endpoint;
    private DateTimeOffset _endpointReadAt;

    public BrowserBridgeClient()
        : this(
            new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                AllowAutoRedirect = false,
            },
            () => Environment.GetEnvironmentVariable("TINADEC_HOME"),
            bridgeFile: null)
    {
    }

    internal BrowserBridgeClient(HttpMessageHandler handler, Func<string?> homeProvider, string? bridgeFile)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _homeProvider = homeProvider;
        _bridgeFileOverride = bridgeFile;
    }

    public async ValueTask<BrowserBridgeResponse> SendAsync(
        BrowserBridgeCall call,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!TryGetEndpoint(out var endpoint))
        {
            return BrowserBridgeResponse.Failure(
                "browser_host_unavailable",
                "The desktop browser host is not running or has not written its bridge endpoint.");
        }

        var payload = BuildRequest(call);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
                {
                    Content = new ByteArrayContent(payload),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    timeoutCts.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateEndpoint();
                    if (attempt == 0 && TryGetEndpoint(out endpoint)) continue;
                    return BrowserBridgeResponse.Failure("browser_unauthorized", "The browser bridge rejected the token.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return BrowserBridgeResponse.Failure(
                        "browser_bridge_error",
                        $"The browser bridge answered HTTP {(int)response.StatusCode}.",
                        permanent: false);
                }

                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var parsed = ParseResponse(body);
                if (!parsed.Ok)
                {
                    Logger.Warn("browser bridge call {Op} {Method} failed: {Code}", call.Op, call.Method, parsed.Code);
                }
                return parsed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return BrowserBridgeResponse.Failure(
                    "browser_timeout",
                    $"The browser bridge did not answer within {timeout.TotalMilliseconds:0} ms.",
                    permanent: false);
            }
            catch (HttpRequestException)
            {
                InvalidateEndpoint();
                if (attempt == 0 && TryGetEndpoint(out endpoint)) continue;
                return BrowserBridgeResponse.Failure(
                    "browser_host_unavailable",
                    "The desktop browser host is not reachable.");
            }
            catch (IOException)
            {
                InvalidateEndpoint();
                if (attempt == 0 && TryGetEndpoint(out endpoint)) continue;
                return BrowserBridgeResponse.Failure(
                    "browser_host_unavailable",
                    "The browser bridge connection closed unexpectedly.");
            }
        }

        return BrowserBridgeResponse.Failure("browser_host_unavailable", "The browser bridge is not reachable.");
    }

    private static BrowserBridgeResponse ParseResponse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return BrowserBridgeResponse.Failure("browser_bridge_error", "The browser bridge returned invalid JSON.", permanent: false);
        }

        using (document)
        {
            var root = document.RootElement;
            JsonElement? data = null;
            if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind != JsonValueKind.Null)
            {
                data = dataElement.Clone();
            }

            BrowserDialogInfo? pendingDialog = null;
            if (root.TryGetProperty("pending_dialog", out var dialogElement) && dialogElement.ValueKind == JsonValueKind.Object)
            {
                pendingDialog = ParseDialog(dialogElement);
            }
            else if (data is { ValueKind: JsonValueKind.Object } d
                     && d.TryGetProperty("pending_dialog", out var nestedDialog)
                     && nestedDialog.ValueKind == JsonValueKind.Object)
            {
                pendingDialog = ParseDialog(nestedDialog);
            }

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString()
                    : null;
                var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
                var permanent = !root.TryGetProperty("permanent", out var permanentElement)
                    || permanentElement.ValueKind != JsonValueKind.False;
                return BrowserBridgeResponse.Failure(code ?? "browser_bridge_error", message, permanent, data, pendingDialog);
            }

            return new BrowserBridgeResponse
            {
                Ok = true,
                Data = data,
                PendingDialog = pendingDialog,
            };
        }
    }

    private static BrowserDialogInfo ParseDialog(JsonElement dialog) => new()
    {
        Type = dialog.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null,
        Message = dialog.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null,
        Url = dialog.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null,
        DefaultPrompt = dialog.TryGetProperty("defaultPrompt", out var def) && def.ValueKind == JsonValueKind.String
            ? def.GetString()
            : dialog.TryGetProperty("default_prompt", out var def2) && def2.ValueKind == JsonValueKind.String
                ? def2.GetString()
                : null,
        TabId = dialog.TryGetProperty("tabId", out var tabId) && tabId.ValueKind == JsonValueKind.String
            ? tabId.GetString()
            : dialog.TryGetProperty("tab_id", out var tabId2) && tabId2.ValueKind == JsonValueKind.String
                ? tabId2.GetString()
                : null,
    };

    private static byte[] BuildRequest(BrowserBridgeCall call)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("op", call.Op);
            writer.WriteString("session_id", call.SessionId);
            if (!string.IsNullOrEmpty(call.TaskSpace)) writer.WriteString("task_space", call.TaskSpace);
            if (!string.IsNullOrEmpty(call.TabId)) writer.WriteString("tab_id", call.TabId);
            if (!string.IsNullOrEmpty(call.CdpSessionId)) writer.WriteString("cdp_session_id", call.CdpSessionId);
            if (!string.IsNullOrEmpty(call.Method)) writer.WriteString("method", call.Method);
            if (call.Parameters is { } parameters
                && parameters.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                writer.WritePropertyName("params");
                parameters.WriteTo(writer);
            }
            if (call.Url is not null) writer.WriteString("url", call.Url);
            if (call.Accept is { } accept) writer.WriteBoolean("accept", accept);
            if (call.PromptText is not null) writer.WriteString("prompt_text", call.PromptText);
            if (call.FullPage is { } fullPage) writer.WriteBoolean("full_page", fullPage);
            if (!string.IsNullOrEmpty(call.Reason)) writer.WriteString("reason", call.Reason);
            if (call.Keep is not null)
            {
                writer.WritePropertyName("keep");
                writer.WriteStartArray();
                foreach (var tab in call.Keep)
                {
                    if (!string.IsNullOrWhiteSpace(tab)) writer.WriteStringValue(tab.Trim());
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private bool TryGetEndpoint(out Endpoint endpoint)
    {
        lock (this)
        {
            var now = DateTimeOffset.UtcNow;
            if (_endpoint is { } cached && now - _endpointReadAt < EndpointTtl)
            {
                endpoint = cached;
                return true;
            }

            var file = _bridgeFileOverride ?? BridgeFile();
            if (file is null || !File.Exists(file))
            {
                _endpoint = null;
                endpoint = default;
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                var token = root.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)
                    || string.IsNullOrWhiteSpace(token)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttp
                    || !uri.IsLoopback)
                {
                    _endpoint = null;
                    endpoint = default;
                    return false;
                }

                endpoint = new Endpoint(uri, token);
                _endpoint = endpoint;
                _endpointReadAt = now;
                return true;
            }
            catch (IOException)
            {
                _endpoint = null;
                endpoint = default;
                return false;
            }
            catch (JsonException)
            {
                _endpoint = null;
                endpoint = default;
                return false;
            }
        }
    }

    private string? BridgeFile()
    {
        var home = _homeProvider();
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathRooted(home)) return null;
        return Path.Combine(home, "state", "agent-browser", "bridge.json");
    }

    private void InvalidateEndpoint()
    {
        lock (this)
        {
            _endpoint = null;
            _endpointReadAt = default;
        }
    }

    private readonly record struct Endpoint(Uri Url, string Token);
}
