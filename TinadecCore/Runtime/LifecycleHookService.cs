using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Runtime;

/// <summary>
/// File-backed lifecycle hook configuration and process runner supporting workspace
/// (<c>&lt;workspace_root&gt;/.tinadec/hooks.json</c>) and global
/// (<c>&lt;data_root&gt;/hooks.json</c>) hooks for <c>PreToolUse</c>, <c>PostToolUse</c>,
/// <c>OnTaskComplete</c>, and <c>OnSessionIdle</c>.
/// </summary>
public sealed class LifecycleHookService : ILifecycleHookService
{
    private const int DefaultTimeoutMs = 5000;
    private const int MinTimeoutMs = 100;
    private const int MaxTimeoutMs = 60000;
    private const int MaxOutputChars = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IConfiguration? _configuration;
    private readonly IScopeStorageLocations? _storageLocations;
    private readonly ILogger<LifecycleHookService>? _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public LifecycleHookService(
        IConfiguration? configuration = null,
        IScopeStorageLocations? storageLocations = null,
        ILogger<LifecycleHookService>? logger = null)
    {
        _configuration = configuration;
        _storageLocations = storageLocations;
        _logger = logger;
    }

    public async Task<LifecycleHooksDocumentDto> GetHooksAsync(
        string? scope = null,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        var (normalizedScope, resolvedRoot, filePath) = ResolveStoreTarget(scope, workspaceRoot, requireWorkspaceIfScoped: false);
        if (filePath is null)
        {
            return new LifecycleHooksDocumentDto
            {
                Scope = normalizedScope,
                WorkspaceRoot = resolvedRoot,
                Hooks = [],
                Revision = 0,
            };
        }

        var sem = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await ReadDocumentAsync(filePath, cancellationToken).ConfigureAwait(false);
            return new LifecycleHooksDocumentDto
            {
                Scope = normalizedScope,
                WorkspaceRoot = normalizedScope == "workspace" ? resolvedRoot : null,
                Hooks = stored.Hooks,
                Revision = stored.Revision,
            };
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<LifecycleHooksDocumentDto> SaveHooksAsync(
        LifecycleHooksWriteRequestDto request,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (normalizedScope, resolvedRoot, filePath) = ResolveStoreTarget(
            request.Scope,
            request.WorkspaceRoot,
            requireWorkspaceIfScoped: true);

        if (filePath is null)
            throw new LifecycleHookException(400, "invalid_request", "workspace_root is required for workspace lifecycle hooks.");

        var normalizedHooks = ValidateAndNormalizeHooks(request.Hooks ?? []);
        var expected = expectedRevision ?? request.ExpectedRevision;

        var sem = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadDocumentAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (expected is >= 0 && existing.Revision != expected.Value)
            {
                throw new LifecycleHookException(
                    412,
                    "conflict",
                    $"Lifecycle hooks revision conflict: expected {expected.Value}, current is {existing.Revision}.");
            }

            var next = new StoredHooksFile
            {
                Revision = existing.Revision + 1,
                Hooks = [.. normalizedHooks],
            };

            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);
            var tmpPath = Path.Combine(dir, $".hooks-{Guid.NewGuid():N}.tmp");
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(fs, next, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(tmpPath, filePath, overwrite: true);

            return new LifecycleHooksDocumentDto
            {
                Scope = normalizedScope,
                WorkspaceRoot = normalizedScope == "workspace" ? resolvedRoot : null,
                Hooks = next.Hooks,
                Revision = next.Revision,
            };
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<LifecycleHookPreToolResult> EvaluatePreToolUseAsync(
        LifecycleHookToolContext context,
        CancellationToken cancellationToken = default)
    {
        var matched = await LoadMatchingHooksAsync(
            LifecycleHookEvents.PreToolUse,
            context.WorkspaceRoot,
            context.ToolId,
            context.TargetPath,
            cancellationToken).ConfigureAwait(false);

        if (matched.Count == 0)
            return new LifecycleHookPreToolResult(Allowed: true);

        var feedback = new List<string>();
        var payloadJson = JsonSerializer.Serialize(new
        {
            @event = LifecycleHookEvents.PreToolUse,
            tool_id = context.ToolId,
            tool_call_id = context.ToolCallId,
            session_id = context.SessionId,
            run_id = context.RunId,
            task_id = context.TaskId,
            agent_slug = context.AgentSlug,
            target_path = context.TargetPath,
            workspace_root = context.WorkspaceRoot,
        }, JsonOptions);

        foreach (var hook in matched)
        {
            var res = await RunHookProcessAsync(
                hook,
                LifecycleHookEvents.PreToolUse,
                context.WorkspaceRoot,
                context.ToolId,
                context.TargetPath,
                context.SessionId,
                context.RunId,
                payloadJson,
                cancellationToken).ConfigureAwait(false);

            var combinedOutput = CombineOutput(res.StdOut, res.StdErr);
            if (!string.IsNullOrWhiteSpace(combinedOutput))
                feedback.Add($"[hook:{hook.Id}] {combinedOutput}");

            if (TryReadBlockDecision(res.StdOut, out var explicitReason))
            {
                if (hook.Blocking)
                    return new LifecycleHookPreToolResult(false, explicitReason ?? "Blocked by lifecycle hook.", hook.Id, feedback);
            }

            if ((res.TimedOut || res.ExitCode != 0) && hook.Blocking)
            {
                var reason = res.TimedOut
                    ? $"Lifecycle hook '{hook.Name ?? hook.Id}' timed out after {hook.TimeoutMs}ms."
                    : !string.IsNullOrWhiteSpace(combinedOutput)
                        ? combinedOutput
                        : $"Lifecycle hook '{hook.Name ?? hook.Id}' exited with code {res.ExitCode}.";
                return new LifecycleHookPreToolResult(false, reason, hook.Id, feedback);
            }
        }

        return new LifecycleHookPreToolResult(Allowed: true, Feedback: feedback);
    }

    public async Task<LifecycleHookPostToolResult> EvaluatePostToolUseAsync(
        LifecycleHookToolContext context,
        CancellationToken cancellationToken = default)
    {
        var matched = await LoadMatchingHooksAsync(
            LifecycleHookEvents.PostToolUse,
            context.WorkspaceRoot,
            context.ToolId,
            context.TargetPath,
            cancellationToken).ConfigureAwait(false);

        if (matched.Count == 0)
            return new LifecycleHookPostToolResult([]);

        var feedback = new List<string>();
        var warnings = new List<string>();
        var hasBlockingFailure = false;

        var payloadJson = JsonSerializer.Serialize(new
        {
            @event = LifecycleHookEvents.PostToolUse,
            tool_id = context.ToolId,
            tool_call_id = context.ToolCallId,
            session_id = context.SessionId,
            run_id = context.RunId,
            task_id = context.TaskId,
            agent_slug = context.AgentSlug,
            target_path = context.TargetPath,
            workspace_root = context.WorkspaceRoot,
            is_success = context.IsSuccess,
            result_summary = context.ResultSummary,
        }, JsonOptions);

        foreach (var hook in matched)
        {
            var res = await RunHookProcessAsync(
                hook,
                LifecycleHookEvents.PostToolUse,
                context.WorkspaceRoot,
                context.ToolId,
                context.TargetPath,
                context.SessionId,
                context.RunId,
                payloadJson,
                cancellationToken).ConfigureAwait(false);

            var combinedOutput = CombineOutput(res.StdOut, res.StdErr);
            if (!string.IsNullOrWhiteSpace(combinedOutput))
                feedback.Add($"[hook:{hook.Name ?? hook.Id}] {combinedOutput}");

            if (res.TimedOut || res.ExitCode != 0)
            {
                var msg = res.TimedOut
                    ? $"Lifecycle hook '{hook.Name ?? hook.Id}' timed out after {hook.TimeoutMs}ms."
                    : $"Lifecycle hook '{hook.Name ?? hook.Id}' exited with code {res.ExitCode}.";
                warnings.Add(msg);
                if (hook.Blocking)
                    hasBlockingFailure = true;
            }
        }

        return new LifecycleHookPostToolResult(feedback, hasBlockingFailure, warnings);
    }

    public Task NotifyTaskCompleteAsync(
        LifecycleHookRunContext context,
        CancellationToken cancellationToken = default) =>
        NotifyRunEventAsync(LifecycleHookEvents.OnTaskComplete, context, cancellationToken);

    public Task NotifySessionIdleAsync(
        LifecycleHookRunContext context,
        CancellationToken cancellationToken = default) =>
        NotifyRunEventAsync(LifecycleHookEvents.OnSessionIdle, context, cancellationToken);

    private async Task NotifyRunEventAsync(
        string eventName,
        LifecycleHookRunContext context,
        CancellationToken cancellationToken)
    {
        var matched = await LoadMatchingHooksAsync(
            eventName,
            context.WorkspaceRoot,
            toolId: null,
            targetPath: null,
            cancellationToken).ConfigureAwait(false);

        if (matched.Count == 0)
            return;

        var payloadJson = JsonSerializer.Serialize(new
        {
            @event = eventName,
            session_id = context.SessionId,
            run_id = context.RunId,
            task_id = context.TaskId,
            agent_slug = context.AgentSlug,
            status = context.Status,
            summary = context.Summary,
            workspace_root = context.WorkspaceRoot,
        }, JsonOptions);

        foreach (var hook in matched)
        {
            try
            {
                var res = await RunHookProcessAsync(
                    hook,
                    eventName,
                    context.WorkspaceRoot,
                    toolId: null,
                    targetPath: null,
                    context.SessionId,
                    context.RunId,
                    payloadJson,
                    cancellationToken).ConfigureAwait(false);
                if (res.TimedOut || res.ExitCode != 0)
                {
                    _logger?.LogWarning(
                        "Lifecycle hook {HookId} ({Event}) exited with {ExitCode} (timedOut={TimedOut}).",
                        hook.Id, eventName, res.ExitCode, res.TimedOut);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Lifecycle hook {HookId} ({Event}) failed.", hook.Id, eventName);
            }
        }
    }

    private async Task<List<LifecycleHookEntryDto>> LoadMatchingHooksAsync(
        string eventName,
        string? workspaceRoot,
        string? toolId,
        string? targetPath,
        CancellationToken cancellationToken)
    {
        var all = new List<LifecycleHookEntryDto>();

        var globalPath = ResolveGlobalHooksFilePath();
        if (File.Exists(globalPath))
        {
            var doc = await ReadDocumentUnlockedAsync(globalPath, cancellationToken).ConfigureAwait(false);
            all.AddRange(doc.Hooks);
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var wsPath = ResolveWorkspaceHooksFilePath(workspaceRoot);
            if (File.Exists(wsPath))
            {
                var doc = await ReadDocumentUnlockedAsync(wsPath, cancellationToken).ConfigureAwait(false);
                all.AddRange(doc.Hooks);
            }
        }

        return all
            .Where(h => h.Enabled
                && string.Equals(h.Event, eventName, StringComparison.OrdinalIgnoreCase)
                && MatchesHook(h.Matcher, toolId, targetPath))
            .ToList();
    }

    private async Task<StoredHooksFile> ReadDocumentUnlockedAsync(string filePath, CancellationToken cancellationToken)
    {
        var sem = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadDocumentAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private static async Task<StoredHooksFile> ReadDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return new StoredHooksFile();

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            var parsed = await JsonSerializer.DeserializeAsync<StoredHooksFile>(fs, JsonOptions, cancellationToken).ConfigureAwait(false);
            return parsed ?? new StoredHooksFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new StoredHooksFile();
        }
    }

    private (string Scope, string? WorkspaceRoot, string? FilePath) ResolveStoreTarget(
        string? scope,
        string? workspaceRoot,
        bool requireWorkspaceIfScoped)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scope)
            ? (!string.IsNullOrWhiteSpace(workspaceRoot) ? "workspace" : "global")
            : scope.Trim().ToLowerInvariant();

        if (normalizedScope is not ("workspace" or "global"))
            throw new LifecycleHookException(400, "invalid_request", "Lifecycle hook scope must be 'workspace' or 'global'.");

        if (normalizedScope == "global")
            return ("global", null, ResolveGlobalHooksFilePath());

        var root = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? workspaceRoot.Trim()
            : _configuration?["TinadecTools:DefaultWorkspaceRoot"]?.Trim();

        if (string.IsNullOrWhiteSpace(root))
        {
            if (requireWorkspaceIfScoped)
                throw new LifecycleHookException(400, "invalid_request", "workspace_root is required when scope is 'workspace'.");
            return ("workspace", null, null);
        }

        return ("workspace", root, ResolveWorkspaceHooksFilePath(root));
    }

