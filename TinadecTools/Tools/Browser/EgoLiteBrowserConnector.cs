using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using TinadecTools.Runtime;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tools.Browser;

public sealed record EgoProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut = false);

public interface IEgoLiteProcessRunner
{
    Task<EgoProcessResult> ExecuteAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class EgoLiteDiscoveryEnvironment
{
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    public bool IsMacOS { get; init; } = OperatingSystem.IsMacOS();
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Func<string, IEnumerable<string>> GetDirectories { get; init; } =
        path => Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;
    public string UserHome { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string ProgramFiles { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
}

internal sealed class ProcessEgoLiteRunner : IEgoLiteProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<EgoProcessResult> ExecuteAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var isBatchShim = OperatingSystem.IsWindows()
            && (cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var psi = new ProcessStartInfo
        {
            FileName = isBatchShim ? "cmd.exe" : cliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        if (isBatchShim)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cliPath);
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new EgoProcessResult(1, string.Empty, "Failed to start ego-browser process.");
            }
        }
        catch (Exception ex)
        {
            return new EgoProcessResult(127, string.Empty, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new EgoProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new EgoProcessResult(124, string.Empty, $"ego-browser timed out after {timeout.TotalMilliseconds:0} ms.", TimedOut: true);
        }
    }
}

/// <summary>
/// External <c>ego-lite</c> browser connector (<c>driver = "ego_lite"</c>).
///
/// Strictly follows the Windows UTF-8 <c>.mjs</c> module execution contract:
/// every browser round writes a new UTF-8 (no-BOM) <c>round-01.mjs</c>, <c>round-02.mjs</c>, ...
/// file and invokes <c>ego-browser nodejs -e "await import('file:///...')"</c> with forward slashes
/// and pure-ASCII URI characters — never passing multi-line, double-quoted, or non-ASCII scripts
/// inline or through PowerShell.
/// </summary>
public sealed class EgoLiteBrowserConnector : IBrowserConnector
{
    public const string ResultSentinel = "__TINADEC_EGO_RESULT__:";
    public const string MacOsDefaultBundleHelper =
        "/Applications/ego lite.app/Contents/Frameworks/ego Framework.framework/Versions/Current/Helpers/ego-browser";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex RefMarkerPattern = new(
        @"\[ref=(\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly IReadOnlyDictionary<string, string> UserControlReasonGuidance =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notifications"] = "A browser permission prompt for notifications has appeared.",
            ["location"] = "A browser permission prompt for location access has appeared.",
            ["camera"] = "A browser permission prompt for camera access has appeared.",
            ["microphone"] = "A browser permission prompt for microphone access has appeared.",
            ["pan_tilt_zoom_microphone"] = "A browser permission prompt for camera control and microphone access has appeared.",
            ["midi"] = "A browser permission prompt for MIDI device access has appeared.",
            ["bluetooth"] = "A browser device chooser for Bluetooth has appeared.",
            ["usb"] = "A browser device chooser for USB has appeared.",
            ["serial"] = "A browser port chooser for serial access has appeared.",
            ["hid"] = "A browser device chooser for HID has appeared.",
            ["protocol_handler"] = "A browser permission prompt for protocol handler registration has appeared.",
            ["clipboard"] = "A browser permission prompt for clipboard access has appeared.",
            ["local_network_access"] = "A browser permission prompt for local network access has appeared.",
            ["storage_access"] = "A browser permission prompt for storage access has appeared.",
            ["window_management"] = "A browser permission prompt for window management has appeared.",
            ["local_fonts"] = "A browser permission prompt for local font access has appeared.",
            ["permission_prompt"] = "A browser permission prompt or device chooser has appeared.",
        };

    private readonly IEgoLiteProcessRunner _runner;
    private readonly EgoLiteDiscoveryEnvironment _discoveryEnv;
    private readonly BrowserNavigationPolicy _defaultNavigationPolicy;
    private readonly string _roundsBaseDirectory;
    private readonly ConcurrentDictionary<string, EgoSpaceState> _spaces = new(StringComparer.Ordinal);
    private int _globalRoundCounter;

    public EgoLiteBrowserConnector()
        : this(
            new ProcessEgoLiteRunner(),
            new EgoLiteDiscoveryEnvironment(),
            new BrowserNavigationPolicy(),
            roundsBaseDirectory: null)
    {
    }

    internal EgoLiteBrowserConnector(
        IEgoLiteProcessRunner runner,
        EgoLiteDiscoveryEnvironment? discoveryEnvironment = null,
        BrowserNavigationPolicy? navigationPolicy = null,
        string? roundsBaseDirectory = null)
    {
        _runner = runner;
        _discoveryEnv = discoveryEnvironment ?? new EgoLiteDiscoveryEnvironment();
        _defaultNavigationPolicy = navigationPolicy ?? new BrowserNavigationPolicy();
        _roundsBaseDirectory = string.IsNullOrWhiteSpace(roundsBaseDirectory)
            ? BuildAsciiSafeRoundsRoot()
            : Path.GetFullPath(roundsBaseDirectory);
    }

    public string DriverId => BrowserDriverKinds.EgoLite;

    // ------------------------------------------------------------------ CLI auto-discovery

    public string? DiscoverCliPath(string? explicitOverride = null) =>
        DiscoverCliPath(_discoveryEnv, explicitOverride);

    public static string? DiscoverCliPath(EgoLiteDiscoveryEnvironment env, string? explicitOverride = null)
    {
        var configured = FirstNonEmpty(
            explicitOverride,
            ToolExecutionContext.Current?.String("browser", "ego_cli_path"),
            env.GetEnvironmentVariable("EGO_BROWSER_CLI"),
            env.GetEnvironmentVariable("EGO_BROWSER_REAL_E2E_CLI"));
        if (configured is not null)
        {
            if (env.FileExists(configured)) return configured;
            var onPath = SearchPath(env, configured);
            if (onPath is not null) return onPath;
        }

        if (env.IsMacOS)
        {
            if (env.FileExists(MacOsDefaultBundleHelper)) return MacOsDefaultBundleHelper;

            foreach (var appBundle in new[]
                     {
                         "/Applications/ego lite.app",
                         string.IsNullOrWhiteSpace(env.UserHome) ? null : CombineUnix(env.UserHome, "Applications/ego lite.app"),
                     })
            {
                if (string.IsNullOrEmpty(appBundle)) continue;
                var helper = FindMacOsFrameworkHelper(env, appBundle);
                if (helper is not null) return helper;
            }

            foreach (var candidate in new[]
                     {
                         string.IsNullOrWhiteSpace(env.UserHome) ? null : CombineUnix(env.UserHome, ".local/bin/ego-browser"),
                         "/opt/homebrew/bin/ego-browser",
                         "/usr/local/bin/ego-browser",
                     })
            {
                if (!string.IsNullOrEmpty(candidate) && env.FileExists(candidate)) return candidate;
            }
        }

        if (env.IsWindows)
        {
            var winCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(env.LocalAppData))
            {
                winCandidates.Add(Path.Combine(env.LocalAppData, "ego lite", "Application", "ego-browser.exe"));
                winCandidates.Add(Path.Combine(env.LocalAppData, "Programs", "ego lite", "ego-browser.exe"));
                winCandidates.Add(Path.Combine(env.LocalAppData, "Programs", "ego-lite", "ego-browser.exe"));
                winCandidates.Add(Path.Combine(env.LocalAppData, "ego-browser", "bin", "ego-browser.exe"));
                winCandidates.Add(Path.Combine(env.LocalAppData, "ego-browser", "bin", "ego-browser.cmd"));
            }
            if (!string.IsNullOrWhiteSpace(env.UserHome))
            {
                winCandidates.Add(Path.Combine(env.UserHome, ".local", "bin", "ego-browser.exe"));
                winCandidates.Add(Path.Combine(env.UserHome, ".local", "bin", "ego-browser.cmd"));
                winCandidates.Add(Path.Combine(env.UserHome, ".ego-browser", "bin", "ego-browser.exe"));
            }
            if (!string.IsNullOrWhiteSpace(env.ProgramFiles))
            {
                winCandidates.Add(Path.Combine(env.ProgramFiles, "ego lite", "Application", "ego-browser.exe"));
                winCandidates.Add(Path.Combine(env.ProgramFiles, "ego lite", "ego-browser.exe"));
            }

            foreach (var candidate in winCandidates)
            {
                if (env.FileExists(candidate)) return candidate;
            }
        }

