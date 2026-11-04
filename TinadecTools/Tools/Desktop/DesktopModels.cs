using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecTools.Tools.Desktop;

public sealed class DesktopListWindowsArgs
{
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("include_all")] public bool? IncludeAll { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class DesktopScreenshotArgs
{
    [JsonPropertyName("window_id")] public long? WindowId { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class DesktopReadTextArgs
{
    [JsonPropertyName("window_id")] public long WindowId { get; set; }
    [JsonPropertyName("max_chars")] public int? MaxChars { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class DesktopLaunchArgs
{
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    [JsonPropertyName("working_directory")] public string? WorkingDirectory { get; set; }
}

public sealed class DesktopFindElementArgs
{
    [JsonPropertyName("window_id")] public long WindowId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("automation_id")] public string? AutomationId { get; set; }
    [JsonPropertyName("control_type")] public string? ControlType { get; set; }
    [JsonPropertyName("class_name")] public string? ClassName { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class DesktopClickArgs
{
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("window_id")] public long? WindowId { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    /// <summary>Click by element: the index from this run's most recent desktop_find_element.</summary>
    [JsonPropertyName("element_index")] public int? ElementIndex { get; set; }
    /// <summary>Click by element: AutomationId match within this run's most recent desktop_find_element.</summary>
    [JsonPropertyName("element_automation_id")] public string? ElementAutomationId { get; set; }
}

public sealed class DesktopTypeArgs
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("window_id")] public long? WindowId { get; set; }
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class DesktopPressKeyArgs
{
    [JsonPropertyName("keys")] public string? Keys { get; set; }
    [JsonPropertyName("window_id")] public long? WindowId { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class DesktopBounds
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class DesktopWindowInfo
{
    [JsonPropertyName("window_id")] public long WindowId { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("process_name")] public string? ProcessName { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("class_name")] public string? ClassName { get; set; }
    [JsonPropertyName("bounds")] public DesktopBounds? Bounds { get; set; }
    [JsonPropertyName("is_foreground")] public bool IsForeground { get; set; }
    [JsonPropertyName("is_minimized")] public bool IsMinimized { get; set; }
}

public sealed class DesktopTextInfo
{
    [JsonPropertyName("window_id")] public long WindowId { get; set; }
    [JsonPropertyName("class_name")] public string? ClassName { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

/// <summary>
/// The model-facing copy of a capture: a PNG already downscaled so its long edge stays within
/// <see cref="DesktopToolRuntime.MaxContextImageEdge"/>. Core reads this object (path is what it
/// loads), so every field must survive AOT serialization; the full-resolution original stays in
/// the result's own path/bytes fields.
/// </summary>
public sealed class DesktopImageInfo
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
}

/// <summary>A plain {x, y} pair, in the space the surrounding object names.</summary>
public sealed class DesktopPointInfo
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
}

/// <summary>
/// One UI Automation element as returned by desktop_find_element: the semantic anchor for
/// clicking without model coordinates. bounds are physical screen pixels straight from UIA.
/// </summary>
public sealed class DesktopElementInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("automation_id")] public string? AutomationId { get; set; }
    [JsonPropertyName("control_type")] public string? ControlType { get; set; }
    [JsonPropertyName("class_name")] public string? ClassName { get; set; }
    [JsonPropertyName("bounds")] public DesktopBounds? Bounds { get; set; }
    [JsonPropertyName("is_enabled")] public bool? IsEnabled { get; set; }
    [JsonPropertyName("is_offscreen")] public bool? IsOffscreen { get; set; }
}

/// <summary>
/// Present only when the coordinate-compensation layer was enabled for a desktop_click: which
/// measured profile row mapped the reported coordinates onto the window, and where they landed.
/// The layer is host-configured and off by default; this object is its audit trail.
/// </summary>
public sealed class DesktopCompensationInfo
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("scale_x")] public double? ScaleX { get; set; }
    [JsonPropertyName("scale_y")] public double? ScaleY { get; set; }
    [JsonPropertyName("from")] public DesktopPointInfo? From { get; set; }
    [JsonPropertyName("to")] public DesktopPointInfo? To { get; set; }
    [JsonPropertyName("image")] public DesktopImageInfo? Image { get; set; }
}

/// <summary>
/// The semantic verification of a compensated click: read the target window's text before and
/// after, judge the click by whether the application's own words changed - never by pixel
/// diffs (focus rectangles and repaints made those lie) and never by the caller's self-report.
/// `verified` means text changed; `failed` means text was readable but unchanged (a miss, or
/// a click with no text-level effect - inspect the screenshots); `unverified` means no text
/// was readable, which is reported as what it is instead of pretending success.
/// </summary>
public sealed class DesktopVerificationInfo
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("before_path")] public string? BeforePath { get; set; }
    [JsonPropertyName("after_path")] public string? AfterPath { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>
/// The single result shape every desktop tool returns, mirroring the browser tool contract:
/// expected failures are `ok = false` with a stable `code`; the model stops and reports
/// them instead of retrying (write actions are never safe to retry blind).
/// </summary>
public sealed class DesktopToolResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("permanent")] public bool? Permanent { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("windows")] public DesktopWindowInfo[]? Windows { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("window")] public DesktopWindowInfo? Window { get; set; }
    [JsonPropertyName("window_id")] public long? WindowId { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("image")] public DesktopImageInfo? Image { get; set; }
    [JsonPropertyName("applied")] public bool? Applied { get; set; }
    [JsonPropertyName("chars")] public int? Chars { get; set; }
    [JsonPropertyName("texts")] public DesktopTextInfo[]? Texts { get; set; }
    [JsonPropertyName("pid")] public int? Pid { get; set; }
    [JsonPropertyName("compensation")] public DesktopCompensationInfo? Compensation { get; set; }
    [JsonPropertyName("verification")] public DesktopVerificationInfo? Verification { get; set; }
    [JsonPropertyName("elements")] public DesktopElementInfo[]? Elements { get; set; }
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("element")] public DesktopElementInfo? Element { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DesktopListWindowsArgs))]
[JsonSerializable(typeof(DesktopScreenshotArgs))]
[JsonSerializable(typeof(DesktopReadTextArgs))]
[JsonSerializable(typeof(DesktopFindElementArgs))]
[JsonSerializable(typeof(DesktopClickArgs))]
[JsonSerializable(typeof(DesktopTypeArgs))]
[JsonSerializable(typeof(DesktopPressKeyArgs))]
[JsonSerializable(typeof(DesktopLaunchArgs))]
[JsonSerializable(typeof(DesktopToolResult))]
[JsonSerializable(typeof(DesktopImageInfo))]
[JsonSerializable(typeof(DesktopPointInfo))]
[JsonSerializable(typeof(DesktopElementInfo))]
[JsonSerializable(typeof(DesktopCompensationInfo))]
[JsonSerializable(typeof(DesktopVerificationInfo))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;
