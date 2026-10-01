using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The project commander's desk (todo E4): one project rolls up its sessions, unfinished runs,
/// open reports and held resources. Foreign sessions never leak in; resources pinned to other
/// sessions stay out.
/// </summary>
public sealed class ProjectOverviewTests : IAsyncLifetime
{
    private readonly ApiEndpointFactory _factory = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => _factory.Dispose();

    [Fact]
    public async Task ProjectOverview_RollsUpRunsAndLeases_AndKeepsOtherSessionsOut()
    {
        var client = _factory.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "desk project", path = Path.Combine(Path.GetTempPath(), "tinadec-desk", Guid.NewGuid().ToString("N")) }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "desk session" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var ledger = _factory.Services.GetRequiredService<IResourceLeaseService>();
        Assert.True((await ledger.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Path, "/work/src", Exclusive: true),
            SessionId: sessionId, RunId: null, Reason: "held by this project's session", Purpose: ResourceLeasePurposes.Assignment))).Granted);
        Assert.True((await ledger.AcquireAsync(new ResourceAcquireRequest(
            new ResourceClaim(ResourceLeaseKinds.Path, "/elsewhere/else", Exclusive: true),
            SessionId: Guid.NewGuid(), RunId: null, Reason: "held by a foreign session", Purpose: ResourceLeasePurposes.Assignment))).Granted);

        var overview = await client.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}/overview");
        Assert.Equal(projectId, overview.GetProperty("project_id").GetGuid());
        Assert.Equal("desk project", overview.GetProperty("name").GetString());
        Assert.Equal(1, overview.GetProperty("sessions").GetInt32());
        Assert.Empty(overview.GetProperty("active_runs").EnumerateArray());
        Assert.Empty(overview.GetProperty("open_reports").EnumerateArray());
        var held = Assert.Single(overview.GetProperty("held_resources").EnumerateArray());
        Assert.Equal("/work/src", held.GetProperty("resource_key").GetString());
        Assert.Equal("held by this project's session", held.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ProjectOverview_UnknownProject_Is404()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{Guid.NewGuid()}/overview")).StatusCode);
    }
}