        return SearchPath(env, "ego-browser");
    }

    /// <summary>
    /// Formats a local <c>.mjs</c> path into a forward-slash, pure-ASCII <c>file:///</c> URL
    /// suitable for <c>ego-browser nodejs -e "await import('file:///...')"</c>.
    /// </summary>
    public static string BuildForwardSlashFileUrl(string absoluteScriptPath)
    {
        var full = Path.GetFullPath(absoluteScriptPath);
        var uri = new Uri(full, UriKind.Absolute).AbsoluteUri;
        return uri.Replace("'", "%27", StringComparison.Ordinal);
    }

    public static string BuildImportEvalExpression(string absoluteScriptPath) =>
        $"await import('{BuildForwardSlashFileUrl(absoluteScriptPath)}')";

    public static string BuildCommandLinePreview(string cliPath, string absoluteScriptPath) =>
        $"{cliPath} nodejs -e \"{BuildImportEvalExpression(absoluteScriptPath)}\"";

    // ------------------------------------------------------------------ explicit TaskSpace lifecycle API

    public async Task<BrowserToolResult> CreateTaskSpaceAsync(
        BrowserConnectorContext context,
        string name,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail("browser_task_space_invalid", "TaskSpace name must be non-empty.", permanent: true);
        }
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var effectiveProfile = FirstNonEmpty(profileId, context.ProfileId, space.ProfileId);
            var script = new StringBuilder();
            AppendResultHelper(script);
            script.AppendLine($"const spaceName = {JsLiteral(name.Trim())};");
            if (!string.IsNullOrEmpty(effectiveProfile))
            {
                script.AppendLine($"const task = await taskSpace(spaceName, {{ profileId: {JsLiteral(effectiveProfile)} }});");
            }
            else
            {
                script.AppendLine("const task = await taskSpace(spaceName);");
            }
            script.AppendLine("const pages = await task.pages();");
            script.AppendLine("const activePage = pages[0]?.label || 'p1';");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), name: task.name, ownership: task.ownership || 'agent', active_page: activePage });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            space.Control = TaskSpaceControlState.Agent;
            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> ClaimTaskSpaceAsync(
        BrowserConnectorContext context,
        int numericSpaceId,
        string? rename = null,
        CancellationToken cancellationToken = default)
    {
        if (numericSpaceId <= 0)
        {
            return Fail("browser_task_space_invalid", "A positive numeric space id is required to claim an Ego Lite Space.", permanent: true);
        }
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var script = new StringBuilder();
            AppendResultHelper(script);
            if (!string.IsNullOrWhiteSpace(rename))
            {
                script.AppendLine($"await globalThis.ego.claimTaskSpace({numericSpaceId}, {JsLiteral(rename.Trim())});");
                script.AppendLine($"const task = await taskSpace({numericSpaceId});");
            }
            else
            {
                script.AppendLine($"const task = await claimTaskSpace({numericSpaceId});");
            }
            script.AppendLine("const existingPages = await task.pages();");
            script.AppendLine("const tabs = await task.tabs();");
            script.AppendLine("const page = existingPages[0] ?? (tabs[0] ? await task.adopt(tabs[0].page) : null);");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: page ? page.label : 'p1', tabs: await task.tabs() });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.NumericSpaceId = numericSpaceId;
            space.Control = TaskSpaceControlState.Agent;
            space.KnownRefs.Clear();
            UpdateSpaceFromPayload(space, exec.Payload);

            return new BrowserToolResult
            {
                Ok = true,
                Code = "browser_task_space_claimed",
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Tabs = ParseEgoTabs(exec.Payload),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ IBrowserConnector

    public async Task<BrowserToolResult> ProfilesListAsync(
        BrowserConnectorContext context,
        BrowserProfilesListArgs args,
        CancellationToken cancellationToken)
    {
        var cli = DiscoverCliPath();
        if (cli is null)
        {
            return new BrowserToolResult
            {
                Ok = false,
                Code = "ego_browser_unavailable",
                Driver = DriverId,
                EgoAvailable = false,
                Message = "Ego Lite (ego-browser CLI) was not found on this machine. Install Ego Lite from https://lite.ego.app/ and complete onboarding.",
                Permanent = true,
            };
        }

        var discoverySpace = GetSpace("__profiles__", null);
        await discoverySpace.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var script = new StringBuilder();
            AppendResultHelper(script);
            script.AppendLine("const list = typeof profiles === 'function'");
            script.AppendLine("  ? await profiles()");
            script.AppendLine("  : ((await globalThis.ego.listProfiles())?.profiles ?? []);");
            script.AppendLine("emitResult({ ok: true, profiles: list });");

            var exec = await RunRoundAsync(discoverySpace, script.ToString(), TimeSpan.FromSeconds(15), cancellationToken, cliOverride: cli)
                .ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            var list = new List<BrowserProfileInfo>();
            if (exec.Payload.TryGetProperty("profiles", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = JsonString(item, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add(new BrowserProfileInfo
                    {
                        Id = id,
                        Name = JsonString(item, "name") ?? id,
                        Driver = DriverId,
                        IsDefault = item.TryGetProperty("isDefault", out var def) && def.ValueKind == JsonValueKind.True,
                    });
                }
            }

            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                EgoAvailable = true,
                Profiles = list.ToArray(),
                Notices = exec.Notices,
            };
        }
        finally
        {
            discoverySpace.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> NavigateAsync(
        BrowserConnectorContext context,
        BrowserNavigateArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (!CurrentNavigationPolicy().TryNormalize(args.Url, out var url, out var urlError))
        {
            return Fail("browser_navigation_blocked", urlError, permanent: true);
        }

        var space = GetSpace(ns!, FirstNonEmpty(args.ProfileId, context.ProfileId));
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space);
            script.AppendLine($"const receipt = await page.goto({JsLiteral(url.ToString())});");
            script.AppendLine("await page.waitForLoadState();");
            AppendPageSummaryEmit(script, includeReceipt: true);

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromSeconds(35), cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.KnownRefs.Clear();
            UpdateSpaceFromPayload(space, exec.Payload);
            var tabUrl = JsonString(exec.Payload, "url") ?? url.ToString();
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = tabUrl,
                Title = JsonString(exec.Payload, "title"),
                Ready = true,
                SiteLearningPath = TryFindSiteLearningPath(tabUrl),
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> SnapshotAsync(
        BrowserConnectorContext context,
        BrowserSnapshotArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var egoScope = NormalizeEgoSnapshotScope(args.Scope);
        if (egoScope is null)
        {
            return Fail("browser_snapshot_invalid", "scope must be full_page, viewport, or subtree.", permanent: true);
        }
        if (egoScope == "subtree" && (args.RootRef is null || args.RootRef <= 0))
        {
            return Fail("browser_snapshot_invalid", "root_ref is required when scope is subtree.", permanent: true);
        }

        var space = GetSpace(ns!, FirstNonEmpty(args.ProfileId, context.ProfileId));
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted)
            {
                return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);
            }

            var maxChars = Math.Clamp(
                args.MaxChars ?? BrowserSnapshotBuilder.DefaultMaxChars,
                BrowserSnapshotBuilder.MinimumMaxChars,
                BrowserSnapshotBuilder.MaximumMaxChars);

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            if (egoScope == "subtree")
            {
                script.AppendLine($"const snap = await page.snapshot({{ scope: 'subtree', root: '@{args.RootRef!.Value}' }});");
            }
            else if (egoScope == "full_page")
            {
                script.AppendLine("const snap = await page.snapshot({ scope: 'full_page' });");
            }
            else
            {
                script.AppendLine("const snap = await page.snapshot();");
            }
            script.AppendLine("const text = typeof snap === 'string' ? snap : (snap?.content ?? String(snap ?? ''));");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: page.label, url: await page.url(), title: await page.title(), snapshot: text });");

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            var rawSnap = SanitizeEgoSnapshotText(JsonString(exec.Payload, "snapshot") ?? string.Empty);
            var truncated = false;
            if (rawSnap.Length > maxChars)
            {
                rawSnap = rawSnap[..maxChars] + "\n… snapshot truncated; raise max_chars or narrow the page with wait/scroll …";
                truncated = true;
            }

            var refs = RecordSnapshotRefs(space, rawSnap, mergeOnly: egoScope == "subtree");
            var tabUrl = JsonString(exec.Payload, "url");
            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = tabUrl,
                Title = JsonString(exec.Payload, "title"),
                Snapshot = rawSnap,
                Truncated = truncated,
                RefCount = refs,
                SiteLearningPath = TryFindSiteLearningPath(tabUrl),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TabsListAsync(BrowserConnectorContext context, CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted)
            {
                return new BrowserToolResult
                {
                    Ok = true,
                    Driver = DriverId,
                    Tabs = Array.Empty<BrowserTabInfo>(),
                };
            }

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceOnly(script, space, allowCreate: false);
            script.AppendLine("const tabs = await task.tabs();");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), tabs });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            var tabs = ParseEgoTabs(exec.Payload);
            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = tabs.FirstOrDefault(t => t.Active)?.Id ?? space.ActivePageLabel,
                Tabs = tabs,
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> WaitAsync(
        BrowserConnectorContext context,
        BrowserWaitArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (string.IsNullOrWhiteSpace(args.Text) && string.IsNullOrWhiteSpace(args.UrlContains) && string.IsNullOrWhiteSpace(args.Selector))
        {
            return Fail("browser_wait_invalid", "Provide text, url_contains, or selector to wait for.", permanent: true);
        }

        var timeoutMs = Math.Clamp(args.TimeoutMs ?? 15_000, 100, 60_000);
        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            if (!string.IsNullOrWhiteSpace(args.Selector))
            {
                script.AppendLine($"await page.waitForSelector({JsLiteral(args.Selector.Trim())}, {{ timeout: {timeoutMs} }});");
                script.AppendLine("emitResult({ ok: true, reason: 'selector', active_page: page.label });");
            }
            else if (!string.IsNullOrWhiteSpace(args.UrlContains))
            {
                script.AppendLine($"await page.waitForURL((u) => String(u).includes({JsLiteral(args.UrlContains.Trim())}), {{ timeout: {timeoutMs} }});");
                script.AppendLine("emitResult({ ok: true, reason: 'url', active_page: page.label });");
            }
            else
            {
                script.AppendLine($"await page.waitForSelector({JsLiteral("text=" + args.Text!.Trim())}, {{ timeout: {timeoutMs} }});");
                script.AppendLine("emitResult({ ok: true, reason: 'text', active_page: page.label });");
            }

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromMilliseconds(timeoutMs + 10_000), cancellationToken)
                .ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                TabId = space.ActivePageLabel,
                Reason = JsonString(exec.Payload, "reason"),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> ScreenshotAsync(
        BrowserConnectorContext context,
        BrowserScreenshotArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var shotsDir = Path.Combine(_roundsBaseDirectory, "shots");
            Directory.CreateDirectory(shotsDir);
            var targetFile = Path.Combine(shotsDir, $"ego-shot-{Guid.NewGuid():N}.png");
            var forwardSlashShot = targetFile.Replace('\\', '/');

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            var fullPageOpt = args.FullPage == true ? ", fullPage: true" : string.Empty;
            script.AppendLine($"const saved = await page.screenshot({{ path: {JsLiteral(forwardSlashShot)}{fullPageOpt} }});");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: page.label, url: await page.url(), title: await page.title(), path: saved || " + JsLiteral(forwardSlashShot) + " });");

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            var resolvedShot = JsonString(exec.Payload, "path") ?? targetFile;
            var bytes = File.Exists(resolvedShot) ? new FileInfo(resolvedShot).Length : (long?)null;
            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = JsonString(exec.Payload, "url"),
                Title = JsonString(exec.Payload, "title"),
                Path = resolvedShot,
                ScreenshotPath = resolvedShot,
                Bytes = bytes,
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> ClickAsync(
        BrowserConnectorContext context,
        BrowserClickArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        var target = FormatEgoTarget(args.Ref, args.Selector);
        if (target is null) return Fail("browser_ref_required", "A ref or selector is required.", permanent: true);

        var button = (args.Button ?? "left").Trim().ToLowerInvariant();
        if (button is not ("left" or "right" or "middle"))
        {
            return Fail("browser_click_invalid", "button must be left, right, or middle.", permanent: true);
        }

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            if ((args.ClickCount ?? 1) >= 2 && button == "left")
            {
                script.AppendLine($"const receipt = await page.dblclick({JsLiteral(target)}, {{ label: 'double click element' }});");
            }
            else
            {
                script.AppendLine($"const receipt = await page.click({JsLiteral(target)}, {{ button: {JsLiteral(button)}, label: 'click target element' }});");
            }
            AppendPageSummaryEmit(script, includeReceipt: true);

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = JsonString(exec.Payload, "url"),
                Title = JsonString(exec.Payload, "title"),
                Ref = args.Ref,
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TypeAsync(
        BrowserConnectorContext context,
        BrowserTypeArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        var target = FormatEgoTarget(args.Ref, args.Selector);
        if (target is null) return Fail("browser_ref_required", "A ref or selector is required.", permanent: true);

        var text = args.Text ?? string.Empty;
        if (text.Length > 32_000) return Fail("browser_type_invalid", "Text exceeds 32000 characters.", permanent: true);

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            script.AppendLine($"const receipt = await page.fill({JsLiteral(target)}, {JsLiteral(text)});");
            if (args.Submit == true)
            {
                script.AppendLine("await page.keyboard.press('Enter');");
            }
            AppendPageSummaryEmit(script, includeReceipt: true);

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = JsonString(exec.Payload, "url"),
                Title = JsonString(exec.Payload, "title"),
                Ref = args.Ref,
                Value = text,
                Verified = true,
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> PressAsync(
        BrowserConnectorContext context,
        BrowserPressArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (string.IsNullOrWhiteSpace(args.Key)) return Fail("browser_key_unsupported", "A key name is required.", permanent: true);

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var target = FormatEgoTarget(args.Ref, args.Selector);
            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            if (target is not null)
            {
                script.AppendLine($"const receipt = await page.press({JsLiteral(target)}, {JsLiteral(args.Key.Trim())});");
            }
            else
            {
                script.AppendLine($"const receipt = await page.keyboard.press({JsLiteral(args.Key.Trim())});");
            }
            AppendPageSummaryEmit(script, includeReceipt: true);

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = JsonString(exec.Payload, "url"),
                Title = JsonString(exec.Payload, "title"),
                Ref = args.Ref,
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> ScrollAsync(
        BrowserConnectorContext context,
        BrowserScrollArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        var deltaX = Math.Clamp(args.DeltaX ?? 0, -2000, 2000);
        var deltaY = Math.Clamp(args.DeltaY ?? 0, -2000, 2000);
        if (deltaX == 0 && deltaY == 0) return Fail("browser_scroll_invalid", "Provide delta_x or delta_y.", permanent: true);

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var target = FormatEgoTarget(args.Ref, args.Selector);
            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            if (target is not null)
            {
                script.AppendLine($"await page.hover({JsLiteral(target)}, {{ label: 'move over scroll area' }});");
            }
            else
            {
                script.AppendLine("await page.mouse.move(480, 360, { label: 'center pointer for wheel' });");
            }
            script.AppendLine($"const receipt = await page.mouse.wheel({deltaX.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {deltaY.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {{ label: 'scroll page content' }});");
            AppendPageSummaryEmit(script, includeReceipt: true);

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = JsonString(exec.Payload, "url"),
                Title = JsonString(exec.Payload, "title"),
                Ref = args.Ref,
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> HistoryAsync(
        BrowserConnectorContext context,
        BrowserHistoryArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        var action = (args.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("back" or "forward" or "reload"))
        {
            return Fail("browser_history_invalid", "action must be back, forward, or reload.", permanent: true);
        }

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            if (action == "reload")
            {
                script.AppendLine("const receipt = await page.reload();");
            }
            else
            {
                var offset = action == "back" ? -1 : 1;
                script.AppendLine("const hist = await page.cdp('Page.getNavigationHistory', {});");
                script.AppendLine($"const targetIdx = (hist?.currentIndex ?? -1) + ({offset});");
                script.AppendLine("if (!Array.isArray(hist?.entries) || targetIdx < 0 || targetIdx >= hist.entries.length) {");
                script.AppendLine($"  emitResult({{ ok: false, code: 'browser_history_unavailable', message: 'There is no {action} entry.', permanent: false }});");
                script.AppendLine("  return;");
                script.AppendLine("}");
                script.AppendLine("await page.cdp('Page.navigateToHistoryEntry', { entryId: hist.entries[targetIdx].id });");
                script.AppendLine("const receipt = {};");
            }
            AppendPageSummaryEmit(script, includeReceipt: true);

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.KnownRefs.Clear();
            UpdateSpaceFromPayload(space, exec.Payload);
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = JsonString(exec.Payload, "url"),
                Title = JsonString(exec.Payload, "title"),
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TabOpenAsync(
        BrowserConnectorContext context,
        BrowserTabOpenArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        Uri? url = null;
        if (!string.IsNullOrWhiteSpace(args.Url))
        {
            if (!CurrentNavigationPolicy().TryNormalize(args.Url, out var parsed, out var navError))
            {
                return Fail("browser_navigation_blocked", navError, permanent: true);
            }
            url = parsed;
        }

        var space = GetSpace(ns!, FirstNonEmpty(args.ProfileId, context.ProfileId));
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceOnly(script, space, allowCreate: true);
            script.AppendLine("const page = await task.newPage();");
            if (url is not null)
            {
                script.AppendLine($"const receipt = await page.goto({JsLiteral(url.ToString())});");
            }
            else
            {
                script.AppendLine("const receipt = {};");
            }
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: page.label, url: await page.url(), title: await page.title(), tabs: await task.tabs(), receipt });");

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.KnownRefs.Clear();
            UpdateSpaceFromPayload(space, exec.Payload);
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Tabs = ParseEgoTabs(exec.Payload),
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TabSelectAsync(
        BrowserConnectorContext context,
        BrowserTabArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (string.IsNullOrWhiteSpace(args.TabId)) return Fail("browser_tab_invalid", "tab_id is required.", permanent: true);

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var wantedLabel = args.TabId.Trim();
            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceOnly(script, space, allowCreate: false);
            script.AppendLine("const tabs = await task.tabs();");
            script.AppendLine($"const match = tabs.find(t => t.label === {JsLiteral(wantedLabel)} || t.targetId === {JsLiteral(wantedLabel)});");
            script.AppendLine("if (!match) { emitResult({ ok: false, code: 'browser_tab_not_found', message: 'Tab not found.', permanent: true }); return; }");
            script.AppendLine("const page = match.label ? task.page(match.label) : await task.adopt(match.page);");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: page.label, tabs: await task.tabs() });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.KnownRefs.Clear();
            UpdateSpaceFromPayload(space, exec.Payload);
            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Tabs = ParseEgoTabs(exec.Payload),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TabCloseAsync(
        BrowserConnectorContext context,
        BrowserTabArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (string.IsNullOrWhiteSpace(args.TabId)) return Fail("browser_tab_invalid", "tab_id is required.", permanent: true);

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var wantedLabel = args.TabId.Trim();
            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceOnly(script, space, allowCreate: false);
            script.AppendLine("const pages = await task.pages();");
            script.AppendLine($"const found = pages.find(p => p.label === {JsLiteral(wantedLabel)});");
            script.AppendLine("if (!found) { emitResult({ ok: false, code: 'browser_tab_not_found', message: 'Managed page not found.', permanent: true }); return; }");
            script.AppendLine("await found.close();");
            script.AppendLine("const remaining = await task.pages();");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: remaining[0]?.label || null, tabs: await task.tabs() });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.KnownRefs.Clear();
            UpdateSpaceFromPayload(space, exec.Payload);
            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Tabs = ParseEgoTabs(exec.Payload),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> BatchAsync(
        BrowserConnectorContext context,
        BrowserBatchArgs args,
        CancellationToken cancellationToken)
    {
        if (args.Steps is not { Length: > 0 and <= 20 })
        {
            return Fail("browser_batch_invalid", "steps must contain between 1 and 20 actions.", permanent: true);
        }

        var stepResults = new List<BrowserBatchStepResult>(args.Steps.Length);
        BrowserToolResult? failure = null;

        for (var i = 0; i < args.Steps.Length; i++)
        {
            var step = args.Steps[i];
            var outcome = step.Tool switch
            {
                "browser_click" => await ClickAsync(context, DeserializeStep(step.Args, BrowserJsonContext.Default.BrowserClickArgs), cancellationToken).ConfigureAwait(false),
                "browser_type" => await TypeAsync(context, DeserializeStep(step.Args, BrowserJsonContext.Default.BrowserTypeArgs), cancellationToken).ConfigureAwait(false),
                "browser_press" => await PressAsync(context, DeserializeStep(step.Args, BrowserJsonContext.Default.BrowserPressArgs), cancellationToken).ConfigureAwait(false),
                "browser_scroll" => await ScrollAsync(context, DeserializeStep(step.Args, BrowserJsonContext.Default.BrowserScrollArgs), cancellationToken).ConfigureAwait(false),
                _ => Fail("browser_batch_invalid", "Each step must be browser_click, browser_type, browser_press, or browser_scroll.", permanent: true),
            };

            if (outcome.Code == "browser_batch_invalid" && i == 0) return outcome;
            stepResults.Add(new BrowserBatchStepResult
            {
                Index = i,
                Tool = step.Tool,
                Ok = outcome.Ok,
                Code = outcome.Code,
                Message = outcome.Message,
            });

            if (!outcome.Ok)
            {
                failure = outcome;
                break;
            }
        }

        var snap = await SnapshotAsync(context, new BrowserSnapshotArgs(), cancellationToken).ConfigureAwait(false);
        if (!snap.Ok) failure ??= snap;

        return new BrowserToolResult
        {
            Ok = failure is null,
            Code = failure?.Code,
            Message = failure?.Message,
            Permanent = failure?.Permanent,
            Driver = DriverId,
            SpaceId = snap.SpaceId,
            TaskSpace = context.TaskSpace,
            TabId = snap.TabId,
            Url = snap.Url,
            Title = snap.Title,
            Steps = stepResults.ToArray(),
            Snapshot = snap.Ok ? snap.Snapshot : null,
            Truncated = snap.Ok ? snap.Truncated : null,
            RefCount = snap.Ok ? snap.RefCount : null,
        };
    }

    public async Task<BrowserToolResult> HandOffAsync(
        BrowserConnectorContext context,
        BrowserHandoffArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var space = GetSpace(ns!, FirstNonEmpty(args.ProfileId, context.ProfileId));
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (space.Control == TaskSpaceControlState.Finished)
            {
                return FailTaskSpaceInactive(SpaceIdString(space));
            }

            var reason = string.IsNullOrWhiteSpace(args.Reason) ? "manual_takeover" : args.Reason.Trim();
            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceOnly(script, space, allowCreate: true);
            script.AppendLine("await task.handOff();");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId) });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            space.Control = TaskSpaceControlState.User;

            var message = string.IsNullOrWhiteSpace(args.Message)
                ? "Control of this Ego Lite TaskSpace is now with the user. Wait until the user confirms completion before calling browser_takeover."
                : args.Message.Trim();

            return new BrowserToolResult
            {
                Ok = true,
                Code = "browser_handoff_requested",
                Message = message,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "user",
                Reason = reason,
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> TakeOverAsync(
        BrowserConnectorContext context,
        BrowserTakeoverArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var space = GetSpace(ns!, FirstNonEmpty(args.ProfileId, context.ProfileId));
        if (!string.IsNullOrWhiteSpace(args.SpaceId) && int.TryParse(args.SpaceId.Trim(), out var explicitNumericId) && explicitNumericId > 0)
        {
            space.NumericSpaceId ??= explicitNumericId;
        }

        // If user explicitly approved claiming a user-owned/finished space with kept tabs, route to ClaimTaskSpaceAsync.
        if (args.ClaimUserSpace == true && space.NumericSpaceId is > 0 and var claimId)
        {
            return await ClaimTaskSpaceAsync(context, claimId, rename: null, cancellationToken).ConfigureAwait(false);
        }

        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Refuse resuming a finished/ended task space cleanly per ego-errors.ts (EGO_TASK_SPACE_INACTIVE).
            if (space.Control == TaskSpaceControlState.Finished)
            {
                return FailTaskSpaceInactive(SpaceIdString(space));
            }
            if (space.NumericSpaceId is null)
            {
                return Fail("browser_not_started", "No Ego Lite TaskSpace is active to take over.", permanent: true);
            }

            var script = new StringBuilder();
            AppendResultHelper(script);
            script.AppendLine($"const spaces = await listTaskSpaces();");
            script.AppendLine($"const entry = spaces.find(s => s.id === {space.NumericSpaceId.Value});");
            script.AppendLine("if (!entry) { emitResult({ ok: false, error_code: 'EGO_TASK_SPACE_NOT_FOUND', error: 'Task space not found.' }); return; }");
            script.AppendLine("if (entry.ownership === 'user') { emitResult({ ok: false, error_code: 'EGO_TASK_SPACE_INACTIVE', error: 'Task space has ended.' }); return; }");
            script.AppendLine($"const task = await takeOverTaskSpace({space.NumericSpaceId.Value});");
            script.AppendLine("const userPage = typeof task.userPage === 'function' ? task.userPage() : null;");
            script.AppendLine("if (userPage && !userPage.label) { await task.adopt(userPage); }");
            script.AppendLine("const pages = await task.pages();");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: pages[0]?.label || 'p1', tabs: await task.tabs() });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.Control = TaskSpaceControlState.Agent;
            space.KnownRefs.Clear();
            UpdateSpaceFromPayload(space, exec.Payload);

            return new BrowserToolResult
            {
                Ok = true,
                Code = "browser_control_resumed",
                Message = "Agent control resumed on Ego Lite TaskSpace. Take a fresh browser_snapshot before acting.",
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Tabs = ParseEgoTabs(exec.Payload),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> FinishAsync(
        BrowserConnectorContext context,
        BrowserFinishArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted)
            {
                space.Control = TaskSpaceControlState.Finished;
                return new BrowserToolResult
                {
                    Ok = true,
                    Code = "browser_task_space_completed",
                    Driver = DriverId,
                    Control = "finished",
                    KeptTabs = Array.Empty<string>(),
                    ClosedTabs = Array.Empty<string>(),
                };
            }

            var keepLabels = (args.Keep ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceOnly(script, space, allowCreate: false);
            script.AppendLine($"const keep = {SerializeStringArrayJs(keepLabels)};");
            script.AppendLine("const beforeTabs = await task.tabs();");
            script.AppendLine("await task.finish({ keep });");
            script.AppendLine("const closed = beforeTabs.map(t => t.label || t.targetId).filter(id => id && !keep.includes(id));");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), kept_tabs: keep, closed_tabs: closed });");

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            space.Control = TaskSpaceControlState.Finished;
            space.KnownRefs.Clear();

            return new BrowserToolResult
            {
                Ok = true,
                Code = "browser_task_space_completed",
                Message = keepLabels.Length > 0
                    ? $"Ego Lite TaskSpace finished; kept {keepLabels.Length} page(s) open for the user."
                    : "Ego Lite TaskSpace finished and intermediate pages closed.",
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "finished",
                KeptTabs = keepLabels,
                ClosedTabs = ReadPayloadStringArray(exec.Payload, "closed_tabs"),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> DialogAsync(
        BrowserConnectorContext context,
        BrowserDialogArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (args.Accept is not { } accept)
        {
            return Fail("browser_dialog_invalid", "accept (true or false) is required.", permanent: true);
        }

        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            if (accept)
            {
                if (args.PromptText is not null)
                {
                    script.AppendLine($"await page.acceptDialog({JsLiteral(args.PromptText)});");
                }
                else
                {
                    script.AppendLine("await page.acceptDialog();");
                }
            }
            else
            {
                script.AppendLine("await page.dismissDialog();");
            }
            AppendPageSummaryEmit(script, includeReceipt: false);

            var exec = await RunRoundAsync(space, script.ToString(), ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = JsonString(exec.Payload, "url"),
                Title = JsonString(exec.Payload, "title"),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> UploadAsync(
        BrowserConnectorContext context,
        BrowserUploadArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;

        var rawPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(args.Path)) rawPaths.Add(args.Path);
        if (args.Paths is { Length: > 0 }) rawPaths.AddRange(args.Paths.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (rawPaths.Count == 0)
        {
            return Fail("browser_upload_invalid", "Provide path or paths inside the allowed workspace.", permanent: true);
        }

        var resolvedForwardSlashPaths = new List<string>(rawPaths.Count);
        string? firstResolved = null;
        foreach (var raw in rawPaths)
        {
            string resolved;
            try
            {
                resolved = WorkspacePathResolver.ResolvePath(raw, allowFinalLink: false, writable: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
            {
                return Fail("browser_path_denied", ex.Message, permanent: true);
            }
            if (!File.Exists(resolved))
            {
                return Fail("browser_file_not_found", $"Upload file '{raw}' does not exist.", permanent: true);
            }
            firstResolved ??= resolved;
            resolvedForwardSlashPaths.Add(resolved.Replace('\\', '/'));
        }

        var target = FormatEgoTarget(args.Ref, args.Selector) ?? "input[type=file]";
        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;
            if (!space.IsStarted) return Fail("browser_not_started", "Open Ego Lite with browser_navigate first.", permanent: true);

            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: false);
            script.AppendLine($"const receipt = await page.setInputFiles({JsLiteral(target)}, {SerializeStringArrayJs(resolvedForwardSlashPaths)});");
            AppendPageSummaryEmit(script, includeReceipt: true);

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Path = firstResolved,
                Ref = args.Ref,
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> DownloadSaveAsync(
        BrowserConnectorContext context,
        BrowserDownloadSaveArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (string.IsNullOrWhiteSpace(args.SavePath))
        {
            return Fail("browser_download_invalid", "save_path inside the writable workspace is required.", permanent: true);
        }

        string resolvedSavePath;
        try
        {
            resolvedSavePath = WorkspacePathResolver.ResolvePath(args.SavePath, allowFinalLink: false, writable: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
        {
            return Fail("browser_path_denied", ex.Message, permanent: true);
        }

        Uri? triggerUrl = null;
        if (!string.IsNullOrWhiteSpace(args.Url))
        {
            if (!CurrentNavigationPolicy().TryNormalize(args.Url, out var parsed, out var navError))
            {
                return Fail("browser_navigation_blocked", navError, permanent: true);
            }
            triggerUrl = parsed;
        }

        var target = FormatEgoTarget(args.Ref, args.Selector);
        if (triggerUrl is null && target is null)
        {
            return Fail("browser_download_invalid", "Provide url, ref, or selector to trigger the download.", permanent: true);
        }

        var timeoutMs = Math.Clamp(args.TimeoutMs ?? 30_000, 500, 120_000);
        var space = GetSpace(ns!, context.ProfileId);
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;

            var forwardSavePath = resolvedSavePath.Replace('\\', '/');
            var script = new StringBuilder();
            AppendResultHelper(script);
            AppendAcquireSpaceAndActivePage(script, space, allowCreate: triggerUrl is not null);
            script.AppendLine($"const downloadPromise = page.waitForEvent('download', {{ timeout: {timeoutMs} }});");
            if (triggerUrl is not null)
            {
                script.AppendLine($"await page.goto({JsLiteral(triggerUrl.ToString())});");
            }
            else
            {
                script.AppendLine($"await page.click({JsLiteral(target!)}, {{ label: 'trigger file download' }});");
            }
            script.AppendLine("const download = await downloadPromise;");
            script.AppendLine($"await download.saveAs({JsLiteral(forwardSavePath)});");
            script.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: page.label, suggested_filename: download.suggestedFilename(), url: download.url() });");

            var exec = await RunRoundAsync(space, script.ToString(), TimeSpan.FromMilliseconds(timeoutMs + 15_000), cancellationToken)
                .ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            var bytes = File.Exists(resolvedSavePath) ? new FileInfo(resolvedSavePath).Length : (long?)null;
            if (bytes is { } fileBytes) ToolExecutionContext.CheckFileSize("write", fileBytes);

            return new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Path = resolvedSavePath,
                Bytes = bytes,
                SuggestedFilename = JsonString(exec.Payload, "suggested_filename"),
                Url = JsonString(exec.Payload, "url"),
                Notices = exec.Notices,
            };
        }
        finally
        {
            space.Gate.Release();
        }
    }

    public async Task<BrowserToolResult> RunAsync(
        BrowserConnectorContext context,
        BrowserRunArgs args,
        CancellationToken cancellationToken)
    {
        var ns = RequireNamespace(context, out var nsError);
        if (nsError is not null) return nsError;
        if (args.Steps is not { Length: > 0 and <= 30 })
        {
            return Fail("browser_run_invalid", "steps must contain between 1 and 30 workflow steps.", permanent: true);
        }

        // Pre-validate every step AND enforce BrowserNavigationPolicy on every `goto` step BEFORE writing any .mjs file!
        var policy = CurrentNavigationPolicy();
        var normalizedUrls = new string?[args.Steps.Length];
        for (var i = 0; i < args.Steps.Length; i++)
        {
            var step = args.Steps[i];
            var action = step.Action?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(action)
                || action is not ("goto" or "fill" or "click" or "select_option" or "press" or "scroll"
                    or "wait_for_selector" or "wait_for_url" or "wait_for_text" or "handle_dialog" or "extract"))
            {
                return Fail("browser_run_invalid", $"Step {i} has unsupported action '{step.Action}'.", permanent: true);
            }

            if (action == "goto")
            {
                if (!policy.TryNormalize(step.Url, out var uri, out var navError))
                {
                    return Fail("browser_navigation_blocked", $"Step {i} (goto): {navError}", permanent: true);
                }
                normalizedUrls[i] = uri.ToString();
            }
            else if (action == "extract" && string.IsNullOrWhiteSpace(step.Selector))
            {
                return Fail("browser_run_invalid", $"Step {i} (extract) requires selector.", permanent: true);
            }
            else if (action == "handle_dialog" && step.Accept is null)
            {
                return Fail("browser_run_invalid", $"Step {i} (handle_dialog) requires accept (boolean).", permanent: true);
            }
        }

        var space = GetSpace(ns!, FirstNonEmpty(args.ProfileId, context.ProfileId));
        await space.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var guard = CheckControlBoundary(space);
            if (guard is not null) return guard;

            string? shotTarget = null;
            if (args.IncludeScreenshot == true)
            {
                var shotsDir = Path.Combine(_roundsBaseDirectory, "shots");
                Directory.CreateDirectory(shotsDir);
                shotTarget = Path.Combine(shotsDir, $"ego-run-{Guid.NewGuid():N}.png");
            }

            var script = BuildWorkflowMjsScript(space, args, normalizedUrls, shotTarget);
            var exec = await RunRoundAsync(space, script, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if (exec.Failure is not null) return exec.Failure;

            UpdateSpaceFromPayload(space, exec.Payload);
            var rawSnap = SanitizeEgoSnapshotText(JsonString(exec.Payload, "snapshot") ?? string.Empty);
            var maxChars = Math.Clamp(
                args.MaxSnapshotChars ?? BrowserSnapshotBuilder.DefaultMaxChars,
                BrowserSnapshotBuilder.MinimumMaxChars,
                BrowserSnapshotBuilder.MaximumMaxChars);
            var truncated = false;
            if (rawSnap.Length > maxChars)
            {
                rawSnap = rawSnap[..maxChars] + "\n… snapshot truncated; raise max_chars or narrow the page with wait/scroll …";
                truncated = true;
            }
            var refCount = string.IsNullOrEmpty(rawSnap) ? (int?)null : RecordSnapshotRefs(space, rawSnap, mergeOnly: false);

            var runSteps = ParseRunStepResults(exec.Payload);
            var extractions = ParseExtractions(exec.Payload);
            var resolvedShot = JsonString(exec.Payload, "screenshot_path") ?? shotTarget;
            var shotBytes = !string.IsNullOrEmpty(resolvedShot) && File.Exists(resolvedShot)
                ? new FileInfo(resolvedShot).Length
                : (long?)null;
            var finalUrl = JsonString(exec.Payload, "url");

            return AttachPayloadReceipt(new BrowserToolResult
            {
                Ok = true,
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                TaskSpace = context.TaskSpace,
                Control = "agent",
                TabId = space.ActivePageLabel,
                Url = finalUrl,
                Title = JsonString(exec.Payload, "title"),
                RunSteps = runSteps,
                Extractions = extractions.Length > 0 ? extractions : null,
                Snapshot = string.IsNullOrEmpty(rawSnap) ? null : rawSnap,
                Truncated = string.IsNullOrEmpty(rawSnap) ? null : truncated,
                RefCount = refCount,
                Path = resolvedShot,
                ScreenshotPath = resolvedShot,
                Bytes = shotBytes,
                SiteLearningPath = TryFindSiteLearningPath(finalUrl),
                Notices = exec.Notices,
            }, exec.Payload);
        }
        finally
        {
            space.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ UTF-8 .mjs round execution

    private sealed class EgoSpaceState
    {
        public required string NamespaceKey { get; init; }
        public required string GoalName { get; init; }
        public required string GoalDirectory { get; init; }
        public string? ProfileId { get; set; }
        public int? NumericSpaceId { get; set; }
        public string ActivePageLabel { get; set; } = "p1";
        public TaskSpaceControlState Control { get; set; } = TaskSpaceControlState.Agent;
        public HashSet<int> KnownRefs { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int RoundSeq;

        public bool IsStarted => NumericSpaceId is > 0;
    }

    private readonly record struct RoundExecutionResult(
        JsonElement Payload,
        BrowserToolResult? Failure,
        BrowserNoticeInfo[]? Notices,
        string ScriptPath);

    private static TimeSpan ActionTimeout => TimeSpan.FromMilliseconds(
        ToolExecutionContext.Current?.Integer("browser", "action_timeout_ms", 15_000, 1_000, 60_000) ?? 15_000);

    private BrowserNavigationPolicy CurrentNavigationPolicy()
    {
        var allowPrivate = ToolExecutionContext.Current?.Boolean("browser", "allow_private_hosts", false) ?? false;
        return allowPrivate ? new BrowserNavigationPolicy(allowPrivate: true) : _defaultNavigationPolicy;
    }

    private EgoSpaceState GetSpace(string namespaceKey, string? profileId)
    {
        var state = _spaces.GetOrAdd(namespaceKey, key =>
        {
            var asciiSlug = BuildAsciiGoalSlug(key);
            var goalDir = Path.Combine(_roundsBaseDirectory, asciiSlug);
            return new EgoSpaceState
            {
                NamespaceKey = key,
                GoalName = $"tinadec-{asciiSlug}",
                GoalDirectory = goalDir,
                ProfileId = profileId,
            };
        });
        if (!string.IsNullOrWhiteSpace(profileId) && state.ProfileId is null)
        {
            state.ProfileId = profileId.Trim();
        }
        return state;
    }

    private async Task<RoundExecutionResult> RunRoundAsync(
        EgoSpaceState space,
        string mjsModuleBody,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? cliOverride = null)
    {
        var cli = cliOverride ?? DiscoverCliPath();
        if (cli is null)
        {
            return new RoundExecutionResult(
                default,
                Fail("ego_browser_unavailable",
                    "Ego Lite (ego-browser CLI) is not installed or not on PATH. Install Ego Lite from https://lite.ego.app/ and complete onboarding.",
                    permanent: true),
                null,
                string.Empty);
        }

        Directory.CreateDirectory(space.GoalDirectory);
        var roundNumber = Interlocked.Increment(ref space.RoundSeq);
        Interlocked.Increment(ref _globalRoundCounter);
        var scriptPath = Path.Combine(space.GoalDirectory, $"round-{roundNumber:D2}.mjs");

        // Write UTF-8 without BOM directly to round-XX.mjs (never via PowerShell Set-Content/Out-File).
        await File.WriteAllBytesAsync(scriptPath, Utf8NoBom.GetBytes(mjsModuleBody), cancellationToken).ConfigureAwait(false);

        var importExpr = BuildImportEvalExpression(scriptPath);
        var arguments = new[] { "nodejs", "-e", importExpr };

        Logger.Debug("ego-lite round {Round}: {Cli} nodejs -e \"{Expr}\"", roundNumber, cli, importExpr);
        var processResult = await _runner.ExecuteAsync(cli, arguments, timeout, cancellationToken).ConfigureAwait(false);

        var notices = ExtractEgoNotices(processResult.Stdout, processResult.Stderr);
        if (processResult.TimedOut)
        {
            return new RoundExecutionResult(
                default,
                Fail("browser_timeout", processResult.Stderr, permanent: false, notices: notices),
                notices,
                scriptPath);
        }

        if (TryExtractSentinelPayload(processResult.Stdout, out var payload))
        {
            var payloadFailure = CheckPayloadFailure(space, payload, notices);
            return new RoundExecutionResult(payload, payloadFailure, notices, scriptPath);
        }

        var combinedError = FirstNonEmpty(processResult.Stderr, processResult.Stdout) ?? $"ego-browser exited with code {processResult.ExitCode}.";
        return new RoundExecutionResult(
            default,
            MapRawEgoErrorText(space, combinedError, notices),
            notices,
            scriptPath);
    }

    private BrowserToolResult? CheckPayloadFailure(EgoSpaceState space, JsonElement payload, BrowserNoticeInfo[]? notices)
    {
        if (payload.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
        {
            var errorCode = JsonString(payload, "error_code") ?? JsonString(payload, "code");
            var errorText = JsonString(payload, "error") ?? JsonString(payload, "message") ?? "Ego Lite command failed.";
            return MapEgoErrorCode(space, errorCode, errorText, payload, notices);
        }
        return null;
    }

    private BrowserToolResult MapRawEgoErrorText(EgoSpaceState space, string rawText, BrowserNoticeInfo[]? notices)
    {
        if (rawText.Contains("EGO_TASK_SPACE_USER_IN_CONTROL", StringComparison.Ordinal)
            || rawText.Contains("Control of this task space is with the user", StringComparison.OrdinalIgnoreCase))
        {
            space.Control = TaskSpaceControlState.User;
            return MapEgoErrorCode(space, "EGO_TASK_SPACE_USER_IN_CONTROL", rawText, default, notices);
        }
        if (rawText.Contains("EGO_TASK_SPACE_INACTIVE", StringComparison.Ordinal)
            || rawText.Contains("This task space is no longer assigned to the agent", StringComparison.OrdinalIgnoreCase))
        {
            space.Control = TaskSpaceControlState.Finished;
            return FailTaskSpaceInactive(SpaceIdString(space), notices);
        }
        if (rawText.Contains("EGO_PROFILE_NOT_FOUND", StringComparison.Ordinal))
        {
            return Fail("browser_profile_not_found", rawText.Trim(), permanent: true, notices: notices);
        }
        if (rawText.Contains("EGO_PAGE_DIALOG_OPENED", StringComparison.Ordinal)
            || rawText.Contains("acceptDialog()", StringComparison.Ordinal))
        {
            return new BrowserToolResult
            {
                Ok = false,
                Code = "browser_dialog_open",
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                Message = rawText.Trim(),
                Permanent = true,
                PendingDialog = new BrowserDialogInfo { Message = rawText.Trim() },
                Notices = notices,
            };
        }
        if (rawText.Contains("EGO_BROWSER_UNAVAILABLE", StringComparison.Ordinal))
        {
            return Fail("ego_browser_unavailable", rawText.Trim(), permanent: true, notices: notices);
        }

        return Fail("ego_browser_error", rawText.Trim(), permanent: false, notices: notices);
    }

    private BrowserToolResult MapEgoErrorCode(
        EgoSpaceState space,
        string? errorCode,
        string errorText,
        JsonElement payload,
        BrowserNoticeInfo[]? notices)
    {
        switch (errorCode)
        {
            case "EGO_TASK_SPACE_USER_IN_CONTROL":
                space.Control = TaskSpaceControlState.User;
                var guidance = UserControlReasonGuidance.TryGetValue(errorText.Trim(), out var specific)
                    ? $"{specific} Wait for the user to handle it, then call browser_takeover after the user confirms."
                    : "Control of this Ego Lite TaskSpace is with the user. Wait until the user explicitly asks to continue, then call browser_takeover.";
                return new BrowserToolResult
                {
                    Ok = false,
                    Code = "browser_user_in_control",
                    Driver = DriverId,
                    SpaceId = SpaceIdString(space),
                    Control = "user",
                    Reason = errorText.Trim(),
                    Message = guidance,
                    Permanent = true,
                    Notices = notices,
                };

            case "EGO_TASK_SPACE_INACTIVE":
                space.Control = TaskSpaceControlState.Finished;
                return FailTaskSpaceInactive(SpaceIdString(space), notices);

            case "EGO_PROFILE_NOT_FOUND":
                return Fail("browser_profile_not_found", errorText, permanent: true, notices: notices);

            case "EGO_BROWSER_UNAVAILABLE":
                return Fail("ego_browser_unavailable", errorText, permanent: true, notices: notices);

            case "EGO_PAGE_DIALOG_OPENED":
                BrowserDialogInfo? dialog = null;
                if (payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("dialog", out var d)
                    && d.ValueKind == JsonValueKind.Object)
                {
                    dialog = new BrowserDialogInfo
                    {
                        Type = JsonString(d, "type"),
                        Message = JsonString(d, "message"),
                        Url = JsonString(d, "url"),
                    };
                }
                return new BrowserToolResult
                {
                    Ok = false,
                    Code = "browser_dialog_open",
                    Driver = DriverId,
                    SpaceId = SpaceIdString(space),
                    Message = errorText,
                    Permanent = true,
                    PendingDialog = dialog ?? new BrowserDialogInfo { Message = errorText },
                    Notices = notices,
                };

            default:
                var mappedCode = !string.IsNullOrEmpty(errorCode) && errorCode.StartsWith("browser_", StringComparison.Ordinal)
                    ? errorCode
                    : "ego_browser_error";
                var permanent = payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("permanent", out var perm)
                    && perm.ValueKind == JsonValueKind.True;
                return Fail(mappedCode, errorText, permanent: permanent, notices: notices);
        }
    }

    private BrowserToolResult? CheckControlBoundary(EgoSpaceState space)
    {
        if (space.Control == TaskSpaceControlState.Finished) return FailTaskSpaceInactive(SpaceIdString(space));
        if (space.Control == TaskSpaceControlState.User)
        {
            return new BrowserToolResult
            {
                Ok = false,
                Code = "browser_user_in_control",
                Driver = DriverId,
                SpaceId = SpaceIdString(space),
                Control = "user",
                Message = "Control of this Ego Lite TaskSpace is with the user. Wait for user confirmation before calling browser_takeover.",
                Permanent = true,
            };
        }
        return null;
    }

    private BrowserToolResult FailTaskSpaceInactive(string? spaceId, BrowserNoticeInfo[]? notices = null) => new()
    {
        Ok = false,
        Code = "browser_task_space_finished",
        Driver = DriverId,
        SpaceId = spaceId,
        Control = "finished",
        Message =
            "This task space is no longer assigned to the agent (EGO_TASK_SPACE_INACTIVE): its task has ended, either through browser_finish or by the user. " +
            "browser_takeover cannot resume an ended task. Wait until the user explicitly asks to continue.",
        Permanent = true,
        Notices = notices,
    };

    // ------------------------------------------------------------------ .mjs code generation helpers

    private static void AppendResultHelper(StringBuilder sb)
    {
        sb.AppendLine($"const SENTINEL = {JsLiteral(ResultSentinel)};");
        sb.AppendLine("function emitResult(payload) { console.log(SENTINEL + JSON.stringify(payload)); }");
        sb.AppendLine("try {");
    }

    private static void AppendAcquireSpaceOnly(StringBuilder sb, EgoSpaceState space, bool allowCreate)
    {
        if (space.NumericSpaceId is > 0 and var id)
        {
            sb.AppendLine($"const task = await taskSpace({id});");
        }
        else if (allowCreate)
        {
            if (!string.IsNullOrEmpty(space.ProfileId))
            {
                sb.AppendLine($"const task = await taskSpace({JsLiteral(space.GoalName)}, {{ profileId: {JsLiteral(space.ProfileId)} }});");
            }
            else
            {
                sb.AppendLine($"const task = await taskSpace({JsLiteral(space.GoalName)});");
            }
        }
        else
        {
            sb.AppendLine("emitResult({ ok: false, code: 'browser_not_started', message: 'TaskSpace is not started.', permanent: true }); return;");
        }
    }

    private static void AppendAcquireSpaceAndActivePage(StringBuilder sb, EgoSpaceState space, bool allowCreate = true)
    {
        AppendAcquireSpaceOnly(sb, space, allowCreate);
        sb.AppendLine("const managedPages = await task.pages();");
        sb.AppendLine($"let page = managedPages.find(p => p.label === {JsLiteral(space.ActivePageLabel)}) || managedPages[0];");
        sb.AppendLine("if (!page) {");
        sb.AppendLine("  const allTabs = await task.tabs();");
        sb.AppendLine("  const activeTab = allTabs.find(t => t.active) || allTabs[0];");
        sb.AppendLine("  page = activeTab ? (activeTab.label ? task.page(activeTab.label) : await task.adopt(activeTab.page)) : await task.newPage();");
        sb.AppendLine("}");
    }

    private static void AppendPageSummaryEmit(StringBuilder sb, bool includeReceipt)
    {
        var receiptExpr = includeReceipt ? "typeof receipt === 'object' && receipt ? receipt : {}" : "null";
        sb.AppendLine($"emitResult({{ ok: true, space_id: String(task.spaceId), active_page: page.label, url: await page.url(), title: await page.title(), receipt: {receiptExpr} }});");
    }

    private static string BuildWorkflowMjsScript(
        EgoSpaceState space,
        BrowserRunArgs args,
        string?[] normalizedGotoUrls,
        string? shotTarget)
    {
        var sb = new StringBuilder();
        AppendResultHelper(sb);
        AppendAcquireSpaceAndActivePage(sb, space, allowCreate: true);
        sb.AppendLine("const run_steps = [];");
        sb.AppendLine("const extractions = [];");
        sb.AppendLine("let lastReceipt = null;");

        for (var i = 0; i < args.Steps!.Length; i++)
        {
            var step = args.Steps[i];
            var action = step.Action!.Trim().ToLowerInvariant();
            sb.AppendLine("{");
            switch (action)
            {
                case "goto":
                    sb.AppendLine($"  lastReceipt = await page.goto({JsLiteral(normalizedGotoUrls[i]!)});");
                    sb.AppendLine("  await page.waitForLoadState();");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'goto', ok: true, url: await page.url() }});");
                    break;

                case "fill":
                {
                    var target = FormatEgoTarget(step.Ref, step.Selector) ?? "input";
                    sb.AppendLine($"  lastReceipt = await page.fill({JsLiteral(target)}, {JsLiteral(step.Text ?? string.Empty)});");
                    if (step.Submit == true) sb.AppendLine("  await page.keyboard.press('Enter');");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'fill', ok: true }});");
                    break;
                }

                case "click":
                {
                    var target = FormatEgoTarget(step.Ref, step.Selector) ?? "button";
                    var btn = (step.Button ?? "left").Trim().ToLowerInvariant();
                    sb.AppendLine($"  lastReceipt = await page.click({JsLiteral(target)}, {{ button: {JsLiteral(btn)}, label: 'workflow click step' }});");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'click', ok: true }});");
                    break;
                }

                case "select_option":
                {
                    var target = FormatEgoTarget(step.Ref, step.Selector) ?? "select";
                    var optionArg = step.Values is { Length: > 0 }
                        ? SerializeStringArrayJs(step.Values)
                        : step.Label is not null
                            ? $"{{ label: {JsLiteral(step.Label)} }}"
                            : step.Index is { } idx
                                ? $"{{ index: {idx} }}"
                                : JsLiteral(step.Value ?? string.Empty);
                    sb.AppendLine($"  lastReceipt = await page.selectOption({JsLiteral(target)}, {optionArg});");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'select_option', ok: true }});");
                    break;
                }

                case "press":
                {
                    var target = FormatEgoTarget(step.Ref, step.Selector);
                    if (target is not null)
                    {
                        sb.AppendLine($"  lastReceipt = await page.press({JsLiteral(target)}, {JsLiteral(step.Key ?? "Enter")});");
                    }
                    else
                    {
                        sb.AppendLine($"  lastReceipt = await page.keyboard.press({JsLiteral(step.Key ?? "Enter")});");
                    }
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'press', ok: true }});");
                    break;
                }

                case "scroll":
                {
                    var dx = Math.Clamp(step.DeltaX ?? 0, -2000, 2000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var dy = Math.Clamp(step.DeltaY ?? 400, -2000, 2000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var target = FormatEgoTarget(step.Ref, step.Selector);
                    if (target is not null) sb.AppendLine($"  await page.hover({JsLiteral(target)}, {{ label: 'hover scroll area' }});");
                    sb.AppendLine($"  lastReceipt = await page.mouse.wheel({dx}, {dy}, {{ label: 'workflow scroll step' }});");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'scroll', ok: true }});");
                    break;
                }

                case "wait_for_selector":
                {
                    var state = (step.State ?? "visible").Trim().ToLowerInvariant();
                    var timeout = Math.Clamp(step.TimeoutMs ?? 15_000, 100, 60_000);
                    sb.AppendLine($"  await page.waitForSelector({JsLiteral(step.Selector ?? "body")}, {{ state: {JsLiteral(state)}, timeout: {timeout} }});");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'wait_for_selector', ok: true }});");
                    break;
                }

                case "wait_for_url":
                {
                    var part = step.UrlContains ?? step.Url ?? string.Empty;
                    var timeout = Math.Clamp(step.TimeoutMs ?? 15_000, 100, 60_000);
                    sb.AppendLine($"  await page.waitForURL((u) => String(u).includes({JsLiteral(part)}), {{ timeout: {timeout} }});");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'wait_for_url', ok: true, url: await page.url() }});");
                    break;
                }

                case "wait_for_text":
                {
                    var timeout = Math.Clamp(step.TimeoutMs ?? 15_000, 100, 60_000);
                    sb.AppendLine($"  await page.waitForSelector({JsLiteral("text=" + (step.Text ?? string.Empty))}, {{ timeout: {timeout} }});");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'wait_for_text', ok: true }});");
                    break;
                }

                case "handle_dialog":
                    if (step.Accept == true)
                    {
                        sb.AppendLine(step.PromptText is not null
                            ? $"  await page.acceptDialog({JsLiteral(step.PromptText)});"
                            : "  await page.acceptDialog();");
                    }
                    else
                    {
                        sb.AppendLine("  await page.dismissDialog();");
                    }
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'handle_dialog', ok: true }});");
                    break;

                case "extract":
                {
                    var extractName = string.IsNullOrWhiteSpace(step.Name) ? $"step_{i}" : step.Name.Trim();
                    var css = step.Selector!.Trim();
                    var attr = step.Attribute?.Trim() ?? string.Empty;
                    var multi = step.Multiple == true ? "true" : "false";
                    var limit = Math.Clamp(step.Limit ?? 20, 1, 100);
                    sb.AppendLine($"  const ext = await page.evaluate((s) => {{");
                    sb.AppendLine("    const nodes = [...document.querySelectorAll(s.selector)];");
                    sb.AppendLine("    const read = (el) => !s.attr || s.attr === 'text' || s.attr === 'textContent' ? (el.textContent || '').trim() : (s.attr === 'href' && el.href ? String(el.href) : (el.getAttribute(s.attr) || ''));");
                    sb.AppendLine("    return s.multiple ? { values: nodes.slice(0, s.limit).map(read) } : { value: nodes[0] ? read(nodes[0]) : null };");
                    sb.AppendLine($"  }}, {{ selector: {JsLiteral(css)}, attr: {JsLiteral(attr)}, multiple: {multi}, limit: {limit} }});");
                    sb.AppendLine($"  extractions.push({{ name: {JsLiteral(extractName)}, selector: {JsLiteral(css)}, value: ext.value ?? null, values: ext.values ?? null }});");
                    sb.AppendLine($"  run_steps.push({{ index: {i}, action: 'extract', ok: true, value: ext.value ?? null }});");
                    break;
                }
            }
            sb.AppendLine("}");
        }

        if (args.IncludeSnapshot != false)
        {
            var snapOpt = string.Equals(args.SnapshotScope, "full_page", StringComparison.OrdinalIgnoreCase)
                ? "{ scope: 'full_page' }"
                : string.Empty;
            sb.AppendLine($"const rawSnap = await page.snapshot({snapOpt});");
            sb.AppendLine("var finalSnapText = typeof rawSnap === 'string' ? rawSnap : (rawSnap?.content ?? '');");
        }
        else
        {
            sb.AppendLine("var finalSnapText = '';");
        }

        if (shotTarget is not null)
        {
            var forwardShot = shotTarget.Replace('\\', '/');
            var fullOpt = args.FullPageScreenshot == true ? ", fullPage: true" : string.Empty;
            sb.AppendLine($"var finalShotPath = (await page.screenshot({{ path: {JsLiteral(forwardShot)}{fullOpt} }})) || {JsLiteral(forwardShot)};");
        }
        else
        {
            sb.AppendLine("var finalShotPath = null;");
        }

        sb.AppendLine("emitResult({ ok: true, space_id: String(task.spaceId), active_page: page.label, url: await page.url(), title: await page.title(), snapshot: finalSnapText, screenshot_path: finalShotPath, run_steps, extractions, receipt: lastReceipt });");
        return FinalizeMjsWithCatch(sb);
    }

    private static string FinalizeMjsWithCatch(StringBuilder sb)
    {
        sb.AppendLine("} catch (err) {");
        sb.AppendLine("  emitResult({");
        sb.AppendLine("    ok: false,");
        sb.AppendLine("    error_code: err?.error_code || err?.code || null,");
        sb.AppendLine("    error: err?.message || String(err),");
        sb.AppendLine("    dialog: err?.dialog || null");
        sb.AppendLine("  });");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private async Task<RoundExecutionResult> RunRoundAsync(
        EgoSpaceState space,
        string rawBody,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var wrapped = rawBody.TrimEnd().EndsWith("}", StringComparison.Ordinal)
                      && rawBody.Contains("} catch (err) {", StringComparison.Ordinal)
            ? rawBody
            : FinalizeMjsWithCatch(new StringBuilder(rawBody));
        return await RunRoundAsync(space, wrapped, timeout, cancellationToken, cliOverride: null).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ parsing + utility helpers

    private static bool TryExtractSentinelPayload(string stdout, out JsonElement payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(stdout)) return false;

        foreach (var line in stdout.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            var idx = trimmed.IndexOf(ResultSentinel, StringComparison.Ordinal);
            if (idx < 0) continue;
            var json = trimmed[(idx + ResultSentinel.Length)..];
            try
            {
                using var doc = JsonDocument.Parse(json);
                payload = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        return false;
    }

    private static BrowserNoticeInfo[]? ExtractEgoNotices(string stdout, string stderr)
    {
        var combined = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
        if (!combined.Contains("[ego-browser:notice]", StringComparison.Ordinal)) return null;
        return
        [
            new BrowserNoticeInfo
            {
                Code = "ego_update_available",
                Mode = "ego_lite",
            },
        ];
    }

    private static void UpdateSpaceFromPayload(EgoSpaceState space, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        var spaceIdStr = JsonString(payload, "space_id");
        if (!string.IsNullOrEmpty(spaceIdStr) && int.TryParse(spaceIdStr, out var numericId) && numericId > 0)
        {
            space.NumericSpaceId = numericId;
        }
        var activePage = JsonString(payload, "active_page");
        if (!string.IsNullOrEmpty(activePage))
        {
            space.ActivePageLabel = activePage;
        }
    }

    private static BrowserToolResult AttachPayloadReceipt(BrowserToolResult result, JsonElement payload)
    {
        BrowserTabInfo[] openedTabs = Array.Empty<BrowserTabInfo>();
        BrowserDialogInfo? pendingDialog = null;

        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("receipt", out var receiptEl)
            && receiptEl.ValueKind == JsonValueKind.Object)
        {
            if (receiptEl.TryGetProperty("popups", out var popups) && popups.ValueKind == JsonValueKind.Array)
            {
                openedTabs = popups.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Object)
                    .Select(p => new BrowserTabInfo
                    {
                        Id = JsonString(p, "label") ?? JsonString(p, "targetId"),
                        Active = false,
                    })
                    .ToArray();
            }
            if (receiptEl.TryGetProperty("dialog", out var dialog) && dialog.ValueKind == JsonValueKind.Object)
            {
                pendingDialog = new BrowserDialogInfo
                {
                    Type = JsonString(dialog, "type"),
                    Message = JsonString(dialog, "message"),
                    Url = JsonString(dialog, "url"),
                };
            }
        }

        result.Receipt = new BrowserActionReceipt
        {
            OpenedTabs = openedTabs,
            PendingDialog = pendingDialog,
            ConsoleErrors = Array.Empty<string>(),
        };
        if (openedTabs.Length > 0) result.OpenedTabs = openedTabs;
        if (pendingDialog is not null) result.PendingDialog = pendingDialog;
        return result;
    }

    private static BrowserTabInfo[] ParseEgoTabs(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("tabs", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BrowserTabInfo>();
        }

        var list = new List<BrowserTabInfo>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new BrowserTabInfo
            {
                Id = JsonString(item, "label") ?? JsonString(item, "targetId"),
                Url = JsonString(item, "url"),
                Title = JsonString(item, "title"),
                Active = item.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True,
            });
        }
        return list.ToArray();
    }

    private static BrowserRunStepResult[] ParseRunStepResults(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("run_steps", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BrowserRunStepResult>();
        }

        var list = new List<BrowserRunStepResult>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new BrowserRunStepResult
            {
                Index = item.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var n) ? n : list.Count,
                Action = JsonString(item, "action"),
                Ok = !item.TryGetProperty("ok", out var ok) || ok.ValueKind == JsonValueKind.True,
                Url = JsonString(item, "url"),
                Value = JsonString(item, "value"),
            });
        }
        return list.ToArray();
    }

    private static BrowserExtractionResult[] ParseExtractions(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("extractions", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BrowserExtractionResult>();
        }

        var list = new List<BrowserExtractionResult>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string[]? vals = null;
            if (item.TryGetProperty("values", out var vArr) && vArr.ValueKind == JsonValueKind.Array)
            {
                vals = vArr.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : x.ToString())
                    .ToArray();
            }
            list.Add(new BrowserExtractionResult
            {
                Name = JsonString(item, "name"),
                Selector = JsonString(item, "selector"),
                Value = JsonString(item, "value"),
                Values = vals,
            });
        }
        return list.ToArray();
    }

    private static string SanitizeEgoSnapshotText(string text) =>
        Regex.Replace(
            text,
            @"(\[ref=[^,\]]+),\s*loc=(?:unstable|ambiguous)(?=,|\])",
            "$1",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static int RecordSnapshotRefs(EgoSpaceState space, string snapshotText, bool mergeOnly)
    {
        if (!mergeOnly) space.KnownRefs.Clear();
        var count = 0;
        foreach (Match match in RefMarkerPattern.Matches(snapshotText))
        {
            if (int.TryParse(match.Groups[1].Value, out var refId) && refId > 0)
            {
                space.KnownRefs.Add(refId);
                count++;
            }
        }
        return count;
    }

    private static string? FormatEgoTarget(int? refId, string? selector)
    {
        if (refId is > 0) return $"@{refId.Value}";
        if (!string.IsNullOrWhiteSpace(selector)) return selector.Trim();
        return null;
    }

    private static string? NormalizeEgoSnapshotScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return "viewport";
        return scope.Trim().ToLowerInvariant() switch
        {
            "viewport" or "only_within_viewport" => "viewport",
            "full_page" or "full" => "full_page",
            "subtree" => "subtree",
            _ => null,
        };
    }

    private static string? SpaceIdString(EgoSpaceState space) =>
        space.NumericSpaceId is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : space.NamespaceKey;

    private static string? RequireNamespace(BrowserConnectorContext context, out BrowserToolResult? error)
    {
        error = null;
        var ns = context.NamespaceKey ?? BrowserConnectorContext.NormalizeSessionId(context.RawSessionId);
        if (ns is null)
        {
            error = Fail("browser_session_invalid", "A valid session id is required.", permanent: true);
        }
        return ns;
    }

    private static BrowserToolResult Fail(
        string code,
        string? message,
        bool permanent,
        BrowserNoticeInfo[]? notices = null) => new()
    {
        Ok = false,
        Code = code,
        Driver = BrowserDriverKinds.EgoLite,
        Message = message,
        Permanent = permanent,
        Notices = notices,
    };

    private static string JsLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string SerializeStringArrayJs(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(JsLiteral)) + "]";

    private static string[] ReadPayloadStringArray(JsonElement payload, string property)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(property, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(x.GetString()))
            .Select(x => x.GetString()!)
            .ToArray();
    }

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var val)
        && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static string? TryFindSiteLearningPath(string? url) =>
        !string.IsNullOrWhiteSpace(url) && BrowserSiteLearningStore.TryLoad(url, out var entry) && entry is not null
            ? entry.RelativePath
            : null;

    private static T DeserializeStep<T>(JsonElement? element, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : new()
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return new T();
        try { return obj.Deserialize(typeInfo) ?? new T(); }
        catch (JsonException) { return new T(); }
    }

    private static string? FindMacOsFrameworkHelper(EgoLiteDiscoveryEnvironment env, string appBundlePath)
    {
        var frameworksDir = CombineUnix(appBundlePath, "Contents/Frameworks");
        foreach (var fw in env.GetDirectories(frameworksDir))
        {
            if (!fw.EndsWith(".framework", StringComparison.OrdinalIgnoreCase)) continue;
            var helper = CombineUnix(fw, "Versions/Current/Helpers/ego-browser");
            if (env.FileExists(helper)) return helper;
        }
        return null;
    }

    private static string? SearchPath(EgoLiteDiscoveryEnvironment env, string commandName)
    {
        var pathEnv = env.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;

        var separator = env.IsWindows ? ';' : ':';
        var extensions = env.IsWindows
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        var hasExt = env.IsWindows
            && (commandName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || commandName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || commandName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        foreach (var entry in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleanDir = entry.Trim('"');
            if (string.IsNullOrWhiteSpace(cleanDir)) continue;

            if (hasExt)
            {
                var direct = env.IsWindows ? Path.Combine(cleanDir, commandName) : CombineUnix(cleanDir, commandName);
                if (env.FileExists(direct)) return direct;
            }
            else
            {
                foreach (var ext in extensions)
                {
                    var candidate = env.IsWindows
                        ? Path.Combine(cleanDir, commandName + ext)
                        : CombineUnix(cleanDir, commandName + ext);
                    if (env.FileExists(candidate)) return candidate;
                }
            }
        }
        return null;
    }

    private static string CombineUnix(string left, string right) =>
        left.TrimEnd('/') + "/" + right.TrimStart('/');

    private static string BuildAsciiGoalSlug(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var prevHyphen = false;
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevHyphen = false;
            }
            else if (sb.Length > 0 && !prevHyphen)
            {
                sb.Append('-');
                prevHyphen = true;
            }
        }
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        var slug = sb.Length == 0 ? "goal" : sb.ToString();
        return slug.Length <= 48 ? slug : slug[..48].TrimEnd('-');
    }

    private static string BuildAsciiSafeRoundsRoot()
    {
        var temp = Path.GetTempPath();
        var hasNonAscii = temp.Any(c => c > 127);
        if (!hasNonAscii)
        {
            return Path.Combine(temp, "tinadec-ego-rounds");
        }
        if (OperatingSystem.IsWindows())
        {
            var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
            if (!string.IsNullOrWhiteSpace(publicDir) && publicDir.All(c => c <= 127))
            {
                return Path.Combine(publicDir, "tinadec-ego-rounds");
            }
        }
        return Path.Combine(temp, "tinadec-ego-rounds");
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }
}
