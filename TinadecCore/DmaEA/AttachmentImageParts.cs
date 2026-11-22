using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// Builds multimodal <see cref="DataContent"/> parts from immutable session attachments and
/// tool-produced visual outputs. Trigger-turn attachments take priority, followed by a bounded
/// window of recent session images so follow-up turns retain visual context without re-uploading.
/// Every image omitted by model capability or size budget leaves an authoritative note so the
/// model never hallucinates unseen visual content.
/// </summary>
internal sealed record AttachmentImageInput(
    IReadOnlyList<AIContent> Parts,
    IReadOnlyList<Guid> DeliveredAttachmentIds,
    string PromptSuffix,
    long TotalBytes = 0)
{
    public static AttachmentImageInput Empty { get; } = new([], [], string.Empty, 0);
}

internal sealed record ToolImageExtractionResult(
    string SanitizedResultJson,
    IReadOnlyList<AIContent> Parts,
    IReadOnlyList<Guid> DeliveredAttachmentIds,
    string PromptNote);

internal static class AttachmentImageParts
{
    internal const long MaxTotalBytesPerTurn = 32L * 1024 * 1024;
    internal const int DefaultMaxRecentHistoricalImages = 4;
    internal const long DefaultMaxRecentHistoricalBytes = 16L * 1024 * 1024;

    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"
    };

    internal static bool IsSupportedImageMediaType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) && SupportedMediaTypes.Contains(mediaType);

    /// <summary>
    /// Single-message attachment builder (trigger message only). Preserved for callers and unit
    /// tests that inspect exact single-message delivery semantics.
    /// </summary>
    internal static Task<AttachmentImageInput> BuildAsync(
        IMessageAttachmentStore store,
        Guid sessionId,
        Guid userMessageId,
        ChatResolution resolution,
        CancellationToken cancellationToken) =>
        BuildCoreAsync(
            store,
            sessionId,
            userMessageId,
            resolution,
            includeRecentSessionImages: false,
            maxRecentHistoricalImages: 0,
            maxRecentHistoricalBytes: 0,
            cancellationToken);

    /// <summary>
    /// Multi-turn attachment builder: delivers trigger-message images first, then fills remaining
    /// turn slots/bytes with the most recent bound images from earlier turns in the same session.
    /// </summary>
    internal static Task<AttachmentImageInput> BuildWithRecentContextAsync(
        IMessageAttachmentStore store,
        Guid sessionId,
        Guid triggerMessageId,
        ChatResolution resolution,
        CancellationToken cancellationToken,
        int maxRecentHistoricalImages = DefaultMaxRecentHistoricalImages,
        long maxRecentHistoricalBytes = DefaultMaxRecentHistoricalBytes) =>
        BuildCoreAsync(
            store,
            sessionId,
            triggerMessageId,
            resolution,
            includeRecentSessionImages: maxRecentHistoricalImages > 0 && maxRecentHistoricalBytes > 0,
            maxRecentHistoricalImages: Math.Max(0, maxRecentHistoricalImages),
            maxRecentHistoricalBytes: Math.Max(0, maxRecentHistoricalBytes),
            cancellationToken);

    private static async Task<AttachmentImageInput> BuildCoreAsync(
        IMessageAttachmentStore store,
        Guid sessionId,
        Guid triggerMessageId,
        ChatResolution resolution,
        bool includeRecentSessionImages,
        int maxRecentHistoricalImages,
        long maxRecentHistoricalBytes,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) return AttachmentImageInput.Empty;

        var allImageRows = (await store.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .Where(row => row.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (allImageRows.Length == 0) return AttachmentImageInput.Empty;

        var triggerRows = triggerMessageId == Guid.Empty
            ? []
            : allImageRows
                .Where(row => row.MessageId == triggerMessageId)
                .OrderBy(row => row.CreatedAt)
                .ToArray();

        var recentCandidates = !includeRecentSessionImages
            ? []
            : allImageRows
                .Where(row => row.MessageId is not null && row.MessageId != triggerMessageId)
                .OrderByDescending(row => row.CreatedAt)
                .Take(maxRecentHistoricalImages)
                .Reverse()
                .ToArray();

        if (triggerRows.Length == 0 && recentCandidates.Length == 0)
            return AttachmentImageInput.Empty;

        var notes = new List<string>();
        if (!resolution.SupportsImageInput)
        {
            notes.AddRange(triggerRows.Select(row => NotDelivered(row, "the resolved model does not accept image input")));
            notes.AddRange(recentCandidates.Select(row => NotDelivered(row, "the resolved model does not accept image input (prior turn attachment)")));
            return new([], [], NoteBlock(notes), 0);
        }

        var parts = new List<AIContent>();
        var delivered = new List<Guid>();
        long total = 0;
        var maxPerTurn = Math.Max(1, resolution.MaxImagesPerTurn);
        var maxPerImage = resolution.MaxImageBytes > 0 ? resolution.MaxImageBytes : MaxTotalBytesPerTurn;

        foreach (var row in triggerRows)
        {
            var added = await TryAppendStoredRowAsync(
                store, row, maxPerTurn, maxPerImage, MaxTotalBytesPerTurn, parts, delivered, notes, total, isHistorical: false, cancellationToken)
                .ConfigureAwait(false);
            total += added;
        }

        if (recentCandidates.Length > 0)
        {
            long recentBytes = 0;
            var recentNames = new List<string>();
            foreach (var row in recentCandidates)
            {
                if (parts.Count >= maxPerTurn || total >= MaxTotalBytesPerTurn || recentBytes >= maxRecentHistoricalBytes)
                {
                    notes.Add(NotDelivered(row, "omitted from recent multi-turn image context to stay within turn image budget"));
                    continue;
                }

                var allowedTotal = Math.Min(MaxTotalBytesPerTurn, total + (maxRecentHistoricalBytes - recentBytes));
                var added = await TryAppendStoredRowAsync(
                    store, row, maxPerTurn, maxPerImage, allowedTotal, parts, delivered, notes, total, isHistorical: true, cancellationToken)
                    .ConfigureAwait(false);
                if (added > 0)
                {
                    total += added;
                    recentBytes += added;
                    recentNames.Add($"'{row.FileName}'");
                }
            }

            if (recentNames.Count > 0)
            {
                notes.Add($"- Recent session image context included from earlier turns: {string.Join(", ", recentNames)}.");
            }
        }

        return new(parts, delivered, NoteBlock(notes), total);
    }

    private static async Task<long> TryAppendStoredRowAsync(
        IMessageAttachmentStore store,
        StoredAttachment row,
        int maxImagesPerTurn,
        long maxImageBytes,
        long maxTotalBytes,
        List<AIContent> parts,
        List<Guid> delivered,
        List<string> notes,
        long currentTotal,
        bool isHistorical,
        CancellationToken cancellationToken)
    {
        if (!SupportedMediaTypes.Contains(row.MediaType))
        {
            notes.Add(NotDelivered(row, $"media type '{row.MediaType}' is not supported for image input"));
            return 0;
        }
        if (parts.Count >= maxImagesPerTurn)
        {
            notes.Add(NotDelivered(row, $"the per-turn limit of {maxImagesPerTurn} images was reached"));
            return 0;
        }
        if (row.ContentLength > maxImageBytes)
        {
            notes.Add(NotDelivered(row, $"the image is {row.ContentLength} bytes; the per-image limit is {maxImageBytes}"));
            return 0;
        }
        if (currentTotal + row.ContentLength > maxTotalBytes)
        {
            notes.Add(NotDelivered(row, isHistorical
                ? "the recent multi-turn image budget was reached"
                : $"the turn image budget of {MaxTotalBytesPerTurn} bytes was reached"));
            return 0;
        }

        await using var stream = await store.OpenContentAsync(row, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            notes.Add(NotDelivered(row, "the stored image bytes are unavailable"));
            return 0;
        }

        var bytes = await ReadBoundedAsync(stream, Math.Min(maxImageBytes, maxTotalBytes - currentTotal), cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            notes.Add(NotDelivered(row, "the stored image exceeded the delivery budget"));
            return 0;
        }

        parts.Add(new DataContent(bytes, row.MediaType) { Name = row.FileName });
        delivered.Add(row.Id);
        return bytes.Length;
    }

    /// <summary>
    /// Extracts visual payloads from a tool execution result (MCP image content blocks, browser
    /// base64/data-URI screenshots, session attachment ids, or screenshot file paths), strips raw
    /// base64 blobs from the JSON text so token budgets stay intact, and converts allowed images to
    /// <see cref="DataContent"/> when <paramref name="resolution"/> supports vision (or produces a
    /// clear text fallback note when vision is disabled).
    /// </summary>
    internal static async Task<ToolImageExtractionResult> ExtractToolResultImagesAsync(
        string toolId,
        string rawResultJson,
        ChatResolution resolution,
        IMessageAttachmentStore? store,
        Guid sessionId,
        int remainingImageSlots,
        long remainingByteBudget,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawResultJson))
            return new(rawResultJson, [], [], string.Empty);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawResultJson);
        }
        catch (JsonException)
        {
            return new(rawResultJson, [], [], string.Empty);
        }

        if (root is not JsonObject obj)
            return new(rawResultJson, [], [], string.Empty);

        var candidates = new List<(string Name, string MediaType, byte[]? InlineBytes, Guid? AttachmentId, string? FilePath)>();
        var sanitized = false;

        // 1. MCP-style content array: { "content": [ { "type": "image", "mimeType": "image/png", "data": "..." } ] }
        if (obj["content"] is JsonArray contentArr)
        {
            var idx = 0;
            foreach (var item in contentArr)
            {
                idx++;
                if (item is not JsonObject block) continue;
                var type = block["type"]?.GetValue<string>();
                if (!string.Equals(type, "image", StringComparison.OrdinalIgnoreCase)) continue;
                var mime = (block["mimeType"] ?? block["mime_type"] ?? block["media_type"])?.GetValue<string>() ?? "image/png";
                var base64Node = block["data"] ?? block["base64"];
                if (base64Node is JsonValue val && val.TryGetValue<string>(out var b64) && TryDecodeBase64Image(b64, mime, out var decoded, out var resolvedMime))
                {
                    var label = block["name"]?.GetValue<string>() ?? $"{toolId}-image-{idx}.{ExtensionForMime(resolvedMime)}";
                    candidates.Add((label, resolvedMime, decoded, null, null));
                    if (block.ContainsKey("data")) block["data"] = $"[binary image omitted from text ({resolvedMime}, {decoded.Length} bytes)]";
                    if (block.ContainsKey("base64")) block["base64"] = $"[binary image omitted from text ({resolvedMime}, {decoded.Length} bytes)]";
                    sanitized = true;
                }
            }
        }

        // 2. Direct root base64 / data URI fields on browser/screenshot tools.
        foreach (var field in new[] { "screenshot", "image", "data_url", "image_url", "image_base64", "screenshot_base64" })
        {
            if (obj[field] is JsonValue val && val.TryGetValue<string>(out var rawText) && !string.IsNullOrWhiteSpace(rawText))
            {
                var explicitMime = (obj["mime_type"] ?? obj["mimeType"] ?? obj["media_type"])?.GetValue<string>();
                if (TryDecodeBase64Image(rawText, explicitMime, out var decoded, out var resolvedMime))
                {
                    var label = obj["file_name"]?.GetValue<string>() ?? $"{toolId}-{field}.{ExtensionForMime(resolvedMime)}";
                    candidates.Add((label, resolvedMime, decoded, null, null));
                    obj[field] = $"[binary image omitted from text ({resolvedMime}, {decoded.Length} bytes)]";
                    sanitized = true;
                }
            }
        }

        // 3. Root `{ "type": "image", "data": "<base64>", "mime_type": "image/png" }`
        if (string.Equals(obj["type"]?.GetValue<string>(), "image", StringComparison.OrdinalIgnoreCase)
            && (obj["data"] ?? obj["base64"]) is JsonValue rootVal
            && rootVal.TryGetValue<string>(out var rootB64))
        {
            var explicitMime = (obj["mime_type"] ?? obj["mimeType"] ?? obj["media_type"])?.GetValue<string>() ?? "image/png";
            if (TryDecodeBase64Image(rootB64, explicitMime, out var decoded, out var resolvedMime))
            {
                var label = obj["name"]?.GetValue<string>() ?? $"{toolId}-image.{ExtensionForMime(resolvedMime)}";
                candidates.Add((label, resolvedMime, decoded, null, null));
                if (obj.ContainsKey("data")) obj["data"] = $"[binary image omitted from text ({resolvedMime}, {decoded.Length} bytes)]";
                if (obj.ContainsKey("base64")) obj["base64"] = $"[binary image omitted from text ({resolvedMime}, {decoded.Length} bytes)]";
                sanitized = true;
            }
        }

        // 4. Session attachment reference (`attachment_id`) when pointing to an image attachment.
        if (store is not null && sessionId != Guid.Empty
            && obj["attachment_id"] is JsonValue attVal
            && attVal.TryGetValue<string>(out var attStr)
            && Guid.TryParse(attStr, out var attId))
        {
            var sessionRows = await store.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var match = sessionRows.FirstOrDefault(r => r.Id == attId && r.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                candidates.Add((match.FileName, match.MediaType, null, match.Id, null));
            }
        }

        // 5. Local screenshot file path (`screenshot_path` / `image_path`).
        foreach (var pathField in new[] { "screenshot_path", "image_path" })
        {
            if (obj[pathField] is JsonValue pVal && pVal.TryGetValue<string>(out var filePath)
                && !string.IsNullOrWhiteSpace(filePath)
                && MimeForExtension(Path.GetExtension(filePath)) is { } fileMime
                && File.Exists(filePath))
            {
                candidates.Add((Path.GetFileName(filePath), fileMime, null, null, filePath));
            }
        }

        if (candidates.Count == 0)
            return new(rawResultJson, [], [], string.Empty);

        var sanitizedJson = sanitized ? obj.ToJsonString() : rawResultJson;
        var notes = new List<string>();
        if (!resolution.SupportsImageInput)
        {
            foreach (var c in candidates)
            {
                notes.Add($"[Tool '{toolId}' produced image '{c.Name}' ({c.MediaType}), which was NOT delivered as visual input because the resolved model does not accept image input. Do not claim to have seen the image.]");
            }
            return new(sanitizedJson, [], [], string.Join(" ", notes));
        }

        var parts = new List<AIContent>();
        var deliveredIds = new List<Guid>();
        var maxPerImage = resolution.MaxImageBytes > 0 ? resolution.MaxImageBytes : MaxTotalBytesPerTurn;
        long usedBytes = 0;

        foreach (var candidate in candidates)
        {
            if (!SupportedMediaTypes.Contains(candidate.MediaType))
            {
                notes.Add($"[Tool image '{candidate.Name}' ({candidate.MediaType}) skipped: unsupported media type.]");
                continue;
            }
            if (parts.Count >= remainingImageSlots)
            {
                notes.Add($"[Tool image '{candidate.Name}' skipped: per-turn image count limit reached.]");
                continue;
            }

            byte[]? bytes = candidate.InlineBytes;
            if (bytes is null && candidate.AttachmentId is { } aid && store is not null)
            {
                var row = (await store.ListAsync(sessionId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Id == aid);
                if (row is not null)
                {
                    await using var stream = await store.OpenContentAsync(row, cancellationToken).ConfigureAwait(false);
                    if (stream is not null)
                    {
                        bytes = await ReadBoundedAsync(stream, Math.Min(maxPerImage, remainingByteBudget - usedBytes), cancellationToken).ConfigureAwait(false);
                        if (bytes is not null) deliveredIds.Add(aid);
                    }
                }
            }
            else if (bytes is null && candidate.FilePath is { } fp && File.Exists(fp))
            {
                await using var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bytes = await ReadBoundedAsync(fs, Math.Min(maxPerImage, remainingByteBudget - usedBytes), cancellationToken).ConfigureAwait(false);
            }

            if (bytes is null || bytes.Length == 0 || bytes.Length > maxPerImage || usedBytes + bytes.Length > remainingByteBudget)
            {
                notes.Add($"[Tool image '{candidate.Name}' skipped: exceeded image byte budget.]");
                continue;
            }

            usedBytes += bytes.Length;
            parts.Add(new DataContent(bytes, candidate.MediaType) { Name = candidate.Name });
        }

        return new(sanitizedJson, parts, deliveredIds, string.Join(" ", notes));
    }

    private static bool TryDecodeBase64Image(string input, string? fallbackMime, out byte[] bytes, out string mediaType)
    {
        bytes = [];
        mediaType = fallbackMime ?? "image/png";
        var trimmed = input.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = trimmed.IndexOf(',');
            if (comma <= 5) return false;
            var header = trimmed[5..comma];
            var semi = header.IndexOf(';');
            var declaredMime = semi >= 0 ? header[..semi] : header;
            if (!declaredMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return false;
            mediaType = declaredMime;
            trimmed = trimmed[(comma + 1)..].Trim();
        }
        else if (string.IsNullOrWhiteSpace(fallbackMime) || !fallbackMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            if (fallbackMime is null && trimmed.Length >= 16 && Base64.IsValid(trimmed))
            {
                mediaType = "image/png";
            }
            else
            {
                return false;
            }
        }

        if (trimmed.Length < 4) return false;
        try
        {
            bytes = Convert.FromBase64String(trimmed);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ExtensionForMime(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/bmp" => "bmp",
        _ => "png"
    };

    private static string? MimeForExtension(string? ext) => ext?.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => null
    };

    private static string NotDelivered(StoredAttachment row, string reason) =>
        $"- Image '{row.FileName}' (attachment {row.Id}) was NOT delivered to the model: {reason}. Do not claim to have seen it.";

    private static string NoteBlock(IReadOnlyList<string> notes) => notes.Count == 0
        ? string.Empty
        : "\n\nAttachment delivery notes (authoritative):\n" + string.Join("\n", notes);

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0 || maxBytes > int.MaxValue) return null;
        using var buffer = new MemoryStream((int)Math.Min(maxBytes, int.MaxValue));
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
    }
}
