using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinadecTools.Runtime;

namespace TinadecTools.Tools.Browser;

public static class BrowserDriverKinds
{
    public const string BuiltinElectron = "builtin_electron";
    public const string EgoLite = "ego_lite";

    public static bool TryNormalize(string? driver, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(driver))
        {
            normalized = BuiltinElectron;
            return true;
        }

        var trimmed = driver.Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "builtin_electron" or "builtin" or "electron":
                normalized = BuiltinElectron;
                return true;
            case "ego_lite" or "ego-lite" or "egolite" or "ego":
                normalized = EgoLite;
                return true;
            default:
                normalized = string.Empty;
                return false;
        }
    }
}

/// <summary>
/// Resolved per-call TaskSpace &amp; driver context. Isolates concurrent Swarm teammates by
/// <c>(sessionId, runId, teammateHandle, taskSpace)</c> while keeping bare <c>sessionId</c>
/// identical when no run/teammate/taskSpace scope is active.
/// </summary>
public sealed record BrowserConnectorContext(
    string? RawSessionId,
    string? NamespaceKey,
    string? RunId,
    string? TeammateHandle,
    string? TaskSpace,
    string Driver,
    string? ProfileId,
    bool HasExplicitDriver)
{
    public static BrowserConnectorContext Resolve(
        string? sessionId,
        JsonElement? executionContext = null,
        string? taskSpaceOverride = null,
        string? driverOverride = null,
        string? profileIdOverride = null)
    {
        var ambient = ToolExecutionContext.Current;
        var runId = FirstNonEmpty(
            ambient?.RunId,
            JsonText(executionContext, "run_id"),
            Environment.GetEnvironmentVariable("TINADEC_RUN_ID"));

        var teammateHandle = FirstNonEmpty(
            JsonText(executionContext, "teammate_handle"),
            ambient?.String("swarm", "teammate_handle"),
            JsonNestedText(executionContext, "settings", "swarm", "teammate_handle"),
            Environment.GetEnvironmentVariable("TINADEC_TEAMMATE_HANDLE"));

        var taskSpace = FirstNonEmpty(
            taskSpaceOverride,
            ambient?.String("browser", "task_space"),
            JsonNestedText(executionContext, "settings", "browser", "task_space"),
            JsonNestedText(executionContext, "browser", "task_space"));

        var explicitDriver = FirstNonEmpty(
            driverOverride,
            ambient?.String("browser", "driver"),
            JsonNestedText(executionContext, "settings", "browser", "driver"),
            JsonNestedText(executionContext, "browser", "driver"),
            JsonNestedText(executionContext, "browser_binding", "driver"));

        var profileId = FirstNonEmpty(
            profileIdOverride,
            ambient?.String("browser", "profile_id"),
            JsonNestedText(executionContext, "settings", "browser", "profile_id"),
            JsonNestedText(executionContext, "browser", "profile_id"),
            JsonNestedText(executionContext, "browser_binding", "profile_id"));

        var namespaceKey = BuildNamespaceKey(sessionId, runId, teammateHandle, taskSpace);
        return new BrowserConnectorContext(
            RawSessionId: sessionId,
            NamespaceKey: namespaceKey,
            RunId: runId,
            TeammateHandle: teammateHandle,
            TaskSpace: taskSpace,
            Driver: explicitDriver ?? BrowserDriverKinds.BuiltinElectron,
            ProfileId: profileId,
            HasExplicitDriver: !string.IsNullOrWhiteSpace(explicitDriver));
    }

    public static string? NormalizeSessionId(string? sessionId)
    {
        var value = sessionId?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 200) return null;
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or ':' or '-')) return null;
        }
        return value;
    }

    public static string? BuildNamespaceKey(
        string? sessionId,
        string? runId = null,
        string? teammateHandle = null,
        string? taskSpace = null)
    {
        var baseId = NormalizeSessionId(sessionId);
        if (baseId is null) return null;

        var cleanRun = SanitizeSegment(runId);
        var cleanTeammate = SanitizeSegment(teammateHandle);
        var cleanSpace = SanitizeSegment(taskSpace);
        if (cleanRun is null && cleanTeammate is null && cleanSpace is null)
        {
            return baseId;
        }

        var builder = new StringBuilder(baseId);
        if (cleanRun is not null) builder.Append("::r:").Append(cleanRun);
        if (cleanTeammate is not null) builder.Append("::tm:").Append(cleanTeammate);
        if (cleanSpace is not null) builder.Append("::ts:").Append(cleanSpace);

        var composite = builder.ToString();
        if (composite.Length <= 180) return composite;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composite)))[..16].ToLowerInvariant();
        return $"{composite[..160].TrimEnd(':')}-{digest}";
    }

    private static string? SanitizeSegment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var item in candidates)
        {
            if (!string.IsNullOrWhiteSpace(item)) return item.Trim();
        }
        return null;
    }

    private static string? JsonText(JsonElement? root, string property)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj) return null;
        return obj.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
    }

    private static string? JsonNestedText(JsonElement? root, params string[] path)
    {
        if (root is not { ValueKind: JsonValueKind.Object } current) return null;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (!current.TryGetProperty(path[i], out var next) || next.ValueKind != JsonValueKind.Object) return null;
            current = next;
        }
        return current.TryGetProperty(path[^1], out var leaf) && leaf.ValueKind == JsonValueKind.String
            ? leaf.GetString()
            : null;
    }
}

public interface IBrowserConnector
{
    string DriverId { get; }

