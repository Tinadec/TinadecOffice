using System.Text.Json;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA;

/// <summary>
/// The image reference a capture tool leaves in its result JSON: a top-level <c>image</c> object
/// (<c>path</c>, <c>media_type</c>, <c>width</c>, <c>height</c>, <c>bytes</c>) pointing at a copy
/// already downscaled for model context. The full-resolution original stays in the tool's own
/// <c>path</c> field.
/// </summary>
internal sealed record ToolResultImageRef(string Path, string MediaType, int? Width, int? Height, long Bytes);

/// <summary>
/// Delivers tool-result images into the model conversation. The carrier is a user-role message
/// right after the tool result, not a part inside the Tool message: wire-probed against the
/// adapters this build ships (evidence in the computer-use design doc), both the openai-chat and
/// openai-responses adapters silently drop a DataContent placed in a Tool-role message — the tool
/// result goes out as a plain string and the image never reaches the provider. A user-role
/// DataContent is the one channel every protocol maps (it is how message attachments already
/// travel), so the screenshot rides that, captioned with the call id it belongs to.
/// <para>
/// Budget: only the most recent tool turns keep their images (see <see cref="KeepLastCarriers"/>),
/// at most <see cref="MaxImagesPerRequest"/> per request, each under <see cref="MaxToolImageBytes"/>
/// (whichever of that and the route's own limit is smaller). Every image that is not delivered —
/// unsupported route, out of budget, oversized, unreadable file — leaves a note inside the tool
/// result string telling the model what happened and what to do instead, so an undelivered
/// screenshot is never silently missing.
/// </para>
/// </summary>
internal static class ToolResultImages
{
    /// <summary>Prefix identifying a carrier message; the demotion walk keys on it.</summary>
    internal const string CarrierMarker = "[tool-image";

    /// <summary>
    /// Hard cap for one tool image. Attachment images may be 20 MB, but tool screenshots repeat
    /// every few turns; 1.5 MB bounds a 1568-long-edge PNG with headroom.
    /// </summary>
    internal const long MaxToolImageBytes = 1_572_864;

    /// <summary>How many of the most recent image-carrying tool turns keep their image.</summary>
    internal const int KeepLastCarriers = 2;

    /// <summary>Total images delivered in one request, budget turns included.</summary>
    internal const int MaxImagesPerRequest = 3;

    internal static ToolResultImageRef? Find(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object) return null;
            if (!image.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) return null;
            var mediaType = image.TryGetProperty("media_type", out var media) && media.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(media.GetString())
                ? media.GetString()!
                : "image/png";
            int? width = image.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null;
            int? height = image.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null;
            long bytes = image.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : 0;
            var refPath = path.GetString()!;
            if (refPath.Length == 0) return null;
            return new ToolResultImageRef(refPath, mediaType, width, height, bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which history indexes (turns whose result carries an image) still deliver it: the last
    /// <see cref="KeepLastCarriers"/> image turns, up to <see cref="MaxImagesPerRequest"/> images.
    /// A worker turn holds one call, so one turn maps to at most one image.
    /// </summary>
    internal static HashSet<int> PlanBudget(IReadOnlyList<ToolResultImageRef?> refs)
    {
        var keep = new HashSet<int>();
        var images = 0;
        for (var i = refs.Count - 1; i >= 0; i--)
        {
            if (refs[i] is null) continue;
            if (keep.Count >= KeepLastCarriers || images >= MaxImagesPerRequest) break;
            keep.Add(i);
            images++;
        }
        return keep;
    }

    internal static long PerImageLimit(ChatResolution resolution) =>
        Math.Min(resolution.MaxImageBytes, MaxToolImageBytes);

    /// <summary>
    /// Why this image will not be delivered, or null when it should be. The reason is embedded in
    /// the tool result string, so the model reads it exactly where it expects the screenshot.
    /// </summary>
    internal static string? NotDeliveredReason(ChatResolution? resolution, bool withinBudget, ToolResultImageRef image)
    {
        if (resolution is not { SupportsImageInput: true })
            return "当前模型路由不接受图像输入。";
        if (!withinBudget)
            return $"为控制上下文体积，仅最近 {KeepLastCarriers} 轮工具结果附带截图。";
        var limit = PerImageLimit(resolution);
        if (image.Bytes > limit)
            return $"截图 {image.Bytes} 字节，超过单图上限 {limit} 字节。";
        return null;
    }

    internal static string Caption(string callId, string toolId, ToolResultImageRef image) =>
        $"{CarrierMarker} {callId} {toolId}] 这是该工具结果的截图（降采样 PNG"
        + (image.Width is { } w && image.Height is { } h ? $"，{w}×{h}" : string.Empty)
        + "）。坐标换算请以工具结果里的 width/height 为准；原图完整保留在其 path 字段。";

    internal static string UndeliveredNote(string reason) =>
        $"[tool-image 未附送] {reason} 不要假装看到了界面内容；需要界面信息时改用 desktop_read_text / 窗口标题，或询问用户。";

    /// <summary>
    /// Builds the user-role carrier for one delivered image. Null when the file cannot be read —
    /// the caller then degrades to the undelivered note instead of failing the turn.
    /// </summary>
    internal static async Task<ChatMessage?> BuildCarrierAsync(
        string callId,
        string toolId,
        ToolResultImageRef image,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(image.Path, cancellationToken).ConfigureAwait(false);
            return new ChatMessage(ChatRole.User,
            [
                new TextContent(Caption(callId, toolId, image)),
                new DataContent(bytes, image.MediaType),
            ]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Online loops accumulate messages across rounds; this walks the carrier messages and strips
    /// the image from every carrier older than the last <see cref="KeepLastCarriers"/>, leaving the
    /// caption plus a note that the file still exists on disk.
    /// </summary>
    internal static void DemoteBeyondBudget(IList<ChatMessage> messages)
    {
        var carriers = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (IsCarrier(messages[i])) carriers.Add(i);
        }
        var excess = carriers.Count - KeepLastCarriers;
        for (var c = 0; c < excess; c++)
        {
            var index = carriers[c];
            var caption = messages[index].Contents.OfType<TextContent>().FirstOrDefault()?.Text ?? CarrierMarker;
            messages[index] = new ChatMessage(ChatRole.User,
                caption + "（此图已移出上下文以控制体积；文件仍在上面的 path。）");
        }
    }

    internal static bool IsCarrier(ChatMessage message) =>
        message.Role == ChatRole.User
        && message.Contents.FirstOrDefault() is TextContent text
        && text.Text.StartsWith(CarrierMarker, StringComparison.Ordinal);
}
