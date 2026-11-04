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

public sealed class DesktopClickArgs
{
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("window_id")] public long? WindowId { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
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
    [JsonPropertyName("applied")] public bool? Applied { get; set; }
    [JsonPropertyName("chars")] public int? Chars { get; set; }
    [JsonPropertyName("texts")] public DesktopTextInfo[]? Texts { get; set; }
    [JsonPropertyName("pid")] public int? Pid { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DesktopListWindowsArgs))]
[JsonSerializable(typeof(DesktopScreenshotArgs))]
[JsonSerializable(typeof(DesktopReadTextArgs))]
[JsonSerializable(typeof(DesktopClickArgs))]
[JsonSerializable(typeof(DesktopTypeArgs))]
[JsonSerializable(typeof(DesktopPressKeyArgs))]
[JsonSerializable(typeof(DesktopLaunchArgs))]
[JsonSerializable(typeof(DesktopToolResult))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;
