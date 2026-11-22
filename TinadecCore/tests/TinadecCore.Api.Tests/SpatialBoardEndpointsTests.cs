using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Task #44 integration verification for the multiplayer Spatial Board REST + WebSocket surface:
/// property-level LWW merge, strict <c>parent_id</c> cycle rejection (<c>parent_cycle_detected</c>),
/// comment thread CRUD, and live WebSocket <c>snapshot</c> / <c>ops_ack</c> / <c>ops_broadcast</c> /
/// <c>presence_update</c> / <c>pong</c> frames.
/// </summary>
public sealed class SpatialBoardEndpointsTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("spatial-board-api");
    private BoardFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new BoardFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SpatialBoard_SupportsPropertyLww_RejectsParentCycles_HandlesCommentCrud_AndStreamsWebSocketFrames()
    {
        using var client = _factory!.CreateClient();

        // Unknown session returns 404
        var missingResp = await client.GetAsync($"/api/v1/sessions/{Guid.NewGuid()}/spatial-board");
        Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);

        // Create a real project & session
        var workspaceDir = Path.Combine(_root, "spatial-workspace");
        Directory.CreateDirectory(workspaceDir);
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "SpatialProj", path = workspaceDir });
        projResp.EnsureSuccessStatusCode();
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var sessResp = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Figma Spatial Session" });
        sessResp.EnsureSuccessStatusCode();
        var sessionId = (await sessResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // 1. Initial GET /spatial-board snapshot
        var initialSnap = await client.GetFromJsonAsync<SpatialBoardSnapshotDto>($"/api/v1/sessions/{sessionId}/spatial-board");
        Assert.NotNull(initialSnap);
        Assert.Equal(sessionId, initialSnap!.SessionId);
        Assert.Equal(0, initialSnap.Seq);
        Assert.Empty(initialSnap.Objects);

        // 2. POST /ops: create sec-1, sec-2 (parent sec-1), and note-1; test property-level LWW + parent_cycle_detected
        var seedOps = new SpatialBoardOpsRequestDto
        {
            ClientId = "writer-a",
            BaseSeq = 0,
            Ops =
            [
                new SpatialPropertyOpDto
                {
                    ClientOpId = "op-sec-1",
                    ObjectId = "sec-1",
                    Kind = "section",
                    X = 100,
                    Y = 200,
                    Width = 600,
                    Height = 400,
                    Props = new Dictionary<string, JsonElement> { ["title"] = JsonSerializer.SerializeToElement("Architecture") },
                    Timestamps = new Dictionary<string, SpatialLwwTimestampDto>
                    {
                        ["kind"] = new(10, "writer-a"),
                        ["x"] = new(10, "writer-a"),
                        ["y"] = new(10, "writer-a"),
                        ["width"] = new(10, "writer-a"),
                        ["height"] = new(10, "writer-a"),
                        ["props.title"] = new(10, "writer-a"),
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "op-sec-2",
                    ObjectId = "sec-2",
                    Kind = "section",
                    ParentId = "sec-1",
                    HasParentId = true,
                    X = 120,
                    Y = 240,
                    Width = 300,
                    Height = 200,
                    Timestamps = new Dictionary<string, SpatialLwwTimestampDto>
                    {
                        ["kind"] = new(10, "writer-a"),
                        ["parent_id"] = new(10, "writer-a"),
                        ["x"] = new(10, "writer-a"),
                        ["y"] = new(10, "writer-a"),
                    }
                }
            ]
        };

        var seedResp = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/spatial-board/ops", seedOps);
        Assert.Equal(HttpStatusCode.OK, seedResp.StatusCode);
        var seedBody = await seedResp.Content.ReadFromJsonAsync<SpatialBoardOpsResponseDto>();
        Assert.NotNull(seedBody);
        Assert.Equal(2, seedBody!.AcceptedOpIds.Count);
        Assert.Empty(seedBody.RejectedOps);
        Assert.Equal("sec-1", seedBody.Objects["sec-2"].ParentId);

        // Concurrent LWW merge (stale x @ lamport 5 loses to 10; newer y @ lamport 15 wins over 10) + cycle rejection (sec-1 -> sec-2)
        var lwwAndCycleReq = new SpatialBoardOpsRequestDto
        {
            ClientId = "writer-b",
            BaseSeq = seedBody.Seq,
            Ops =
            [
                new SpatialPropertyOpDto
                {
                    ClientOpId = "op-lww",
                    ObjectId = "sec-1",
                    X = 999,
                    Y = 555,
                    Timestamps = new Dictionary<string, SpatialLwwTimestampDto>
                    {
                        ["x"] = new(5, "writer-b"),
                        ["y"] = new(15, "writer-b"),
                    }
                },
                new SpatialPropertyOpDto
                {
                    ClientOpId = "op-cycle",
                    ObjectId = "sec-1",
                    ParentId = "sec-2",
                    HasParentId = true,
                    Timestamps = new Dictionary<string, SpatialLwwTimestampDto>
                    {
                        ["parent_id"] = new(20, "writer-b"),
                    }
                }
            ]
        };

        var lwwResp = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/spatial-board/ops", lwwAndCycleReq);
        Assert.Equal(HttpStatusCode.OK, lwwResp.StatusCode);
        var lwwBody = await lwwResp.Content.ReadFromJsonAsync<SpatialBoardOpsResponseDto>();
        Assert.NotNull(lwwBody);
        Assert.Contains("op-lww", lwwBody!.AcceptedOpIds);
        var rejected = Assert.Single(lwwBody.RejectedOps);
        Assert.Equal("op-cycle", rejected.ClientOpId);
        Assert.Equal("parent_cycle_detected", rejected.Code);

        var sec1After = lwwBody.Objects["sec-1"];
        Assert.Equal(100, sec1After.X); // stale lamport 5 ignored
        Assert.Equal(555, sec1After.Y); // newer lamport 15 applied
        Assert.Null(sec1After.ParentId); // cycle rejected without mutating state

        // 3. Comment Pin CRUD
        var createPinResp = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/spatial-board/comments", new
        {
            object_id = "sec-1",
            x = 140.0,
            y = 220.0,
            author_id = "u-1",
            author_name = "Alice",
            body = "@team review this section layout",
            mentions = new[] { "team" }
        });
        Assert.Equal(HttpStatusCode.Created, createPinResp.StatusCode);
        var createdPin = await createPinResp.Content.ReadFromJsonAsync<SpatialBoardCommentPinDto>();
        Assert.NotNull(createdPin);
        var pinId = createdPin!.PinId;

        var replyResp = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/spatial-board/comments/{pinId}/replies", new
        {
            author_id = "u-2",
            author_name = "Bob",
            body = "Looks crisp!"
        });
        Assert.Equal(HttpStatusCode.OK, replyResp.StatusCode);
        var repliedPin = await replyResp.Content.ReadFromJsonAsync<SpatialBoardCommentPinDto>();
        Assert.Single(repliedPin!.Replies);

        var patchPinResp = await client.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}/spatial-board/comments/{pinId}", new { resolved = true });
        Assert.Equal(HttpStatusCode.OK, patchPinResp.StatusCode);
        Assert.True((await patchPinResp.Content.ReadFromJsonAsync<SpatialBoardCommentPinDto>())!.Resolved);

        var delPinResp = await client.DeleteAsync($"/api/v1/sessions/{sessionId}/spatial-board/comments/{pinId}");
        Assert.Equal(HttpStatusCode.OK, delPinResp.StatusCode);

        // 4. Live WebSocket two-peer sync (snapshot -> hello/presence -> ops_ack + ops_broadcast -> ping/pong)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wsClientA = _factory.Server.CreateWebSocketClient();
        var wsClientB = _factory.Server.CreateWebSocketClient();
        var wsUriA = new Uri(_factory.Server.BaseAddress, $"/api/v1/sessions/{sessionId}/spatial-board/ws?client_id=ws-a&display_name=Alice");
        var wsUriB = new Uri(_factory.Server.BaseAddress, $"/api/v1/sessions/{sessionId}/spatial-board/ws?client_id=ws-b&display_name=Bob");

        using var sockA = await wsClientA.ConnectAsync(wsUriA, cts.Token);
        using var sockB = await wsClientB.ConnectAsync(wsUriB, cts.Token);

        var snapFrameA = await ReadUntilTypeAsync(sockA, "snapshot", cts.Token);
        Assert.Equal("snapshot", snapFrameA.GetProperty("type").GetString());
        Assert.True(snapFrameA.GetProperty("objects").TryGetProperty("sec-1", out _));

        _ = await ReadUntilTypeAsync(sockB, "snapshot", cts.Token);

        await SendJsonAsync(sockA, new
        {
            type = "presence",
            cursor = new { x = 320.5, y = 410.0 },
            selection = new[] { "sec-1" },
            active_tool = "sticky"
        }, cts.Token);
        var presenceOnB = await ReadUntilTypeAsync(sockB, "presence_update", cts.Token);
        Assert.Contains(presenceOnB.GetProperty("collaborators").EnumerateArray(),
            c => c.GetProperty("client_id").GetString() == "ws-a");

        await SendJsonAsync(sockA, new
        {
            type = "ops",
            request_id = "req-sticky-1",
            client_id = "ws-a",
            base_seq = lwwBody.Seq,
            ops = new[]
            {
                new
                {
                    client_op_id = "ws-op-1",
                    object_id = "sticky-live-1",
                    kind = "sticky",
                    x = 400.0,
                    y = 300.0,
                    props = new { text = "Live multiplayer sticky" },
                    timestamps = new
                    {
                        kind = new { lamport = 30, writer_id = "ws-a" },
                        x = new { lamport = 30, writer_id = "ws-a" },
                        y = new { lamport = 30, writer_id = "ws-a" }
                    }
                }
            }
        }, cts.Token);

        var ackOnA = await ReadUntilTypeAsync(sockA, "ops_ack", cts.Token);
        Assert.Equal("req-sticky-1", ackOnA.GetProperty("request_id").GetString());
        Assert.Contains("ws-op-1", ackOnA.GetProperty("accepted_op_ids").EnumerateArray().Select(x => x.GetString()));

        var broadcastOnB = await ReadUntilTypeAsync(sockB, "ops_broadcast", cts.Token);
        Assert.True(broadcastOnB.GetProperty("objects").TryGetProperty("sticky-live-1", out _));

        await SendJsonAsync(sockA, new { type = "ping", ts = 123456L }, cts.Token);
        var pongOnA = await ReadUntilTypeAsync(sockA, "pong", cts.Token);
        Assert.Equal(123456L, pongOnA.GetProperty("ts").GetInt64());

        await sockA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        await sockB.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    private static async Task SendJsonAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<JsonElement> ReadUntilTypeAsync(WebSocket socket, string expectedType, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        for (var i = 0; i < 16; i++)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"WebSocket closed before receiving '{expectedType}'.");
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var doc = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
            if (doc.TryGetProperty("type", out var typeProp) && typeProp.GetString() == expectedType)
                return doc;
        }
        throw new TimeoutException($"Did not observe WebSocket frame '{expectedType}' within 16 frames.");
    }

    private sealed class BoardFactory(string root) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(root);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