    Task<BrowserToolResult> SnapshotAsync(BrowserConnectorContext context, BrowserSnapshotArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> NavigateAsync(BrowserConnectorContext context, BrowserNavigateArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> TabsListAsync(BrowserConnectorContext context, CancellationToken cancellationToken);
    Task<BrowserToolResult> WaitAsync(BrowserConnectorContext context, BrowserWaitArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> ScreenshotAsync(BrowserConnectorContext context, BrowserScreenshotArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> ClickAsync(BrowserConnectorContext context, BrowserClickArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> TypeAsync(BrowserConnectorContext context, BrowserTypeArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> PressAsync(BrowserConnectorContext context, BrowserPressArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> ScrollAsync(BrowserConnectorContext context, BrowserScrollArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> HistoryAsync(BrowserConnectorContext context, BrowserHistoryArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> TabOpenAsync(BrowserConnectorContext context, BrowserTabOpenArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> TabSelectAsync(BrowserConnectorContext context, BrowserTabArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> TabCloseAsync(BrowserConnectorContext context, BrowserTabArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> BatchAsync(BrowserConnectorContext context, BrowserBatchArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> HandOffAsync(BrowserConnectorContext context, BrowserHandoffArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> TakeOverAsync(BrowserConnectorContext context, BrowserTakeoverArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> FinishAsync(BrowserConnectorContext context, BrowserFinishArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> DialogAsync(BrowserConnectorContext context, BrowserDialogArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> ProfilesListAsync(BrowserConnectorContext context, BrowserProfilesListArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> UploadAsync(BrowserConnectorContext context, BrowserUploadArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> DownloadSaveAsync(BrowserConnectorContext context, BrowserDownloadSaveArgs args, CancellationToken cancellationToken);
    Task<BrowserToolResult> RunAsync(BrowserConnectorContext context, BrowserRunArgs args, CancellationToken cancellationToken);
}

/// <summary>
/// Routes browser tool invocations to <c>builtin_electron</c> or <c>ego_lite</c> per session/context,
/// remembering the active driver for each namespaced TaskSpace when subsequent calls omit an override.
/// </summary>
public sealed class BrowserConnectorRouter
{
    private static readonly Lazy<BrowserConnectorRouter> LazyDefault = new(
        () => new BrowserConnectorRouter(
            new BuiltinElectronBrowserConnector(BrowserToolRuntime.Default),
            new EgoLiteBrowserConnector()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IBrowserConnector _builtinConnector;
    private readonly IBrowserConnector _egoLiteConnector;
    private readonly ConcurrentDictionary<string, string> _stickyDrivers = new(StringComparer.Ordinal);

    public BrowserConnectorRouter(IBrowserConnector builtinConnector, IBrowserConnector egoLiteConnector)
    {
        _builtinConnector = builtinConnector;
        _egoLiteConnector = egoLiteConnector;
    }

    public static BrowserConnectorRouter Default => LazyDefault.Value;

    public bool TrySelectConnector(
        BrowserConnectorContext context,
        out IBrowserConnector connector,
        out BrowserToolResult? error)
    {
        connector = _builtinConnector;
        error = null;

        string selectedDriver;
        if (context.HasExplicitDriver)
        {
            if (!BrowserDriverKinds.TryNormalize(context.Driver, out selectedDriver))
            {
                error = new BrowserToolResult
                {
                    Ok = false,
                    Code = "browser_driver_invalid",
                    Message = $"Unsupported browser driver '{context.Driver}'. Expected 'builtin_electron' or 'ego_lite'.",
                    Permanent = true,
                };
                return false;
            }

            if (context.NamespaceKey is { } ns)
            {
                _stickyDrivers[ns] = selectedDriver;
            }
        }
        else if (context.NamespaceKey is { } ns && _stickyDrivers.TryGetValue(ns, out var sticky))
        {
            selectedDriver = sticky;
        }
        else
        {
            selectedDriver = BrowserDriverKinds.BuiltinElectron;
        }

        connector = selectedDriver == BrowserDriverKinds.EgoLite ? _egoLiteConnector : _builtinConnector;
        return true;
    }

    public async Task<BrowserToolResult> ProfilesListAsync(
        BrowserConnectorContext context,
        BrowserProfilesListArgs args,
        CancellationToken cancellationToken)
    {
        var filter = args.Driver?.Trim();
        if (!string.IsNullOrEmpty(filter) && !string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!BrowserDriverKinds.TryNormalize(filter, out var singleDriver))
            {
                return new BrowserToolResult
                {
                    Ok = false,
                    Code = "browser_driver_invalid",
                    Message = $"Unsupported browser driver '{filter}'. Expected 'builtin_electron', 'ego_lite', or 'all'.",
                    Permanent = true,
                };
            }

            var target = singleDriver == BrowserDriverKinds.EgoLite ? _egoLiteConnector : _builtinConnector;
            return await target.ProfilesListAsync(context with { Driver = singleDriver, HasExplicitDriver = true }, args, cancellationToken)
                .ConfigureAwait(false);
        }

        var builtinResult = await _builtinConnector.ProfilesListAsync(context, args, cancellationToken).ConfigureAwait(false);
        var egoResult = await _egoLiteConnector.ProfilesListAsync(context, args, cancellationToken).ConfigureAwait(false);

        var combined = new List<BrowserProfileInfo>();
        if (builtinResult.Profiles is { Length: > 0 }) combined.AddRange(builtinResult.Profiles);
        if (egoResult.Ok && egoResult.Profiles is { Length: > 0 }) combined.AddRange(egoResult.Profiles);

        return new BrowserToolResult
        {
            Ok = true,
            Profiles = combined.ToArray(),
            EgoAvailable = egoResult.Ok,
            Message = egoResult.Ok ? null : egoResult.Message,
        };
    }
}
