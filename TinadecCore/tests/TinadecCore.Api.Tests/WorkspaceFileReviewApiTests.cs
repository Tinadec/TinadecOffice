using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Reviewing and undoing a workspace snapshot one file at a time.
///
/// The whole-workspace restore already existed; what it could not answer is the question a review
/// card actually asks — "this one file, back to what it was, and don't touch the other four". The
/// snapshot manifest already holds per-file hashes and, under a ceiling, per-file bytes, so this is
/// a read of data that was already being written, not a new store.
///
/// Hashes are read back from the listing rather than computed here: the guard under test compares
/// against the value the API minted, and a locally computed hash would keep passing if the two ever
/// disagreed on encoding.
/// </summary>
public sealed class WorkspaceFileReviewApiTests : IAsyncLifetime
{
    private const int PreviewCeilingBytes = 256 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-file-review-tests", Guid.NewGuid().ToString("N"));
    private ReviewFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new ReviewFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes <paramref name="files"/> into a fresh workspace, binds a project to it and snapshots
    /// it. <paramref name="maxBytes"/> is the provider's content budget: below it the manifest keeps
    /// only hashes, which is how a file stays reportable but irreversible.
    /// </summary>
    private async Task<(string Workspace, Guid SnapshotId)> SnapshotAsync(
        string label,
        (string Name, string Content)[] files,
        long maxBytes = 4 * 1024 * 1024)
    {
        var workspace = Path.Combine(_root, label + "-workspace");
        Directory.CreateDirectory(workspace);
        foreach (var (name, content) in files)
        {
            var target = Path.Combine(workspace, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects",
            new { name = $"Review {label}", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetGuid();
        var created = await client.PostAsJsonAsync($"/api/v1/projects/{projectId}/snapshots",
            new { idempotency_key = $"snapshot-{label}", max_files = 200, max_bytes = maxBytes });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (workspace, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private async Task<Dictionary<string, JsonElement>> ChangesAsync(Guid snapshotId)
    {
        var rows = await _factory!.CreateClient()
            .GetFromJsonAsync<JsonElement[]>($"/api/v1/workspace-snapshots/{snapshotId}/files");
        Assert.NotNull(rows);
        // Path is the key so a row that appears twice cannot hide in a positional read.
        return rows!.ToDictionary(row => row.GetProperty("path").GetString()!, StringComparer.Ordinal);
    }

    private static string HashOf(JsonElement row, string side) => row.GetProperty(side).GetString()!;

    /// <summary>
    /// This host drops nulls on write, so "absent" and "null" are the same wire fact and both mean
    /// "no value". Read that way instead of through GetProperty, which throws on the absent one.
    /// </summary>
    private static JsonValueKind KindOf(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) ? value.ValueKind : JsonValueKind.Undefined;

    // ── the listing ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_SeesAddedModifiedDeletedAndUnchangedFilesSeparately()
    {
        var (workspace, snapshotId) = await SnapshotAsync("statuses", [
            ("keep.txt", "untouched"),
            ("edit.txt", "first"),
            ("drop.txt", "gone soon")]);
        File.WriteAllText(Path.Combine(workspace, "edit.txt"), "second");
        File.Delete(Path.Combine(workspace, "drop.txt"));
        File.WriteAllText(Path.Combine(workspace, "brand-new.txt"), "appeared");

        var rows = await ChangesAsync(snapshotId);

        Assert.Equal("unchanged", rows["keep.txt"].GetProperty("status").GetString());
        Assert.Equal("modified", rows["edit.txt"].GetProperty("status").GetString());
        Assert.Equal("deleted", rows["drop.txt"].GetProperty("status").GetString());
        Assert.Equal("added", rows["brand-new.txt"].GetProperty("status").GetString());
        // A deleted row keeps the hash it had and has none now: "we lost it" is not "it was empty".
        // Read as "no string on the wire", because this host drops nulls rather than writing them.
        Assert.Equal(JsonValueKind.String, KindOf(rows["drop.txt"], "before_sha256"));
        Assert.True(KindOf(rows["drop.txt"], "after_sha256") is JsonValueKind.Null or JsonValueKind.Undefined);
        // The added file has no before to compare against, so the API must not invent a hash for it.
        Assert.True(KindOf(rows["brand-new.txt"], "before_sha256") is JsonValueKind.Null or JsonValueKind.Undefined);
    }

    [Fact]
    public async Task Listing_ReportsWhichRowsCanBeUndone()
    {
        // One byte of content budget: every file keeps a hash and loses its body. The review card
        // can still say what changed; it must not offer a button that cannot do anything.
        var (_, snapshotId) = await SnapshotAsync("budget", [("note.txt", "thirty-ish bytes of text")], maxBytes: 1);

        var rows = await ChangesAsync(snapshotId);

        Assert.False(rows["note.txt"].GetProperty("restorable").GetBoolean());
        var restore = await _factory!.CreateClient().PostAsJsonAsync(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/restore",
            new { path = "note.txt", expected_sha256 = HashOf(rows["note.txt"], "after_sha256") });
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Equal("file_content_not_captured", (await restore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownSnapshot_IsCodedNotFound_NotABareStatus()
    {
        var response = await _factory!.CreateClient()
            .GetAsync($"/api/v1/workspace-snapshots/{Guid.NewGuid()}/files");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("snapshot_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ── the diff ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_ServesBothBodiesOfAModifiedFile()
    {
        var (workspace, snapshotId) = await SnapshotAsync("diff", [("src/note.txt", "BEFORE-LINE\nshared\n")]);
        File.WriteAllText(Path.Combine(workspace, "src", "note.txt"), "shared\nAFTER-LINE\n");

        var diff = await _factory!.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/v1/workspace-snapshots/{snapshotId}/files/diff?path=src/note.txt");

        Assert.NotNull(diff);
        Assert.Equal("modified", diff!.GetProperty("status").GetString());
        Assert.Equal("BEFORE-LINE\nshared\n", diff.GetProperty("before").GetProperty("text").GetString());
        Assert.Equal("shared\nAFTER-LINE\n", diff.GetProperty("after").GetProperty("text").GetString());
        Assert.True(diff.GetProperty("before").GetProperty("present").GetBoolean());
        Assert.True(diff.GetProperty("after").GetProperty("present").GetBoolean());
    }

    [Fact]
    public async Task Diff_MarksADeletedFileAbsentRatherThanEmpty()
    {
        var (workspace, snapshotId) = await SnapshotAsync("deleted", [("gone.txt", "content that existed")]);
        File.Delete(Path.Combine(workspace, "gone.txt"));

        var diff = await _factory!.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/v1/workspace-snapshots/{snapshotId}/files/diff?path=gone.txt");

        Assert.True(diff!.TryGetProperty("before", out var deletedBefore), diff.GetRawText());
        Assert.True(deletedBefore.GetProperty("present").GetBoolean());
        Assert.True(diff.TryGetProperty("after", out var deletedAfter), diff.GetRawText());
        Assert.False(deletedAfter.GetProperty("present").GetBoolean(), deletedAfter.GetRawText());
        // The distinguishing fact of a deletion: no body is served at all. The host drops nulls on
        // write, so "absent" and "null" are one wire fact and both mean "there is nothing to show".
        Assert.NotEqual(JsonValueKind.String, KindOf(deletedAfter, "text"));
        Assert.True(diff.GetProperty("restorable").GetBoolean());
    }

    [Fact]
    public async Task Diff_CutsALargeFileAndStopsSayingItIsTheWholeThing()
    {
        var body = new string('x', PreviewCeilingBytes) + "TAIL-MARKER-4b7e";
        var (workspace, snapshotId) = await SnapshotAsync("truncated", [("big.txt", body)]);
        File.WriteAllText(Path.Combine(workspace, "big.txt"), body + " and an appended clause");

        var diff = await _factory!.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/v1/workspace-snapshots/{snapshotId}/files/diff?path=big.txt");

        var before = diff!.GetProperty("before");
        Assert.True(before.GetProperty("truncated").GetBoolean());
        var served = before.GetProperty("text").GetString()!;
        Assert.Equal(PreviewCeilingBytes, served.Length);
        // A repeated payload would let the cut tail survive in the retained head; this marker only
        // exists past the ceiling, so finding it would mean the flag lied or the cut did not happen.
        Assert.DoesNotContain("TAIL-MARKER-4b7e", served, StringComparison.Ordinal);
        // The hash still describes the whole file, which is why a truncated row is still restorable.
        Assert.True(diff.GetProperty("restorable").GetBoolean());
    }

    [Fact]
    public async Task Diff_KeepsBinaryOutTheText()
    {
        var workspace = Path.Combine(_root, "binary-workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllBytes(Path.Combine(workspace, "blob.bin"), [0x00, 0x01, 0xFE, 0xFF, 0x41, 0x42]);
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects",
            new { name = "Binary", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var snapshot = await client.PostAsJsonAsync(
            $"/api/v1/projects/{project.GetProperty("id").GetGuid()}/snapshots", new { idempotency_key = "binary-1" });
        var snapshotId = (await snapshot.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        File.WriteAllBytes(Path.Combine(workspace, "blob.bin"), [0x00, 0x02, 0xFE, 0xFF, 0x41, 0x42]);

        var diff = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/diff?path=blob.bin");

        Assert.True(diff!.TryGetProperty("before", out var binaryBefore), diff.GetRawText());
        Assert.True(binaryBefore.TryGetProperty("binary", out var binaryFlag), binaryBefore.GetRawText());
        Assert.True(binaryFlag.GetBoolean());
        Assert.NotEqual(JsonValueKind.String, KindOf(binaryBefore, "text"));
        // Refusing to print it is not the same as claiming it is unchanged.
        Assert.Equal("modified", diff.GetProperty("status").GetString());
    }

    // ── the restore ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_UndoesOneFileAndLeavesTheOthersForTheirOwnReview()
    {
        var (workspace, snapshotId) = await SnapshotAsync("undo", [
            ("undo-me.txt", "ORIGINAL-TEXT"),
            ("keep-me.txt", "ORIGINAL-OTHER")]);
        File.WriteAllText(Path.Combine(workspace, "undo-me.txt"), "AGENT-WROTE-THIS");
        File.WriteAllText(Path.Combine(workspace, "keep-me.txt"), "AGENT-WROTE-OTHER");
        var rows = await ChangesAsync(snapshotId);

        var response = await _factory!.CreateClient().PostAsJsonAsync(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/restore",
            new { path = "undo-me.txt", expected_sha256 = HashOf(rows["undo-me.txt"], "after_sha256") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var restored = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unchanged", restored.GetProperty("status").GetString());
        Assert.Equal("ORIGINAL-TEXT", File.ReadAllText(Path.Combine(workspace, "undo-me.txt")));
        // The point of a per-file restore: the sibling is still the agent's edit, not reverted and
        // not silently kept in the review card either — it now reports as still modified.
        Assert.Equal("AGENT-WROTE-OTHER", File.ReadAllText(Path.Combine(workspace, "keep-me.txt")));
        var after = await ChangesAsync(snapshotId);
        Assert.Equal("unchanged", after["undo-me.txt"].GetProperty("status").GetString());
        Assert.Equal("modified", after["keep-me.txt"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Restore_RefusesToOverwriteAnEditNobodyReviewed()
    {
        var (workspace, snapshotId) = await SnapshotAsync("race", [("note.txt", "ORIGINAL")]);
        File.WriteAllText(Path.Combine(workspace, "note.txt"), "AGENT-WROTE-THIS");
        var reviewed = HashOf((await ChangesAsync(snapshotId))["note.txt"], "after_sha256");

        // The user opens the card, and while they read it something else writes the file again.
        File.WriteAllText(Path.Combine(workspace, "note.txt"), "A-LATER-EDIT");
        var response = await _factory!.CreateClient().PostAsJsonAsync(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/restore",
            new { path = "note.txt", expected_sha256 = reviewed });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace_conflict", body.GetProperty("code").GetString());
        Assert.Contains("note.txt", body.GetProperty("conflicts").ToString(), StringComparison.Ordinal);
        Assert.Equal("A-LATER-EDIT", File.ReadAllText(Path.Combine(workspace, "note.txt")));
    }

    [Fact]
    public async Task Restore_WithoutTheHashTheReviewerWasShown_IsRejected()
    {
        var (workspace, snapshotId) = await SnapshotAsync("guard", [("note.txt", "ORIGINAL")]);
        File.WriteAllText(Path.Combine(workspace, "note.txt"), "AGENT-WROTE-THIS");

        var response = await _factory!.CreateClient().PostAsJsonAsync(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/restore", new { path = "note.txt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("AGENT-WROTE-THIS", File.ReadAllText(Path.Combine(workspace, "note.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("./../outside.txt")]
    [InlineData("notes/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/etc/passwd")]
    public async Task Restore_RefusesEveryPathShapeThatLeavesTheWorkspace(string escape)
    {
        var (workspace, snapshotId) = await SnapshotAsync("containment", [("note.txt", "ORIGINAL")]);
        var outside = Path.Combine(Directory.GetParent(workspace)!.FullName, "outside.txt");
        File.WriteAllText(outside, "NOT-MINE");

        var response = await _factory!.CreateClient().PostAsJsonAsync(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/restore",
            new { path = escape, expected_sha256 = "ab" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("path_outside_workspace", body.GetProperty("code").GetString());
        // The refusal is the whole job here: the neighbouring file must still hold its own text.
        Assert.Equal("NOT-MINE", File.ReadAllText(outside));

        // The read half of the same boundary must answer with the same code. Two routes refusing
        // one shape in two ways is how a client ends up handling containment by message text.
        var diffResponse = await _factory.CreateClient().GetAsync(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/diff?path={Uri.EscapeDataString(escape)}");
        Assert.Equal(HttpStatusCode.BadRequest, diffResponse.StatusCode);
        Assert.Equal("path_outside_workspace",
            (await diffResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Restore_BringsBackAFileTheAgentDeleted()
    {
        var (workspace, snapshotId) = await SnapshotAsync("undelete", [("needed.txt", "STILL-WANTED")]);
        File.Delete(Path.Combine(workspace, "needed.txt"));
        var rows = await ChangesAsync(snapshotId);

        var response = await _factory!.CreateClient().PostAsJsonAsync(
            $"/api/v1/workspace-snapshots/{snapshotId}/files/restore",
            new { path = "needed.txt", expected_sha256 = "" });

        // A deleted file has no current hash to name, so what the reviewer asserts is "absent" —
        // an empty expectation, which is a different fact from omitting the field (that is refused).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("STILL-WANTED", File.ReadAllText(Path.Combine(workspace, "needed.txt")));
    }

    private sealed class ReviewFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public ReviewFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
