using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AspNetCore;
using TinadecCore.AspNetCore.Endpoints;
using TinadecCore.Lifecycle;
using TinadecCore.Peers;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests.Peers;

public sealed class PeerFoundationApiTests : IAsyncLifetime
{
    private readonly string _root = ApiTestStorage.CreateRoot("peer-foundation");
    private readonly AdjustableTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-27T00:00:00Z"));
    private PeerTestFactory? _factory;

    private string DbPath => Path.Combine(_root, "tinadec.db");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new PeerTestFactory(_root, _clock);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InboundDefaultClosed_SecretKeysZeroSqliteLeakage_AndLoopbackGuardsHold()
    {
        using var client = _factory!.CreateClient();

        // 1. Factory default posture: inbound disabled, default port 48735, valid P-256 SHA-256 fingerprint
        var inboundResp = await client.GetAsync("/api/v1/peers/inbound");
        Assert.Equal(HttpStatusCode.OK, inboundResp.StatusCode);
        var inbound = await inboundResp.Content.ReadFromJsonAsync<PeerInboundSettingsDto>();
        Assert.NotNull(inbound);
        Assert.False(inbound!.Enabled);
        Assert.Equal(48735, inbound.ListenPort);
        Assert.NotEmpty(inbound.LocalDeviceId);
        Assert.Equal(64, inbound.LocalFingerprint.Length);
        Assert.All(inbound.LocalFingerprint, c => Assert.True(char.IsAsciiHexDigitLower(c) || char.IsDigit(c)));

        // 2. Handshake verification while inbound is disabled must fail with 403 inbound_disabled
        using (var disabledVerifyReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-dev-1",
            display_name = "Dev Laptop",
            remote_fingerprint = new string('a', 64),
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "grant"
        }))
        {
            var disabledResp = await client.SendAsync(disabledVerifyReq);
            Assert.Equal(HttpStatusCode.Forbidden, disabledResp.StatusCode);
            var err = await disabledResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(PeerErrorCodes.InboundDisabled, err.GetProperty("code").GetString());
        }

        // 3. Internal endpoint without gateway-internal principal must be rejected
        var unauthInternal = await client.GetAsync("/api/v1/peers/internal/local-tls-material");
        Assert.Equal(HttpStatusCode.Forbidden, unauthInternal.StatusCode);
        Assert.Equal(PeerErrorCodes.Unauthorized, (await unauthInternal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // 4. Remote peer controller headers must NEVER access worker peer security management endpoints
        foreach (var protectedPath in new[] { "/api/v1/peers/inbound", "/api/v1/peers/pairing-code", "/api/v1/peers/grants", "/api/v1/peers/devices" })
        {
            using var remoteMgmtReq = new HttpRequestMessage(HttpMethod.Get, protectedPath);
            remoteMgmtReq.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, "remote-controller-99");
            var blockedResp = await client.SendAsync(remoteMgmtReq);
            Assert.Equal(HttpStatusCode.Forbidden, blockedResp.StatusCode);
            Assert.Equal(PeerErrorCodes.PolicyDenied, (await blockedResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // 5. Retrieve TLS material via authorized gateway-internal header & mint active pairing code
        PeerLocalTlsMaterialDto tlsMaterial;
        using (var tlsReq = CreateInternalRequest(HttpMethod.Get, "/api/v1/peers/internal/local-tls-material"))
        {
            var tlsResp = await client.SendAsync(tlsReq);
            Assert.Equal(HttpStatusCode.OK, tlsResp.StatusCode);
            tlsMaterial = (await tlsResp.Content.ReadFromJsonAsync<PeerLocalTlsMaterialDto>())!;
        }
        Assert.Contains("BEGIN CERTIFICATE", tlsMaterial.CertPem, StringComparison.Ordinal);
        Assert.Contains("BEGIN PRIVATE KEY", tlsMaterial.PrivateKeyPem, StringComparison.Ordinal);
        using (var parsedCert = X509Certificate2.CreateFromPem(tlsMaterial.CertPem))
        {
            var expectedFp = Convert.ToHexString(SHA256.HashData(parsedCert.RawData)).ToLowerInvariant();
            Assert.Equal(expectedFp, tlsMaterial.LocalFingerprint);
        }

        var codeResp = await client.PostAsJsonAsync("/api/v1/peers/pairing-code", new { ttl_seconds = 300 });
        Assert.Equal(HttpStatusCode.OK, codeResp.StatusCode);
        var codeDto = (await codeResp.Content.ReadFromJsonAsync<PeerPairingCodeStatusDto>())!;
        Assert.True(codeDto.Active);
        Assert.NotNull(codeDto.PairingCode);
        Assert.InRange(codeDto.PairingCode!.Length, 6, 8);
        Assert.Equal(5, codeDto.RemainingAttempts);

        // GET /pairing-code never re-exposes plaintext pairing_code
        var statusDto = await client.GetFromJsonAsync<PeerPairingCodeStatusDto>("/api/v1/peers/pairing-code");
        Assert.NotNull(statusDto);
        Assert.True(statusDto!.Active);
        Assert.Null(statusDto.PairingCode);

        // 6. SecretStore holds private key & pairing code hash; SQLite binary/text scan has ZERO secrets!
        var secretStore = _factory.Services.GetRequiredService<ISecretStore>();
        Assert.True(await secretStore.ExistsAsync(PeerSecretKeys.LocalCertPem));
        Assert.True(await secretStore.ExistsAsync(PeerSecretKeys.LocalPrivateKeyPem));
        Assert.True(await secretStore.ExistsAsync(PeerSecretKeys.ActivePairingCode));
        var storedPairingJson = await secretStore.GetAsync(PeerSecretKeys.ActivePairingCode);
        Assert.NotNull(storedPairingJson);
        Assert.DoesNotContain(codeDto.PairingCode, storedPairingJson!, StringComparison.Ordinal);
        using var pairingSecretDoc = JsonDocument.Parse(storedPairingJson!);
        var codeHash = pairingSecretDoc.RootElement.GetProperty("codeHash").GetString()!;
        var salt = pairingSecretDoc.RootElement.GetProperty("salt").GetString()!;

        // Flush WAL into main SQLite file and scan all SQLite files raw bytes & UTF-8 text
        await using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(FULL);";
            await cmd.ExecuteNonQueryAsync();
        }

        var privateKeyBodyLine = tlsMaterial.PrivateKeyPem
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First(line => !line.StartsWith("-----", StringComparison.Ordinal));

        foreach (var candidateFile in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
        {
            if (!File.Exists(candidateFile)) continue;
            var rawBytes = await ReadAllBytesSharedAsync(candidateFile);
            var rawLatin1 = Encoding.Latin1.GetString(rawBytes);
            var rawUtf8 = Encoding.UTF8.GetString(rawBytes);
            foreach (var text in new[] { rawLatin1, rawUtf8 })
            {
                Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(privateKeyBodyLine, text, StringComparison.Ordinal);
                Assert.DoesNotContain(codeHash, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(salt, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(PeerSecretKeys.ActivePairingCode, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task PairingCode_Enforces5MinuteTtl_5AttemptLockout_OneTimeUse_GrantReconnect_AndPolicySse()
    {
        using var client = _factory!.CreateClient();
        var secretStore = _factory.Services.GetRequiredService<ISecretStore>();

        // Enable inbound
        var enableResp = await client.PutAsJsonAsync("/api/v1/peers/inbound", new
        {
            enabled = true,
            display_name = "Worker Rig",
            listen_port = 48735
        });
        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode);
        var enabledInbound = (await enableResp.Content.ReadFromJsonAsync<PeerInboundSettingsDto>())!;
        Assert.True(enabledInbound.Enabled);
        Assert.Equal("Worker Rig", enabledInbound.DisplayName);

        // --- Part 1: 5 consecutive failed attempts lock out and delete the active code ---
        var firstCode = (await (await client.PostAsJsonAsync("/api/v1/peers/pairing-code", new { })).Content.ReadFromJsonAsync<PeerPairingCodeStatusDto>())!;
        var controllerFp = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        for (var i = 1; i <= 5; i++)
        {
            using var wrongReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
            {
                transport = "lan",
                controller_device_id = "ctrl-attacker",
                display_name = "Attacker",
                remote_fingerprint = controllerFp,
                protocol_major = 1,
                protocol_minor = 0,
                pairing_mode = "code",
                pairing_code = "000000"
            });
            var wrongResp = await client.SendAsync(wrongReq);
            Assert.Equal(HttpStatusCode.Forbidden, wrongResp.StatusCode);
            Assert.Equal(PeerErrorCodes.PairingCodeRejected, (await wrongResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        Assert.False(await secretStore.ExistsAsync(PeerSecretKeys.ActivePairingCode));
        // Even presenting the original valid code on attempt 6 must now be rejected
        using (var sixthReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-legit",
            display_name = "Legit",
            remote_fingerprint = controllerFp,
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "code",
            pairing_code = firstCode.PairingCode
        }))
        {
            var sixthResp = await client.SendAsync(sixthReq);
            Assert.Equal(HttpStatusCode.Forbidden, sixthResp.StatusCode);
            Assert.Equal(PeerErrorCodes.PairingCodeRejected, (await sixthResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // --- Part 2: 5-minute TTL expiration ---
        var expiringCode = (await (await client.PostAsJsonAsync("/api/v1/peers/pairing-code", new { })).Content.ReadFromJsonAsync<PeerPairingCodeStatusDto>())!;
        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using (var expiredReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-legit",
            display_name = "Legit",
            remote_fingerprint = controllerFp,
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "code",
            pairing_code = expiringCode.PairingCode
        }))
        {
            var expiredResp = await client.SendAsync(expiredReq);
            Assert.Equal(HttpStatusCode.Forbidden, expiredResp.StatusCode);
            Assert.Equal(PeerErrorCodes.PairingCodeExpired, (await expiredResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.False(await secretStore.ExistsAsync(PeerSecretKeys.ActivePairingCode));

        // --- Part 3: Valid one-time pairing -> immediate secret deletion -> durable grant reconnect & fingerprint pinning ---
        var validCode = (await (await client.PostAsJsonAsync("/api/v1/peers/pairing-code", new { })).Content.ReadFromJsonAsync<PeerPairingCodeStatusDto>())!;
        using (var pairVerifyReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-mac-studio",
            display_name = "Mac Studio",
            remote_fingerprint = controllerFp,
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "code",
            pairing_code = validCode.PairingCode,
            expected_remote_fingerprint = enabledInbound.LocalFingerprint
        }))
        {
            var pairVerifyResp = await client.SendAsync(pairVerifyReq);
            Assert.Equal(HttpStatusCode.OK, pairVerifyResp.StatusCode);
            var verified = (await pairVerifyResp.Content.ReadFromJsonAsync<PeerHandshakeVerifyResponseDto>())!;
            Assert.True(verified.Accepted);
            Assert.Equal("operator", verified.Policy.Role);
            Assert.Equal("all", verified.Policy.WorkspaceMode);
            Assert.NotEmpty(verified.PolicyRevision);
        }

        // Pairing code secret MUST be deleted immediately after single use
        Assert.False(await secretStore.ExistsAsync(PeerSecretKeys.ActivePairingCode));

        // Reusing the consumed pairing code must fail
        using (var replayCodeReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-other",
            display_name = "Other",
            remote_fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "code",
            pairing_code = validCode.PairingCode
        }))
        {
            var replayResp = await client.SendAsync(replayCodeReq);
            Assert.Equal(HttpStatusCode.Forbidden, replayResp.StatusCode);
            Assert.Equal(PeerErrorCodes.PairingCodeRejected, (await replayResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // Reconnect using durable peer_grants (no pairing code required) succeeds
        using (var grantReconnectReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-mac-studio",
            display_name = "Mac Studio",
            remote_fingerprint = controllerFp,
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "grant"
        }))
        {
            var reconnectResp = await client.SendAsync(grantReconnectReq);
            Assert.Equal(HttpStatusCode.OK, reconnectResp.StatusCode);
        }

        // Forged certificate fingerprint on grant reconnect MUST fail with fingerprint_mismatch
        var forgedFp = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using (var forgedReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-mac-studio",
            display_name = "Mac Studio",
            remote_fingerprint = forgedFp,
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "grant"
        }))
        {
            var forgedResp = await client.SendAsync(forgedReq);
            Assert.Equal(HttpStatusCode.Forbidden, forgedResp.StatusCode);
            Assert.Equal(PeerErrorCodes.FingerprintMismatch, (await forgedResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // --- Part 4: Policy SSE stream & revocation ---
        using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sseReq = CreateInternalRequest(HttpMethod.Get, "/api/v1/peers/internal/policy-events");
        using var sseResp = await client.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        await using var sseStream = await sseResp.Content.ReadAsStreamAsync(sseCts.Token);
        using var sseReader = new StreamReader(sseStream, Encoding.UTF8);

        // Read initial ': connected' comment line
        var bannerLine = await sseReader.ReadLineAsync(sseCts.Token);
        Assert.Equal(": connected", bannerLine);
        _ = await sseReader.ReadLineAsync(sseCts.Token);

        // Update policy to observer + selected workspaces
        var policyUpdateResp = await client.PutAsJsonAsync("/api/v1/peers/grants/ctrl-mac-studio/policy", new
        {
            role = "observer",
            workspace_mode = "selected",
            workspace_ids = new[] { "proj-1", "proj-2" },
            sync_all_sessions = true
        });
        Assert.Equal(HttpStatusCode.OK, policyUpdateResp.StatusCode);

        var updatedEventLine = await ReadNextDataLineAsync(sseReader, sseCts.Token);
        using (var evtDoc = JsonDocument.Parse(updatedEventLine["data: ".Length..]))
        {
            Assert.Equal("policy_updated", evtDoc.RootElement.GetProperty("type").GetString());
            Assert.Equal("ctrl-mac-studio", evtDoc.RootElement.GetProperty("controller_device_id").GetString());
            Assert.Equal("observer", evtDoc.RootElement.GetProperty("policy").GetProperty("role").GetString());
        }

        // Revoke grant -> emits grant_revoked and blocks subsequent reconnects
        var revokeResp = await client.DeleteAsync("/api/v1/peers/grants/ctrl-mac-studio");
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        var revokedEventLine = await ReadNextDataLineAsync(sseReader, sseCts.Token);
        using (var revDoc = JsonDocument.Parse(revokedEventLine["data: ".Length..]))
        {
            Assert.Equal("grant_revoked", revDoc.RootElement.GetProperty("type").GetString());
            Assert.Equal("ctrl-mac-studio", revDoc.RootElement.GetProperty("controller_device_id").GetString());
        }
        sseCts.Cancel();

        using (var afterRevokeReq = CreateInternalRequest(HttpMethod.Post, "/api/v1/peers/internal/handshake/verify", new
        {
            transport = "lan",
            controller_device_id = "ctrl-mac-studio",
            display_name = "Mac Studio",
            remote_fingerprint = controllerFp,
            protocol_major = 1,
            protocol_minor = 0,
            pairing_mode = "grant"
        }))
        {
            var afterRevokeResp = await client.SendAsync(afterRevokeReq);
            Assert.Equal(HttpStatusCode.Unauthorized, afterRevokeResp.StatusCode);
            Assert.Equal(PeerErrorCodes.Unauthorized, (await afterRevokeResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // --- Part 5: Controller device directory, workspaces mount, and 13 error classifications ---
        var pairDeviceResp = await client.PostAsJsonAsync("/api/v1/peers/devices/pair", new
        {
            remote_device_id = "worker-node-1",
            display_name = "Build Box",
            transport = "lan",
            address = "192.168.1.50:48735",
            certificate_fingerprint = controllerFp,
            expected_fingerprint = controllerFp,
            protocol_major = 1,
            protocol_minor = 0,
            last_state = "ready"
        });
        Assert.Equal(HttpStatusCode.OK, pairDeviceResp.StatusCode);
        var pairedDevice = (await pairDeviceResp.Content.ReadFromJsonAsync<PeerDeviceDto>())!;
        Assert.Equal("ready", pairedDevice.LastState);

        var mountResp = await client.PutAsJsonAsync($"/api/v1/peers/devices/{pairedDevice.DeviceId}/workspaces/mount", new
        {
            mounts = new[]
            {
                new { remote_project_id = "proj-alpha", name = "Alpha Repo", remote_root_path = "/srv/alpha", mounted = true, visible = true }
            }
        });
        Assert.Equal(HttpStatusCode.OK, mountResp.StatusCode);
        var mountedJson = await mountResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(mountedJson.GetProperty("workspaces").EnumerateArray());

        foreach (var code in new[]
        {
            PeerErrorCodes.Unreachable,
            PeerErrorCodes.InboundDisabled,
            PeerErrorCodes.PairingCodeRejected,
            PeerErrorCodes.PairingCodeExpired,
            PeerErrorCodes.FingerprintMismatch,
            PeerErrorCodes.Unauthorized,
            PeerErrorCodes.ProtocolMajorMismatch,
            PeerErrorCodes.EncryptionUnavailable,
            PeerErrorCodes.HandshakeFailed,
            PeerErrorCodes.Timeout,
            PeerErrorCodes.PolicyDenied,
            PeerErrorCodes.DeviceOffline,
            PeerErrorCodes.SubmissionUncertain
        })
        {
            var classified = ErrorClassification.Classify(code, 403);
            Assert.NotEmpty(classified.Category);
        }
    }

    [Fact]
    public async Task ApprovalAtomicCas_IdempotentRepeat_Conflicting409_ObserverDenied_AndPeerDeviceAttribution()
    {
        using var client = _factory!.CreateClient();
        var (_, sessionId, runId) = await SeedRunAsync(client);
        var approval1 = await SeedToolApprovalAsync(sessionId, runId, "shell", "{\"command\":\"bun test\"}");
        var approval2 = await SeedToolApprovalAsync(sessionId, runId, "write_file", "{\"path\":\"out.txt\",\"content\":\"ok\"}");

        // 1. Observer role cannot decide approvals -> 403 policy_denied
        using (var observerReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{approval1}/decision")
        {
            Content = JsonContent.Create(new { decision = "approved" })
        })
        {
            observerReq.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, "peer-ipad");
            observerReq.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, "iPad Observer");
            observerReq.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, "observer");
            var observerResp = await client.SendAsync(observerReq);
            Assert.Equal(HttpStatusCode.Forbidden, observerResp.StatusCode);
            Assert.Equal(PeerErrorCodes.PolicyDenied, (await observerResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // 2. Operator peer approves approval1 -> 200 OK with decided_by_device_id & decided_by_device_name
        using (var peerApproveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{approval1}/decision")
        {
            Content = JsonContent.Create(new { decision = "approved", reason = "Verified on dev laptop" })
        })
        {
            peerApproveReq.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, "peer-macbook-pro");
            peerApproveReq.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, "Lin's MacBook Pro");
            peerApproveReq.Headers.Add(PeerDeviceEndpoints.PeerRoleHeader, "operator");
            var approveResp = await client.SendAsync(peerApproveReq);
            Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
            var approveBody = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("approved", approveBody.GetProperty("status").GetString());
            Assert.Equal("peer-macbook-pro", approveBody.GetProperty("decided_by_device_id").GetString());
            Assert.Equal("Lin's MacBook Pro", approveBody.GetProperty("decided_by_device_name").GetString());
        }

        // 3. Repeating the IDENTICAL decision ("approved") from local device -> idempotent 200 OK preserving original winner attribution
        var repeatSameResp = await client.PostAsJsonAsync($"/api/v1/approvals/{approval1}/decision", new
        {
            decision = "approved"
        });
        Assert.Equal(HttpStatusCode.OK, repeatSameResp.StatusCode);
        var repeatSameBody = await repeatSameResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approved", repeatSameBody.GetProperty("status").GetString());
        Assert.Equal("peer-macbook-pro", repeatSameBody.GetProperty("decided_by_device_id").GetString());
        Assert.Equal("Lin's MacBook Pro", repeatSameBody.GetProperty("decided_by_device_name").GetString());

        // 4. Submitting CONFLICTING opposite decision ("rejected") -> 409 Conflict
        var conflictResp = await client.PostAsJsonAsync($"/api/v1/approvals/{approval1}/decision", new
        {
            decision = "rejected",
            reason = "Too late"
        });
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
        Assert.Equal("conflict", (await conflictResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // 5. Approval projection (GET /api/v1/approvals/{id} & list) and run event carry decided_by_device_id & decided_by_device_name
        var getApprovalJson = await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/{approval1}");
        Assert.Equal("peer-macbook-pro", getApprovalJson.GetProperty("decided_by_device_id").GetString());
        Assert.Equal("Lin's MacBook Pro", getApprovalJson.GetProperty("decided_by_device_name").GetString());

        var listApprovalsJson = await client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals?run_id={runId}");
        var listedItem = listApprovalsJson.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == approval1);
        Assert.Equal("peer-macbook-pro", listedItem.GetProperty("decided_by_device_id").GetString());
        Assert.Equal("Lin's MacBook Pro", listedItem.GetProperty("decided_by_device_name").GetString());

        var lifecycle = _factory.Services.GetRequiredService<StorageLifecycleService>();
        var events = await lifecycle.FollowEventsAsync(sessionId, new Dictionary<Guid, long>());
        var decidedEvt = Assert.Single(events, e => e.EventType == "approval.decided");
        var payload = Assert.IsType<JsonElement>(decidedEvt.Payload["payload"]);
        Assert.Equal("peer-macbook-pro", payload.GetProperty("decided_by_device_id").GetString());
        Assert.Equal("Lin's MacBook Pro", payload.GetProperty("decided_by_device_name").GetString());

        // 6. Local decision without peer headers defaults to decided_by_device_id == "local"
        var localRejectResp = await client.PostAsJsonAsync($"/api/v1/approvals/{approval2}/decision", new
        {
            decision = "rejected",
            reason = "Local reject"
        });
        Assert.Equal(HttpStatusCode.OK, localRejectResp.StatusCode);
        var localRejectBody = await localRejectResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("local", localRejectBody.GetProperty("decided_by_device_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(localRejectBody.GetProperty("decided_by_device_name").GetString()));

        // Conflicting approve after local reject -> 409 Conflict
        var localConflictResp = await client.PostAsJsonAsync($"/api/v1/approvals/{approval2}/decision", new
        {
            decision = "approved"
        });
        Assert.Equal(HttpStatusCode.Conflict, localConflictResp.StatusCode);

        // 7. Governance permission-request endpoint atomic CAS + peer device attribution
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var permReq = await authorization.RequestPermissionAsync(new PermissionRequestCommand(
            tenant.PrincipalId,
            SubjectAgentInstanceId: null,
            ParentAgentInstanceId: null,
            new CapabilityClaim("tool.invoke", "execute", "tool://git_push"),
            runId,
            TaskId: null,
            TimeSpan.FromMinutes(30),
            RequestedUses: 1,
            Risk: "high",
            ExpectedCost: 0,
            Rationale: "Push branch",
            IdempotencyKey: $"perm-cas:{Guid.NewGuid():N}"));
        Assert.Equal(PermissionRequestStatuses.AwaitingUser, permReq.Request.Status);

        using (var govApproveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/governance/permission-requests/{permReq.Request.Id}/decision")
        {
            Content = JsonContent.Create(new { approve = true, reason = "Approved on workstation" })
        })
        {
            govApproveReq.Headers.Add(PeerDeviceEndpoints.PeerControllerIdHeader, "peer-workstation");
            govApproveReq.Headers.Add(PeerDeviceEndpoints.PeerControllerNameHeader, "Linux Workstation");
            var govResp = await client.SendAsync(govApproveReq);
            Assert.Equal(HttpStatusCode.OK, govResp.StatusCode);
            var govJson = await govResp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("peer-workstation", govJson.GetProperty("decision").GetProperty("decided_by_device_id").GetString());
            Assert.Equal("Linux Workstation", govJson.GetProperty("decision").GetProperty("decided_by_device_name").GetString());
        }

        // Repeat same approve -> idempotent 200 preserving Linux Workstation
        var govRepeatResp = await client.PostAsJsonAsync($"/api/v1/governance/permission-requests/{permReq.Request.Id}/decision", new { approve = true });
        Assert.Equal(HttpStatusCode.OK, govRepeatResp.StatusCode);
        Assert.Equal("peer-workstation", (await govRepeatResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("decision").GetProperty("decided_by_device_id").GetString());

        // Conflicting opposite decision -> 409 Conflict
        var govConflictResp = await client.PostAsJsonAsync($"/api/v1/governance/permission-requests/{permReq.Request.Id}/decision", new { approve = false });
        Assert.Equal(HttpStatusCode.Conflict, govConflictResp.StatusCode);
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static HttpRequestMessage CreateInternalRequest(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(PeerDeviceEndpoints.PrincipalHeader, PeerDeviceEndpoints.GatewayInternalPrincipal);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    private static async Task<string> ReadNextDataLineAsync(StreamReader reader, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            Assert.NotNull(line);
            if (line!.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line;
            }
        }
    }

    private async Task<(Guid ProjectId, Guid SessionId, Guid RunId)> SeedRunAsync(HttpClient client)
    {
        var scope = _factory!.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var wsDir = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wsDir);

        var projectResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "peer-cas-proj", path = wsDir });
        Assert.Equal(HttpStatusCode.Created, projectResp.StatusCode);
        var projectId = (await projectResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var sessionResp = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = projectId.ToString(), title = "peer cas session" });
        Assert.Equal(HttpStatusCode.Created, sessionResp.StatusCode);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var runId = Guid.NewGuid();
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>().CreateDbContextAsync();
        db.Runs.Add(new RunRecord
        {
            Id = runId,
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            SessionId = sessionId,
            InitiatedByPrincipalId = scope.PrincipalId,
            TriggerMessageId = Guid.NewGuid(),
            Status = "awaiting_approval",
            PermissionMode = "ask",
            RuntimeProfileId = "default",
            ConfigurationHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (projectId, sessionId, runId);
    }

    private async Task<Guid> SeedToolApprovalAsync(Guid sessionId, Guid runId, string toolId, string parametersJson)
    {
        var scope = _factory!.Services.GetRequiredService<ITenantContextAccessor>().Current;
        var session = await _factory.Services.GetRequiredService<ISessionLocator>().FindAsync(sessionId);
        var coordinator = _factory.Services.GetRequiredService<IToolExecutionCoordinator>();
        var prep = await coordinator.PrepareAsync(new ToolExecutionPrepareRequest(
            scope.TenantId,
            scope.WorkspaceId,
            session?.ProjectId ?? Guid.Empty,
            sessionId,
            runId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            toolId,
            "high",
            MutatesWorkspace: true,
            RequiresApproval: true,
            parametersJson,
            TinadecCore.Tools.ToolParametersHash.Compute(parametersJson),
            $"call:{Guid.NewGuid():N}",
            Summary: $"Execute {toolId}"));
        return prep.Execution.ApprovalId!.Value;
    }

    private sealed class PeerTestFactory(string root, TimeProvider clock) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecPersistence:SecretStore"] = SecretStoreFactory.EncryptedFile,
                ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            });
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private long _utcTicks = initialUtc.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
    }
}
