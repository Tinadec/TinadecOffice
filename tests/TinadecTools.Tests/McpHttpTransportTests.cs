using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TinadecTools.Tools.Mcp;

namespace TinadecTools.Tests;

/// <summary>
/// A real Streamable HTTP MCP handshake, not a stub of our own client: the SDK's
/// <c>HttpClientTransport</c> speaks to a tiny JSON-RPC server over TCP. The stdio fixture cannot
/// cover this path, and a mocked transport would only prove the mock.
/// </summary>
[Collection("McpRuntime")]
public sealed class McpHttpTransportTests : IAsyncLifetime
{
    private HttpMcpServer? _server;
    private string? _configPath;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await McpRuntime.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        if (_configPath is not null && File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Fact]
    public async Task McpList_ConnectsOverStreamableHttp()
    {
        await ConfigureAsync();

        var response = await McpListTool.HandleAsync(new McpListParams(), CancellationToken.None);

        var server = Assert.Single(response.Servers);
        Assert.Equal("remote", server.Id);
        Assert.True(server.Status == "connected", server.Error);
        Assert.Contains(server.Tools, tool => tool.Name == "echo" && tool.Description?.Contains("Echoes") == true);
    }

    [Fact]
    public async Task McpInvoke_CallsAToolOverStreamableHttp()
    {
        await ConfigureAsync();
        using var arguments = JsonDocument.Parse("{\"message\":\"hello\"}");

        var response = await McpInvokeTool.HandleAsync(new McpInvokeParams
        {
            ServerId = "remote",
            ToolName = "echo",
            Arguments = arguments.RootElement.Clone(),
        }, CancellationToken.None);

        Assert.True(response.Success, response.Error);
        Assert.Contains("echo:hello", response.Result.GetRawText());
        Assert.Equal(1, _server!.ToolCalls);
    }

    private async Task ConfigureAsync()
    {
        _server = await HttpMcpServer.StartAsync();
        _configPath = Path.Combine(Path.GetTempPath(), $"tinadec-tools-mcp-http-{Guid.NewGuid():N}.json");
        var config = new McpServersFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "remote",
                    Name = "Remote MCP",
                    Transport = "http",
                    Url = _server.Url,
                },
            ],
        };
        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(config, McpJsonContext.Default.McpServersFile));
        McpRuntime.ConfigureForTests(_configPath);
    }
}

/// <summary>One request per connection; enough for the Streamable HTTP client's POSTs.</summary>
internal sealed class HttpMcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private int _toolCalls;

    public string Url { get; }
    public int ToolCalls => Volatile.Read(ref _toolCalls);

    private HttpMcpServer(TcpListener listener)
    {
        _listener = listener;
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Url = $"http://127.0.0.1:{endpoint.Port}/mcp/";
        _loop = AcceptAsync();
    }

    public static Task<HttpMcpServer> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new HttpMcpServer(listener));
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is null) return;
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, 405, "Method Not Allowed", null);
                    return;
                }
                var body = Respond(request.Body);
                await WriteAsync(stream, 200, "OK", body);
            }
            catch { /* A failed test connection is reported by the assertion, not by the server. */ }
        }
    }

    private string? Respond(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : "";
        object? result = method switch
        {
            "initialize" => new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "tinadec-http-fixture", version = "1.0.0" },
            },
            "ping" => new { },
            "tools/list" => new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "echo",
                        description = "Echoes a message back.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new { message = new { type = "string" } },
                            required = new[] { "message" },
                        },
                    },
                },
            },
            "tools/call" => CallTool(root),
            _ => null,
        };
        if (result is null) return Error(id, -32601, "Method not found");
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
    }

    private object CallTool(JsonElement root)
    {
        Interlocked.Increment(ref _toolCalls);
        var message = "";
        if (root.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("arguments", out var arguments)
            && arguments.TryGetProperty("message", out var value))
            message = value.GetString() ?? "";
        return new
        {
            content = new object[] { new { type = "text", text = "echo:" + message } },
            isError = false,
        };
    }

    private static string Error(JsonElement id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single);
            if (read == 0) return null;
            buffer.Add(single[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n') break;
            if (buffer.Count > 64 * 1024) return null;
        }
        var headerText = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3) return null;
        var length = 0;
        var chunked = false;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out length);
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                chunked = value.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        }
        if (chunked)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(stream);
                if (sizeLine is null) break;
                var size = int.Parse(sizeLine.Split(';')[0], System.Globalization.NumberStyles.HexNumber);
                if (size == 0)
                {
                    await ReadLineAsync(stream);
                    break;
                }
                var chunk = new byte[size];
                var offset = 0;
                while (offset < size)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(offset, size - offset));
                    if (read == 0) break;
                    offset += read;
                }
                await body.WriteAsync(chunk.AsMemory(0, offset));
                await ReadLineAsync(stream);
            }
            return new(requestLine[0], Encoding.UTF8.GetString(body.ToArray()));
        }
        if (length <= 0) return new(requestLine[0], null);
        var payload = new byte[length];
        var consumed = 0;
        while (consumed < length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(consumed, length - consumed));
            if (read == 0) break;
            consumed += read;
        }
        return new(requestLine[0], Encoding.UTF8.GetString(payload, 0, consumed));
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var line = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single);
            if (read == 0) return line.Count == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
            if (single[0] == (byte)'\n') break;
            if (single[0] != (byte)'\r') line.Add(single[0]);
        }
        return Encoding.ASCII.GetString(line.ToArray());
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string reason, string? body)
    {
        var bytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (bytes.Length > 0) await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _shutdown.Dispose();
    }

    private sealed record HttpRequest(string Method, string? Body);
}

[CollectionDefinition("McpRuntime", DisableParallelization = true)]
public sealed class McpRuntimeCollection;
