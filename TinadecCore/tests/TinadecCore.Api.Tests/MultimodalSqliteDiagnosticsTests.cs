using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Models.Harness;
using TinadecCore.Persistence;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

public sealed class MultimodalSqliteDiagnosticsTests
{
    [Fact]
    public async Task MultimodalContext_DeliversRecentHistoricalImages_AndConvertsToolVisualOutputsWithVisionFallback()
    {
        var sessionId = Guid.NewGuid();
        var turn1Msg = Guid.NewGuid();
        var turn2TriggerMsg = Guid.NewGuid();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        var draftBytes = new byte[] { 0x47, 0x49, 0x46, 0x38 };

        var priorShot = MakeAttachment(sessionId, turn1Msg, "prior-ui.png", "image/png", pngBytes, DateTimeOffset.UtcNow.AddMinutes(-3));
        var triggerShot = MakeAttachment(sessionId, turn2TriggerMsg, "current-bug.jpg", "image/jpeg", jpegBytes, DateTimeOffset.UtcNow);
        var unboundDraft = MakeAttachment(sessionId, null, "draft-pending.gif", "image/gif", draftBytes, DateTimeOffset.UtcNow);

        var store = new InMemoryAttachmentStore(
            (priorShot, pngBytes),
            (triggerShot, jpegBytes),
            (unboundDraft, draftBytes));

        var visionResolution = new ChatResolution
        {
            IsAvailable = true,
            SupportsImageInput = true,
            MaxImagesPerTurn = 4,
            MaxImageBytes = 1024 * 1024
        };

        // 1. Multi-turn image context: trigger-turn image first, then recent prior-turn image; unbound composer draft excluded.
        var multiTurn = await AttachmentImageParts.BuildWithRecentContextAsync(
            store,
            sessionId,
            turn2TriggerMsg,
            visionResolution,
            CancellationToken.None);

        var deliveredParts = multiTurn.Parts.OfType<DataContent>().ToArray();
        Assert.Equal(2, deliveredParts.Length);
        Assert.Equal("current-bug.jpg", deliveredParts[0].Name);
        Assert.Equal("image/jpeg", deliveredParts[0].MediaType);
        Assert.Equal("prior-ui.png", deliveredParts[1].Name);
        Assert.Equal("image/png", deliveredParts[1].MediaType);
        Assert.Contains("prior-ui.png", multiTurn.PromptSuffix);
        Assert.DoesNotContain("draft-pending.gif", multiTurn.PromptSuffix);

        // 2. Tool visual outputs: MCP image block + browser data URI -> DataContent when vision=true, keeping FunctionCall/FunctionResult paired.
        var mcpResultJson = JsonSerializer.Serialize(new
        {
            status = "ok",
            content = new[]
            {
                new
                {
                    type = "image",
                    mimeType = "image/png",
                    data = Convert.ToBase64String(pngBytes),
                    name = "browser-capture.png"
                }
            }
        });
        var history = new[]
        {
            new WorkerToolTurn
            {
                CallId = "call-shot-1",
                ToolId = "browser_screenshot",
                ArgumentsJson = """{"url":"http://localhost:3000"}""",
                ResultJson = mcpResultJson
            }
        };

        var visionConversation = await ExecutionAgent.BuildConversationAsync(
            "Inspect screenshot" + multiTurn.PromptSuffix,
            history,
            [],
            maxHistoryMessages: 16,
            CancellationToken.None,
            multiTurn.Parts,
            fullResults: null,
            visionResolution,
            store,
            sessionId,
            multiTurn.TotalBytes);

        // Goal(User) -> Assistant(FunctionCall) -> Tool(FunctionResult, sanitized base64) -> User(DataContent visual output)
        Assert.Equal(4, visionConversation.Count);
        Assert.Equal(ChatRole.Assistant, visionConversation[1].Role);
        Assert.Single(visionConversation[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal(ChatRole.Tool, visionConversation[2].Role);
        var toolResultText = Assert.Single(visionConversation[2].Contents.OfType<FunctionResultContent>()).Result?.ToString() ?? string.Empty;
        Assert.Contains("binary image omitted from text", toolResultText);
        Assert.DoesNotContain(Convert.ToBase64String(pngBytes), toolResultText);

        Assert.Equal(ChatRole.User, visionConversation[3].Role);
        var toolDataContent = Assert.Single(visionConversation[3].Contents.OfType<DataContent>());
        Assert.Equal("image/png", toolDataContent.MediaType);
        Assert.Equal(pngBytes, toolDataContent.Data.ToArray());

        // 3. Text-only model fallback: zero DataContent parts and explicit non-delivery warning.
        var textOnlyResolution = new ChatResolution { IsAvailable = true, SupportsImageInput = false };
        var textOnlyConversation = await ExecutionAgent.BuildConversationAsync(
            "Inspect screenshot",
            history,
            [],
            maxHistoryMessages: 16,
            CancellationToken.None,
            goalParts: null,
            fullResults: null,
            textOnlyResolution,
            store,
            sessionId);

        Assert.Empty(textOnlyConversation.SelectMany(m => m.Contents).OfType<DataContent>());
        var fallbackToolResult = Assert.Single(textOnlyConversation[2].Contents.OfType<FunctionResultContent>()).Result?.ToString() ?? string.Empty;
        Assert.Contains("NOT delivered as visual input", fallbackToolResult);
    }

    [Fact]
    public async Task WorkspaceDiagnosticsCollector_CatchesBrokenJson_ParsesCompilerErrors_AndRespectsTimeoutsAndNoOps()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tinadec-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. Broken JSON edit produces immediate file:line:col [JSON001] diagnostic.
            var brokenJsonPath = Path.Combine(tempRoot, "package.json");
            await File.WriteAllTextAsync(brokenJsonPath, "{\n  \"name\": \"demo\",\n  \"version\": \n}");
            using var writeParams = JsonDocument.Parse(JsonSerializer.Serialize(new { path = "package.json" }));
            var jsonDiags = await WorkspaceDiagnosticsCollector.Default.CollectAfterEditAsync(
                tempRoot,
                "write_file",
                mutatesWorkspace: true,
                writeParams.RootElement,
                result: null);

            var singleJsonDiag = Assert.Single(jsonDiags);
            Assert.Equal("JSON001", singleJsonDiag.Code);
            Assert.Contains("package.json:", singleJsonDiag.FormatConcise(tempRoot));

            // 2. Unrestored C# file in synthetic temp dir is a fast 0ms no-op (no false-positive build errors).
            var csPath = Path.Combine(tempRoot, "Foo.cs");
            await File.WriteAllTextAsync(csPath, "class Broken {");
            using var csParams = JsonDocument.Parse(JsonSerializer.Serialize(new { path = "Foo.cs" }));
            Assert.Empty(await WorkspaceDiagnosticsCollector.Default.CollectAfterEditAsync(
                tempRoot, "edit_file", mutatesWorkspace: true, csParams.RootElement, result: null));

            // 3. MSBuild + TypeScript compiler output parser extracts concise file:line:col items.
            var compilerOutput = """
                src/App.ts(24,9): error TS2322: Type 'number' is not assignable to type 'string'.
                Services/Worker.cs(88,15): error CS1002: ; expected [C:\repo\App.csproj]
                """;
            var parsed = WorkspaceDiagnosticsCollector.ParseCompilerOutput(compilerOutput, tempRoot);
            Assert.Equal(2, parsed.Count);
            Assert.Equal("src/App.ts:24:9 [TS2322] error: Type 'number' is not assignable to type 'string'.", parsed[0].FormatConcise(tempRoot));
            Assert.Equal("Services/Worker.cs:88:15 [CS1002] error: ; expected", parsed[1].FormatConcise(tempRoot));

            // 4. Slow custom probe times out cleanly without throwing when caller token is active.
            var slowCollector = new WorkspaceDiagnosticsCollector(
                TimeSpan.FromMilliseconds(80),
                async (_, ct) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return [new WorkspaceDiagnosticItem(csPath, 1, 1, "error", "SLOW01", "never")];
                });
            Assert.Empty(await slowCollector.CollectAfterEditAsync(
                tempRoot, "edit_file", mutatesWorkspace: true, csParams.RootElement, result: null));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SqliteWalAndBusyTimeout_EliminateConcurrentReadWriteLocks_AndCliProbeSurfacesExplicitTimeout()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tinadec-sqlite-wal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "concurrent.db");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TinadecPersistence:Enabled"] = "true",
                    ["TinadecPersistence:Provider"] = "Sqlite",
                    ["TinadecPersistence:Sqlite:DatabasePath"] = dbPath,
                    ["TinadecPersistence:Sqlite:BusyTimeoutMs"] = "15000",
                    ["TinadecPersistence:Sqlite:EnableWal"] = "true",
                    ["TinadecPersistence:DataRoot"] = Path.Combine(tempRoot, "data")
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddTinadecPersistence(config, tempRoot);
            services.AddDbContextFactory<WalProbeDbContext>((sp, opt) => opt.UseTinadecDatabase(sp));