    private string ResolveGlobalHooksFilePath()
    {
        var root = _storageLocations?.Data
            ?? _storageLocations?.Root
            ?? _configuration?["TinadecPersistence:DataRoot"]
            ?? _configuration?["TinadecStorage:UserRoot"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tinadec");
        }
        return Path.Combine(root, "hooks.json");
    }

    private static string ResolveWorkspaceHooksFilePath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".tinadec", "hooks.json");

    private static IReadOnlyList<LifecycleHookEntryDto> ValidateAndNormalizeHooks(IReadOnlyList<LifecycleHookEntryDto> input)
    {
        var result = new List<LifecycleHookEntryDto>(input.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in input)
        {
            if (raw is null)
                throw new LifecycleHookException(400, "invalid_request", "Hook entries cannot be null.");

            string normalizedEvent;
            try
            {
                normalizedEvent = LifecycleHookEvents.Normalize(raw.Event ?? string.Empty);
            }
            catch (ArgumentException ex)
            {
                throw new LifecycleHookException(400, "invalid_request", ex.Message);
            }

            if (string.IsNullOrWhiteSpace(raw.Command))
                throw new LifecycleHookException(400, "invalid_request", "Each lifecycle hook requires a non-empty command.");

            var timeoutMs = raw.TimeoutMs == 0 ? DefaultTimeoutMs : raw.TimeoutMs;
            if (timeoutMs < MinTimeoutMs || timeoutMs > MaxTimeoutMs)
                throw new LifecycleHookException(400, "invalid_request", $"Hook timeout_ms must be between {MinTimeoutMs} and {MaxTimeoutMs}.");

            var id = string.IsNullOrWhiteSpace(raw.Id) ? Guid.NewGuid().ToString("N")[..12] : raw.Id.Trim();
            if (!seenIds.Add(id))
                throw new LifecycleHookException(400, "invalid_request", $"Duplicate lifecycle hook id '{id}'.");

            result.Add(new LifecycleHookEntryDto
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(raw.Name) ? null : raw.Name.Trim(),
                Event = normalizedEvent,
                Matcher = string.IsNullOrWhiteSpace(raw.Matcher) ? null : raw.Matcher.Trim(),
                Command = raw.Command.Trim(),
                TimeoutMs = timeoutMs,
                Blocking = raw.Blocking,
                Enabled = raw.Enabled,
            });
        }

        return result;
    }

    internal static bool MatchesHook(string? matcher, string? toolId, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(matcher) || matcher.Trim() == "*")
            return true;

        var normalizedPath = targetPath?.Replace('\\', '/');
        var fileName = !string.IsNullOrWhiteSpace(targetPath) ? Path.GetFileName(targetPath) : null;

        foreach (var rawPattern in matcher.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPattern == "*")
                return true;

            var regex = GlobToRegex(rawPattern);
            if (!string.IsNullOrWhiteSpace(toolId) && regex.IsMatch(toolId))
                return true;
            if (!string.IsNullOrWhiteSpace(normalizedPath) && regex.IsMatch(normalizedPath))
                return true;
            if (!string.IsNullOrWhiteSpace(fileName) && regex.IsMatch(fileName))
                return true;
        }

        return false;
    }

    private static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Trim().Replace('\\', '/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (ch == '*')
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (ch == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task<HookRunResult> RunHookProcessAsync(
        LifecycleHookEntryDto hook,
        string eventName,
        string? workspaceRoot,
        string? toolId,
        string? targetPath,
        Guid sessionId,
        Guid runId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var workingDir = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : Environment.CurrentDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            // ArgumentList escapes inner quotes as \", which breaks cmd.exe /c commands containing quotes (e.g. >>"...").
            // cmd.exe /s /c "..." strips only the outer quotes and passes hook.Command verbatim.
            psi.Arguments = $"/d /s /c \"{hook.Command}\"";
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(hook.Command);
        }

        psi.Environment["TINADEC_HOOK_EVENT"] = eventName;
        if (!string.IsNullOrWhiteSpace(toolId)) psi.Environment["TINADEC_HOOK_TOOL"] = toolId;
        if (!string.IsNullOrWhiteSpace(targetPath)) psi.Environment["TINADEC_HOOK_PATH"] = targetPath;
        psi.Environment["TINADEC_HOOK_SESSION_ID"] = sessionId.ToString();
        psi.Environment["TINADEC_HOOK_RUN_ID"] = runId.ToString();

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new HookRunResult(-1, string.Empty, "Failed to start hook process.", false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HookRunResult(-1, string.Empty, ex.Message, false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Clamp(hook.TimeoutMs, MinTimeoutMs, MaxTimeoutMs));

        try
        {
            await process.StandardInput.WriteAsync(payloadJson.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch
        {
            // Ignore broken stdin pipes when commands do not read stdin.
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        var stdout = await SafeAwaitAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeAwaitAsync(stderrTask).ConfigureAwait(false);
        var exitCode = timedOut ? -1 : process.ExitCode;
        return new HookRunResult(exitCode, stdout, stderr, timedOut);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        var sb = new StringBuilder();
        try
        {
            while (sb.Length < MaxOutputChars)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxOutputChars - sb.Length)), ct).ConfigureAwait(false);
                if (read <= 0) break;
                sb.Append(buffer, 0, read);
            }
        }
        catch
        {
            // Process killed or closed stream.
        }
        return sb.ToString().Trim();
    }

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return stderr;
        if (string.IsNullOrWhiteSpace(stderr)) return stdout;
        return $"{stdout}\n{stderr}";
    }

    private static bool TryReadBlockDecision(string stdout, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(stdout)) return false;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var blocked = (root.TryGetProperty("decision", out var dec) && dec.ValueKind == JsonValueKind.String
                    && string.Equals(dec.GetString(), "block", StringComparison.OrdinalIgnoreCase))
                || (root.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.False);
            if (!blocked) return false;
            reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class StoredHooksFile
    {
        public long Revision { get; set; }
        public List<LifecycleHookEntryDto> Hooks { get; set; } = [];
    }

    private sealed record HookRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
}
