using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The attachment surface, exercised over HTTP rather than against the store, because the
/// properties that matter here live at the boundary: what a client may name, what Core
/// echoes back into a response header, and what happens to bytes two uploads share.
/// </summary>
public sealed class AttachmentApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-core-attachment-tests", Guid.NewGuid().ToString("N"));
    private AttachmentFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new AttachmentFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<(HttpClient Client, Guid SessionId)> OpenSessionAsync(string label)
    {
        var client = _factory!.CreateClient();
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects",
            new { name = $"Attachment {label}", path = Path.Combine(_root, $"{label}-workspace") });
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();

        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions",
            new { project_id = project.GetProperty("id").GetGuid(), title = $"Attachment {label}" });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (client, session.GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid sessionId, byte[] bytes, string? fileName, string? mediaType = null)
    {
        var query = new List<string>();
        if (fileName is not null) query.Add($"filename={Uri.EscapeDataString(fileName)}");
        if (mediaType is not null) query.Add($"media_type={Uri.EscapeDataString(mediaType)}");
        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
        using var content = new ByteArrayContent(bytes);
        return await client.PostAsync($"/api/v1/sessions/{sessionId}/attachments{suffix}", content);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Upload_ListAndDownload_RoundTripTheBytesWithTheStoresOwnHash()
    {
        var (client, sessionId) = await OpenSessionAsync("roundtrip");
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("alpha-beta\n", 3)));

        var created = await UploadAsync(client, sessionId, payload, "notes.txt", "text/plain");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await BodyAsync(created);
        var id = dto.GetProperty("id").GetGuid();

        // The hash is what the store computed over the bytes it actually received. This is
        // the only assertion here able to tell "the upload arrived intact" apart from
        // "Core recorded whatever the caller claimed".
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), dto.GetProperty("content_hash").GetString());
        Assert.Equal(payload.LongLength, dto.GetProperty("content_length").GetInt64());
        Assert.Equal("notes.txt", dto.GetProperty("file_name").GetString());
        Assert.Equal("text/plain", dto.GetProperty("media_type").GetString());

        // The storage path is Core-owned. Echoing it would hand a client a handle into the
        // data root, and it is exactly the field a later refactor could start leaking.
        Assert.False(dto.TryGetProperty("content_reference", out _));
        Assert.False(dto.TryGetProperty("message_id", out _));

        var listed = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/attachments");
        Assert.Single(listed!);
        Assert.Equal(id, listed![0].GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/attachments/{id}")).StatusCode);
        var bytes = await (await client.GetAsync($"/api/v1/attachments/{id}/content")).Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task Upload_ToAnUnknownSession_IsNotFound_AndLeavesNoBytesBehind()
    {
        var (client, _) = await OpenSessionAsync("foreign");
        var payload = Encoding.UTF8.GetBytes("must-not-be-stored");

        var response = await UploadAsync(client, Guid.NewGuid(), payload, "ghost.txt");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", (await BodyAsync(response)).GetProperty("code").GetString());

        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var contentRoot = Path.Combine(_root, "data", "content");
        Assert.False(Directory.Exists(contentRoot) &&
            Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories).Any(path => Path.GetFileName(path) == hash),
            "a rejected upload still wrote its bytes into the content store");
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\dev\Desktop\notes.txt", "notes.txt")]
    [InlineData("   spaced.txt   ", "spaced.txt")]
    [InlineData("..hidden", "hidden")]
    public async Task Upload_StoresOnlyTheLeafOfWhateverNameWasSent(string sent, string expected)
    {
        var (client, sessionId) = await OpenSessionAsync($"name-{expected}");
        var response = await UploadAsync(client, sessionId, Encoding.UTF8.GetBytes("x"), sent);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(expected, (await BodyAsync(response)).GetProperty("file_name").GetString());
    }

    [Fact]
    public async Task Upload_WithoutAFilename_IsRejected()
    {
        var (client, sessionId) = await OpenSessionAsync("noname");
        var response = await UploadAsync(client, sessionId, Encoding.UTF8.GetBytes("x"), null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("attachment_filename_invalid", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Upload_WithNoBytes_IsRejected()
    {
        var (client, sessionId) = await OpenSessionAsync("empty");
        var response = await UploadAsync(client, sessionId, Array.Empty<byte>(), "empty.txt");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("attachment_empty", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Upload_ServedBackAsHtml_IsDowngradedToAnAttachmentDownload()
    {
        var (client, sessionId) = await OpenSessionAsync("html");
        var created = await UploadAsync(client, sessionId,
            Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "page.html", "text/html");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await BodyAsync(created)).GetProperty("id").GetGuid();

        // These bytes are served from the origin the renderer trusts, so an inline HTML
        // response is script execution on that origin. Only types that cannot carry a
        // script stay inline.
        var response = await client.GetAsync($"/api/v1/attachments/{id}/content");
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        // Content-Disposition is a content header; reading it off response.Headers throws
        // "Misused header name" rather than returning anything.
        Assert.StartsWith("attachment;", response.Content.Headers.GetValues("Content-Disposition").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_ServedBackAsAnImage_StaysInlineWithItsType()
    {
        var (client, sessionId) = await OpenSessionAsync("png");
        var created = await UploadAsync(client, sessionId, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "shot.png", "image/png");
        var id = (await BodyAsync(created)).GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/api/v1/attachments/{id}/content");
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.GetValues("Content-Disposition").Single();
        Assert.StartsWith("inline;", disposition, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''shot.png", disposition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_WithControlCharactersInItsDeclaredType_CannotWriteResponseHeaders()
    {
        var (client, sessionId) = await OpenSessionAsync("crack");
        var created = await UploadAsync(client, sessionId, Encoding.UTF8.GetBytes("x"), "safe.txt",
            "image/png\r\nX-Injected: yes");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await BodyAsync(created);
        var id = dto.GetProperty("id").GetGuid();
        // The declared type never survives: it is stored as octet-stream, so nothing
        // CRLF-shaped can reach a response header later.
        Assert.Equal("application/octet-stream", dto.GetProperty("media_type").GetString());

        var response = await client.GetAsync($"/api/v1/attachments/{id}/content");
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("X-Injected"));
        Assert.Single(response.Content.Headers.GetValues("Content-Disposition"));
    }

    [Fact]
    public async Task Delete_RemovesTheRowButKeepsBytesASecondUploadShares()
    {
        var (client, sessionId) = await OpenSessionAsync("dedupe");
        var payload = Encoding.UTF8.GetBytes("identical-bytes-once");

        var first = await UploadAsync(client, sessionId, payload, "one.txt", "text/plain");
        var second = await UploadAsync(client, sessionId, payload, "two.txt", "text/plain");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstDto = await BodyAsync(first);
        var secondDto = await BodyAsync(second);
        var firstId = firstDto.GetProperty("id").GetGuid();
        var secondId = secondDto.GetProperty("id").GetGuid();

        // Identical hashes prove the content store kept one file behind two rows, which is
        // precisely why deleting a row must not delete content.
        Assert.Equal(firstDto.GetProperty("content_hash").GetString(), secondDto.GetProperty("content_hash").GetString());

        var deleted = await client.DeleteAsync($"/api/v1/attachments/{firstId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/attachments/{firstId}")).StatusCode);

        var survivor = await client.GetAsync($"/api/v1/attachments/{secondId}/content");
        Assert.Equal(HttpStatusCode.OK, survivor.StatusCode);
        Assert.Equal(payload, await survivor.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Delete_OfAnUnknownId_IsNotFound()
    {
        var (client, _) = await OpenSessionAsync("missing");
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/attachments/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task AnExistingDatabaseGainsTheAttachmentTableFromBootstrap()
    {
        var databasePath = Path.Combine(_root, "legacy.db");
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        // A pre-attachment database whose messages table even predates reverted_at, so what
        // follows is reconciliation of an existing schema rather than a fresh create.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE messages (
                    id TEXT NOT NULL PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    workspace_id TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    run_id TEXT NULL,
                    turn_id TEXT NULL,
                    client_message_id TEXT NULL,
                    sequence INTEGER NOT NULL,
                    role TEXT NOT NULL,
                    content_reference TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    content_length INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
        await using (var db = new MemoryDbContext(options))
        {
            await DbContextSchemaBootstrapper.EnsureTablesAsync(db);
        }

        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('message_attachments')";
            Assert.True(Convert.ToInt32(probe.ExecuteScalar()) > 0, "message_attachments was never created");
        }
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'reverted_at'";
            Assert.Equal(1, Convert.ToInt32(probe.ExecuteScalar()));
        }

        await using (var db = new MemoryDbContext(options))
        {
            db.MessageAttachments.Add(new MessageAttachmentRecord
            {
                Id = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                FileName = "written.txt",
                MediaType = "text/plain",
                ContentReference = "content/tenants/x/y/attachment/deadbeef",
                ContentHash = "deadbeef",
                ContentLength = 8,
                CreatedByPrincipalId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            Assert.Equal(1, await db.SaveChangesAsync());
            Assert.Equal("written.txt", await db.MessageAttachments.Select(x => x.FileName).SingleAsync());
        }
    }

// ── binding: the step that makes an upload part of a conversation ──────────────

    private IMessageAttachmentStore Store() => _factory!.Services.GetRequiredService<IMessageAttachmentStore>();

    private async Task<Guid> AppendMessageAsync(HttpClient client, Guid sessionId, string content)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyAsync(response)).GetProperty("id").GetGuid();
    }
    private async Task<Guid> UploadRecordedAsync(HttpClient client, Guid sessionId, byte[] bytes, string fileName, string mediaType)
    {
        var created = await UploadAsync(client, sessionId, bytes, fileName, mediaType);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await BodyAsync(created)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> MessagesAsync(HttpClient client, Guid sessionId)
        => await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/messages");

    [Fact]
    public async Task Binding_ClaimedAttachmentRidesWithTheMessageAndNeverLeaksItsReference()
    {
        var (client, sessionId) = await OpenSessionAsync("bind-ride");
        var id = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes("line one\n"), "notes.txt", "text/plain");
        var messageId = await AppendMessageAsync(client, sessionId, "read this");

        Assert.Equal(1, await Store().BindToMessageAsync(sessionId, messageId, [id]));

        var page = await MessagesAsync(client, sessionId);
        var message = page.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == messageId);
        var attachment = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("notes.txt", attachment.GetProperty("file_name").GetString());
        Assert.Equal("text/plain", attachment.GetProperty("media_type").GetString());
        Assert.False(attachment.TryGetProperty("content_reference", out _));
        Assert.NotNull(attachment.GetProperty("bound_at").GetString());
    }

    [Fact]
    public async Task Binding_TheSameRowsToTheSameMessage_IsAnExactNoOp()
    {
        // The interrupted-send case: the message landed, the client never got the answer,
        // and the retry carries the same ids. A refusal here would make a stuck send
        // unfixable from the client side.
        var (client, sessionId) = await OpenSessionAsync("bind-idempotent");
        var id = await UploadRecordedAsync(client, sessionId, new byte[] { 1 }, "one.bin", "application/octet-stream");
        var messageId = await AppendMessageAsync(client, sessionId, "first");

        Assert.Equal(1, await Store().BindToMessageAsync(sessionId, messageId, [id]));
        var boundAt = (await Store().FindAsync(id))!.BoundAt;
        Assert.NotNull(boundAt);
        Assert.Equal(1, await Store().BindToMessageAsync(sessionId, messageId, [id]));
        Assert.Equal(boundAt, (await Store().FindAsync(id))!.BoundAt);
    }

    [Fact]
    public async Task Binding_AttachmentAlreadyCarriedByAnotherMessage_IsRefused()
    {
        var (client, sessionId) = await OpenSessionAsync("bind-taken");
        var id = await UploadRecordedAsync(client, sessionId, new byte[] { 1 }, "one.bin", "application/octet-stream");
        var first = await AppendMessageAsync(client, sessionId, "first");
        var second = await AppendMessageAsync(client, sessionId, "second");

        await Store().BindToMessageAsync(sessionId, first, [id]);
        var error = await Assert.ThrowsAsync<AttachmentBindingException>(
            () => Store().BindToMessageAsync(sessionId, second, [id]));
        Assert.Equal("attachment_already_bound", error.Code);
        Assert.NotNull((await Store().FindAsync(id))!.MessageId);
    }

    [Fact]
    public async Task Binding_ReadsAForeignSessionRow_AsMissingAndANonexistentMessage_AsNotFound()
    {
        var (clientA, sessionA) = await OpenSessionAsync("bind-scope-a");
        var (clientB, sessionB) = await OpenSessionAsync("bind-scope-b");
        var idInB = await UploadRecordedAsync(clientB, sessionB, new byte[] { 1 }, "b.bin", "application/octet-stream");
        var messageInA = await AppendMessageAsync(clientA, sessionA, "mine");

        var crossSession = await Assert.ThrowsAsync<AttachmentBindingException>(
            () => Store().BindToMessageAsync(sessionA, messageInA, [idInB]));
        Assert.Equal("attachment_not_found", crossSession.Code);

        var strayMessage = await Assert.ThrowsAsync<AttachmentBindingException>(
            () => Store().BindToMessageAsync(sessionA, Guid.NewGuid(), [idInB]));
        Assert.Equal("message_not_found", strayMessage.Code);

        // The refused claim left the row exactly where it was, in its own session.
        Assert.Null((await Store().FindAsync(idInB))!.MessageId);
        Assert.Equal(1, await Store().BindToMessageAsync(sessionB, await AppendMessageAsync(clientB, sessionB, "mine too"), [idInB]));
    }

    // ── admission contract: what a client may name when it sends ───────────────────

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Guid sessionId, object body)
        => client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", body);

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
        => (await BodyAsync(response)).GetProperty("code").GetString()!;

    [Fact]
    public async Task Interaction_RejectsAttachmentIdsThatAreNotAnArrayOfGuids()
    {
        var (client, sessionId) = await OpenSessionAsync("bind-not-array");
        var notArray = await SendAsync(client, sessionId, new
        {
            content = "hello",
            dispatch_mode = "queued",
            attachment_ids = "one-file"
        });
        Assert.Equal(HttpStatusCode.BadRequest, notArray.StatusCode);
        Assert.Equal("attachment_ids_invalid", await ErrorCodeAsync(notArray));

        var notAGuid = await SendAsync(client, sessionId, new
        {
            content = "hello",
            dispatch_mode = "queued",
            attachment_ids = new[] { "look-at-my-file" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, notAGuid.StatusCode);
        Assert.Equal("attachment_ids_invalid", await ErrorCodeAsync(notAGuid));
    }

    [Fact]
    public async Task Interaction_RejectsMoreAttachmentsThanAMessageMayCarry()
    {
        var (client, sessionId) = await OpenSessionAsync("bind-count");
        var response = await SendAsync(client, sessionId, new
        {
            content = "hello",
            dispatch_mode = "queued",
            attachment_ids = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray()
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("attachment_count_exceeded", body.GetProperty("code").GetString());
        Assert.Equal(8, body.GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task Interaction_RejectsAnIdThatIsNotAnUnboundRowOfThisSession_BeforeAnyRunExists()
    {
        var (client, sessionId) = await OpenSessionAsync("bind-unknown");
        var response = await SendAsync(client, sessionId, new
        {
            content = "hello",
            dispatch_mode = "queued",
            attachment_ids = new[] { Guid.NewGuid() }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("attachment_not_found", await ErrorCodeAsync(response));
        // A refused send may not leave a run, a directive or a message behind.
        var page = await MessagesAsync(client, sessionId);
        Assert.Equal(0, page.GetArrayLength());
    }

    [Fact]
    public async Task Interaction_SteeringCannotCarryAttachments_BecauseItAppendsNoMessage()
    {
        var (client, sessionId) = await OpenSessionAsync("bind-steering");
        var response = await SendAsync(client, sessionId, new
        {
            content = "steer",
            dispatch_mode = "insert",
            target_run_id = Guid.NewGuid(),
            attachment_ids = new[] { Guid.NewGuid() }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("attachment_dispatch_unsupported", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Interaction_WithUploadedFiles_BindsThemToTheMessageItAppends()
    {
        // The happy path over HTTP, not just the refusals: a send that names two uploaded
        // ids must end with a user message that carries both, and with a stored message
        // body that is still exactly what the user typed. The attachment text reaches the
        // model at context build time, so a content field that grew by 20 KB here would
        // mean the transcript and the prompt had silently diverged.
        var (client, sessionId) = await OpenSessionAsync("send-bind");
        var textId = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes("first file"), "one.txt", "text/plain");
        var jsonId = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes("{\"a\":1}"), "two.json", "application/json");

        var response = await SendAsync(client, sessionId, new
        {
            content = "read these",
            client_message_id = "send-bind-1",
            dispatch_mode = "queued",
            attachment_ids = new[] { textId, jsonId }
        });
        Assert.True(HttpStatusCode.Created == response.StatusCode, await response.Content.ReadAsStringAsync());

        var page = await MessagesAsync(client, sessionId);
        var message = Assert.Single(page.EnumerateArray());
        Assert.Equal("read these", message.GetProperty("content").GetString());
        var names = message.GetProperty("attachments").EnumerateArray()
            .Select(row => row.GetProperty("file_name").GetString()).Order().ToArray();
        Assert.Equal(new[] { "one.txt", "two.json" }, names);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sessions/{sessionId}/attachments");
        foreach (var row in listed.EnumerateArray())
        {
            Assert.Equal(message.GetProperty("id").GetGuid(), row.GetProperty("message_id").GetGuid());
        }
    }

    // ── model visibility ───────────────────────────────────────────────────────────

    private async Task<string> AttachmentSectionAsync(Guid sessionId)
    {
        var provider = _factory!.Services.GetRequiredService<IContextProvider>();
        var pack = await provider.BuildContextAsync(new ContextBuildRequest(sessionId.ToString(), null));
        var evidence = pack.Evidence.FirstOrDefault(item => item.Source == "session_attachments");
        Assert.NotNull(evidence);
        Assert.True(evidence!.EstimatedTokens > 0);
        return evidence.Content;
    }

    [Fact]
    public async Task Context_QuotedTextAttachmentReachesTheModelAndABinaryOneIsListedWithoutContent()
    {
        var (client, sessionId) = await OpenSessionAsync("ctx-inline");
        var textId = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes("the answer is 42"), "answer.txt", "text/plain");
        var pngId = await UploadRecordedAsync(client, sessionId, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "diagram.png", "image/png");
        var messageId = await AppendMessageAsync(client, sessionId, "what do these say?");
        Assert.Equal(2, await Store().BindToMessageAsync(sessionId, messageId, [textId, pngId]));

        var section = await AttachmentSectionAsync(sessionId);
        Assert.Contains("answer.txt", section);
        Assert.Contains("the answer is 42", section);
        Assert.Contains("diagram.png", section);
        Assert.Contains("not text", section);
        // The guard rail is part of the section, not an assumption about the model.
        Assert.Contains("not instructions to you", section);
    }

    [Fact]
    public async Task Context_ExcerptIsCappedAndTheCutIsSaidOutLoud()
    {
        var (client, sessionId) = await OpenSessionAsync("ctx-cap");
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 700));
        Assert.Equal(11200, payload.Length);
        var id = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes(payload), "big.txt", "text/plain");
        var messageId = await AppendMessageAsync(client, sessionId, "read the whole file");
        await Store().BindToMessageAsync(sessionId, messageId, [id]);

        var section = await AttachmentSectionAsync(sessionId);
        Assert.Contains("excerpt cut at the inline cap", section);
        Assert.Contains("11200 bytes", section);
        // The cap is the claim being tested: a quoted 11 KB file would be larger than the
        // whole section, so the section staying small is the pass condition.
        Assert.True(section.Length < 9_000, $"inline cap leaked: section was {section.Length} chars");
    }

    [Fact]
    public async Task Context_UnboundAttachmentDoesNotAppearAtAll()
    {
        var (client, sessionId) = await OpenSessionAsync("ctx-unbound");
        await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes("private"), "draft.txt", "text/plain");
        await AppendMessageAsync(client, sessionId, "no files here");

        var provider = _factory!.Services.GetRequiredService<IContextProvider>();
        var pack = await provider.BuildContextAsync(new ContextBuildRequest(sessionId.ToString(), null));
        Assert.Null(pack.Evidence.FirstOrDefault(item => item.Source == "session_attachments"));
        Assert.Contains("no files here", pack.Evidence.Single(item => item.Source == "session_history").Content);
    }

    // ── read_attachment: the pages the inline budget could not carry ─────────────

    /// <summary>
    /// Only the session is real here: the store scopes bytes to (tenant, workspace) itself through
    /// the ambient identity, and the tool's own promise is the narrower one - that a row from another
    /// CONVERSATION stays unreachable. Random ids elsewhere keep that distinction visible.
    /// </summary>
    private static ToolInvocationScope ScopeOf(Guid sessionId) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        sessionId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        string.Empty, ["*"], [], "frozen-config-hash", 120, 0, false);

    private static ToolWireRequestDto ReadCall(string? parameters) => new()
    {
        ToolId = CoreAttachmentReadTool.ToolId,
        ToolCallId = 1,
        Params = parameters is null ? default : JsonDocument.Parse(parameters).RootElement,
    };

    private Task<ToolWireResponseDto> ReadAsync(Guid sessionId, string parameters) =>
        CoreAttachmentReadTool.ExecuteAsync(Store(), ScopeOf(sessionId), ReadCall(parameters));

    [Fact]
    public async Task ReadTool_PagesTextPastTheInlineBudgetAndNamesWhereTheNextPageStarts()
    {
        var (client, sessionId) = await OpenSessionAsync("read-page");
        var payload = string.Concat(Enumerable.Repeat("abcdefgh", 3_000));
        Assert.Equal(24_000, payload.Length);
        var id = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes(payload), "crash.log", "text/plain");
        var messageId = await AppendMessageAsync(client, sessionId, "find the failing line");
        await Store().BindToMessageAsync(sessionId, messageId, [id]);

        var first = await ReadAsync(sessionId, $"{{\"attachment_id\":\"{id}\",\"offset\":0,\"limit\":100}}");
        Assert.True(first.IsSuccess);
        Assert.Equal(payload[..100], first.Result!.Value.GetProperty("content").GetString());
        Assert.Equal(100, first.Result!.Value.GetProperty("next_offset").GetInt32());
        Assert.True(first.Result!.Value.GetProperty("has_more").GetBoolean());
        Assert.Equal("crash.log", first.Result!.Value.GetProperty("file_name").GetString());
        // The sizes a model reasons about are labelled apart: a byte count and a character window are
        // not the same number for anything but ASCII, and this file happens to be ASCII.
        Assert.Equal(24_000, first.Result!.Value.GetProperty("content_length_bytes").GetInt64());

        var second = await ReadAsync(sessionId, $"{{\"attachment_id\":\"{id}\",\"offset\":100,\"limit\":100}}");
        Assert.Equal(payload.Substring(100, 100), second.Result!.Value.GetProperty("content").GetString());

        var tail = await ReadAsync(sessionId, $"{{\"attachment_id\":\"{id}\",\"offset\":{payload.Length - 50},\"limit\":100}}");
        Assert.Equal(50, tail.Result!.Value.GetProperty("chars_returned").GetInt32());
        Assert.False(tail.Result!.Value.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tail.Result!.Value.GetProperty("next_offset").ValueKind);
    }

    [Fact]
    public async Task ReadTool_LimitIsCappedSoOneCallCannotSpendTheWholeBudget()
    {
        var (client, sessionId) = await OpenSessionAsync("read-cap");
        var payload = new string('x', CoreAttachmentReadTool.MaxCharsPerPage * 3);
        var id = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes(payload), "huge.txt", "text/plain");
        var messageId = await AppendMessageAsync(client, sessionId, "read it all");
        await Store().BindToMessageAsync(sessionId, messageId, [id]);

        var over = await ReadAsync(sessionId,
            $"{{\"attachment_id\":\"{id}\",\"limit\":{CoreAttachmentReadTool.MaxCharsPerPage * 10}}}");

        Assert.True(over.IsSuccess);
        Assert.Equal(CoreAttachmentReadTool.MaxCharsPerPage, over.Result!.Value.GetProperty("chars_returned").GetInt32());
        Assert.True(over.Result!.Value.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task ReadTool_OffsetPastTheEndIsAnEmptyPage_NotAFailure()
    {
        var (client, sessionId) = await OpenSessionAsync("read-past");
        var id = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes("short"), "s.txt", "text/plain");
        var messageId = await AppendMessageAsync(client, sessionId, "how long is it?");
        await Store().BindToMessageAsync(sessionId, messageId, [id]);

        var response = await ReadAsync(sessionId, $"{{\"attachment_id\":\"{id}\",\"offset\":999999}}");

        Assert.True(response.IsSuccess);
        Assert.Equal(string.Empty, response.Result!.Value.GetProperty("content").GetString());
        Assert.Equal(0, response.Result!.Value.GetProperty("chars_returned").GetInt32());
        Assert.False(response.Result!.Value.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task ReadTool_NonTextAttachmentIsRefusedWithoutInventingContent()
    {
        var (client, sessionId) = await OpenSessionAsync("read-binary");
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var id = await UploadRecordedAsync(client, sessionId, bytes, "diagram.png", "image/png");
        var messageId = await AppendMessageAsync(client, sessionId, "what does this show?");
        await Store().BindToMessageAsync(sessionId, messageId, [id]);

        var response = await ReadAsync(sessionId, $"{{\"attachment_id\":\"{id}\"}}");

        Assert.False(response.IsSuccess);
        Assert.Contains("no binary content reaches a model", response.Error);
        // The temptation this refuses: hand back base64 and let the model describe an image it cannot
        // see. An error that leaked the encoding would be the bug.
        Assert.DoesNotContain(Convert.ToBase64String(bytes), response.Error);
    }

    [Fact]
    public async Task ReadTool_StaysInsideTheConversationAndRefusesAnIdWithNoHandle()
    {
        var (clientA, sessionA) = await OpenSessionAsync("read-scope-a");
        var (_, sessionB) = await OpenSessionAsync("read-scope-b");
        var idInB = await UploadRecordedAsync(clientA, sessionB, Encoding.UTF8.GetBytes("another chat's log"), "other.log", "text/plain");

        var foreign = await ReadAsync(sessionA, $"{{\"attachment_id\":\"{idInB}\"}}");

        // Same tenant, same workspace, different conversation: the store alone would have answered.
        Assert.False(foreign.IsSuccess);
        Assert.Contains("No attachment in this session", foreign.Error);

        var missing = await ReadAsync(sessionA, "{}");
        Assert.False(missing.IsSuccess);
        Assert.Contains("attachment_id", missing.Error);
    }

    [Fact]
    public async Task ReadTool_TheIdTheContextSectionAdvertisesIsTheIdTheToolAccepts()
    {
        var (client, sessionId) = await OpenSessionAsync("read-handle");
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 700));
        var id = await UploadRecordedAsync(client, sessionId, Encoding.UTF8.GetBytes(payload), "big.txt", "text/plain");
        var messageId = await AppendMessageAsync(client, sessionId, "read the whole file");
        await Store().BindToMessageAsync(sessionId, messageId, [id]);

        var section = await AttachmentSectionAsync(sessionId);
        Assert.Contains($"(id:{id}", section);
        Assert.Contains("read_attachment", section);

        // The coupling, not just the text: a section that named a different handle would leave the
        // tool unreachable while still telling the model to use it.
        var continued = await ReadAsync(sessionId, $"{{\"attachment_id\":\"{id}\",\"offset\":4096,\"limit\":16}}");
        Assert.True(continued.IsSuccess);
        Assert.Equal(payload.Substring(4_096, 16), continued.Result!.Value.GetProperty("content").GetString());
    }

    private sealed class AttachmentFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public AttachmentFactory(string root) => _root = root;

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
            // Interaction admission freezes a tool manifest before it appends anything, and
            // a real provider process is not what these tests are about: without this the
            // send path stops at TOOL_MANIFEST_WORKSPACE_UNAVAILABLE and never reaches the
            // binding under test. Same double DmaeaEndpointTests uses.
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IToolManifestSnapshotResolver, NoProviderManifestResolver>();
            });
        }
    }

    private sealed class NoProviderManifestResolver : IToolManifestSnapshotResolver
    {
        private static readonly ToolManifestSnapshot Snapshot = new(
            2,
            ToolManifestHasher.Compute(Array.Empty<FrozenToolManifestEntry>()),
            []);

        public Task<ToolManifestSnapshot> ResolveAsync(
            ToolManifestSnapshotRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }
}
