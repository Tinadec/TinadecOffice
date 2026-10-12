using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

public sealed partial class StorageApiTests
{
    [Fact]
    public async Task TaskSnapshots_ConcurrentUpdatesPreserveEveryTaskAndAllowHeldReaders()
    {
        using var client = _factory!.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Concurrent task snapshots" });
        created.EnsureSuccessStatusCode();
        var sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var sent = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "Snapshot trigger" });
        sent.EnsureSuccessStatusCode();
        var messageId = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.StartRunAsync(sessionId, messageId);
        var path = Path.Combine(_root, "data", "tasks", run.Id + ".tasks.json");
        // The debug projection holds the previous immutable file while the writer
        // replaces its name. Windows must keep that reader valid, not block the run.
        await using var oldReader = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        const int count = 24;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Enumerable.Range(0, count).Select(async index =>
        {
            await start.Task;
            await lifecycle.UpdateTaskAsync(run.Id, new { task_id = index, status = "completed" });
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(updates);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var previous = await JsonSerializer.DeserializeAsync<TaskSnapshotFile>(oldReader, options);
        Assert.Empty(previous!.Tasks);
        await using (var currentReader = File.OpenRead(path))
        {
            var current = (await JsonSerializer.DeserializeAsync<TaskSnapshotFile>(currentReader, options))!;
            Assert.Equal(run.Id, current.RunId);
            Assert.Equal(count, current.Tasks.Count);
            Assert.Equal(Enumerable.Range(0, count), current.Tasks.Cast<JsonElement>().Select(task => task.GetProperty("task_id").GetInt32()).Order());
        }
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        Assert.Equal(count, (await db.Runs.SingleAsync(item => item.Id == run.Id)).TaskRevision);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
