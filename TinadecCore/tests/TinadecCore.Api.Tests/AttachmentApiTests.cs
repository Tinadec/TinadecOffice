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
using TinadecCore.Memory;
using TinadecCore.Persistence;

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
        }
    }
}
