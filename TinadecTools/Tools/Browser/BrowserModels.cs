using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecTools.Tools.Browser;

public sealed class BrowserSnapshotArgs
{
    [JsonPropertyName("max_chars")] public int? MaxChars { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("root_ref")] public int? RootRef { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
}

public sealed class BrowserNavigateArgs
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("wait_ms")] public int? WaitMs { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
}

public sealed class BrowserWaitArgs
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("url_contains")] public string? UrlContains { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("timeout_ms")] public int? TimeoutMs { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserScreenshotArgs
{
    [JsonPropertyName("full_page")] public bool? FullPage { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserClickArgs
{
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("click_count")] public int? ClickCount { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserTypeArgs
{
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("submit")] public bool? Submit { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserPressArgs
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserScrollArgs
{
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("delta_x")] public double? DeltaX { get; set; }
    [JsonPropertyName("delta_y")] public double? DeltaY { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserHistoryArgs
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserTabOpenArgs
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
}

public sealed class BrowserTabArgs
{
    [JsonPropertyName("tab_id")] public string? TabId { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserTabsListArgs
{
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserBatchStep
{
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("args")] public JsonElement? Args { get; set; }
}

public sealed class BrowserBatchArgs
{
    [JsonPropertyName("steps")] public BrowserBatchStep[]? Steps { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserBatchStepResult
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class BrowserHandoffArgs
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
}

public sealed class BrowserTakeoverArgs
{
    [JsonPropertyName("space_id")] public string? SpaceId { get; set; }
    [JsonPropertyName("claim_user_space")] public bool? ClaimUserSpace { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
}

public sealed class BrowserFinishArgs
{
    [JsonPropertyName("keep")] public string[]? Keep { get; set; }
    [JsonPropertyName("space_id")] public string? SpaceId { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserDialogArgs
{
    [JsonPropertyName("accept")] public bool? Accept { get; set; }
    [JsonPropertyName("prompt_text")] public string? PromptText { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserProfilesListArgs
{
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserUploadArgs
{
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("paths")] public string[]? Paths { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserDownloadSaveArgs
{
    [JsonPropertyName("save_path")] public string? SavePath { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("timeout_ms")] public int? TimeoutMs { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
}

public sealed class BrowserRunStep
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("wait_ms")] public int? WaitMs { get; set; }
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("submit")] public bool? Submit { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("click_count")] public int? ClickCount { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("index")] public int? Index { get; set; }
    [JsonPropertyName("values")] public string[]? Values { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("delta_x")] public double? DeltaX { get; set; }
    [JsonPropertyName("delta_y")] public double? DeltaY { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("url_contains")] public string? UrlContains { get; set; }
    [JsonPropertyName("timeout_ms")] public int? TimeoutMs { get; set; }
    [JsonPropertyName("accept")] public bool? Accept { get; set; }
    [JsonPropertyName("prompt_text")] public string? PromptText { get; set; }
    [JsonPropertyName("attribute")] public string? Attribute { get; set; }
    [JsonPropertyName("multiple")] public bool? Multiple { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class BrowserRunArgs
{
    [JsonPropertyName("steps")] public BrowserRunStep[]? Steps { get; set; }
    [JsonPropertyName("include_snapshot")] public bool? IncludeSnapshot { get; set; }
    [JsonPropertyName("snapshot_scope")] public string? SnapshotScope { get; set; }
    [JsonPropertyName("max_snapshot_chars")] public int? MaxSnapshotChars { get; set; }
    [JsonPropertyName("include_screenshot")] public bool? IncludeScreenshot { get; set; }
    [JsonPropertyName("full_page_screenshot")] public bool? FullPageScreenshot { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }
}

public sealed class BrowserRunStepResult
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

public sealed class BrowserExtractionResult
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("selector")] public string? Selector { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("values")] public string[]? Values { get; set; }
}

public sealed class BrowserProfileInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("is_default")] public bool IsDefault { get; set; }
}

public sealed class BrowserDialogInfo
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("default_prompt")] public string? DefaultPrompt { get; set; }
    [JsonPropertyName("tab_id")] public string? TabId { get; set; }
}

public sealed class BrowserActionReceipt
{
    [JsonPropertyName("opened_tabs")] public BrowserTabInfo[]? OpenedTabs { get; set; }
    [JsonPropertyName("pending_dialog")] public BrowserDialogInfo? PendingDialog { get; set; }
    [JsonPropertyName("console_errors")] public string[]? ConsoleErrors { get; set; }
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
/// The single result shape every browser tool returns. Expected outcomes (including hard stops)
/// are `ok = false` with a stable `code`; the model is expected to stop and report those rather
/// than retry them. Notices and action receipts are always included so a side effect (popup,
/// download, permission prompt, JS dialog, console error) is never silently swallowed.
/// </summary>
public sealed class BrowserToolResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("permanent")] public bool? Permanent { get; set; }
    [JsonPropertyName("driver")] public string? Driver { get; set; }
    [JsonPropertyName("space_id")] public string? SpaceId { get; set; }
    [JsonPropertyName("task_space")] public string? TaskSpace { get; set; }
    [JsonPropertyName("control")] public string? Control { get; set; }
    [JsonPropertyName("tab_id")] public string? TabId { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("snapshot")] public string? Snapshot { get; set; }
    [JsonPropertyName("truncated")] public bool? Truncated { get; set; }
    [JsonPropertyName("ref_count")] public int? RefCount { get; set; }
    [JsonPropertyName("tabs")] public BrowserTabInfo[]? Tabs { get; set; }
    [JsonPropertyName("kept_tabs")] public string[]? KeptTabs { get; set; }
    [JsonPropertyName("closed_tabs")] public string[]? ClosedTabs { get; set; }
    [JsonPropertyName("profiles")] public BrowserProfileInfo[]? Profiles { get; set; }
    [JsonPropertyName("ego_available")] public bool? EgoAvailable { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("screenshot_path")] public string? ScreenshotPath { get; set; }
    [JsonPropertyName("suggested_filename")] public string? SuggestedFilename { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
    [JsonPropertyName("ready")] public bool? Ready { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("ref")] public int? Ref { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("verified")] public bool? Verified { get; set; }
    [JsonPropertyName("steps")] public BrowserBatchStepResult[]? Steps { get; set; }
    [JsonPropertyName("run_steps")] public BrowserRunStepResult[]? RunSteps { get; set; }
    [JsonPropertyName("extractions")] public BrowserExtractionResult[]? Extractions { get; set; }
    [JsonPropertyName("receipt")] public BrowserActionReceipt? Receipt { get; set; }
    [JsonPropertyName("opened_tabs")] public BrowserTabInfo[]? OpenedTabs { get; set; }
    [JsonPropertyName("pending_dialog")] public BrowserDialogInfo? PendingDialog { get; set; }
    [JsonPropertyName("console_errors")] public string[]? ConsoleErrors { get; set; }
    [JsonPropertyName("site_learning_path")] public string? SiteLearningPath { get; set; }
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
    bool? FullPage = null,
    string? TaskSpace = null,
    string? Reason = null,
    IReadOnlyList<string>? Keep = null,
    string? CdpSessionId = null);

internal sealed class BrowserBridgeResponse
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public bool Permanent { get; init; }
    public JsonElement? Data { get; init; }
    public BrowserDialogInfo? PendingDialog { get; init; }

    public static BrowserBridgeResponse Failure(
        string code,
        string? message,
        bool permanent = true,
        JsonElement? data = null,
        BrowserDialogInfo? pendingDialog = null) =>
        new()
        {
            Ok = false,
            Code = code,
            Message = message,
            Permanent = permanent,
            Data = data,
            PendingDialog = pendingDialog,
        };
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
[JsonSerializable(typeof(BrowserTabsListArgs))]
[JsonSerializable(typeof(BrowserBatchStep))]
[JsonSerializable(typeof(BrowserBatchArgs))]
[JsonSerializable(typeof(BrowserBatchStep[]))]
[JsonSerializable(typeof(BrowserBatchStepResult))]
[JsonSerializable(typeof(BrowserBatchStepResult[]))]
[JsonSerializable(typeof(BrowserHandoffArgs))]
[JsonSerializable(typeof(BrowserTakeoverArgs))]
[JsonSerializable(typeof(BrowserFinishArgs))]
[JsonSerializable(typeof(BrowserDialogArgs))]
[JsonSerializable(typeof(BrowserProfilesListArgs))]
[JsonSerializable(typeof(BrowserUploadArgs))]
[JsonSerializable(typeof(BrowserDownloadSaveArgs))]
[JsonSerializable(typeof(BrowserRunStep))]
[JsonSerializable(typeof(BrowserRunStep[]))]
[JsonSerializable(typeof(BrowserRunArgs))]
[JsonSerializable(typeof(BrowserRunStepResult))]
[JsonSerializable(typeof(BrowserRunStepResult[]))]
[JsonSerializable(typeof(BrowserExtractionResult))]
[JsonSerializable(typeof(BrowserExtractionResult[]))]
[JsonSerializable(typeof(BrowserProfileInfo))]
[JsonSerializable(typeof(BrowserProfileInfo[]))]
[JsonSerializable(typeof(BrowserDialogInfo))]
[JsonSerializable(typeof(BrowserActionReceipt))]
[JsonSerializable(typeof(BrowserEmptyArgs))]
[JsonSerializable(typeof(BrowserTabInfo))]
[JsonSerializable(typeof(BrowserNoticeInfo))]
[JsonSerializable(typeof(BrowserToolResult))]
[JsonSerializable(typeof(BrowserTabInfo[]))]
[JsonSerializable(typeof(BrowserNoticeInfo[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal partial class BrowserJsonContext : JsonSerializerContext
{
}
