using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecTools.Tools.Browser;

public sealed class BrowserSnapshotArgs
{
    [JsonPropertyName("max_chars")] public int? MaxChars { get; set; }
}

public sealed class BrowserNavigateArgs
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("wait_ms")] public int? WaitMs { get; set; }
}

public sealed class BrowserWaitArgs
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("url_contains")] public string? UrlContains { get; set; }
    [JsonPropertyName("timeout_ms")] public int? TimeoutMs { get; set; }
}

public sealed class BrowserScreenshotArgs
{
    [JsonPropertyName("full_page")] public bool? FullPage { get; set; }
}

public sealed class BrowserClickArgs
{
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("click_count")] public int? ClickCount { get; set; }
}

public sealed class BrowserTypeArgs
{
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("submit")] public bool? Submit { get; set; }
}

public sealed class BrowserPressArgs
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("ref")] public int? Ref { get; set; }
}

public sealed class BrowserScrollArgs
{
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("delta_x")] public double? DeltaX { get; set; }
    [JsonPropertyName("delta_y")] public double? DeltaY { get; set; }
}

public sealed class BrowserHistoryArgs
{
    [JsonPropertyName("action")] public string? Action { get; set; }
}

public sealed class BrowserTabOpenArgs
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class BrowserTabArgs
{
    [JsonPropertyName("tab_id")] public string? TabId { get; set; }
}

public sealed class BrowserBatchStep
{
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("args")] public JsonElement? Args { get; set; }
}

public sealed class BrowserBatchArgs
{
    [JsonPropertyName("steps")] public BrowserBatchStep[]? Steps { get; set; }
}

public sealed class BrowserBatchStepResult
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class BrowserEmptyArgs
{
}

public sealed class BrowserTabInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
}

public sealed class BrowserNoticeInfo
{
    [JsonPropertyName("seq")] public long? Seq { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("permission")] public string? Permission { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>
/// The model-facing copy of a browser capture, mirroring the desktop tools' contract: a PNG the
/// Electron bridge already downscaled (long edge ≤1568) when the page was larger. Core loads
/// <c>path</c> into the model conversation; the full-resolution original stays in the result's
/// own path/bytes fields. Dimensions are optional — older bridges report path/bytes only.
/// </summary>
public sealed class BrowserImageInfo
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
}

/// <summary>
/// The single result shape every browser tool returns. Expected outcomes (including hard stops)
/// are `ok = false` with a stable `code`; the model is expected to stop and report those rather
/// than retry them. Notices are always included so a side effect (popup, download, permission
/// prompt) is never silently swallowed.
/// </summary>
public sealed class BrowserToolResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("permanent")] public bool? Permanent { get; set; }
    [JsonPropertyName("tab_id")] public string? TabId { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
    [JsonPropertyName("truncated")] public bool? Truncated { get; set; }
    [JsonPropertyName("ref_count")] public int? RefCount { get; set; }
    [JsonPropertyName("tabs")] public BrowserTabInfo[]? Tabs { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("image")] public BrowserImageInfo? Image { get; set; }
    [JsonPropertyName("ready")] public bool? Ready { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("verified")] public bool? Verified { get; set; }
    [JsonPropertyName("steps")] public BrowserBatchStepResult[]? Steps { get; set; }
    [JsonPropertyName("notices")] public BrowserNoticeInfo[]? Notices { get; set; }
}

/// <summary>One request to the Electron bridge. `Parameters` is the raw CDP params object.</summary>
internal sealed record BrowserBridgeCall(
    string Op,
    string SessionId,
    string? TabId = null,
    string? Method = null,
    JsonElement? Parameters = null,
    string? Url = null,
    bool? Accept = null,
    string? PromptText = null,
    bool? FullPage = null);

internal sealed class BrowserBridgeResponse
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public bool Permanent { get; init; }
    public JsonElement? Data { get; init; }

    public static BrowserBridgeResponse Failure(string code, string? message, bool permanent = true) =>
        new() { Ok = false, Code = code, Message = message, Permanent = permanent };
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserSnapshotArgs))]
[JsonSerializable(typeof(BrowserNavigateArgs))]
[JsonSerializable(typeof(BrowserWaitArgs))]
[JsonSerializable(typeof(BrowserScreenshotArgs))]
[JsonSerializable(typeof(BrowserClickArgs))]
[JsonSerializable(typeof(BrowserTypeArgs))]
[JsonSerializable(typeof(BrowserPressArgs))]
[JsonSerializable(typeof(BrowserScrollArgs))]
[JsonSerializable(typeof(BrowserHistoryArgs))]
[JsonSerializable(typeof(BrowserTabOpenArgs))]
[JsonSerializable(typeof(BrowserTabArgs))]
[JsonSerializable(typeof(BrowserBatchStep))]
[JsonSerializable(typeof(BrowserBatchArgs))]
[JsonSerializable(typeof(BrowserBatchStep[]))]
[JsonSerializable(typeof(BrowserBatchStepResult))]
[JsonSerializable(typeof(BrowserBatchStepResult[]))]
[JsonSerializable(typeof(BrowserEmptyArgs))]
[JsonSerializable(typeof(BrowserTabInfo))]
[JsonSerializable(typeof(BrowserNoticeInfo))]
[JsonSerializable(typeof(BrowserImageInfo))]
[JsonSerializable(typeof(BrowserToolResult))]
[JsonSerializable(typeof(BrowserTabInfo[]))]
[JsonSerializable(typeof(BrowserNoticeInfo[]))]
internal partial class BrowserJsonContext : JsonSerializerContext
{
}