            await using (var provider = services.BuildServiceProvider())
            {
                var factory = provider.GetRequiredService<IDbContextFactory<WalProbeDbContext>>();
                await using (var setup = await factory.CreateDbContextAsync())
                {
                    await setup.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS kv_items (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);");
                    var journalMode = await setup.Database.SqlQueryRaw<string>("SELECT journal_mode AS \"Value\" FROM pragma_journal_mode").SingleAsync();
                    var busyTimeout = await setup.Database.SqlQueryRaw<int>("SELECT timeout AS \"Value\" FROM pragma_busy_timeout").SingleAsync();
                    Assert.Equal("wal", journalMode, ignoreCase: true);
                    Assert.Equal(15_000, busyTimeout);
                }

                // Hammer 12 concurrent writers + readers against the same SQLite file: zero SQLITE_BUSY errors!
                var tasks = Enumerable.Range(0, 12).Select(async idx =>
                {
                    await using var db = await factory.CreateDbContextAsync();
                    if (idx % 2 == 0)
                    {
                        await db.Database.ExecuteSqlRawAsync("INSERT INTO kv_items (name) VALUES ({0});", $"item-{idx}");
                    }
                    var count = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM kv_items").SingleAsync();
                    Assert.True(count >= 0);
                });
                await Task.WhenAll(tasks);
            }

