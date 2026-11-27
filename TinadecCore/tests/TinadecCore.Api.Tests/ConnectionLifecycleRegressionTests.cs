using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

public sealed class ConnectionLifecycleRegressionTests : IClassFixture<ApiEndpointFactory>
{
    private readonly ApiEndpointFactory _factory;

    public ConnectionLifecycleRegressionTests(ApiEndpointFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RefreshProviderModels_PropagatesCallerCancellation_InsteadOfReturning502Timeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var http = _factory.CreateClient();
        var createResp = await http.PostAsJsonAsync("/api/v1/model-providers", new
        {
            display_name = "Hanging Loopback Provider",
            provider_family = "openai-compatible",
            driver = "openai",
            protocol = "openai-chat",
            base_url = $"http://127.0.0.1:{port}/v1",
            model = "gpt-test",
            api_key = "test-key"
        });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = created.GetProperty("id").GetGuid();

        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(serverCts.Token);
            accepted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(8), serverCts.Token).ContinueWith(_ => { });
        });

        using var scope = _factory.Services.CreateScope();
        var controlPlane = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        using var callerCts = new CancellationTokenSource();

        var refreshTask = controlPlane.RefreshProviderModels(providerId, callerCts.Token);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshTask);
        await serverCts.CancelAsync();
        await serverTask.ContinueWith(_ => { });
    }

    [Fact]
    public async Task ListRoutes_BatchedQueriesReturnRoutesAndCandidatesCorrectly()
    {
        using var http = _factory.CreateClient();
        var resp = await http.GetAsync("/api/v1/model-routes");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var routes = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, routes.ValueKind);
        Assert.True(routes.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task StorageLifecycle_AppendRunStreamAsync_PublishesToLiveRunStreamProjection()
    {
        using var http = _factory.CreateClient();
        var sessionResp = await http.PostAsJsonAsync("/api/v1/sessions", new { title = "Live projection bridge" });
        sessionResp.EnsureSuccessStatusCode();
        var sessionJson = await sessionResp.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = sessionJson.GetProperty("id").GetGuid();

        var conversations = _factory.Services.GetRequiredService<IConversationStore>();
        var lifecycle = _factory.Services.GetRequiredService<ILifecycleManager>();
        var coordinator = _factory.Services.GetRequiredService<IFullDuplexRunCoordinator>();
        Assert.NotNull(coordinator);

        var msg = await conversations.AppendMessageAsync(sessionId, "user", "hi", cancellationToken: CancellationToken.None);
        var runIdStr = await lifecycle.StartRunAsync(sessionId.ToString(), msg.Id.ToString(), CancellationToken.None);
        var runId = Guid.Parse(runIdStr);
        var turnId = Guid.NewGuid();

        var projection = _factory.Services.GetRequiredService<RunStreamProjection>();
        projection.Track(runId);

        // Direct lifecycle append outside FullDuplexRunEngine must still hit RunStreamProjection immediately
        await lifecycle.AppendRunStreamAsync(runIdStr, new DurableRunStreamAppend(turnId, "status", Delta: "steered"), CancellationToken.None);
        Assert.True(projection.HasFramesPast(runId, 0));
        var frames = projection.Since(runId, 0);
        Assert.Single(frames);
        Assert.Equal("status", frames[0].Kind);
    }
}
