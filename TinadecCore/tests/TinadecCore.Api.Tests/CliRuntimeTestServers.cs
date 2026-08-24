using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Api.Tests;

/// <summary>JSON-RPC 2.0 over SSE fake ACP server: POST / handles rpc calls, GET / streams buffered events.</summary>
internal sealed class FakeAcpServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WebApplication _app;
    private readonly Channel<string> _sse = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _serverCts = new();
    private int _sessionCounter;
    private int _messageCounter;
    public int InitializeCalls;
    public int MessageCreates;
    public readonly List<string> Sessions = [];
    public string Url { get; private set; } = "";
    public bool SendPermissionRequest;
    public string? FailMessage;

    private FakeAcpServer(WebApplication app) => _app = app;

    public static async Task<FakeAcpServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeAcpServer(app);
        app.MapPost("/", server.HandleRpcAsync);
        app.MapGet("/", server.HandleSseAsync);
        await app.StartAsync();
        server.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(a => a.StartsWith("http://127.0.0.1:"));
        return server;
    }

    private async Task HandleRpcAsync(HttpContext context)
    {
        using var document = JsonDocument.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync());
        var root = document.RootElement;
        var method = root.TryGetProperty("method", out var m) ? m.GetString() : "";
        var parameters = root.TryGetProperty("parameters", out var p) ? p : default;
        if (method == "initialize") InitializeCalls++;
        object? result = method switch
        {
            "initialize" => new { protocolVersion = "0.1" },
            "session/new" => NewSession(),
            "session/event" => new { },
            "message/create" => CreateMessage(parameters),
            "session/cancel" => new { },
            _ => throw new InvalidOperationException($"fake ACP: unexpected method {method}")
        };
        await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = root.TryGetProperty("id", out var id) ? id.GetString() : null, result });
    }

    private object NewSession()
    {
        var sessionId = $"s{++_sessionCounter}";
        Sessions.Add(sessionId);
        return new { sessionId };
    }

    private object CreateMessage(JsonElement parameters)
    {
        if (parameters.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind == JsonValueKind.String) Sessions.Add(sessionId.GetString()!);
        MessageCreates++;
        var messageId = $"m{_messageCounter++}";
        if (SendPermissionRequest)
        {
            _sse.Writer.TryWrite(JsonSerializer.Serialize(new { method = "permission.request", @params = new { messageId } }, Json));
        }
        else if (FailMessage is { } failure)
        {
            _sse.Writer.TryWrite(JsonSerializer.Serialize(new { method = "message.error", @params = new { messageId, error = failure } }, Json));
        }
        else
        {
            foreach (var piece in "Hello from fake ACP".Chunk(2).Select(c => new string(c)))
            {
                _sse.Writer.TryWrite(JsonSerializer.Serialize(new { method = "message.delta", @params = new { messageId, delta = new { type = "text", text = piece } } }, Json));
            }
            _sse.Writer.TryWrite(JsonSerializer.Serialize(new { method = "message.done", @params = new { messageId } }, Json));
        }
        return new { message = new { id = messageId } };
    }

    private async Task HandleSseAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        try
        {
await context.Response.WriteAsync(": connected\n\n");
        await context.Response.Body.FlushAsync(context.RequestAborted);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _serverCts.Token);
        await foreach (var data in _sse.Reader.ReadAllAsync(linked.Token))
        {
            await context.Response.WriteAsync($"event: message\ndata: {data}\n\n");
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sse.Writer.TryComplete();
        _serverCts.Cancel();
        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await _app.StopAsync(stopCts.Token);
        }
        await _app.DisposeAsync();
    }
}

/// <summary>opencode serve protocol fake: POST /session, POST /session/{id}/message (SSE), POST abort.</summary>
internal sealed class FakeOpenCodeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; private set; } = "";
    public int Sessions;
    public int Messages;

    private FakeOpenCodeServer(WebApplication app) => _app = app;

    public static async Task<FakeOpenCodeServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeOpenCodeServer(app);
        app.MapPost("/session", (HttpContext context) => server.CreateSessionAsync(context));
        app.MapPost("/session/{id}/message", (string id, HttpContext context) => server.SendMessageAsync(id, context));
        app.MapPost("/session/{id}/abort", () => Results.Ok());
        await app.StartAsync();
        server.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First(a => a.StartsWith("http://127.0.0.1:"));
        return server;
    }

    private async Task CreateSessionAsync(HttpContext context)
    {
        Sessions++;
        await context.Response.WriteAsJsonAsync(new { id = "os-1" });
    }

    private async Task SendMessageAsync(string id, HttpContext context)
    {
        Messages++;
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync("event: message.part.updated\ndata: {\"part\":{\"type\":\"text\",\"text\":\"Hello from fake opencode\"}}\n\n");
        await context.Response.WriteAsync("event: message.completed\ndata: {}\n\n");
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await _app.StopAsync(stopCts.Token);
        }
        await _app.DisposeAsync();
    }
}