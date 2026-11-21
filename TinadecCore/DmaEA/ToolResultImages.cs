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
/// How a tool-result image reaches the provider for one route.
/// <para><see cref="NativeInToolResult"/>: the image rides the tool result itself in the
/// protocol's native form. Wire-probed (evidence in the computer-use design doc): the
/// function_call_output of the Responses API accepts a content-part array (input_text +
/// input_image) and the model demonstrably reads the image there — but every M.E.AI.OpenAI
/// adapter up to 10.11 drops a DataContent placed in a Tool-role message, so the native form is
/// produced by an HTTP-layer rewrite (<c>ToolImageRewriteHandler</c>) instead of the SDK.</para>
/// <para><see cref="UserCarrier"/>: a user-role message right after the tool result. Used for
/// every protocol without a (reachable) native tool-result image form.</para>
/// </summary>
internal enum ToolImageDeliveryMode
{
    NativeInToolResult,
    UserCarrier,
}

/// <summary>
/// Thrown when a Tool-role message carries image data but the resolved route cannot represent it
/// (no native form, carrier disabled) — the hard stop that replaces the adapters' old silent drop.
/// </summary>
internal sealed class ToolImageUnrepresentableException(string protocol, string callId)
    : InvalidOperationException(
        $"Route protocol '{protocol}' cannot carry the image of tool call '{callId}' in its tool result, and the user-carrier fallback is disabled for this route. The request was not sent: fix the delivery mode for this protocol instead of letting the adapter silently drop the image.")
{
    public string Protocol { get; } = protocol;
    public string CallId { get; } = callId;
}

/// <summary>
/// Delivers tool-result images into the model conversation. Two modes, mutually exclusive per
/// route (see <see cref="ToolImageDeliveryMode"/>): on the Responses API the image is rewritten
/// into the function_call_output as a native content-part array at the HTTP layer; everywhere
/// else it rides a user-role message right after the tool result — wire-probed, both the
/// openai-chat and openai-responses M.E.AI adapters silently drop a DataContent placed in a
/// Tool-role message, and a user-role DataContent is the one channel every protocol maps (it is
/// how message attachments already travel).
/// <para>
/// Budget: only the most recent tool turns keep their images (see <see cref="KeepLastCarriers"/>),
/// at most <see cref="MaxImagesPerRequest"/> per request, each under <see cref="MaxToolImageBytes"/>
/// (whichever of that and the route's own limit is smaller). Every image that is not delivered —
/// unsupported route, out of budget, oversized, unreadable file — leaves a note inside the tool
/// result string telling the model what happened and what to do instead, so an undelivered
/// screenshot is never silently missing. In native mode an out-of-budget image is additionally
/// stripped from the result JSON so the HTTP rewrite cannot pick it up.
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

    /// <summary>
    /// The delivery mode for one resolved route. Only the Responses API has a native (and
    /// backend-verified) tool-result image form; note the Anthropic Messages protocol has one on
    /// paper (image blocks inside tool_result) but that route is unreachable in this build — see
    /// the design doc's dependency note — so it stays on the carrier until that changes.
    /// </summary>
    internal static ToolImageDeliveryMode DeliveryModeFor(ChatResolution? resolution) =>
        resolution is { SupportsImageInput: true }
        && TinadecCore.Abstractions.Ports.ChatProtocols.Normalize(resolution.Protocol) == ChatProtocols.OpenAiResponses
            ? ToolImageDeliveryMode.NativeInToolResult
            : ToolImageDeliveryMode.UserCarrier;

    /// <summary>
    /// The hard stop that replaces the adapters' silent drop: a Tool-role message that still
    /// carries a DataContent is a bug — either it should be a native-mode result (the HTTP rewrite
    /// reads the image from the result JSON, never from the message) or the route has no native
    /// form and the carrier message is the only representation.
    /// </summary>
    internal static void AssertNoToolMessageImages(IReadOnlyList<ChatMessage> messages, ChatResolution? resolution)
    {
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Tool) continue;
            foreach (var content in message.Contents)
            {
                if (content is not DataContent) continue;
                var callId = message.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId ?? "(unknown)";
                throw new ToolImageUnrepresentableException(
                    ChatProtocols.Normalize(resolution?.Protocol ?? "chat"),
                    callId);
            }
        }
    }

    /// <summary>
    /// Native mode only: removes the <c>image</c> object from a tool-result JSON so the HTTP
    /// rewrite cannot turn an out-of-budget image into a native part. Everything else in the
    /// result survives byte-for-byte semantics (re-serialized compactly, like ModelFacingResult).
    /// Returns the input unchanged when there is nothing to strip.
    /// </summary>
    internal static string StripImageFromResult(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("image", out var image)
                || image.ValueKind != JsonValueKind.Object)
                return resultJson;
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("image")) continue;
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return resultJson;
        }
    }

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
