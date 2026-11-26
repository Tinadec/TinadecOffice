using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

/// <summary>
/// Cross-device chunked attachment pre-staging and workspace zero-copy reference service.
///
/// Guarantees:
/// 1. When <c>origin == "workspace"</c>, records only the validated workspace <c>source_path</c>
///    with zero byte copying into <c>IContentStore</c> or temp chunk files.
/// 2. When <c>origin == "composer"</c>, receives arbitrary-order or sequential chunks into a
///    bounded per-stage directory, verifies total size (<c>&lt;= 32 MiB</c>) and optional SHA-256,
///    stores assembled bytes into <see cref="IMessageAttachmentStore"/> once complete, and removes
///    temporary chunk files immediately.
/// </summary>
public sealed class PeerAttachmentStageService : IPeerAttachmentStageService
{
    public const long MaxAttachmentBytes = 32L * 1024 * 1024;
    public const int MaxChunksPerAttachment = 4096;
    private static readonly TimeSpan StageTtl = TimeSpan.FromHours(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly IMessageAttachmentStore? _attachmentStore;
    private readonly IScopeStorageLocations? _locations;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _stageLocks = new(StringComparer.Ordinal);
    private readonly string _fallbackTempRoot;

    public PeerAttachmentStageService(
        IDbContextFactory<PeerDbContext> dbFactory,
        IMessageAttachmentStore? attachmentStore = null,
        IScopeStorageLocations? locations = null,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _attachmentStore = attachmentStore;
        _locations = locations;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fallbackTempRoot = Path.Combine(Path.GetTempPath(), "tinadec-peer-stage", Guid.NewGuid().ToString("N"));
    }

    public async Task<PeerAttachmentStageResultDto> StageChunkOrReferenceAsync(
        PeerAttachmentStageCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var stageId = NormalizeOrCreateStageId(command.StageId);
        var origin = string.IsNullOrWhiteSpace(command.Origin) ? AttachmentOrigins.Composer : command.Origin.Trim().ToLowerInvariant();
        if (!AttachmentOrigins.IsKnown(origin))
        {
            throw new PeerException(400, "attachment_origin_invalid", "origin must be composer or workspace.");
        }

        if (string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new PeerException(400, "attachment_filename_invalid", "filename is required.");
        }

        var stageGate = _stageLocks.GetOrAdd(stageId, static _ => new SemaphoreSlim(1, 1));
        await stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var expiresAtMs = nowMs + (long)StageTtl.TotalMilliseconds;

            if (origin == AttachmentOrigins.Workspace)
            {
                if (string.IsNullOrWhiteSpace(command.ValidatedWorkspaceSourcePath))
                {
                    throw new PeerException(400, "attachment_source_required", "origin=workspace requires a validated source_path.");
                }

                var canonicalPath = command.ValidatedWorkspaceSourcePath;
                var fileBytes = command.WorkspaceSourceFileBytes ?? 0L;

                await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                    var existing = await db.StagedAttachments
                        .SingleOrDefaultAsync(x => x.StageId == stageId, ct)
                        .ConfigureAwait(false);

                    if (existing is null)
                    {
                        existing = new PeerStagedAttachmentRecord
                        {
                            StageId = stageId,
                            SessionId = command.SessionId?.ToString(),
                            ProjectId = command.ProjectId?.ToString(),
                            Filename = command.FileName,
                            MediaType = command.MediaType,
                            Origin = AttachmentOrigins.Workspace,
                            TotalBytes = fileBytes,
                            TotalChunks = 1,
                            Sha256Hex = null,
                            ReceivedChunksJson = "[0]",
                            Status = "completed",
                            SourcePath = canonicalPath,
                            AttachmentId = null,
                            CreatedAt = nowMs,
                            ExpiresAt = expiresAtMs,
                            UpdatedAt = nowMs
                        };
                        db.StagedAttachments.Add(existing);
                    }
                    else
                    {
                        existing.SessionId = command.SessionId?.ToString() ?? existing.SessionId;
                        existing.ProjectId = command.ProjectId?.ToString() ?? existing.ProjectId;
                        existing.Filename = command.FileName;
                        existing.MediaType = command.MediaType;
                        existing.Origin = AttachmentOrigins.Workspace;
                        existing.TotalBytes = fileBytes;
                        existing.TotalChunks = 1;
                        existing.ReceivedChunksJson = "[0]";
                        existing.Status = "completed";
                        existing.SourcePath = canonicalPath;
                        existing.ExpiresAt = expiresAtMs;
                        existing.UpdatedAt = nowMs;
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return 1;
                }, cancellationToken).ConfigureAwait(false);

                return new PeerAttachmentStageResultDto
                {
                    StageId = stageId,
                    Complete = true,
                    ZeroCopy = true,
                    Origin = AttachmentOrigins.Workspace,
                    SourcePath = canonicalPath,
                    AttachmentId = null,
                    Attachment = null,
                    ReceivedChunks = 1,
                    TotalChunks = 1
                };
            }

            // Composer chunked upload path
            if (command.TotalChunks < 1 || command.TotalChunks > MaxChunksPerAttachment)
            {
                throw new PeerException(400, "attachment_chunks_invalid", $"total_chunks must be between 1 and {MaxChunksPerAttachment}.");
            }

            if (command.ChunkIndex < 0 || command.ChunkIndex >= command.TotalChunks)
            {
                throw new PeerException(400, "attachment_chunk_index_invalid", "chunk_index must satisfy 0 <= chunk_index < total_chunks.");
            }

            if (command.DeclaredTotalBytes is > MaxAttachmentBytes)
            {
                throw new PeerException(413, "attachment_too_large", $"An attachment may not exceed {MaxAttachmentBytes} bytes.");
            }

            var chunkBytes = command.ChunkBytes ?? [];
            if (chunkBytes.LongLength > MaxAttachmentBytes)
            {
                throw new PeerException(413, "attachment_too_large", $"An attachment may not exceed {MaxAttachmentBytes} bytes.");
            }
            if (command.TotalChunks == 1 && chunkBytes.Length == 0)
            {
                throw new PeerException(400, "attachment_empty", "Staged attachment chunk cannot be empty.");
            }

            var expectedHash = NormalizeOptionalSha256(command.ExpectedSha256Hex);

            // Check if this stage_id is already completed (idempotent replay of final chunk)
            var currentRow = await ReadStageRecordAsync(stageId, cancellationToken).ConfigureAwait(false);
            if (currentRow is not null && currentRow.Status == "completed")
            {
                StoredAttachment? existingStored = null;
                if (_attachmentStore is not null && Guid.TryParse(currentRow.AttachmentId, out var parsedAttId))
                {
                    existingStored = await _attachmentStore.FindAsync(parsedAttId, cancellationToken).ConfigureAwait(false);
                }

                return new PeerAttachmentStageResultDto
                {
                    StageId = stageId,
                    Complete = true,
                    ZeroCopy = false,
                    Origin = AttachmentOrigins.Composer,
                    AttachmentId = currentRow.AttachmentId,
                    Attachment = existingStored is null ? null : ToAttachmentDto(existingStored),
                    ReceivedChunks = currentRow.TotalChunks,
                    TotalChunks = currentRow.TotalChunks
                };
            }

            var stageDir = GetStageDirectory(stageId);
            Directory.CreateDirectory(stageDir);
            var chunkFilePath = Path.Combine(stageDir, $"chunk_{command.ChunkIndex:D6}.part");
            await File.WriteAllBytesAsync(chunkFilePath, chunkBytes, cancellationToken).ConfigureAwait(false);

            var receivedSet = ParseReceivedChunks(currentRow?.ReceivedChunksJson);
            receivedSet.Add(command.ChunkIndex);

            // Enforce cumulative byte limit across received chunks on disk
            long cumulativeBytes = 0;
            foreach (var idx in receivedSet)
            {
                var partPath = Path.Combine(stageDir, $"chunk_{idx:D6}.part");
                if (File.Exists(partPath))
                {
                    cumulativeBytes += new FileInfo(partPath).Length;
                }
            }

            if (cumulativeBytes > MaxAttachmentBytes)
            {
                TryDeleteDirectory(stageDir);
                throw new PeerException(413, "attachment_too_large", $"An attachment may not exceed {MaxAttachmentBytes} bytes.");
            }

            var sortedIndices = receivedSet.OrderBy(x => x).ToArray();
            var receivedJson = JsonSerializer.Serialize(sortedIndices, JsonOptions);
            var allChunksPresent = sortedIndices.Length == command.TotalChunks;

            if (!allChunksPresent)
            {
                await UpsertComposerStageRowAsync(
                    stageId,
                    command,
                    expectedHash,
                    cumulativeBytes,
                    receivedJson,
                    status: "staging",
                    attachmentId: null,
                    nowMs,
                    expiresAtMs,
                    cancellationToken).ConfigureAwait(false);

                return new PeerAttachmentStageResultDto
                {
                    StageId = stageId,
                    Complete = false,
                    ZeroCopy = false,
                    Origin = AttachmentOrigins.Composer,
                    AttachmentId = null,
                    Attachment = null,
                    ReceivedChunks = sortedIndices.Length,
                    TotalChunks = command.TotalChunks
                };
            }

            // All chunks received: assemble sequentially and compute SHA-256
            var assembledPath = Path.Combine(stageDir, "assembled.bin");
            string computedSha256;
            long assembledLength = 0;
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var output = new FileStream(assembledPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024];
                for (var i = 0; i < command.TotalChunks; i++)
                {
                    var partPath = Path.Combine(stageDir, $"chunk_{i:D6}.part");
                    await using var partStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    int read;
                    while ((read = await partStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        assembledLength += read;
                        if (assembledLength > MaxAttachmentBytes)
                        {
                            output.Close();
                            TryDeleteDirectory(stageDir);
                            throw new PeerException(413, "attachment_too_large", $"An attachment may not exceed {MaxAttachmentBytes} bytes.");
                        }
                        sha256.AppendData(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                computedSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            }

            if (assembledLength == 0)
            {
                TryDeleteDirectory(stageDir);
                throw new PeerException(400, "attachment_empty", "Assembled attachment carried no bytes.");
            }

            var targetHash = expectedHash ?? currentRow?.Sha256Hex;
            if (!string.IsNullOrWhiteSpace(targetHash) && !string.Equals(targetHash, computedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(stageDir);
                throw new PeerException(400, "attachment_hash_mismatch", $"Assembled SHA-256 '{computedSha256}' did not match expected '{targetHash}'.");
            }

            // Delete individual part files once assembled.bin is verified
            for (var i = 0; i < command.TotalChunks; i++)
            {
                var partPath = Path.Combine(stageDir, $"chunk_{i:D6}.part");
                if (File.Exists(partPath)) File.Delete(partPath);
            }

            var resolvedSessionId = command.SessionId
                ?? (Guid.TryParse(currentRow?.SessionId, out var prevSess) ? prevSess : null);
            var resolvedProjectId = command.ProjectId
                ?? (Guid.TryParse(currentRow?.ProjectId, out var prevProj) ? prevProj : null);

            StoredAttachment? storedAttachment = null;
            string? attachmentId = null;

            if (resolvedSessionId is { } sessionGuid && _attachmentStore is not null)
            {
                await using (var readStream = new FileStream(assembledPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    storedAttachment = await _attachmentStore.StoreAsync(
                        new StoreAttachmentRequest(
                            sessionGuid,
                            currentRow?.Filename ?? command.FileName,
                            currentRow?.MediaType ?? command.MediaType,
                            readStream)
                        {
                            Origin = AttachmentOrigins.Composer,
                            ProjectId = resolvedProjectId
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                if (storedAttachment is null)
                {
                    TryDeleteDirectory(stageDir);
                    throw new PeerException(404, "session_not_found", "No session in the current workspace has that id.");
                }

                attachmentId = storedAttachment.Id.ToString();
                TryDeleteDirectory(stageDir);
            }

            await UpsertComposerStageRowAsync(
                stageId,
                command,
                computedSha256,
                assembledLength,
                receivedJson,
                status: "completed",
                attachmentId: attachmentId,
                nowMs,
                expiresAtMs,
                cancellationToken).ConfigureAwait(false);

            return new PeerAttachmentStageResultDto
            {
                StageId = stageId,
                Complete = true,
                ZeroCopy = false,
                Origin = AttachmentOrigins.Composer,
                AttachmentId = attachmentId,
                Attachment = storedAttachment is null ? null : ToAttachmentDto(storedAttachment),
                ReceivedChunks = command.TotalChunks,
                TotalChunks = command.TotalChunks
            };
        }
        finally
        {
            stageGate.Release();
        }
    }

    public async Task<PeerResolvedStagedAttachment?> ResolveForMessageAsync(
        Guid sessionId,
        string idOrStageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idOrStageId)) return null;
        var candidate = idOrStageId.Trim();

        // Check staged table by StageId OR AttachmentId first
        var stageRow = await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.StagedAttachments.AsNoTracking()
                .FirstOrDefaultAsync(x => x.StageId == candidate || x.AttachmentId == candidate, ct)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (stageRow is null)
        {
            return null;
        }

        if (stageRow.Status != "completed")
        {
            throw new PeerException(400, "attachment_stage_incomplete", $"Staged attachment '{stageRow.StageId}' is not yet complete (status: {stageRow.Status}).");
        }

        if (Guid.TryParse(stageRow.SessionId, out var boundSession) && boundSession != sessionId)
        {
            throw new PeerException(400, "attachment_not_found", $"Staged attachment '{stageRow.StageId}' belongs to a different session.");
        }

        if (stageRow.Origin == AttachmentOrigins.Workspace)
        {
            return new PeerResolvedStagedAttachment(
                StageId: stageRow.StageId,
                Origin: AttachmentOrigins.Workspace,
                ZeroCopy: true,
                AttachmentId: null,
                FileName: stageRow.Filename,
                MediaType: stageRow.MediaType,
                TotalBytes: stageRow.TotalBytes,
                SourcePath: stageRow.SourcePath);
        }

        // Composer attachment: if already stored in IMessageAttachmentStore, return its Guid
        if (Guid.TryParse(stageRow.AttachmentId, out var existingAttachmentGuid))
        {
            return new PeerResolvedStagedAttachment(
                StageId: stageRow.StageId,
                Origin: AttachmentOrigins.Composer,
                ZeroCopy: false,
                AttachmentId: existingAttachmentGuid,
                FileName: stageRow.Filename,
                MediaType: stageRow.MediaType,
                TotalBytes: stageRow.TotalBytes,
                SourcePath: null);
        }

        // Otherwise materialize assembled.bin into IMessageAttachmentStore for this session now
        if (_attachmentStore is null)
        {
            throw new PeerException(500, "attachment_store_unavailable", "Attachment store is not registered.");
        }

        var stageGate = _stageLocks.GetOrAdd(stageRow.StageId, static _ => new SemaphoreSlim(1, 1));
        await stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await ReadStageRecordAsync(stageRow.StageId, cancellationToken).ConfigureAwait(false);
            if (refreshed is not null && Guid.TryParse(refreshed.AttachmentId, out var racedGuid))
            {
                return new PeerResolvedStagedAttachment(
                    StageId: refreshed.StageId,
                    Origin: AttachmentOrigins.Composer,
                    ZeroCopy: false,
                    AttachmentId: racedGuid,
                    FileName: refreshed.Filename,
                    MediaType: refreshed.MediaType,
                    TotalBytes: refreshed.TotalBytes,
                    SourcePath: null);
            }

            var stageDir = GetStageDirectory(stageRow.StageId);
            var assembledPath = Path.Combine(stageDir, "assembled.bin");
            if (!File.Exists(assembledPath))
            {
                throw new PeerException(404, "attachment_content_missing", $"Assembled bytes for staged attachment '{stageRow.StageId}' were not found.");
            }

            StoredAttachment? stored;
            await using (var stream = new FileStream(assembledPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stored = await _attachmentStore.StoreAsync(
                    new StoreAttachmentRequest(sessionId, stageRow.Filename, stageRow.MediaType, stream)
                    {
                        Origin = AttachmentOrigins.Composer,
                        ProjectId = Guid.TryParse(stageRow.ProjectId, out var p) ? p : null
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (stored is null)
            {
                throw new PeerException(404, "session_not_found", "No session in the current workspace has that id.");
            }

            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await db.StagedAttachments
                    .Where(x => x.StageId == stageRow.StageId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(x => x.SessionId, sessionId.ToString())
                            .SetProperty(x => x.AttachmentId, stored.Id.ToString())
                            .SetProperty(x => x.UpdatedAt, nowMs),
                        ct)
                    .ConfigureAwait(false);
                return 1;
            }, cancellationToken).ConfigureAwait(false);

            TryDeleteDirectory(stageDir);

            return new PeerResolvedStagedAttachment(
                StageId: stageRow.StageId,
                Origin: AttachmentOrigins.Composer,
                ZeroCopy: false,
                AttachmentId: stored.Id,
                FileName: stored.FileName,
                MediaType: stored.MediaType,
                TotalBytes: stored.ContentLength,
                SourcePath: null);
        }
        finally
        {
            stageGate.Release();
        }
    }

    private async Task<PeerStagedAttachmentRecord?> ReadStageRecordAsync(string stageId, CancellationToken cancellationToken)
    {
        return await SqliteTransientRetry.ReadAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.StagedAttachments.AsNoTracking()
                .SingleOrDefaultAsync(x => x.StageId == stageId, ct)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertComposerStageRowAsync(
        string stageId,
        PeerAttachmentStageCommand command,
        string? sha256Hex,
        long totalBytes,
        string receivedJson,
        string status,
        string? attachmentId,
        long nowMs,
        long expiresAtMs,
        CancellationToken cancellationToken)
    {
        await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.StagedAttachments
                .SingleOrDefaultAsync(x => x.StageId == stageId, ct)
                .ConfigureAwait(false);

            if (row is null)
            {
                row = new PeerStagedAttachmentRecord
                {
                    StageId = stageId,
                    SessionId = command.SessionId?.ToString(),
                    ProjectId = command.ProjectId?.ToString(),
                    Filename = command.FileName,
                    MediaType = command.MediaType,
                    Origin = AttachmentOrigins.Composer,
                    TotalBytes = totalBytes,
                    TotalChunks = command.TotalChunks,
                    Sha256Hex = sha256Hex,
                    ReceivedChunksJson = receivedJson,
                    Status = status,
                    SourcePath = null,
                    AttachmentId = attachmentId,
                    CreatedAt = nowMs,
                    ExpiresAt = expiresAtMs,
                    UpdatedAt = nowMs
                };
                db.StagedAttachments.Add(row);
            }
            else
            {
                if (command.SessionId is { } s) row.SessionId = s.ToString();
                if (command.ProjectId is { } p) row.ProjectId = p.ToString();
                if (!string.IsNullOrWhiteSpace(command.FileName)) row.Filename = command.FileName;
                if (!string.IsNullOrWhiteSpace(command.MediaType)) row.MediaType = command.MediaType;
                row.TotalBytes = totalBytes;
                row.TotalChunks = command.TotalChunks;
                if (!string.IsNullOrWhiteSpace(sha256Hex)) row.Sha256Hex = sha256Hex;
                row.ReceivedChunksJson = receivedJson;
                row.Status = status;
                if (attachmentId is not null) row.AttachmentId = attachmentId;
                row.ExpiresAt = expiresAtMs;
                row.UpdatedAt = nowMs;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return 1;
        }, cancellationToken).ConfigureAwait(false);
    }

    private string GetStageDirectory(string stageId)
    {
        var baseTemp = _locations is not null ? _locations.Temp : _fallbackTempRoot;
        return Path.Combine(baseTemp, "peer-stage", stageId);
    }

    private static string NormalizeOrCreateStageId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Guid.NewGuid().ToString("N");
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 128)
        {
            throw new PeerException(400, "attachment_stage_id_invalid", "stage_id must be at most 128 characters.");
        }

        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            {
                throw new PeerException(400, "attachment_stage_id_invalid", "stage_id may only contain ASCII letters, digits, '-' or '_'.");
            }
        }

        return trimmed;
    }

    private static string? NormalizeOptionalSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed.Length != 64 || trimmed.Any(c => !(char.IsAsciiHexDigitLower(c) || char.IsDigit(c))))
        {
            throw new PeerException(400, "attachment_hash_invalid", "sha256_hex must be a 64-character hex string.");
        }
        return trimmed;
    }

    private static HashSet<int> ParseReceivedChunks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize<int[]>(json, JsonOptions);
            return arr is null ? [] : [.. arr];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static MessageAttachmentDto ToAttachmentDto(StoredAttachment stored) => new()
    {
        Id = stored.Id.ToString(),
        SessionId = stored.SessionId.ToString(),
        MessageId = stored.MessageId?.ToString(),
        FileName = stored.FileName,
        MediaType = stored.MediaType,
        ContentHash = stored.ContentHash,
        ContentLength = stored.ContentLength,
        Origin = stored.Origin,
        ProjectId = stored.ProjectId,
        SourcePath = stored.SourcePath,
        DeliveredToModel = stored.DeliveredToModel,
        DeliveredRunId = stored.DeliveredRunId,
        DeliveredAt = stored.DeliveredAt,
        CreatedAt = stored.CreatedAt,
        BoundAt = stored.BoundAt
    };
}
