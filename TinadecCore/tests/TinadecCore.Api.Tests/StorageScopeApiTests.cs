using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

public sealed class StorageScopeApiTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("storage-scopes");
    private ScopeFactory? _factory;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private string? _previousToken;
    public Task InitializeAsync()
    { _previousToken = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN"); Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", _token); Directory.CreateDirectory(_root); _factory = new(_root, _token); return Task.CompletedTask; }
    public async Task DisposeAsync()
    { try { if (_factory is not null) await _factory.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
      finally { Environment.SetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN", _previousToken); } }

    [Fact]
    public async Task ConcurrentOpen_IsIdempotent_AndDoesNotOverwriteProjectConfiguration()
    {
        using var client = _factory!.CreateClient();
        var path = Path.Combine(_root, "project"); Directory.CreateDirectory(path);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path })));
        foreach (var response in responses) response.EnsureSuccessStatusCode();
        var scopes = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Single(scopes.Select(x => x.GetProperty("storage_id").GetString()).Distinct());
        Assert.True(File.Exists(Path.Combine(path, ".tinadec", "project.toml")));
        var configuration = Path.Combine(path, ".tinadec", "config", "tools.toml");
        await File.AppendAllTextAsync(configuration, "\n# user comment\n");
        (await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path })).EnsureSuccessStatusCode();
        Assert.Contains("# user comment", await File.ReadAllTextAsync(configuration));
        var ignore = await File.ReadAllTextAsync(Path.Combine(path, ".tinadec", ".gitignore"));
        Assert.Contains("/data/", ignore); Assert.DoesNotContain("/config/", ignore);
    }

    [Fact]
    public async Task ProjectRequests_AreIsolated_AndUnknownScopeCannotSelectArbitraryStorage()
    {
        using var client = _factory!.CreateClient();
        var a = await OpenAsync(client, "a"); var b = await OpenAsync(client, "b");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sessions") { Content = JsonContent.Create(new { project_id = a.GetProperty("project_id").GetString(), title = "A session" }) };
        create.Headers.Add("X-Tinadec-Storage-Id", a.GetProperty("storage_id").GetString());
        var response = await client.SendAsync(create); response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sessions/" + session.GetProperty("id").GetString() + "/messages");
        wrong.Headers.Add("X-Tinadec-Storage-Id", b.GetProperty("storage_id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(wrong)).StatusCode);
        using var unknown = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sessions"); unknown.Headers.Add("X-Tinadec-Storage-Id", Path.Combine(_root, "data"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(unknown)).StatusCode);
        var aggregate = await client.GetFromJsonAsync<JsonElement>("/api/v1/sessions");
        Assert.Contains(aggregate.EnumerateArray(), x => x.GetProperty("id").GetString() == session.GetProperty("id").GetString() && x.GetProperty("storage_id").GetString() == a.GetProperty("storage_id").GetString());
    }

    [Fact]
    public async Task CacheCleanup_RequiresPreview_AndPreservesDataConfigAndCredentials()
    {
        using var client = _factory!.CreateClient(); var scope = await OpenAsync(client, "cleanup");
        var storage = scope.GetProperty("storage_root").GetString()!; var id = scope.GetProperty("storage_id").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(storage, "cache", "rebuildable"), "cache");
        await File.WriteAllTextAsync(Path.Combine(storage, "data", "fact"), "fact");
        var previewResponse = await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/cleanup-preview", new { category = "cache" }); previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/cleanup", new { preview_id = preview.GetProperty("preview_id").GetString() })).StatusCode);
        using var apply = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/storage/scopes/{id}/cleanup")
        { Content = JsonContent.Create(new { preview_id = preview.GetProperty("preview_id").GetString() }) };
        apply.Headers.Add("X-Tinadec-Host-Control", _token);
        (await client.SendAsync(apply)).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", _token);
        Assert.False(File.Exists(Path.Combine(storage, "cache", "rebuildable"))); Assert.True(File.Exists(Path.Combine(storage, "data", "fact")));
        Assert.True(File.Exists(Path.Combine(storage, "config", "agents.toml")));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/cleanup-preview", new { category = "data" })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/write-policy", new { allow_storage_write = true })).StatusCode);
    }

    [Fact]
    public async Task Move_RebindsHostIdentity_WhileCopyGetsIndependentStorageIdentity()
    {
        using var client = _factory!.CreateClient();
        var original = await OpenAsync(client, "original");
        var id = original.GetProperty("storage_id").GetString()!;
        (await client.PostAsync($"/api/v1/storage/scopes/{id}/close", null)).EnsureSuccessStatusCode();
        var movedPath = Path.Combine(_root, "moved");
        Directory.Move(Path.Combine(_root, "original"), movedPath);
        var movedResponse = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = movedPath });
        movedResponse.EnsureSuccessStatusCode(); var moved = await movedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, moved.GetProperty("storage_id").GetString());
        Assert.Equal(original.GetProperty("project_id").GetString(), moved.GetProperty("project_id").GetString());
        (await client.PostAsync($"/api/v1/storage/scopes/{id}/close", null)).EnsureSuccessStatusCode();
        var copyPath = Path.Combine(_root, "copy"); Directory.CreateDirectory(copyPath);
        await StorageScopeInitializer.CopyOwnedTreeAsync(movedPath, copyPath, CancellationToken.None);
        var copiedResponse = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = copyPath });
        copiedResponse.EnsureSuccessStatusCode(); var copied = await copiedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(id, copied.GetProperty("storage_id").GetString());
        Assert.Equal(moved.GetProperty("project_id").GetString(), copied.GetProperty("project_id").GetString());
    }

    [Fact]
    public async Task ExternalMode_RecoversItsRegistration_WithoutWritingProjectStorage()
    {
        using var client = _factory!.CreateClient();
        var path = Path.Combine(_root, "external-project"); Directory.CreateDirectory(path);
        var storage = Path.Combine(_root, "external-store");
        var response = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path, storage_root = storage });
        response.EnsureSuccessStatusCode(); var original = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(original.GetProperty("external").GetBoolean()); Assert.False(Directory.Exists(Path.Combine(path, ".tinadec")));
        await File.AppendAllTextAsync(Path.Combine(storage, "config", "tools.toml"), "\n# external user edit\n");
        await _factory.DisposeAsync(); _factory = new(_root, _token);
        using var restarted = _factory.CreateClient();
        var opened = await restarted.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path }); opened.EnsureSuccessStatusCode();
        var restored = await opened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(original.GetProperty("storage_id").GetString(), restored.GetProperty("storage_id").GetString());
        Assert.Equal(storage, restored.GetProperty("storage_root").GetString());
        Assert.Contains("# external user edit", await File.ReadAllTextAsync(Path.Combine(storage, "config", "tools.toml")));
    }

    [Fact]
    public async Task ConflictingOrCorruptStorage_IsReportedAndNeverOverwritten()
    {
        using var client = _factory!.CreateClient();
        var path = Path.Combine(_root, "conflict"); Directory.CreateDirectory(path);
        var storage = Path.Combine(path, ".tinadec"); await File.WriteAllTextAsync(storage, "user file");
        var conflict = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Equal("user file", await File.ReadAllTextAsync(storage));
        File.Delete(storage); Directory.CreateDirectory(storage);
        await File.WriteAllTextAsync(Path.Combine(storage, "project.toml"), "schema_version = [broken");
        var corrupt = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path });
        Assert.Equal(HttpStatusCode.Conflict, corrupt.StatusCode);
        var corruptProblem = await corrupt.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("configuration_invalid", corruptProblem.GetProperty("code").GetString());
        Assert.Equal("user_action_required", corruptProblem.GetProperty("category").GetString());
        Assert.Contains("open_settings", corruptProblem.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(corruptProblem.GetProperty("trace_id").GetString()));
        Assert.Equal("schema_version = [broken", await File.ReadAllTextAsync(Path.Combine(storage, "project.toml")));
        var missing = await client.PostAsJsonAsync("/api/v1/storage/scopes/preview", new { project_path = Path.Combine(_root, "missing") });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        var missingProblem = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("storage_scope_unavailable", missingProblem.GetProperty("code").GetString());
        Assert.Equal("environment_unavailable", missingProblem.GetProperty("category").GetString());
    }

    [Fact]
    public async Task Close_WaitsForAnExistingLease_AndCancellationRestoresAdmission()
    {
        using var client = _factory!.CreateClient(); var scope = await OpenAsync(client, "leases");
        var id = scope.GetProperty("storage_id").GetString()!;
        var registry = _factory.Services.GetRequiredService<IStorageScopeRegistry>();
        var lease = await registry.AcquireAsync(id);
        var closing = registry.CloseAsync(id);
        Assert.False(closing.IsCompleted);
        client.DefaultRequestHeaders.Add("X-Tinadec-Storage-Id", id);
        using var refusedStream = await client.GetAsync($"/api/v1/runs/{Guid.NewGuid()}/stream/", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Conflict, refusedStream.StatusCode);
        Assert.Contains("closing", await refusedStream.Content.ReadAsStringAsync());
        await lease.DisposeAsync(); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        await using var reopened = await registry.AcquireAsync(id);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.CloseAsync(id, cancellation.Token));
        await using var admitted = await registry.AcquireAsync(id);
        Assert.Equal(id, admitted.Descriptor.StorageId);
    }

    [Fact]
    public async Task ChangingStorageRoot_CreatesIndependentData_AndRetainsOldStoreAndProjectIdentity()
    {
        using var client = _factory!.CreateClient(); var original = await OpenAsync(client, "relocate");
        var id = original.GetProperty("storage_id").GetString()!;
        var oldRoot = original.GetProperty("storage_root").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "data", "retained-fact"), "do not migrate");
        var newRoot = Path.Combine(_root, "new-storage");
        var changed = await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/configure", new { backend = "sqlite", storage_root = newRoot });
        changed.EnsureSuccessStatusCode(); var dto = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newRoot, dto.GetProperty("storage_root").GetString());
        Assert.Equal(original.GetProperty("project_id").GetString(), dto.GetProperty("project_id").GetString());
        Assert.True(File.Exists(Path.Combine(oldRoot, "data", "retained-fact")));
        Assert.False(File.Exists(Path.Combine(newRoot, "data", "retained-fact")));
        Assert.Contains("backend = \"sqlite\"", await File.ReadAllTextAsync(Path.Combine(newRoot, "config", "storage.toml")));
    }

    [Fact]
    public async Task DeletingProjectStorage_RequiresItsPreview_AndPreservesCode()
    {
        using var client = _factory!.CreateClient(); var scope = await OpenAsync(client, "delete-storage");
        var id = scope.GetProperty("storage_id").GetString()!;
        var code = Path.Combine(scope.GetProperty("project_root").GetString()!, "main.txt"); await File.WriteAllTextAsync(code, "project code");
        var previewResponse = await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/storage-delete-preview", new { });
        Assert.True(previewResponse.IsSuccessStatusCode, await previewResponse.Content.ReadAsStringAsync());
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/storage-delete", new { preview_id = preview.GetProperty("preview_id").GetString() })).StatusCode);
        client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", _token);
        var deleted = await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/storage-delete", new { preview_id = preview.GetProperty("preview_id").GetString() });
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync()); Assert.True(File.Exists(code));
        Assert.False(Directory.Exists(scope.GetProperty("storage_root").GetString()));
        var registrations = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage/scopes");
        Assert.DoesNotContain(registrations.EnumerateArray(), row => row.GetProperty("storage_id").GetString() == id);
    }

    /// <summary>
    /// A registered workspace whose source folder was deleted (a leftover test fixture, for
    /// example) cannot mount, so archive/trash have no database to act on. Unregistering must
    /// still work, keep the stored data, and leave other workspaces usable.
    /// </summary>
    [Fact]
    public async Task UnavailableRegisteredWorkspace_CanBeUnregisteredWithoutMounting()
    {
        using var client = _factory!.CreateClient();
        var path = Path.Combine(_root, "vanished-workspace"); Directory.CreateDirectory(path);
        // External storage keeps the scope's database outside the folder we are about to delete,
        // which is exactly the leftover shape: source folder gone, storage still registered.
        var storage = Path.Combine(_root, "vanished-store");
        var opened = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path, storage_root = storage });
        opened.EnsureSuccessStatusCode(); var scope = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var id = scope.GetProperty("storage_id").GetString()!;
        Assert.True(Directory.Exists(storage));

        Directory.Delete(path, recursive: true);

        // A live mount can keep serving the previous runtime, so the unavailable state only
        // becomes visible after a restart — which is exactly how the user hit it.
        await _factory.DisposeAsync(); _factory = new(_root, _token);
        using var restarted = _factory.CreateClient();

        var listing = await restarted.GetFromJsonAsync<JsonElement[]>("/api/v1/projects");
        var unavailable = Assert.Single(listing!, row => row.GetProperty("storage_id").GetString() == id);
        Assert.Equal("error", unavailable.GetProperty("availability").GetString());
        // The row carries the same error contract as a problem response, so the client can offer
        // an escape hatch (unregister) instead of a dead end.
        Assert.Equal("storage_scope_unavailable", unavailable.GetProperty("availability_code").GetString());
        Assert.Equal("environment_unavailable", unavailable.GetProperty("category").GetString());
        Assert.Contains("unregister_workspace", unavailable.GetProperty("actions").EnumerateArray().Select(x => x.GetString()));

        // Archive/trash need the scope's database, so they cannot rescue this state; the
        // request fails either as "scope unavailable" (409) or "project not found" (404).
        var archived = await restarted.PostAsJsonAsync($"/api/v1/projects/{scope.GetProperty("project_id").GetString()}/archive", new { });
        Assert.False(archived.IsSuccessStatusCode, await archived.Content.ReadAsStringAsync());
        Assert.Contains(archived.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.NotFound });

        var removed = await restarted.DeleteAsync($"/api/v1/storage/scopes/{id}");
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());

        var projects = await restarted.GetFromJsonAsync<JsonElement[]>("/api/v1/projects");
        Assert.DoesNotContain(projects!, row => row.GetProperty("storage_id").GetString() == id);
        // Unregister is not deletion: the stored data stays until an explicit storage delete.
        Assert.True(Directory.Exists(storage));
        Assert.True(File.Exists(Path.Combine(storage, "project.toml")));
    }

    /// <summary>
    /// Every refusal must say who can fix it and what to do next. A bare status code plus a
    /// sentence is what left the user with "move project to trash failed" and no way forward.
    /// </summary>
    [Fact]
    public async Task UnavailableScope_AnswersWithAClassifiedRecoverableProblem()
    {
        using var client = _factory!.CreateClient();
        var path = Path.Combine(_root, "classified-error"); Directory.CreateDirectory(path);
        var storage = Path.Combine(_root, "classified-store");
        var opened = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path, storage_root = storage });
        opened.EnsureSuccessStatusCode(); var scope = await opened.Content.ReadFromJsonAsync<JsonElement>();
        var storageId = scope.GetProperty("storage_id").GetString()!;
        var projectId = scope.GetProperty("project_id").GetString()!;
        Directory.Delete(path, recursive: true);
        await _factory.DisposeAsync(); _factory = new(_root, _token);

        using var restarted = _factory.CreateClient();
        // Trash is the call the user actually made ("move project to trash failed"): it mounts the
        // scope's database, so a deleted source folder refuses here rather than in a listing.
        using var trashRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{projectId}/trash");
        // The sidebar always states the scope; without it the request means the user scope.
        trashRequest.Headers.Add("X-Tinadec-Storage-Id", storageId);
        var trashed = await restarted.SendAsync(trashRequest);

        Assert.Equal(HttpStatusCode.Conflict, trashed.StatusCode);
        var problem = JsonDocument.Parse(await trashed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("storage_scope_unavailable", problem.GetProperty("code").GetString());
        Assert.Equal("environment_unavailable", problem.GetProperty("category").GetString());
        Assert.False(problem.GetProperty("retryable").GetBoolean());
        var actions = problem.GetProperty("actions").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Contains("unregister_workspace", actions);
        Assert.Contains("open_storage_settings", actions);
        // Correlation must survive so a person can quote it in a bug report.
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("trace_id").GetString()));
    }

    [Fact]
    public async Task ScopeListingIsolatesMissingCorruptAndUnauthorizedWorkspacesAndKeepsDatabaseFailuresDistinct()
    {
        using var client = _factory!.CreateClient();
        var healthy = await OpenAsync(client, "healthy-listing");
        var damaged = await OpenAsync(client, "damaged-listing");
        var missing = await OpenAsync(client, "missing-listing");
        var healthyId = healthy.GetProperty("storage_id").GetString()!;
        var damagedId = damaged.GetProperty("storage_id").GetString()!;
        var missingId = missing.GetProperty("storage_id").GetString()!;
        var manifest = Path.Combine(damaged.GetProperty("storage_root").GetString()!, "project.toml");
        var original = await File.ReadAllTextAsync(manifest);
        (await client.PostAsync($"/api/v1/storage/scopes/{damagedId}/close", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/storage/scopes/{missingId}/close", null)).EnsureSuccessStatusCode();
        Directory.Delete(missing.GetProperty("project_root").GetString()!, recursive: true);
        await File.WriteAllTextAsync(manifest, "schema_version = [broken");

        var scopes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/storage/scopes");
        Assert.Equal("ready", Assert.Single(scopes!, row => row.GetProperty("storage_id").GetString() == healthyId).GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, Assert.Single(scopes!, row => row.GetProperty("storage_id").GetString() == "user").GetProperty("workspace").ValueKind);
        var invalid = Assert.Single(scopes!, row => row.GetProperty("storage_id").GetString() == damagedId);
        Assert.Equal("configuration_invalid", invalid.GetProperty("availability_code").GetString());
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("workspace").ValueKind);
        Assert.Equal("user_action_required", invalid.GetProperty("category").GetString());
        Assert.False(string.IsNullOrWhiteSpace(invalid.GetProperty("trace_id").GetString()));
        Assert.Equal(JsonValueKind.Array, invalid.GetProperty("diagnostics").ValueKind);
        var unavailable = Assert.Single(scopes!, row => row.GetProperty("storage_id").GetString() == missingId);
        Assert.Equal("storage_scope_unavailable", unavailable.GetProperty("availability_code").GetString());
        Assert.Contains("unregister_workspace", unavailable.GetProperty("actions").EnumerateArray().Select(row => row.GetString()));
        Assert.False(unavailable.TryGetProperty("lifecycle_status", out _));

        var projects = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/projects");
        Assert.Equal("ready", Assert.Single(projects!, row => row.GetProperty("storage_id").GetString() == healthyId).GetProperty("availability").GetString());
        Assert.False(Assert.Single(projects!, row => row.GetProperty("storage_id").GetString() == damagedId).TryGetProperty("lifecycle_status", out _));
        Assert.Equal("schema_version = [broken", await File.ReadAllTextAsync(manifest));

        var outside = Path.Combine(_root, "outside-listing"); Directory.CreateDirectory(outside);
        Assert.Contains("path = \".\"", original);
        await File.WriteAllTextAsync(manifest, original.Replace("path = \".\"", "path = \"../outside-listing\"", StringComparison.Ordinal));
        scopes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/storage/scopes");
        var unauthorized = Assert.Single(scopes!, row => row.GetProperty("storage_id").GetString() == damagedId);
        Assert.Equal("workspace_authorization_required", unauthorized.GetProperty("availability_code").GetString());
        Assert.NotEmpty(unauthorized.GetProperty("diagnostics").EnumerateArray());

        await File.WriteAllTextAsync(manifest, original);
        await File.WriteAllTextAsync(Path.Combine(damaged.GetProperty("storage_root").GetString()!, "data", "tinadec.db"), "not a SQLite database");
        projects = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/projects");
        var databaseFailure = Assert.Single(projects!, row => row.GetProperty("storage_id").GetString() == damagedId);
        Assert.Equal("storage_database_error", databaseFailure.GetProperty("availability_code").GetString());
        Assert.Equal("internal", databaseFailure.GetProperty("category").GetString());
        Assert.DoesNotContain("SqliteException", databaseFailure.GetProperty("availability_error").GetString());
        Assert.Contains(_factory.Services.GetRequiredService<TinadecCore.AspNetCore.ServerFailureJournal>().Recent(), row => row.Code == "storage_database_error");
    }

    [Theory]
    [InlineData("archive", "archived")]
    [InlineData("trash", "trashed")]
    public async Task HistoricalWorkspaceMountPreservesLifecycleAndSessionsAcrossCloseAndRestart(string action, string lifecycle)
    {
        using var client = _factory!.CreateClient();
        var scope = await OpenAsync(client, "historical-" + action);
        var storageId = scope.GetProperty("storage_id").GetString()!;
        var projectId = scope.GetProperty("project_id").GetString()!;
        client.DefaultRequestHeaders.Add("X-Tinadec-Storage-Id", storageId);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId, title = "Retained history" });
        created.EnsureSuccessStatusCode();
        var sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        (await client.PostAsync($"/api/v1/projects/{projectId}/{action}", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/v1/storage/scopes/{storageId}/close", null)).EnsureSuccessStatusCode();
        var afterClose = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/projects?lifecycle_status={lifecycle}");
        Assert.Equal(projectId, Assert.Single(afterClose!).GetProperty("id").GetString());
        Assert.Equal(lifecycle, Assert.Single(afterClose!).GetProperty("lifecycle_status").GetString());

        await _factory.DisposeAsync(); _factory = new(_root, _token);
        using var restarted = _factory.CreateClient();
        restarted.DefaultRequestHeaders.Add("X-Tinadec-Storage-Id", storageId);
        var history = await restarted.GetFromJsonAsync<JsonElement[]>($"/api/v1/projects?lifecycle_status={lifecycle}");
        Assert.Equal(projectId, Assert.Single(history!).GetProperty("id").GetString());
        Assert.Equal(lifecycle, Assert.Single(history!).GetProperty("lifecycle_status").GetString());
        var sessions = await restarted.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions?project_id={projectId}");
        Assert.Equal(sessionId, Assert.Single(sessions!).GetProperty("id").GetString());
        (await restarted.PostAsync($"/api/v1/projects/{projectId}/restore", null)).EnsureSuccessStatusCode();
        var active = await restarted.GetFromJsonAsync<JsonElement[]>("/api/v1/projects");
        Assert.Equal(projectId, Assert.Single(active!).GetProperty("id").GetString());
        Assert.Equal("active", Assert.Single(active!).GetProperty("lifecycle_status").GetString());
        Assert.Single(await restarted.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions?project_id={projectId}") ?? []);
    }

    private static async Task<JsonElement> InScopeAsync(HttpClient client, HttpMethod method, string path, string storageId, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Tinadec-Storage-Id", storageId);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> CreateUserProviderAsync(HttpClient client, string name) =>
        await InScopeAsync(client, HttpMethod.Post, "/api/v1/model-providers", "user",
            new { driver = "openai", display_name = name, base_url = "http://localhost", model = name + "-model", api_key = "test" });

    private static async Task<string[]> ProviderNamesAsync(HttpClient client, string storageId) =>
        (await InScopeAsync(client, HttpMethod.Get, "/api/v1/model-providers", storageId)).EnumerateArray()
            .Select(row => row.GetProperty("display_name").GetString()!).ToArray();

    /// <summary>
    /// The reported bug: a model set up in the model center (user scope) was missing in a
    /// workspace, and one deleted there stayed in use, because a workspace ran on a copy of
    /// models.toml taken when it was created. A new workspace now runs on the user's file.
    /// </summary>
    [Fact]
    public async Task NewWorkspace_RunsOnTheUserModels_SoModelCenterEditsAndDeletionsReachIt()
    {
        using var client = _factory!.CreateClient();
        var scope = await OpenAsync(client, "follows");
        var id = scope.GetProperty("storage_id").GetString()!;
        Assert.True(scope.GetProperty("follow_user_models").GetBoolean());

        var provider = await CreateUserProviderAsync(client, "Added later");
        Assert.Contains("Added later", await ProviderNamesAsync(client, id));

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/model-providers/{provider.GetProperty("id").GetGuid()}?force=true"))
        {
            delete.Headers.Add("X-Tinadec-Storage-Id", "user");
            delete.Headers.TryAddWithoutValidation("If-Match", $"\"{provider.GetProperty("revision").GetInt64()}\"");
            Assert.True((await client.SendAsync(delete)).IsSuccessStatusCode);
        }
        Assert.DoesNotContain("Added later", await ProviderNamesAsync(client, id));

        var userDocument = await InScopeAsync(client, HttpMethod.Get, "/api/v1/configuration/documents/models", "user");
        var workspaceDocument = await InScopeAsync(client, HttpMethod.Get, "/api/v1/configuration/documents/models", id);
        Assert.Equal(userDocument.GetProperty("path").GetString(), workspaceDocument.GetProperty("path").GetString());
        // Other modules keep the workspace's own files.
        var tools = await InScopeAsync(client, HttpMethod.Get, "/api/v1/configuration/documents/tools", id);
        Assert.StartsWith(scope.GetProperty("storage_root").GetString()!, tools.GetProperty("path").GetString()!);
    }

    [Fact]
    public async Task Workspace_CanKeepItsOwnModels_AndFollowTheUserAgain_AcrossRestart()
    {
        using var client = _factory!.CreateClient();
        var id = (await OpenAsync(client, "switches")).GetProperty("storage_id").GetString()!;

        await InScopeAsync(client, HttpMethod.Post, $"/api/v1/storage/scopes/{id}/model-source", "user", new { follow_user = false });
        await CreateUserProviderAsync(client, "Only in user");
        Assert.DoesNotContain("Only in user", await ProviderNamesAsync(client, id));
        var own = await InScopeAsync(client, HttpMethod.Get, "/api/v1/configuration/documents/models", id);
        Assert.StartsWith(Path.Combine(_root, "switches"), own.GetProperty("path").GetString()!);

        await InScopeAsync(client, HttpMethod.Post, $"/api/v1/storage/scopes/{id}/model-source", "user", new { follow_user = true });
        Assert.Contains("Only in user", await ProviderNamesAsync(client, id));

        // The choice lives in this machine's host registration and survives a restart.
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _factory = new(_root, _token);
        using var restarted = _factory.CreateClient();
        var scopes = await InScopeAsync(restarted, HttpMethod.Get, "/api/v1/storage/scopes", "user");
        Assert.True(scopes.EnumerateArray().Single(row => row.GetProperty("storage_id").GetString() == id).GetProperty("follow_user_models").GetBoolean());
        Assert.Contains("Only in user", await ProviderNamesAsync(restarted, id));
    }

    [Fact]
    public async Task UserScope_HasNoModelSourceToSwitch()
    {
        using var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/storage/scopes/user/model-source", new { follow_user = false });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonElement> OpenAsync(HttpClient client, string name)
    {
        var path = Path.Combine(_root, name); Directory.CreateDirectory(path);
        var response = await client.PostAsJsonAsync("/api/v1/storage/scopes/open", new { project_path = path, name }); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task DeletionPreviewRejectsChangedDatabaseFactsAndPreservesProjectStorage()
    {
        using var client = _factory!.CreateClient(); var scope = await OpenAsync(client, "changed-facts");
        var id = scope.GetProperty("storage_id").GetString()!;
        var previewResponse = await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/storage-delete-preview", new { });
        Assert.True(previewResponse.IsSuccessStatusCode, await previewResponse.Content.ReadAsStringAsync());
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Add("X-Tinadec-Storage-Id", id);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = scope.GetProperty("project_id").GetString(), title = "new fact after preview" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var deleted = await client.PostAsJsonAsync($"/api/v1/storage/scopes/{id}/storage-delete", new { preview_id = preview.GetProperty("preview_id").GetString() });
        var deletionDetail = await deleted.Content.ReadAsStringAsync();
        Assert.True(deleted.StatusCode == HttpStatusCode.Conflict, deletionDetail);
        Assert.Contains("changed after the preview", deletionDetail);
        Assert.True(Directory.Exists(scope.GetProperty("storage_root").GetString()));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scope.GetProperty("storage_root").GetString()!, "temp"), "deletion-preview-*.db"));
    }
    [Fact]
    public async Task UntrustedLoopbackCannotReadDataInitializeStorageOrApproveActions()
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");
        var path = Path.Combine(_root, "untrusted"); Directory.CreateDirectory(path);
        foreach (var request in new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/api/v1/sessions"),
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/storage/scopes/open") { Content = JsonContent.Create(new { project_path = path }) },
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{Guid.NewGuid()}/decision") { Content = JsonContent.Create(new { decision = "approved" }) }
        })
        {
            using (request) Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        }
        Assert.False(Directory.Exists(Path.Combine(path, ".tinadec")));
    }

    [Fact]
    public async Task HostShutdownAwaitsConcurrentDisposalAndReleasesOwnedFileHandles()
    {
        using var client = _factory!.CreateClient();
        var scope = await OpenAsync(client, "shutdown");
        var registry = (StorageScopeRegistry)_factory.Services.GetRequiredService<IStorageScopeRegistry>();
        await Task.WhenAll(registry.DisposeAsync().AsTask(), registry.DisposeAsync().AsTask(), _factory.DisposeAsync().AsTask());
        Assert.True(registry.DisposeAsync().IsCompletedSuccessfully);
        foreach (var path in new[]
        {
            Path.Combine(scope.GetProperty("storage_root").GetString()!, "data", "tinadec.db"),
            Path.Combine(_root, "user", "data", "tinadec.db"),
            Path.Combine(_root, "user", "state", "host.lock")
        })
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(probe.CanWrite);
        }
    }

    [Fact]
    public async Task PublicChallengeProvesTheCoreRoleAndRejectsInvalidNonces()
    {
        using var client = _factory!.CreateClient(); client.DefaultRequestHeaders.Remove("X-Tinadec-Host-Control");
        var nonce = new string('n', 43);
        var response = await client.GetAsync("/api/v1/host-challenge?nonce=" + nonce);
        response.EnsureSuccessStatusCode(); var proof = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("core", proof.GetProperty("role").GetString()); Assert.Equal(nonce, proof.GetProperty("nonce").GetString());
        var expected = System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_token), System.Text.Encoding.UTF8.GetBytes("tinadec-host-v1\0core\0" + nonce));
        Assert.Equal(Convert.ToHexString(expected).ToLowerInvariant(), proof.GetProperty("proof").GetString());
        Assert.DoesNotContain(_token, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/host-challenge?nonce=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/host-challenge?nonce={nonce}&nonce={nonce}")).StatusCode);
    }

    private sealed class ScopeFactory(string root, string token) : IsolatedApiFactory
    {
        protected override bool UsesManagedStorage => true;
        protected override string? ManagedUserRoot => Path.Combine(root, "user");
        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("X-Tinadec-Host-Control", token);
        }
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TinadecStorage:Enabled"] = "true", ["TinadecStorage:UserRoot"] = Path.Combine(root, "user"),
                ["TinadecTools:DefaultWorkspaceRoot"] = Path.Combine(root, "workspace"), ["Logging:LogLevel:Default"] = "Warning" }));
        }
    }
}
