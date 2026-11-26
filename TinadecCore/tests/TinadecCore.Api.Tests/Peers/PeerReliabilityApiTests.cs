using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AspNetCore.Endpoints;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Peers;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests.Peers;

public sealed class PeerReliabilityApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _controllerRoot = ApiTestStorage.CreateRoot("peer-reliability-ctrl");
    private readonly string _workerRoot = ApiTestStorage.CreateRoot("peer-reliability-worker");
    private readonly RecordingAdmissionGate _admissionRecorder = new();
    private PeerReliabilityTestFactory? _controllerFactory;
    private PeerReliabilityTestFactory? _workerFactory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_controllerRoot);
        Directory.CreateDirectory(_workerRoot);
        _controllerFactory = new PeerReliabilityTestFactory(_controllerRoot, admissionGate: null);
        _workerFactory = new PeerReliabilityTestFactory(_workerRoot, admissionGate: _admissionRecorder);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _controllerFactory?.Dispose();
        _workerFactory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_controllerRoot))
            Directory.Delete(_controllerRoot, recursive: true);
        if (Directory.Exists(_workerRoot))
            Directory.Delete(_workerRoot, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MultiWriterConcurrentSubmissions_ExecuteInStrictFifoOrder_WithZeroOverlapAndOriginAttribution()
    {
        using var workerClient = _workerFactory!.CreateClient();
        var (_, sessionId, _) = await CreateProjectAndSessionAsync(workerClient, _workerRoot, "fifo-ws");

        // Configure a deterministic 60ms delay inside SubmitAsync on the first admitted turn so concurrent
        // submissions pile up in peer_ingress_queue and prove strict non-overlapping FIFO execution.
        _admissionRecorder.Reset(firstSubmissionDelay: TimeSpan.FromMilliseconds(60));

        HttpRequestMessage BuildSubmit(string clientReqId, string content, string? peerId, string? peerName)
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/interactions")
            {
                Content = JsonContent.Create(new
                {
                    content,
                    client_request_id = clientReqId,
                    client_message_id = clientReqId,
                    dispatch_mode = "parallel"
                })
            };
            msg.Headers.Add("x-tinadec-client-request-id", clientReqId);
            if (peerId is not null)
                msg.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, peerId);
            if (peerName is not null)
                msg.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, peerName);
            return msg;
        }

        // Fire 4 concurrent submissions (2 remote controllers + 2 local writers) simultaneously
        var specs = new (string ReqId, string Content, string? PeerId, string? PeerName)[]
        {
            ("fifo-req-alpha", "Turn from Mac Studio", "ctrl-mac-studio", "Mac Studio"),
            ("fifo-req-beta", "Turn from local Worker keyboard", null, null),
            ("fifo-req-gamma", "Turn from Linux Laptop", "ctrl-thinkpad", "ThinkPad X1"),
            ("fifo-req-delta", "Second local turn", "local", "Worker Console")
        };

        var tasks = specs.Select(async spec =>
        {
            using var req = BuildSubmit(spec.ReqId, spec.Content, spec.PeerId, spec.PeerName);
            var resp = await workerClient.SendAsync(req);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return (resp.StatusCode, Body: body);
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        foreach (var item in results)
        {
            Assert.Equal(HttpStatusCode.Created, item.StatusCode);
            Assert.False(item.Body.GetProperty("idempotent_replay").GetBoolean());
            Assert.Equal("processed", item.Body.GetProperty("stage").GetString());
        }

        // Verify SQLite peer_ingress_queue assigned contiguous monotonic enqueued_seq 1..4
        var dbFactory = _workerFactory.Services.GetRequiredService<IDbContextFactory<PeerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var queueRows = await db.IngressQueue.AsNoTracking()
            .Where(x => x.SessionId == sessionId.ToString())
            .OrderBy(x => x.EnqueuedSeq)
            .ToListAsync();

        Assert.Equal(4, queueRows.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, queueRows.Select(r => r.EnqueuedSeq).ToArray());
        Assert.All(queueRows, r =>
        {
            Assert.Equal("processed", r.Stage);
            Assert.NotNull(r.RunId);
            Assert.NotNull(r.TurnId);
        });

        // Verify origin_device_id attribution: local writers MUST persist "" (empty string), remote peers persist controller_device_id
        var byReqId = queueRows.ToDictionary(r => r.ClientRequestId, StringComparer.Ordinal);
        Assert.Equal("ctrl-mac-studio", byReqId["fifo-req-alpha"].OriginDeviceId);
        Assert.Equal("Mac Studio", byReqId["fifo-req-alpha"].OriginDisplayName);
        Assert.Equal(string.Empty, byReqId["fifo-req-beta"].OriginDeviceId);
        Assert.Equal("ctrl-thinkpad", byReqId["fifo-req-gamma"].OriginDeviceId);
        Assert.Equal(string.Empty, byReqId["fifo-req-delta"].OriginDeviceId);

        // Verify coordinator admission executed in EXACT ascending enqueued_seq order with ZERO concurrency overlap
        Assert.Equal(0, _admissionRecorder.MaxObservedConcurrency);
        Assert.Equal(4, _admissionRecorder.ExecutionOrderByClientMessageId.Count);
        Assert.Equal(
            queueRows.Select(r => r.ClientRequestId).ToArray(),
            _admissionRecorder.ExecutionOrderByClientMessageId.ToArray());
    }

    [Fact]
    public async Task DuplicateClientRequestId_ConcurrentAndSequentialRetries_SpawnExactlyOneRunAndTurn()
    {
        using var workerClient = _workerFactory!.CreateClient();
        var (_, sessionId, _) = await CreateProjectAndSessionAsync(workerClient, _workerRoot, "idem-ws");

        _admissionRecorder.Reset(firstSubmissionDelay: TimeSpan.FromMilliseconds(50));
        const string duplicateReqId = "req-idempotent-777";

        HttpRequestMessage BuildDuplicateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/interactions")
            {
                Content = JsonContent.Create(new
                {
                    content = "Run integration suite once only",
                    client_request_id = duplicateReqId,
                    client_message_id = duplicateReqId,
                    dispatch_mode = "queued"
                })
            };
            req.Headers.Add("x-tinadec-client-request-id", duplicateReqId);
            req.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, "ctrl-dev-laptop");
            req.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, "Dev Laptop");
            return req;
        }

        // 5 simultaneous concurrent requests carrying the identical client_request_id
        var concurrentResponses = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var req = BuildDuplicateRequest();
            var resp = await workerClient.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode);
            return await resp.Content.ReadFromJsonAsync<JsonElement>();
        }));

        // Plus 2 sequential post-completion retries (e.g. network retry after lost HTTP response)
        using var retry1Req = BuildDuplicateRequest();
        var retry1Resp = await workerClient.SendAsync(retry1Req);
        Assert.True(retry1Resp.IsSuccessStatusCode);
        var retry1Body = await retry1Resp.Content.ReadFromJsonAsync<JsonElement>();

        using var retry2Req = BuildDuplicateRequest();
        var retry2Resp = await workerClient.SendAsync(retry2Req);
        Assert.True(retry2Resp.IsSuccessStatusCode);
        var retry2Body = await retry2Resp.Content.ReadFromJsonAsync<JsonElement>();

        var allBodies = concurrentResponses.Concat([retry1Body, retry2Body]).ToArray();

        // Exactly 1 primary execution (idempotent_replay = false) and 6 idempotent replays (idempotent_replay = true)
        Assert.Single(allBodies, b => !b.GetProperty("idempotent_replay").GetBoolean());
        Assert.Equal(6, allBodies.Count(b => b.GetProperty("idempotent_replay").GetBoolean()));

        var distinctRunIds = allBodies.Select(b => b.GetProperty("run_id").GetString()).Distinct().ToArray();
        var distinctTurnIds = allBodies.Select(b => b.GetProperty("turn_id").GetString()).Distinct().ToArray();
        var distinctSeqs = allBodies.Select(b => b.GetProperty("enqueued_seq").GetInt64()).Distinct().ToArray();

        Assert.Single(distinctRunIds);
        Assert.Single(distinctTurnIds);
        Assert.Equal(new long[] { 1L }, distinctSeqs);

        // Verify coordinator SubmitAsync was invoked ONCE, 1 Run exists, and 1 user message exists
        Assert.Single(_admissionRecorder.ExecutionOrderByClientMessageId);
        var runs = await workerClient.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/runs");
        Assert.Single(runs!);
        var messages = await workerClient.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/messages");
        Assert.Single(messages!, m => m.GetProperty("role").GetString() == "user");
    }

    [Fact]
    public async Task IngressStatusReconciliation_RecoversSubmissionUncertain_SupportsAfterSeqCatchUp_AndObserverGuard()
    {
        using var workerClient = _workerFactory!.CreateClient();
        var (_, sessionId, _) = await CreateProjectAndSessionAsync(workerClient, _workerRoot, "reconcile-ws");
        _admissionRecorder.Reset(firstSubmissionDelay: TimeSpan.Zero);

        // 0. Observer role cannot submit interactions -> 403 policy_denied
        using (var observerPost = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/interactions")
        {
            Content = JsonContent.Create(new { content = "Forbidden observer write", client_request_id = "obs-1", dispatch_mode = "queued" })
        })
        {
            observerPost.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, "peer-observer");
            observerPost.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, "observer");
            var obsResp = await workerClient.SendAsync(observerPost);
            Assert.Equal(HttpStatusCode.Forbidden, obsResp.StatusCode);
            Assert.Equal(PeerErrorCodes.PolicyDenied, (await obsResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // 1. Unknown client_request_id returns 200 { found: false } so UI knows it never reached Worker and enables safe resend
        var missingStatus = await workerClient.GetFromJsonAsync<PeerIngressStatusDto>(
            $"/api/v1/sessions/{sessionId}/ingress/never-arrived-req-404");
        Assert.NotNull(missingStatus);
        Assert.False(missingStatus!.Found);
        Assert.Equal("never-arrived-req-404", missingStatus.ClientRequestId);

        // 2. Admitted request whose response was "lost in transit" -> controller reconciles via GET .../ingress/:clientRequestId?after_seq=
        const string uncertainReqId = "req-uncertain-dropped-ack";
        var submitResp = await workerClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "Analyze memory footprint",
            client_request_id = uncertainReqId,
            client_message_id = uncertainReqId,
            dispatch_mode = "queued"
        });
        Assert.Equal(HttpStatusCode.Created, submitResp.StatusCode);
        var admittedBody = await submitResp.Content.ReadFromJsonAsync<JsonElement>();
        var runId = admittedBody.GetProperty("run_id").GetGuid();
        var turnId = admittedBody.GetProperty("turn_id").GetString();

        // Append sequenced durable events on the run so we can verify after_seq delta catch-up
        var lifecycle = _workerFactory.Services.GetRequiredService<StorageLifecycleService>();
        await lifecycle.AppendEventAsync(runId, "peer.test.step1", new { step = 1 }, "Step 1 completed", "info");
        await lifecycle.AppendEventAsync(runId, "peer.test.step2", new { step = 2 }, "Step 2 completed", "info");

        var fullCatchUp = await workerClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/sessions/{sessionId}/ingress/{uncertainReqId}?after_seq=0");
        Assert.True(fullCatchUp.GetProperty("found").GetBoolean());
        Assert.Equal("processed", fullCatchUp.GetProperty("stage").GetString());
        Assert.Equal(runId.ToString(), fullCatchUp.GetProperty("run_id").GetString());
        Assert.Equal(turnId, fullCatchUp.GetProperty("turn_id").GetString());
        var highWaterSeq = fullCatchUp.GetProperty("last_event_seq").GetInt64();
        Assert.True(highWaterSeq >= 2);
        var allEvents = fullCatchUp.GetProperty("events").EnumerateArray().ToArray();
        Assert.True(allEvents.Length >= 2);

        // Append one more event after highWaterSeq and request delta ?after_seq=<highWaterSeq>
        await lifecycle.AppendEventAsync(runId, "peer.test.step3", new { step = 3 }, "Step 3 completed", "info");
        var deltaCatchUp = await workerClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/sessions/{sessionId}/ingress/{uncertainReqId}?after_seq={highWaterSeq}");
        var deltaEvents = deltaCatchUp.GetProperty("events").EnumerateArray().ToArray();
        Assert.Contains(deltaEvents, e => e.GetProperty("event_type").GetString() == "peer.test.step3");
        Assert.DoesNotContain(deltaEvents, e => e.GetProperty("event_type").GetString() == "peer.test.step1");

        // 3. Rejected request records stage='rejected' + reject_error_code, and allows explicit user retry
        const string rejectedReqId = "req-stale-settings-rev";
        var staleResp = await workerClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "With wrong settings revision",
            client_request_id = rejectedReqId,
            expected_settings_revision = 999,
            dispatch_mode = "queued"
        });
        Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);

        var rejectedStatus = await workerClient.GetFromJsonAsync<PeerIngressStatusDto>(
            $"/api/v1/sessions/{sessionId}/ingress/{rejectedReqId}");
        Assert.NotNull(rejectedStatus);
        Assert.True(rejectedStatus!.Found);
        Assert.Equal("rejected", rejectedStatus.Stage);
        Assert.Equal("session_settings_conflict", rejectedStatus.RejectErrorCode);

        // Explicit user retry after refreshing settings succeeds and transitions to 'processed'
        var retriedResp = await workerClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "With refreshed settings revision",
            client_request_id = rejectedReqId,
            dispatch_mode = "parallel"
        });
        Assert.Equal(HttpStatusCode.Created, retriedResp.StatusCode);
        var recoveredStatus = await workerClient.GetFromJsonAsync<PeerIngressStatusDto>(
            $"/api/v1/sessions/{sessionId}/ingress/{rejectedReqId}");
        Assert.Equal("processed", recoveredStatus!.Stage);
        Assert.Null(recoveredStatus.RejectErrorCode);
    }

    [Fact]
    public async Task ShadowSessions_SupportOfflineReadAndNullUncertainTurnDigest_AndClearLocalCacheNeverTouchesWorkerOrPairing()
    {
        using var ctrlClient = _controllerFactory!.CreateClient();
        using var workerClient = _workerFactory!.CreateClient();

        // Seed real project + session + message on Worker B
        var (workerProjectId, workerSessionId, _) = await CreateProjectAndSessionAsync(workerClient, _workerRoot, "worker-truth-ws");
        var workerMsgResp = await workerClient.PostAsJsonAsync($"/api/v1/sessions/{workerSessionId}/messages", new
        {
            content = "Authoritative message stored on Worker B disk"
        });
        Assert.Equal(HttpStatusCode.Created, workerMsgResp.StatusCode);

        // Pair Worker B in Controller A's local peer_devices directory
        var fp = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var pairResp = await ctrlClient.PostAsJsonAsync("/api/v1/peers/devices/pair", new
        {
            remote_device_id = "worker-b-node",
            display_name = "Worker B Build Machine",
            transport = "lan",
            address = "192.168.10.22:48735",
            certificate_fingerprint = fp,
            expected_fingerprint = fp,
            protocol_major = 1,
            protocol_minor = 0,
            last_state = "disconnected"
        });
        Assert.Equal(HttpStatusCode.OK, pairResp.StatusCode);
        var pairedDevice = (await pairResp.Content.ReadFromJsonAsync<PeerDeviceDto>())!;

        // Sync workspaces + shadow session + 1 confirmed turn + 1 uncertain unconfirmed turn (digest_json = null)
        var syncResp = await ctrlClient.PostAsJsonAsync($"/api/v1/peers/devices/{pairedDevice.DeviceId}/shadow/sync", new
        {
            workspaces = new[]
            {
                new
                {
                    remote_project_id = workerProjectId.ToString(),
                    name = "Worker Truth Workspace",
                    remote_root_path = "/srv/worker-truth",
                    visible = true
                }
            },
            sessions = new[]
            {
                new
                {
                    remote_session_id = workerSessionId.ToString(),
                    remote_project_id = workerProjectId.ToString(),
                    title = "Cross-Device Architecture Review",
                    run_status = "awaiting_approval",
                    initiator_device_id = "ctrl-a-node",
                    last_event_seq = 24L,
                    turns = new object[]
                    {
                        new
                        {
                            turn_id = "turn-confirmed-1",
                            digest = new
                            {
                                user_content = "Check build logs",
                                assistant_content = "Build succeeded with 0 warnings."
                            }
                        },
                        new
                        {
                            turn_id = "turn-uncertain-2",
                            digest_json = (string?)null
                        }
                    }
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, syncResp.StatusCode);
        var syncResult = (await syncResp.Content.ReadFromJsonAsync<PeerShadowSyncResultDto>())!;
        Assert.Equal(1, syncResult.SyncedWorkspaces);
        Assert.Equal(1, syncResult.SyncedSessions);
        Assert.Equal(2, syncResult.SyncedTurns);

        // Read shadow session list while offline (filtered by project_id)
        var listShadow = await ctrlClient.GetFromJsonAsync<PeerShadowSessionListDto>(
            $"/api/v1/peers/devices/{pairedDevice.DeviceId}/shadow/sessions?project_id={workerProjectId}");
        Assert.NotNull(listShadow);
        var shadowSummary = Assert.Single(listShadow!.Sessions);
        Assert.Equal(workerSessionId.ToString(), shadowSummary.RemoteSessionId);
        Assert.Equal(24L, shadowSummary.LastEventSeq);
        Assert.Equal("awaiting_approval", shadowSummary.RunStatus);

        // Read shadow session detail including turns & synthesized message previews
        var shadowDetail = await ctrlClient.GetFromJsonAsync<PeerShadowSessionDetailDto>(
            $"/api/v1/peers/devices/{pairedDevice.DeviceId}/shadow/sessions/{workerSessionId}");
        Assert.NotNull(shadowDetail);
        Assert.Equal(2, shadowDetail!.Turns.Count);
        Assert.NotNull(shadowDetail.Turns.Single(t => t.TurnId == "turn-confirmed-1").DigestJson);
        Assert.Null(shadowDetail.Turns.Single(t => t.TurnId == "turn-uncertain-2").DigestJson);
        Assert.Contains(shadowDetail.Messages, m => m.TurnId == "turn-uncertain-2" && m.PendingConfirmation);

        // Clear Controller A local shadow cache via DELETE /api/v1/peers/devices/:deviceId/local-cache
        var clearResp = await ctrlClient.DeleteAsync($"/api/v1/peers/devices/{pairedDevice.DeviceId}/local-cache");
        Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);
        var clearJson = await clearResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(clearJson.GetProperty("cleared").GetBoolean());
        Assert.Equal(4, clearJson.GetProperty("removed_entries").GetInt32()); // 2 turns + 1 session + 1 workspace

        // 1. Controller A local shadow tables are now empty
        var afterClearList = await ctrlClient.GetFromJsonAsync<PeerShadowSessionListDto>(
            $"/api/v1/peers/devices/{pairedDevice.DeviceId}/shadow/sessions");
        Assert.Empty(afterClearList!.Sessions);
        var afterClearWs = await ctrlClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/peers/devices/{pairedDevice.DeviceId}/workspaces");
        Assert.Empty(afterClearWs.GetProperty("workspaces").EnumerateArray());

        // 2. Controller A peer_devices pairing record is 100% preserved!
        var devicesAfterClear = await ctrlClient.GetFromJsonAsync<JsonElement>("/api/v1/peers/devices");
        var preservedDevice = Assert.Single(devicesAfterClear.GetProperty("devices").EnumerateArray());
        Assert.Equal(pairedDevice.DeviceId, preservedDevice.GetProperty("device_id").GetString());
        Assert.Equal(fp, preservedDevice.GetProperty("certificate_fingerprint").GetString());

        // 3. Worker B authoritative session & messages are 100% untouched!
        var workerMsgsAfter = await workerClient.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{workerSessionId}/messages");
        Assert.Single(workerMsgsAfter!);
        Assert.Equal("Authoritative message stored on Worker B disk", workerMsgsAfter![0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task StagedAttachments_AssembleMultiChunkJsonAndBinary_AndEnforceWorkspaceSourcePathZeroCopy()
    {
        using var workerClient = _workerFactory!.CreateClient();
        var (projectId, sessionId, wsPath) = await CreateProjectAndSessionAsync(workerClient, _workerRoot, "stage-ws");
        _admissionRecorder.Reset(firstSubmissionDelay: TimeSpan.Zero);

        // --- Part 1: Multi-chunk (> 256KiB) JSON base64 upload out-of-order + SHA-256 verification ---
        var largeBytes = RandomNumberGenerator.GetBytes(600 * 1024); // 600 KiB
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(largeBytes)).ToLowerInvariant();
        var chunkSize = 256 * 1024;
        var chunks = new[]
        {
            largeBytes[..chunkSize],
            largeBytes[chunkSize..(chunkSize * 2)],
            largeBytes[(chunkSize * 2)..]
        };
        var jsonStageId = $"stage_json_{Guid.NewGuid():N}";

        // Upload chunk 1, then chunk 0, then chunk 2 (out-of-order resilience)
        foreach (var (idx, expectComplete) in new[] { (1, false), (0, false), (2, true) })
        {
            var stageResp = await workerClient.PostAsJsonAsync("/api/v1/attachments/stage", new
            {
                stage_id = jsonStageId,
                session_id = sessionId.ToString(),
                project_id = projectId.ToString(),
                filename = "screenshot-capture.png",
                media_type = "image/png",
                total_bytes = largeBytes.Length,
                sha256_hex = expectedSha256,
                chunk_index = idx,
                total_chunks = 3,
                chunk_base64 = Convert.ToBase64String(chunks[idx]),
                origin = "composer"
            });
            Assert.Equal(HttpStatusCode.OK, stageResp.StatusCode);
            var stageDto = (await stageResp.Content.ReadFromJsonAsync<PeerAttachmentStageResultDto>(SnakeJson))!;
            Assert.Equal(jsonStageId, stageDto.StageId);
            Assert.Equal(expectComplete, stageDto.Complete);
            Assert.False(stageDto.ZeroCopy);

            if (expectComplete)
            {
                Assert.NotNull(stageDto.AttachmentId);
                Assert.NotNull(stageDto.Attachment);
                Assert.Equal(expectedSha256, stageDto.Attachment!.ContentHash);
                Assert.Equal(largeBytes.Length, stageDto.Attachment.ContentLength);

                // Verify downloaded content matches original 600 KiB byte-for-byte
                var downloaded = await workerClient.GetByteArrayAsync($"/api/v1/attachments/{stageDto.AttachmentId}/content");
                Assert.Equal(largeBytes, downloaded);
            }
        }

        // Bind staged attachment to an interaction via staged_attachment_ids
        var sendStagedResp = await workerClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "Please inspect this staged screenshot",
            client_request_id = "req-with-staged-json",
            staged_attachment_ids = new[] { jsonStageId },
            dispatch_mode = "parallel"
        });
        Assert.Equal(HttpStatusCode.Created, sendStagedResp.StatusCode);

        // --- Part 2: Binary application/octet-stream chunk mode (materialized on /interactions send) ---
        var binPayload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("binary-chunk-line-0123456789\n", 4000)));
        var half = binPayload.Length / 2;
        var binStageId = $"stage_bin_{Guid.NewGuid():N}";

        for (var idx = 0; idx < 2; idx++)
        {
            var slice = idx == 0 ? binPayload[..half] : binPayload[half..];
            using var binReq = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/attachments/stage?stage_id={binStageId}&filename=build.log&media_type=text/plain&chunk_index={idx}&total_chunks=2")
            {
                Content = new ByteArrayContent(slice)
            };
            binReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var binResp = await workerClient.SendAsync(binReq);
            Assert.Equal(HttpStatusCode.OK, binResp.StatusCode);
            var binDto = (await binResp.Content.ReadFromJsonAsync<PeerAttachmentStageResultDto>())!;
            Assert.Equal(idx == 1, binDto.Complete);
        }

        var bindBinResp = await workerClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "Review binary staged log",
            client_request_id = "req-with-staged-bin",
            attachment_ids = new[] { binStageId },
            dispatch_mode = "parallel"
        });
        Assert.Equal(HttpStatusCode.Created, bindBinResp.StatusCode);

        // --- Part 3: Workspace source_path zero-copy reference & unauthorized outside-workspace block ---
        var authorizedFile = Path.Combine(wsPath, "architecture-spec.md");
        var wsFileContent = new string('#', 512 * 1024); // 512 KiB workspace file
        await File.WriteAllTextAsync(authorizedFile, wsFileContent, Encoding.UTF8);

        var outsideDir = Path.Combine(_workerRoot, "outside-secret-dir");
        Directory.CreateDirectory(outsideDir);
        var unauthorizedFile = Path.Combine(outsideDir, "secrets.txt");
        await File.WriteAllTextAsync(unauthorizedFile, "top-secret", Encoding.UTF8);

        // Unauthorized path outside workspace MUST fail with 400 attachment_source_unauthorized
        var unauthStageResp = await workerClient.PostAsJsonAsync("/api/v1/attachments/stage", new
        {
            session_id = sessionId.ToString(),
            project_id = projectId.ToString(),
            filename = "secrets.txt",
            media_type = "text/plain",
            origin = "workspace",
            source_path = unauthorizedFile
        });
        Assert.Equal(HttpStatusCode.BadRequest, unauthStageResp.StatusCode);
        Assert.Equal("attachment_source_unauthorized", (await unauthStageResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Measure worker ContentStore disk footprint BEFORE zero-copy workspace staging
        var dataDir = Path.Combine(_workerRoot, "data");
        var bytesBeforeWorkspaceStage = GetDirectoryTotalBytes(dataDir);

        var wsStageResp = await workerClient.PostAsJsonAsync("/api/v1/attachments/stage", new
        {
            stage_id = "stage_ws_zerocopy_1",
            session_id = sessionId.ToString(),
            project_id = projectId.ToString(),
            filename = "architecture-spec.md",
            media_type = "text/markdown",
            origin = "workspace",
            source_path = authorizedFile,
            chunk_index = 0,
            total_chunks = 1
        });
        Assert.Equal(HttpStatusCode.OK, wsStageResp.StatusCode);
        var wsStageDto = (await wsStageResp.Content.ReadFromJsonAsync<PeerAttachmentStageResultDto>())!;
        Assert.True(wsStageDto.Complete);
        Assert.True(wsStageDto.ZeroCopy);
        Assert.Equal("workspace", wsStageDto.Origin);
        Assert.Null(wsStageDto.AttachmentId);
        Assert.Equal(Path.GetFullPath(authorizedFile), wsStageDto.SourcePath);

        // ContentStore bytes MUST NOT grow by the 512 KiB workspace file during staging!
        var bytesAfterWorkspaceStage = GetDirectoryTotalBytes(dataDir);
        Assert.True(bytesAfterWorkspaceStage - bytesBeforeWorkspaceStage < 4096);

        // Reference zero-copy workspace stage_id in interaction submission
        var wsInteractResp = await workerClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new
        {
            content = "Summarize this workspace spec",
            client_request_id = "req-with-ws-zerocopy",
            staged_attachment_ids = new[] { wsStageDto.StageId },
            dispatch_mode = "parallel"
        });
        Assert.Equal(HttpStatusCode.Created, wsInteractResp.StatusCode);
        var wsInteractJson = await wsInteractResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            wsInteractJson.GetProperty("workspace_references").EnumerateArray(),
            r => r.GetProperty("stage_id").GetString() == "stage_ws_zerocopy_1");

        // Even after interaction admission, NO attachment row/blob was created for the zero-copy workspace file
        // (only the 2 composer uploads from Parts 1 & 2 exist in session attachments).
        var sessionAttachments = await workerClient.GetFromJsonAsync<JsonElement[]>($"/api/v1/sessions/{sessionId}/attachments");
        Assert.Equal(2, sessionAttachments!.Length);
        Assert.DoesNotContain(sessionAttachments, a => a.GetProperty("file_name").GetString() == "architecture-spec.md");
    }

    private static long GetDirectoryTotalBytes(string dir)
    {
        if (!Directory.Exists(dir)) return 0L;
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    private static async Task<(Guid ProjectId, Guid SessionId, string WorkspacePath)> CreateProjectAndSessionAsync(
        HttpClient client,
        string root,
        string prefix)
    {
        var wsPath = Path.Combine(root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(wsPath);

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = prefix, path = wsPath });
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var sessResp = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId.ToString(), title = $"{prefix}-session" });
        Assert.Equal(HttpStatusCode.Created, sessResp.StatusCode);
        var sessionId = (await sessResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        return (projectId, sessionId, wsPath);
    }

    private sealed class RecordingAdmissionGate
    {
        private TimeSpan _firstDelay = TimeSpan.Zero;
        private int _callIndex;
        private int _currentActive;
        private int _maxConcurrentOverlap;
        public ConcurrentQueue<string> ExecutionOrderByClientMessageId { get; } = new();

        public int MaxObservedConcurrency => Volatile.Read(ref _maxConcurrentOverlap);

        public void Reset(TimeSpan firstSubmissionDelay)
        {
            _firstDelay = firstSubmissionDelay;
            Interlocked.Exchange(ref _callIndex, 0);
            Interlocked.Exchange(ref _currentActive, 0);
            Interlocked.Exchange(ref _maxConcurrentOverlap, 0);
            while (ExecutionOrderByClientMessageId.TryDequeue(out _)) { }
        }

        public async Task OnBeforeSubmitAsync(FullDuplexInvocation invocation, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _currentActive);
            if (active > 1)
            {
                Interlocked.Exchange(ref _maxConcurrentOverlap, active);
            }

            ExecutionOrderByClientMessageId.Enqueue(invocation.ClientMessageId ?? string.Empty);
            var idx = Interlocked.Increment(ref _callIndex);
            if (idx == 1 && _firstDelay > TimeSpan.Zero)
            {
                await Task.Delay(_firstDelay, ct).ConfigureAwait(false);
            }
        }

        public void OnAfterSubmit()
        {
            Interlocked.Decrement(ref _currentActive);
        }
    }

    private sealed class InstrumentedRunCoordinator(
        IFullDuplexRunCoordinator inner,
        RecordingAdmissionGate gate) : IFullDuplexRunCoordinator
    {
        public async Task<RunSubmission> SubmitAsync(FullDuplexInvocation invocation, CancellationToken cancellationToken = default)
        {
            await gate.OnBeforeSubmitAsync(invocation, cancellationToken).ConfigureAwait(false);
            try
            {
                return await inner.SubmitAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.OnAfterSubmit();
            }
        }

        public IAsyncEnumerable<RunStreamChunk> FollowAsync(
            Guid runId,
            Guid? turnId = null,
            long afterSequence = 0,
            CancellationToken cancellationToken = default) =>
            inner.FollowAsync(runId, turnId, afterSequence, cancellationToken);

        public Task<RunControlResult> ControlAsync(Guid runId, RunControlCommand command, CancellationToken cancellationToken = default) =>
            inner.ControlAsync(runId, command, cancellationToken);
    }

    private sealed class PeerReliabilityTestFactory(string root, RecordingAdmissionGate? admissionGate) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            var wsRoot = Path.Combine(root, "default-ws");
            Directory.CreateDirectory(wsRoot);

            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecPersistence:SecretStore"] = SecretStoreFactory.EncryptedFile,
                ["TinadecTools:DefaultWorkspaceRoot"] = wsRoot,
                ["Logging:LogLevel:Default"] = "Warning"
            }));

            if (admissionGate is not null)
            {
                builder.ConfigureServices(services =>
                {
                    var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IFullDuplexRunCoordinator));
                    if (existing is not null)
                    {
                        services.Remove(existing);
                        services.AddSingleton<IFullDuplexRunCoordinator>(sp =>
                        {
                            var inner = existing.ImplementationInstance as IFullDuplexRunCoordinator
                                ?? (existing.ImplementationFactory is not null
                                    ? (IFullDuplexRunCoordinator)existing.ImplementationFactory(sp)
                                    : (IFullDuplexRunCoordinator)ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!));
                            return new InstrumentedRunCoordinator(inner, admissionGate);
                        });
                    }
                });
            }
        }
    }
}
