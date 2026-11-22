namespace TinadecTools.Tools.Browser;

/// <summary>
/// Built-in Electron satellite-window browser connector (<c>driver = "builtin_electron"</c>),
/// delegating all operations to <see cref="BrowserToolRuntime"/> with TaskSpace-isolated session keys.
/// </summary>
public sealed class BuiltinElectronBrowserConnector : IBrowserConnector
{
    public const string BuiltinProfileId = "persist:tinadec-agent-browser";

    private readonly BrowserToolRuntime _runtime;

    internal BuiltinElectronBrowserConnector(BrowserToolRuntime runtime)
    {
        _runtime = runtime;
    }

    public string DriverId => BrowserDriverKinds.BuiltinElectron;

    public Task<BrowserToolResult> SnapshotAsync(BrowserConnectorContext context, BrowserSnapshotArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.SnapshotAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> NavigateAsync(BrowserConnectorContext context, BrowserNavigateArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.NavigateAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> TabsListAsync(BrowserConnectorContext context, CancellationToken cancellationToken) =>
        Tag(context, _runtime.TabsListAsync(EffectiveSessionId(context), cancellationToken));

    public Task<BrowserToolResult> WaitAsync(BrowserConnectorContext context, BrowserWaitArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.WaitAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> ScreenshotAsync(BrowserConnectorContext context, BrowserScreenshotArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.ScreenshotAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> ClickAsync(BrowserConnectorContext context, BrowserClickArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.ClickAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> TypeAsync(BrowserConnectorContext context, BrowserTypeArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.TypeAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> PressAsync(BrowserConnectorContext context, BrowserPressArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.PressAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> ScrollAsync(BrowserConnectorContext context, BrowserScrollArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.ScrollAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> HistoryAsync(BrowserConnectorContext context, BrowserHistoryArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.HistoryAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> TabOpenAsync(BrowserConnectorContext context, BrowserTabOpenArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.TabOpenAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> TabSelectAsync(BrowserConnectorContext context, BrowserTabArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.TabSelectAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> TabCloseAsync(BrowserConnectorContext context, BrowserTabArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.TabCloseAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> BatchAsync(BrowserConnectorContext context, BrowserBatchArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.BatchAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> HandOffAsync(BrowserConnectorContext context, BrowserHandoffArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.HandOffAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> TakeOverAsync(BrowserConnectorContext context, BrowserTakeoverArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.TakeOverAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> FinishAsync(BrowserConnectorContext context, BrowserFinishArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.FinishAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> DialogAsync(BrowserConnectorContext context, BrowserDialogArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.DialogAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> ProfilesListAsync(BrowserConnectorContext context, BrowserProfilesListArgs args, CancellationToken cancellationToken) =>
        Task.FromResult(new BrowserToolResult
        {
            Ok = true,
            Driver = DriverId,
            Profiles =
            [
                new BrowserProfileInfo
                {
                    Id = BuiltinProfileId,
                    Name = "Tinadec Built-In Agent Browser",
                    Driver = DriverId,
                    IsDefault = true,
                },
            ],
        });

    public Task<BrowserToolResult> UploadAsync(BrowserConnectorContext context, BrowserUploadArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.UploadAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> DownloadSaveAsync(BrowserConnectorContext context, BrowserDownloadSaveArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.DownloadSaveAsync(EffectiveSessionId(context), args, cancellationToken));

    public Task<BrowserToolResult> RunAsync(BrowserConnectorContext context, BrowserRunArgs args, CancellationToken cancellationToken) =>
        Tag(context, _runtime.RunAsync(EffectiveSessionId(context), args, cancellationToken));

    private static string? EffectiveSessionId(BrowserConnectorContext context) =>
        context.NamespaceKey ?? context.RawSessionId;

    private static async Task<BrowserToolResult> Tag(BrowserConnectorContext context, Task<BrowserToolResult> task)
    {
        var result = await task.ConfigureAwait(false);
        result.Driver ??= BrowserDriverKinds.BuiltinElectron;
        if (!string.IsNullOrEmpty(context.TaskSpace)) result.TaskSpace ??= context.TaskSpace;
        return result;
    }
}
