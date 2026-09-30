using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The environment steward's registry (todo E1): registrations are the user's, occupancy is the
/// resource ledger's — one run per slot, a refusal names the holder, and giving back frees the slot.
/// </summary>
public sealed class EnvironmentRegistryTests : IDisposable
{
    private readonly ApiEndpointFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private IEnvironmentRegistry Registry => _factory.Services.GetRequiredService<IEnvironmentRegistry>();

    private static EnvironmentAcquireRequest Ask(Guid runId, string? key = null, string? kind = null) =>
        new(Guid.NewGuid(), runId, null, null, key, kind, "integration tests");

    [Fact]
    public async Task EachSlotHasOneHolder_ARefusalNamesIt_AndGivingBackFreesIt()
    {
        await Registry.RegisterAsync(new EnvironmentRegistration("staging", EnvironmentKinds.Test, "Staging", null, "{\"url\":\"https://staging.example\"}", Capacity: 2));
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var first = await Registry.AcquireAsync(Ask(a, key: "staging"));
        var second = await Registry.AcquireAsync(Ask(b, kind: EnvironmentKinds.Test));
        Assert.True(first.Granted);
        Assert.True(second.Granted);
        Assert.NotEqual(first.Slot, second.Slot);
        // Asking again for what a run already holds is not a second slot.
        Assert.Equal(first.LeaseId, (await Registry.AcquireAsync(Ask(a, key: "staging"))).LeaseId);

        var refused = await Registry.AcquireAsync(Ask(c, key: "staging"));
        Assert.False(refused.Granted);
        Assert.Contains("fully in use", refused.Error);
        Assert.Equal(new[] { a, b }.Order(), refused.BusyHolders.Select(holder => holder.RunId!.Value).Order());

        Assert.Equal(1, await Registry.ReleaseAsync(a, "staging"));
        var afterRelease = await Registry.AcquireAsync(Ask(c, key: "staging"));
        Assert.True(afterRelease.Granted);
        Assert.Equal(first.Slot, afterRelease.Slot);
        var listed = Assert.Single(await Registry.ListAsync());
        Assert.Equal(0, listed.FreeSlots);
        Assert.Equal(new[] { b, c }.Order(), listed.Holders.Select(holder => holder.RunId!.Value).Order());
    }

    [Fact]
    public async Task ADisabledEnvironmentIsNotHandedOut_AndUnknownNamesListTheChoices()
    {
        var created = await Registry.RegisterAsync(new EnvironmentRegistration("gpu-box", EnvironmentKinds.Remote, "GPU box", null, null));
        await Registry.UpdateAsync(created.Id, new EnvironmentUpdate(Status: EnvironmentStatuses.Disabled));

        Assert.Contains("disabled", (await Registry.AcquireAsync(Ask(Guid.NewGuid(), key: "gpu-box"))).Error);
        Assert.False((await Registry.AcquireAsync(Ask(Guid.NewGuid(), kind: EnvironmentKinds.Remote))).Granted);
        var unknown = await Registry.AcquireAsync(Ask(Guid.NewGuid(), key: "nope"));
        Assert.Contains("gpu-box (remote", unknown.Error);
    }

    [Fact]
    public async Task TheApiRegistersListsAndUpdates_AndRefusesCredentialsAndDuplicates()
    {
        var client = _factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/environments", new
        {
            key = "cloud-dev", kind = "cloud", display_name = "Cloud dev", connection = new { region = "eu-1", secret_ref = "cloud-dev-key" }, capacity = 3
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cloud-dev", body.GetProperty("key").GetString());
        Assert.Equal(3, body.GetProperty("free_slots").GetInt32());
        Assert.Equal("cloud-dev-key", body.GetProperty("connection").GetProperty("secret_ref").GetString());

        var duplicate = await client.PostAsJsonAsync("/api/v1/environments", new { key = "cloud-dev", kind = "cloud", display_name = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        // A credential in the connection is refused, however deep it sits.
        var secret = await client.PostAsJsonAsync("/api/v1/environments", new { key = "leaky", kind = "remote", display_name = "Leaky", connection = new { ssh = new { host = "h", password = "hunter2" } } });
        Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
        Assert.Contains("password", await secret.Content.ReadAsStringAsync());
        var badKind = await client.PostAsJsonAsync("/api/v1/environments", new { key = "x", kind = "mainframe", display_name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);

        var id = body.GetProperty("id").GetGuid();
        var patched = await client.PatchAsJsonAsync($"/api/v1/environments/{id}", new { status = "disabled", capacity = 1 });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/environments");
        var only = Assert.Single(list!);
        Assert.Equal("disabled", only.GetProperty("status").GetString());
        Assert.Equal(0, only.GetProperty("free_slots").GetInt32());
    }

    [Fact]
    public void CredentialLookingFieldsAreFoundAnywhere_ButSecretRefIsTheWayToNameOne()
    {
        Assert.Null(EnvironmentRules.ConnectionError("{\"host\":\"h\",\"secret_ref\":\"k\"}"));
        Assert.NotNull(EnvironmentRules.ConnectionError("{\"hosts\":[{\"apiKey\":\"x\"}]}"));
        Assert.NotNull(EnvironmentRules.ConnectionError("{\"Token\":\"x\"}"));
        Assert.NotNull(EnvironmentRules.ConnectionError("[1]"));
        Assert.NotNull(EnvironmentRules.KeyError("Has Space"));
        Assert.Null(EnvironmentRules.KeyError("test-env.1"));
    }
}