            // CLI probe timeout test: stalling TCP listener triggers explicit timeout message instead of spawning missing binary!
            using var stallingListener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            stallingListener.Start();
            var port = ((System.Net.IPEndPoint)stallingListener.LocalEndpoint).Port;
            using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acceptLoop = Task.Run(async () =>
            {
                try
                {
                    using var socket = await stallingListener.AcceptTcpClientAsync(acceptCts.Token);
                    await Task.Delay(TimeSpan.FromSeconds(2), acceptCts.Token);
                }
                catch { }
            });

            await using var manager = new OpencodeServeProcessManager(
                NullLogger<OpencodeServeProcessManager>.Instance,
                TimeSpan.FromMilliseconds(150));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.EnsureRunningAsync(new OpencodeServeConfig(
                    BinaryPath: Path.Combine(tempRoot, "missing-opencode.exe"),
                    ServerUrl: $"http://127.0.0.1:{port}")));

            Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("could not be started", ex.Message, StringComparison.OrdinalIgnoreCase);
            acceptCts.Cancel();
            await acceptLoop;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class WalProbeDbContext(DbContextOptions<WalProbeDbContext> options) : DbContext(options);

    private static StoredAttachment MakeAttachment(
        Guid sessionId,
        Guid? messageId,
        string fileName,
        string mediaType,
        byte[] bytes,
        DateTimeOffset createdAt) => new(
        Guid.NewGuid(),
        sessionId,
        messageId,
        fileName,
        mediaType,
        Convert.ToHexStringLower(SHA256.HashData(bytes)),
        bytes.Length,
        createdAt,
        null)
    {
        Origin = "composer",
        DeliveredToModel = false
    };

    private sealed class InMemoryAttachmentStore(params (StoredAttachment Row, byte[] Bytes)[] items) : IMessageAttachmentStore
    {
        public Task<StoredAttachment?> StoreAsync(StoreAttachmentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredAttachment>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredAttachment>>(items.Where(x => x.Row.SessionId == sessionId).Select(x => x.Row).ToArray());

        public Task<StoredAttachment?> FindAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.Where(x => x.Row.Id == attachmentId).Select(x => (StoredAttachment?)x.Row).SingleOrDefault());

        public Task<Stream?> OpenContentAsync(StoredAttachment attachment, CancellationToken cancellationToken = default)
        {
            var found = items.FirstOrDefault(x => x.Row.Id == attachment.Id);
            return Task.FromResult<Stream?>(found.Row is null ? null : new MemoryStream(found.Bytes, writable: false));
        }

        public Task<bool> DeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> BindToMessageAsync(Guid sessionId, Guid messageId, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task MarkDeliveredAsync(IReadOnlyList<Guid> attachmentIds, Guid runId, DateTimeOffset deliveredAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
