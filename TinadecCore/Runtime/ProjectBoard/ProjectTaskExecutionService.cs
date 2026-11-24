using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.DmaEA.LiveExplainer;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;

namespace TinadecCore.Runtime.ProjectBoard;

public sealed record BoardCliHarnessExecutionRequest(
    string HarnessId,
    HarnessSpec Spec,
    string Prompt,
    string WorkingDirectory,
    Guid SessionId,
    Guid TaskId);

public sealed record BoardCliHarnessExecutionResult(
    bool Succeeded,
    string OutputSummary,
    string? ErrorMessage = null);

public interface IBoardCliHarnessExecutor
{
    Task<BoardCliHarnessExecutionResult> ExecuteAsync(
        BoardCliHarnessExecutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default external CLI harness runner backed by <see cref="HarnessCatalog"/>.
/// Resolves verified executables and headless argv/stdin framing from the catalog.
/// </summary>
public sealed class CatalogBoardCliHarnessExecutor : IBoardCliHarnessExecutor
{
    public async Task<BoardCliHarnessExecutionResult> ExecuteAsync(
        BoardCliHarnessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = request.Spec;
        var executable = ResolveHarnessBinary(spec)
            ?? throw new InvalidOperationException($"CLI harness '{spec.DisplayName}' ({spec.Id}) binary was not found on this host.");

        var cliChannel = spec.Channel(AgentChannels.Cli);
        IReadOnlyList<string> argv;
        string? stdinPayload = null;

        if (cliChannel is not null)
        {
            argv = HarnessCatalog.MaterializeChannelArgv(spec.Id, AgentChannels.Cli, request.Prompt);
            stdinPayload = cliChannel.PromptDelivery switch
            {
                HarnessPromptDeliveries.Stdin => request.Prompt,
                HarnessPromptDeliveries.StdinMessage => JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new
                    {
                        role = "user",
                        content = new[] { new { type = "text", text = request.Prompt } }
                    }
                }) + "\n",
                _ => null
            };
        }
        else
        {
            throw new InvalidOperationException($"Harness '{spec.Id}' does not expose a headless CLI batch channel.");
        }

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = stdinPayload is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (OperatingSystem.IsWindows() && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(executable);
            foreach (var arg in argv) psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.FileName = executable;
            foreach (var arg in argv) psi.ArgumentList.Add(arg);
        }

        psi.Environment.Remove("TINADEC_HOST_CONTROL_TOKEN");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start CLI harness '{spec.Id}'.");

        using var reg = cancellationToken.Register(static state =>
        {
            try
            {
                var proc = (Process)state!;
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { }
        }, process);

        if (stdinPayload is not null)
        {
            await using var stdin = process.StandardInput;
            await stdin.WriteAsync(stdinPayload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode == 0)
        {
            var summary = ExtractCompactOutputSummary(stdout, $"Completed via {spec.DisplayName}");
            return new BoardCliHarnessExecutionResult(true, summary);
        }

        var err = !string.IsNullOrEmpty(stderr) ? stderr : (!string.IsNullOrEmpty(stdout) ? stdout : $"Exited with code {process.ExitCode}.");
        return new BoardCliHarnessExecutionResult(false, string.Empty, Truncate(err, 500));
    }

    internal static string? ResolveHarnessBinary(HarnessSpec spec)
    {
        foreach (var envVar in spec.EnvOverrides)
        {
            var val = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(val) && File.Exists(val.Trim()))
                return Path.GetFullPath(val.Trim());
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var rawRoot in spec.ExtraSearchRoots)
        {
            var root = ExpandKnownTokens(rawRoot);
            if (!Directory.Exists(root)) continue;
            foreach (var bin in spec.BinaryNames)
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(root, bin + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (!Directory.Exists(trimmed)) continue;
            foreach (var bin in spec.BinaryNames)
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(trimmed, bin + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        return null;
    }

    private static string ExpandKnownTokens(string template) =>
        template
            .Replace("{ProgramFiles}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
            .Replace("{LocalAppData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{AppData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{UserProfile}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);

    private static string ExtractCompactOutputSummary(string stdout, string fallback)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return fallback;
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.StartsWith('{') && line.EndsWith('}'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(res.GetString()))
                        return Truncate(res.GetString()!, 240);
                    if (root.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(txt.GetString()))
                        return Truncate(txt.GetString()!, 240);
                    if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(resp.GetString()))
                        return Truncate(resp.GetString()!, 240);
                }
                catch (JsonException)
                {
                }
            }
        }

        return Truncate(lines[^1], 240);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// Explicit task execution orchestrator:
/// - Dispatches only on explicit API actions (NEVER from passive Markdown sync);
/// - Allocates isolated Git worktrees and <see cref="IResourceLeaseService"/> claims when available;
/// - Creates/reuses sessions tagged with <c>Origin = "board_task"</c> and <c>BoardTaskId = taskId</c>;
/// - Drives <c>human</c>, <c>native</c> (Core session runs), and <c>cli</c> (<see cref="HarnessCatalog"/>);
/// - Synchronizes run lifecycle back to the board (<c>in_progress</c> / <c>in_review</c> / <c>blocked</c>),
///   inspects Git diffs, and handles <c>approve</c>, <c>request-changes</c>, and <c>cancel</c>.
/// </summary>
public sealed class ProjectTaskExecutionService
{
    private static readonly Guid CliFallbackModeVersionId = Guid.Parse("00000000-0000-0000-0000-00000000b0a1");

    private readonly ProjectBoardCoordinator _coordinator;
    private readonly ProjectSessionStore _sessionStore;
    private readonly IDbContextFactory<ProjectBoardDbContext> _dbFactory;
    private readonly IDbContextFactory<AgentConfigurationDbContext>? _cfgFactory;
    private readonly GlobalAgentConfiguration? _globalConfig;
    private readonly ITenantContextAccessor _tenant;
    private readonly IServiceProvider _services;
    private readonly ILogger<ProjectTaskExecutionService>? _logger;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCliExecutions = new();
    private long _dispatchCount;

    public long DispatchCount => Interlocked.Read(ref _dispatchCount);

    public ProjectTaskExecutionService(
        ProjectBoardCoordinator coordinator,
        ProjectSessionStore sessionStore,
        IDbContextFactory<ProjectBoardDbContext> dbFactory,
        ITenantContextAccessor tenant,
        IServiceProvider services,
        IDbContextFactory<AgentConfigurationDbContext>? cfgFactory = null,
        GlobalAgentConfiguration? globalConfig = null,
        ILogger<ProjectTaskExecutionService>? logger = null)
    {
        _coordinator = coordinator;
        _sessionStore = sessionStore;
        _dbFactory = dbFactory;
        _tenant = tenant;
        _services = services;
        _cfgFactory = cfgFactory;
        _globalConfig = globalConfig;
        _logger = logger;
    }

    public async Task<ProjectBoardTaskDto?> DispatchTaskAsync(
        Guid projectId,
        Guid taskId,
        DispatchProjectBoardTaskRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new DispatchProjectBoardTaskRequest();
        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var settings = await _coordinator.GetSettingsAsync(projectId, cancellationToken).ConfigureAwait(false);

        var current = await _coordinator.GetTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return null;

        current = await RefreshSingleTaskRunStateAsync(projectId, current, cancellationToken).ConfigureAwait(false);

        if (current.ActiveRunId.HasValue && current.RunStatus is not (null or "completed" or "failed" or "cancelled"))
        {
            throw new ProjectBoardConflictStateException("task_already_running", $"Task '{current.TaskKey}' already has an active run '{current.ActiveRunId}'.");
        }

        if (_activeCliExecutions.ContainsKey(taskId))
        {
            throw new ProjectBoardConflictStateException("task_already_running", $"Task '{current.TaskKey}' already has an active CLI execution.");
        }

        var effectiveAssignee = ProjectBoardCoordinator.NormalizeAssignee(request.AssigneeOverride)
            ?? current.Assignee
            ?? new ProjectBoardAssigneeDto { Kind = settings.DefaultAssigneeKind };

        // 1. Human assignee: mark in_progress without starting any AI session or CLI run.
        if (effectiveAssignee.Kind == ProjectBoardAssigneeKinds.Human)
        {
            Interlocked.Increment(ref _dispatchCount);
            return await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
            {
                row.AssigneeJson = JsonSerializer.Serialize(effectiveAssignee, ProjectBoardMarkdownCodec.CanonicalJson);
                row.Status = ProjectBoardTaskStatuses.InProgress;
                row.Blocked = false;
                row.BlockedReason = null;
                row.RunStatus = null;
            }, exportDeclarativeMarkdown: true, cancellationToken).ConfigureAwait(false);
        }

        // Validate CLI harness early before allocating worktree/session.
        HarnessSpec? cliHarnessSpec = null;
        if (effectiveAssignee.Kind == ProjectBoardAssigneeKinds.Cli)
        {
            var harnessId = effectiveAssignee.HarnessId ?? effectiveAssignee.Id;
            cliHarnessSpec = HarnessCatalog.Find(harnessId)
                ?? throw new ArgumentException($"CLI dispatch requires a valid harness_id from HarnessCatalog (got '{harnessId}').");
            effectiveAssignee = new ProjectBoardAssigneeDto
            {
                Kind = ProjectBoardAssigneeKinds.Cli,
                Id = effectiveAssignee.Id ?? cliHarnessSpec.Id,
                DisplayName = effectiveAssignee.DisplayName ?? cliHarnessSpec.DisplayName,
                HarnessId = cliHarnessSpec.Id,
                ModeVersionId = effectiveAssignee.ModeVersionId
            };
        }

        // 2. Allocate isolated Git Worktree & ResourceLease when requested and available.
        var useWorktree = request.UseWorktree ?? true;
        var worktreePath = current.WorktreePath;
        if (useWorktree && (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath)))
        {
            worktreePath = TryAllocateGitWorktree(project, current.TaskKey, current.TaskId);
        }

        var workingDirectory = !string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath)
            ? worktreePath
            : project.RootPath;

        // 3. Create or reuse session tagged with origin = "board_task" and board_task_id = taskId.
        var session = await EnsureBoardTaskSessionAsync(
            project,
            current,
            effectiveAssignee,
            useWorktree: worktreePath is not null,
            requirePublishedMode: effectiveAssignee.Kind != ProjectBoardAssigneeKinds.Cli,
            cancellationToken).ConfigureAwait(false);

        var leaseId = current.LeaseId;
        if (!leaseId.HasValue && _services.GetService<IResourceLeaseService>() is { } leaseService)
        {
            var claimKind = worktreePath is not null ? ResourceLeaseKinds.Worktree : ResourceLeaseKinds.Path;
            var claim = new ResourceClaim(claimKind, workingDirectory, Exclusive: worktreePath is not null);
            var decision = await leaseService.AcquireAsync(new ResourceAcquireRequest(
                claim,
                SessionId: session.Id,
                RunId: null,
                TaskId: taskId,
                Reason: $"Board task {current.TaskKey}: {current.Title}",
                Purpose: ResourceLeasePurposes.Assignment), cancellationToken).ConfigureAwait(false);

            if (decision.Granted && decision.Lease is not null)
            {
                leaseId = decision.Lease.Id;
            }
            else if (request.FailOnLeaseContention && decision.Conflicts.Count > 0)
            {
                throw new ProjectBoardLeaseContentionException(ResourceLeaseMessages.Describe(claim, decision.Conflicts));
            }
        }

        Interlocked.Increment(ref _dispatchCount);
        var prompt = await BuildDispatchPromptAsync(project, settings, current, workingDirectory, request.ExtraPrompt, cancellationToken).ConfigureAwait(false);

        // 4A. External CLI Harness execution
        if (effectiveAssignee.Kind == ProjectBoardAssigneeKinds.Cli && cliHarnessSpec is not null)
        {
            await _sessionStore.AddMessageAsync(session.Id, prompt, "user", null, cancellationToken).ConfigureAwait(false);
            var cliExecutor = _services.GetService<IBoardCliHarnessExecutor>() ?? new CatalogBoardCliHarnessExecutor();

            using var cliCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCliExecutions[taskId] = cliCts;
            try
            {
                _ = await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
                {
                    row.AssigneeJson = JsonSerializer.Serialize(effectiveAssignee, ProjectBoardMarkdownCodec.CanonicalJson);
                    row.Status = ProjectBoardTaskStatuses.InProgress;
                    row.Blocked = false;
                    row.BlockedReason = null;
                    row.ActiveSessionId = session.Id;
                    row.RunStatus = "executing";
                    row.WorktreePath = worktreePath;
                    row.LeaseId = leaseId;
                    row.LiveDigestSummary = $"Running {cliHarnessSpec.DisplayName}…";
                }, exportDeclarativeMarkdown: true, cliCts.Token).ConfigureAwait(false);

                var outcome = await cliExecutor.ExecuteAsync(new BoardCliHarnessExecutionRequest(
                    cliHarnessSpec.Id,
                    cliHarnessSpec,
                    prompt,
                    workingDirectory,
                    session.Id,
                    taskId), cliCts.Token).ConfigureAwait(false);

                if (outcome.Succeeded && !IsHumanDecisionRequiredOutput(outcome.OutputSummary, out _))
                {
                    await _sessionStore.AddMessageAsync(session.Id, outcome.OutputSummary, "assistant", null, CancellationToken.None).ConfigureAwait(false);
                    var diff = ComputeGitDiff(workingDirectory, worktreePath);
                    var diffStats = new ProjectBoardDiffStatsDto
                    {
                        Files = diff.Files.Count,
                        Added = diff.Files.Sum(f => f.Added),
                        Removed = diff.Files.Sum(f => f.Removed)
                    };

                    var completedTask = await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
                    {
                        row.Status = ProjectBoardTaskStatuses.InReview;
                        row.RunStatus = "completed";
                        row.Blocked = false;
                        row.BlockedReason = null;
                        row.LiveDigestSummary = outcome.OutputSummary;
                        row.DiffStatsJson = JsonSerializer.Serialize(diffStats, ProjectBoardMarkdownCodec.CanonicalJson);
                    }, exportDeclarativeMarkdown: true, CancellationToken.None).ConfigureAwait(false);

                    await TryAppendTaskProgressNoteAsync(
                        projectId,
                        $"- [{current.TaskKey}] Completed ({cliHarnessSpec.DisplayName}): {current.Title} — {outcome.OutputSummary}",
                        CancellationToken.None).ConfigureAwait(false);

                    return completedTask;
                }

                _ = IsHumanDecisionRequiredOutput(outcome.OutputSummary, out var humanDecisionReason);
                await TryReleaseLeaseSafeAsync(leaseId).ConfigureAwait(false);

                return await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
                {
                    row.RunStatus = outcome.Succeeded ? "awaiting_user" : "failed";
                    row.LeaseId = null;
                    row.Blocked = true;
                    row.BlockedReason = humanDecisionReason
                        ?? outcome.ErrorMessage
                        ?? $"CLI harness '{cliHarnessSpec.DisplayName}' failed.";
                    row.LiveDigestSummary = row.BlockedReason;
                }, exportDeclarativeMarkdown: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cliCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await TryReleaseLeaseSafeAsync(leaseId).ConfigureAwait(false);
                return await _coordinator.GetTaskAsync(projectId, taskId, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _activeCliExecutions.TryRemove(taskId, out _);
            }
        }

        // 4B. Native Agent / Coordinator execution via Core session run coordinator
        Guid? startedRunId = null;
        string runStatus = "executing";
        string? digestSummary = "Task dispatched to native agent";

        if (_services.GetService<IFullDuplexRunCoordinator>() is { } runCoordinator)
        {
            var submission = await runCoordinator.SubmitAsync(new FullDuplexInvocation(
                session.Id,
                prompt,
                ClientMessageId: $"board-dispatch-{taskId:N}-{Guid.NewGuid():N}",
                PermissionMode: session.PermissionMode ?? "default",
                TargetRunId: null,
                ExpectedContextRevision: null,
                MeetingModelOverride: null,
                ModeVersionId: session.ModeVersionId,
                QueueBehindActiveRun: true,
                SpaceOptions: ProjectSessionStore.ReadSpaceOptions(session),
                SessionSettingsCaptured: true), cancellationToken).ConfigureAwait(false);

            startedRunId = submission.RunId;
        }
        else
        {
            await _sessionStore.AddMessageAsync(session.Id, prompt, "user", null, cancellationToken).ConfigureAwait(false);
        }

        var dispatched = await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
        {
            row.AssigneeJson = JsonSerializer.Serialize(effectiveAssignee, ProjectBoardMarkdownCodec.CanonicalJson);
            row.Status = ProjectBoardTaskStatuses.InProgress;
            row.Blocked = false;
            row.BlockedReason = null;
            row.ActiveSessionId = session.Id;
            row.ActiveRunId = startedRunId;
            row.RunStatus = runStatus;
            row.WorktreePath = worktreePath;
            row.LeaseId = leaseId;
            row.LiveDigestSummary = digestSummary;
        }, exportDeclarativeMarkdown: true, cancellationToken).ConfigureAwait(false);

        if (dispatched is not null && startedRunId.HasValue)
        {
            ObserveRunLifecycleInBackground(projectId, taskId, startedRunId.Value);
        }

        return dispatched;
    }

    public async Task<ProjectBoardTaskDiffDto?> GetTaskDiffAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.LifecycleStatus != LifecycleStatuses.Active)
            return null;

        var task = await _coordinator.GetTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return null;

        var workingDir = !string.IsNullOrWhiteSpace(task.WorktreePath) && Directory.Exists(task.WorktreePath)
            ? task.WorktreePath
            : project.RootPath;

        var diff = ComputeGitDiff(workingDir, task.WorktreePath);

        // Keep cached diff_stats synchronized when diff is inspected.
        var stats = new ProjectBoardDiffStatsDto
        {
            Files = diff.Files.Count,
            Added = diff.Files.Sum(f => f.Added),
            Removed = diff.Files.Sum(f => f.Removed)
        };
        if (task.DiffStats is null || task.DiffStats.Files != stats.Files || task.DiffStats.Added != stats.Added || task.DiffStats.Removed != stats.Removed)
        {
            _ = await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
            {
                row.DiffStatsJson = JsonSerializer.Serialize(stats, ProjectBoardMarkdownCodec.CanonicalJson);
            }, exportDeclarativeMarkdown: false, cancellationToken).ConfigureAwait(false);
        }

        return diff;
    }

    public async Task<ProjectBoardTaskDto?> ApproveTaskAsync(
        Guid projectId,
        Guid taskId,
        ApproveProjectBoardTaskRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new ApproveProjectBoardTaskRequest();
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var task = await _coordinator.GetTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return null;

        if (!string.IsNullOrWhiteSpace(request.Note) && task.ActiveSessionId.HasValue)
        {
            try
            {
                await _sessionStore.AddMessageAsync(
                    task.ActiveSessionId.Value,
                    $"[Approved {task.TaskKey}] {request.Note.Trim()}",
                    "user",
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
            }
        }

        var cleanedWorktree = false;
        if (request.MergeOrCleanWorktree == true && !string.IsNullOrWhiteSpace(task.WorktreePath))
        {
            await IntegrateAndCleanWorktreeAsync(project.RootPath, task, cancellationToken).ConfigureAwait(false);
            cleanedWorktree = true;
        }

        if (task.LeaseId.HasValue && _services.GetService<IResourceLeaseService>() is { } leaseService)
        {
            try
            {
                await leaseService.ReleaseAsync(task.LeaseId.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Could not release lease {LeaseId} during task approval.", task.LeaseId.Value);
            }
        }

        var approvedTask = await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
        {
            row.Status = ProjectBoardTaskStatuses.Done;
            row.Blocked = false;
            row.BlockedReason = null;
            row.ActiveRunId = null;
            row.RunStatus = "completed";
            row.LeaseId = null;
            if (cleanedWorktree)
                row.WorktreePath = null;
            if (!string.IsNullOrWhiteSpace(request.Note))
                row.LiveDigestSummary = $"Approved: {request.Note.Trim()}";
        }, exportDeclarativeMarkdown: true, cancellationToken).ConfigureAwait(false);

        if (approvedTask is not null)
        {
            var noteSuffix = string.IsNullOrWhiteSpace(request.Note) ? string.Empty : $" — {request.Note.Trim()}";
            await TryAppendTaskProgressNoteAsync(
                projectId,
                $"- [{task.TaskKey}] Approved & Done: {task.Title}{noteSuffix}",
                cancellationToken).ConfigureAwait(false);
        }

        return approvedTask;
    }

    public async Task<ProjectBoardTaskDto?> RequestTaskChangesAsync(
        Guid projectId,
        Guid taskId,
        RequestTaskChangesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var feedback = request.Feedback?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(feedback))
            throw new ArgumentException("feedback is required when requesting task changes.");

        var task = await _coordinator.GetTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return null;

        if (task.Assignee?.Kind == ProjectBoardAssigneeKinds.Human)
        {
            if (task.ActiveSessionId.HasValue)
            {
                try
                {
                    await _sessionStore.AddMessageAsync(task.ActiveSessionId.Value, feedback, "user", null, cancellationToken).ConfigureAwait(false);
                }
                catch (KeyNotFoundException)
                {
                }
            }

            return await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
            {
                row.Status = ProjectBoardTaskStatuses.InProgress;
                row.Blocked = false;
                row.BlockedReason = null;
                row.LiveDigestSummary = $"Changes requested: {feedback}";
            }, exportDeclarativeMarkdown: true, cancellationToken).ConfigureAwait(false);
        }

        // Reuse the existing session & worktree via DispatchTaskAsync with the review feedback as extra prompt.
        return await DispatchTaskAsync(
            projectId,
            taskId,
            new DispatchProjectBoardTaskRequest
            {
                AssigneeOverride = task.Assignee,
                UseWorktree = !string.IsNullOrWhiteSpace(task.WorktreePath),
                ExtraPrompt = $"## Review Feedback (Changes Requested)\n{feedback}"
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectBoardTaskDto?> CancelTaskExecutionAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await _coordinator.GetTaskAsync(projectId, taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return null;

        if (_activeCliExecutions.TryRemove(taskId, out var cliCts))
        {
            try { cliCts.Cancel(); } catch { }
        }

        if (task.ActiveRunId.HasValue)
        {
            if (_services.GetService<IFullDuplexRunCoordinator>() is { } runCoordinator)
            {
                try
                {
                    await runCoordinator.ControlAsync(task.ActiveRunId.Value, new RunControlCommand("cancel", null, null), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Run cancel control returned error for run {RunId}.", task.ActiveRunId.Value);
                }
            }
            else if (_services.GetService<StorageLifecycleService>() is { } lifecycle)
            {
                try
                {
                    await lifecycle.SetRunStatusAsync(task.ActiveRunId.Value, "cancelled", "Cancelled from project board.", cancellationToken).ConfigureAwait(false);
                }
                catch { }
            }
        }

        if (task.LeaseId.HasValue && _services.GetService<IResourceLeaseService>() is { } leaseService)
        {
            try
            {
                await leaseService.ReleaseAsync(task.LeaseId.Value, cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }

        return await _coordinator.MutateTaskRuntimeStateAsync(projectId, taskId, row =>
        {
            if (row.Status == ProjectBoardTaskStatuses.InProgress)
                row.Status = ProjectBoardTaskStatuses.Ready;
            row.ActiveRunId = null;
            row.RunStatus = "cancelled";
            row.LeaseId = null;
            row.Blocked = false;
            row.BlockedReason = null;
        }, exportDeclarativeMarkdown: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectBoardTaskDto>> RefreshTasksRunStateAsync(
        Guid projectId,
        IReadOnlyList<ProjectBoardTaskDto> tasks,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Count == 0 || tasks.All(t => !t.ActiveRunId.HasValue))
            return tasks;

        var refreshed = new List<ProjectBoardTaskDto>(tasks.Count);
        foreach (var task in tasks)
        {
            if (task.ActiveRunId.HasValue)
                refreshed.Add(await RefreshSingleTaskRunStateAsync(projectId, task, cancellationToken).ConfigureAwait(false));
            else
                refreshed.Add(task);
        }
        return refreshed;
    }

    public async Task<ProjectBoardTaskDto> RefreshSingleTaskRunStateAsync(
        Guid projectId,
        ProjectBoardTaskDto task,
        CancellationToken cancellationToken = default)
    {
        if (!task.ActiveRunId.HasValue)
            return task;

        var lifecycle = _services.GetService<StorageLifecycleService>();
        if (lifecycle is null)
            return task;

        var runId = task.ActiveRunId.Value;
        var run = await lifecycle.FindRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
            return task;

        string? digestHeadline = null;
        if (_services.GetService<LiveExplainerService>() is { } explainer)
        {
            try
            {
                var snap = await explainer.GetSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(snap.Headline))
                    digestHeadline = snap.Headline;
            }
            catch
            {
            }
        }
        digestHeadline ??= string.IsNullOrWhiteSpace(run.Summary) ? task.LiveDigestSummary : run.Summary;

        var mappedRunStatus = run.Status.Trim().ToLowerInvariant();
        var nextTaskStatus = task.Status;
        var nextBlocked = task.Blocked;
        var nextBlockedReason = task.BlockedReason;
        Guid? nextActiveRunId = runId;
        ProjectBoardDiffStatsDto? computedDiffStats = task.DiffStats;

        switch (mappedRunStatus)
        {
            case "planning" or "understanding" or "executing" or "replanning" or "reviewing":
                if (nextTaskStatus is ProjectBoardTaskStatuses.Backlog or ProjectBoardTaskStatuses.Ready)
                    nextTaskStatus = ProjectBoardTaskStatuses.InProgress;
                nextBlocked = false;
                break;

            case "awaiting_approval" or "awaiting_user" or "awaiting_delegate" or "paused":
                nextBlocked = true;
                nextBlockedReason ??= $"Run requires attention ({mappedRunStatus}).";
                break;

            case "completed":
                if (nextTaskStatus != ProjectBoardTaskStatuses.Done)
                    nextTaskStatus = ProjectBoardTaskStatuses.InReview;
                nextBlocked = false;
                nextBlockedReason = null;
                nextActiveRunId = null;

                var project = await _sessionStore.GetProjectAnyStatusAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (project is not null)
                {
                    var wd = !string.IsNullOrWhiteSpace(task.WorktreePath) && Directory.Exists(task.WorktreePath)
                        ? task.WorktreePath
                        : project.RootPath;
                    var diff = ComputeGitDiff(wd, task.WorktreePath);
                    computedDiffStats = new ProjectBoardDiffStatsDto
                    {
                        Files = diff.Files.Count,
                        Added = diff.Files.Sum(f => f.Added),
                        Removed = diff.Files.Sum(f => f.Removed)
                    };
                }
                break;

            case "failed":
                nextBlocked = true;
                nextBlockedReason = string.IsNullOrWhiteSpace(run.Summary) ? "Task run failed." : run.Summary;
                nextActiveRunId = null;
                break;

            case "cancelled":
                nextActiveRunId = null;
                break;
        }

        var unchanged = nextTaskStatus == task.Status
            && nextBlocked == task.Blocked
            && string.Equals(nextBlockedReason, task.BlockedReason, StringComparison.Ordinal)
            && nextActiveRunId == task.ActiveRunId
            && string.Equals(mappedRunStatus, task.RunStatus, StringComparison.Ordinal)
            && string.Equals(digestHeadline, task.LiveDigestSummary, StringComparison.Ordinal);

        if (unchanged)
            return task;

        var declarativeChanged = nextTaskStatus != task.Status
            || nextBlocked != task.Blocked
            || !string.Equals(nextBlockedReason, task.BlockedReason, StringComparison.Ordinal);

        var transitionedToComplete = mappedRunStatus == "completed"
            && task.Status is not (ProjectBoardTaskStatuses.InReview or ProjectBoardTaskStatuses.Done);
        var releaseLeaseOnFailure = mappedRunStatus is "failed" or "cancelled" && task.LeaseId.HasValue;
        if (releaseLeaseOnFailure)
        {
            await TryReleaseLeaseSafeAsync(task.LeaseId).ConfigureAwait(false);
        }

        var updated = await _coordinator.MutateTaskRuntimeStateAsync(projectId, task.TaskId, row =>
        {
            row.Status = nextTaskStatus;
            row.Blocked = nextBlocked;
            row.BlockedReason = nextBlockedReason;
            row.ActiveRunId = nextActiveRunId;
            row.RunStatus = mappedRunStatus;
            row.LiveDigestSummary = digestHeadline;
            if (releaseLeaseOnFailure)
                row.LeaseId = null;
            if (computedDiffStats is not null)
                row.DiffStatsJson = JsonSerializer.Serialize(computedDiffStats, ProjectBoardMarkdownCodec.CanonicalJson);
        }, exportDeclarativeMarkdown: declarativeChanged, cancellationToken).ConfigureAwait(false);

        if (transitionedToComplete && updated is not null)
        {
            var summaryPart = string.IsNullOrWhiteSpace(digestHeadline) ? "Run completed." : digestHeadline;
            await TryAppendTaskProgressNoteAsync(
                projectId,
                $"- [{task.TaskKey}] Completed (Native Agent): {task.Title} — {summaryPart}",
                cancellationToken).ConfigureAwait(false);
        }

        return updated ?? task;
    }

    private void ObserveRunLifecycleInBackground(Guid projectId, Guid taskId, Guid runId)
    {
        var lifecycle = _services.GetService<StorageLifecycleService>();
        if (lifecycle is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                for (var tick = 0; tick < 600; tick++)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    var current = await _coordinator.GetTaskAsync(projectId, taskId).ConfigureAwait(false);
                    if (current is null || current.ActiveRunId != runId)
                        return;

                    var refreshed = await RefreshSingleTaskRunStateAsync(projectId, current).ConfigureAwait(false);
                    if (!refreshed.ActiveRunId.HasValue || refreshed.RunStatus is "completed" or "failed" or "cancelled")
                        return;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Background run lifecycle watcher stopped for task {TaskId}.", taskId);
            }
        });
    }

    private async Task<SessionRecord> EnsureBoardTaskSessionAsync(
        ProjectRecord project,
        ProjectBoardTaskDto task,
        ProjectBoardAssigneeDto assignee,
        bool useWorktree,
        bool requirePublishedMode,
        CancellationToken ct)
    {
        if (task.ActiveSessionId.HasValue)
        {
            var existing = await _sessionStore.GetSessionAsync(task.ActiveSessionId.Value, ct).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }

        Guid? modeVersionId = assignee.ModeVersionId;
        string? conversationNodeKey = null;
        string? conversationTemplateSlug = null;

        if (_cfgFactory is not null && _globalConfig is not null)
        {
            var tenantScope = _tenant.Current;
            modeVersionId ??= await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
                _cfgFactory, _globalConfig, tenantScope.TenantId, tenantScope.WorkspaceId, ct).ConfigureAwait(false);

            if (modeVersionId.HasValue)
            {
                var modeFactory = await AgentConfigurationLookup.SelectModeFactoryAsync(
                    _cfgFactory, _globalConfig, tenantScope.TenantId, tenantScope.WorkspaceId, modeVersionId.Value, ct).ConfigureAwait(false);
                await using var cfg = await modeFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var modeVersion = await cfg.ModeVersions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == modeVersionId.Value
                        && x.TenantId == tenantScope.TenantId
                        && x.WorkspaceId == tenantScope.WorkspaceId
                        && x.Status == "published", ct)
                    .ConfigureAwait(false);

                if (modeVersion is not null)
                {
                    var defs = await cfg.AgentDefinitions.AsNoTracking()
                        .Where(x => x.TenantId == tenantScope.TenantId && x.WorkspaceId == tenantScope.WorkspaceId)
                        .Select(x => new ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    var identity = ConversationIdentityResolver.Resolve(modeVersion.SnapshotJson, defs);
                    conversationNodeKey = identity?.NodeKey;
                    conversationTemplateSlug = identity?.TemplateSlug;
                }
                else if (requirePublishedMode)
                {
                    throw new ArgumentException("mode_version_id must reference a published Agent Mode version.");
                }
            }
        }

        if (!modeVersionId.HasValue)
        {
            if (requirePublishedMode && _services.GetService<IFullDuplexRunCoordinator>() is not null)
                throw new ProjectBoardConflictStateException("agent_mode_not_configured", "A published default Agent Mode must be configured before dispatching a native agent task.");
            modeVersionId = CliFallbackModeVersionId;
        }

        return await _sessionStore.CreateSessionAsync(
            projectId: project.Id,
            title: $"[{task.TaskKey}] {task.Title}",
            modeVersionId: modeVersionId.Value,
            meetingModelOverride: null,
            conversationNodeKey: conversationNodeKey,
            conversationTemplateSlug: conversationTemplateSlug,
            cancellationToken: ct,
            viewMode: "flat",
            permissionMode: "default",
            spaceOptions: useWorktree ? new SpaceRunOptions(Worktree: true) : null,
            origin: "board_task",
            boardTaskId: task.TaskId).ConfigureAwait(false);
    }

    private async Task<string> BuildDispatchPromptAsync(
        ProjectRecord project,
        ProjectBoardSettingsDto settings,
        ProjectBoardTaskDto task,
        string workingDirectory,
        string? extraPrompt,
        CancellationToken ct)
    {
        var sb = new StringBuilder(1024);
        sb.Append("# Task ").Append(task.TaskKey).Append(": ").Append(task.Title).Append("\n\n");
        sb.Append("- Project: ").Append(project.Name).Append('\n');
        sb.Append("- Working Directory: ").Append(workingDirectory).Append('\n');

        if (_services.GetService<IWorkspaceDefinitionProvider>()?.Read() is { } workspaceDef)
        {
            if (workspaceDef.Roots.Count > 0)
            {
                sb.Append("- Authorized Roots: ")
                    .Append(string.Join(", ", workspaceDef.Roots.Select(r => r.Path)))
                    .Append('\n');
            }
            if (workspaceDef.Files.Count > 0)
            {
                sb.Append("- Authorized Files: ")
                    .Append(string.Join(", ", workspaceDef.Files.Select(f => f.Path)))
                    .Append('\n');
            }
        }

        var instructions = settings.ProjectInstructions?.Trim() ?? string.Empty;
        if (instructions.Length > ProjectBoardFileSyncService.DefaultNotesContextMaxChars)
            instructions = instructions[..ProjectBoardFileSyncService.DefaultNotesContextMaxChars].TrimEnd() + "\n…";

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.Append("\n## Project Instructions\n").Append(instructions).Append('\n');
        }

        string notesSummary = string.Empty;
        if (_services.GetService<IProjectNotesContextReader>() is { } contextReader)
        {
            var snap = await contextReader.ReadProjectContextAsync(
                project.Id,
                ProjectBoardFileSyncService.DefaultNotesContextMaxChars,
                ct).ConfigureAwait(false);
            if (snap is not null)
                notesSummary = snap.NotesSummaryMarkdown;
        }
        else
        {
            var notesPath = Path.Combine(project.RootPath, ".tinadec", "project", "notes.md");
            if (File.Exists(notesPath))
            {
                try
                {
                    notesSummary = ProjectBoardFileSyncService.SummarizeProjectNotes(
                        await File.ReadAllTextAsync(notesPath, ct).ConfigureAwait(false),
                        ProjectBoardFileSyncService.DefaultNotesContextMaxChars);
                }
                catch
                {
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(notesSummary))
        {
            sb.Append("\n## Project Notes (notes.md)\n").Append(notesSummary).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(task.DescriptionMd))
        {
            sb.Append("\n## Task Description\n").Append(task.DescriptionMd.Trim()).Append('\n');
        }

        if (task.Checklist.Count > 0)
        {
            sb.Append("\n## Acceptance Criteria\n");
            foreach (var item in task.Checklist)
            {
                sb.Append(item.Done ? "- [x] " : "- [ ] ").Append(item.Text).Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(extraPrompt))
        {
            sb.Append('\n').Append(extraPrompt.Trim()).Append('\n');
        }

        return sb.ToString();
    }

    private async Task TryAppendTaskProgressNoteAsync(Guid projectId, string entry, CancellationToken ct)
    {
        try
        {
            if (_coordinator.GetSyncHook() is { } syncHook)
            {
                _ = await syncHook.AppendProjectNoteEntryAsync(projectId, entry, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Could not append task progress note for project {ProjectId}.", projectId);
        }
    }

    private async Task TryReleaseLeaseSafeAsync(Guid? leaseId)
    {
        if (!leaseId.HasValue || _services.GetService<IResourceLeaseService>() is not { } leaseService)
            return;

        try
        {
            await leaseService.ReleaseAsync(leaseId.Value, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static bool IsHumanDecisionRequiredOutput(string? output, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var trimmed = output.Trim();
        foreach (var prefix in new[] { "[BLOCKED]", "BLOCKED:", "HUMAN_DECISION_REQUIRED:" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var detail = trimmed[prefix.Length..].Trim();
                reason = string.IsNullOrEmpty(detail) ? "Task requires human decision before continuing." : detail;
                return true;
            }
        }
        return false;
    }

    private string? TryAllocateGitWorktree(ProjectRecord project, string taskKey, Guid taskId)
    {
        if (!IsGitCheckout(project.RootPath))
            return null;

        var headCheck = TryRunGit(project.RootPath, "rev-parse", "--verify", "HEAD");
        if (!headCheck.Success)
            return null;

        var scopeRoot = _services.GetService<IScopeStorageLocations>()?.Root
            ?? Path.Combine(project.RootPath, ".tinadec");
        var safeKey = new string(taskKey.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        var worktreeDir = Path.Combine(scopeRoot, "worktrees", $"board-{safeKey}-{taskId.ToString("N")[..8]}");
        var branchName = $"tinadec/board/{safeKey}";

        if (Directory.Exists(worktreeDir) && IsGitCheckout(worktreeDir))
            return worktreeDir;

        Directory.CreateDirectory(Path.GetDirectoryName(worktreeDir)!);

        var branchExists = TryRunGit(project.RootPath, "show-ref", "--verify", "--quiet", $"refs/heads/{branchName}").Success;
        var addResult = branchExists
            ? TryRunGit(project.RootPath, "worktree", "add", worktreeDir, branchName)
            : TryRunGit(project.RootPath, "worktree", "add", "-b", branchName, worktreeDir);

        return addResult.Success && Directory.Exists(worktreeDir) ? worktreeDir : null;
    }

    private async Task IntegrateAndCleanWorktreeAsync(string repoRoot, ProjectBoardTaskDto task, CancellationToken ct)
    {
        var wtPath = task.WorktreePath!;
        if (!Directory.Exists(wtPath))
            return;

        if (IsGitCheckout(repoRoot) && IsGitCheckout(wtPath))
        {
            var branchOut = TryRunGit(wtPath, "rev-parse", "--abbrev-ref", "HEAD");
            var branchName = branchOut.Success ? branchOut.Stdout.Trim() : null;

            if (_services.GetService<IWorktreeIntegratorService>() is { } integrator
                && !string.IsNullOrWhiteSpace(branchName)
                && branchName != "HEAD")
            {
                try
                {
                    var spec = new TeammateWorktreeSpec(task.TaskKey, wtPath, branchName, WriteScope: null);
                    _ = await integrator.IntegrateAsync(
                        new WorktreeIntegrationRequest(
                            repoRoot,
                            [spec],
                            Strategy: WorktreeIntegrationStrategies.NoFf,
                            AutoCommitDirtyWorktrees: true,
                            CommitMessagePrefix: $"board({task.TaskKey})"),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Worktree integration skipped during task {TaskKey} approval.", task.TaskKey);
                }
            }

            _ = TryRunGit(repoRoot, "worktree", "remove", "--force", wtPath);
        }

        try
        {
            if (Directory.Exists(wtPath))
                Directory.Delete(wtPath, recursive: true);
        }
        catch
        {
        }
    }

    internal static ProjectBoardTaskDiffDto ComputeGitDiff(string workingDirectory, string? worktreePath)
    {
        if (!Directory.Exists(workingDirectory) || !IsGitCheckout(workingDirectory))
        {
            return new ProjectBoardTaskDiffDto
            {
                WorktreePath = worktreePath,
                DiffText = string.Empty,
                Files = []
            };
        }

        var hasHead = TryRunGit(workingDirectory, "rev-parse", "--verify", "HEAD").Success;
        var diffOut = hasHead
            ? TryRunGit(workingDirectory, "diff", "HEAD")
            : TryRunGit(workingDirectory, "diff");

        var numstatOut = hasHead
            ? TryRunGit(workingDirectory, "diff", "--numstat", "HEAD")
            : TryRunGit(workingDirectory, "diff", "--numstat");

        var statusOut = TryRunGit(workingDirectory, "status", "--porcelain");

        var statusByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var untrackedPaths = new List<string>();

        if (statusOut.Success && !string.IsNullOrWhiteSpace(statusOut.Stdout))
        {
            foreach (var rawLine in statusOut.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawLine.Length < 4) continue;
                var code = rawLine[..2];
                var relPath = rawLine[3..].Trim().Trim('"').Replace('\\', '/');
                if (relPath.StartsWith(".tinadec/", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (code == "??")
                {
                    statusByPath[relPath] = "added";
                    untrackedPaths.Add(relPath);
                }
                else if (code.Contains('A'))
                {
                    statusByPath[relPath] = "added";
                }
                else if (code.Contains('D'))
                {
                    statusByPath[relPath] = "deleted";
                }
                else if (code.Contains('R'))
                {
                    statusByPath[relPath] = "renamed";
                }
                else
                {
                    statusByPath[relPath] = "modified";
                }
            }
        }

        var files = new List<ProjectBoardDiffFileDto>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (numstatOut.Success && !string.IsNullOrWhiteSpace(numstatOut.Stdout))
        {
            foreach (var line in numstatOut.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var relPath = parts[2].Trim().Replace('\\', '/');
                if (relPath.StartsWith(".tinadec/", StringComparison.OrdinalIgnoreCase))
                    continue;

                _ = int.TryParse(parts[0], out var added);
                _ = int.TryParse(parts[1], out var removed);
                var status = statusByPath.GetValueOrDefault(relPath, "modified");
                seenPaths.Add(relPath);
                files.Add(new ProjectBoardDiffFileDto
                {
                    Path = relPath,
                    Status = status,
                    Added = added,
                    Removed = removed
                });
            }
        }

        var diffBuilder = new StringBuilder(diffOut.Success ? diffOut.Stdout : string.Empty);

        foreach (var untracked in untrackedPaths)
        {
            if (!seenPaths.Add(untracked)) continue;
            var fullUntracked = Path.Combine(workingDirectory, untracked.Replace('/', Path.DirectorySeparatorChar));
            var lineCount = 0;
            try
            {
                if (File.Exists(fullUntracked) && new FileInfo(fullUntracked).Length <= 256 * 1024)
                {
                    var text = File.ReadAllText(fullUntracked);
                    var lines = ProjectBoardMarkdownCodec.NormalizeLineEndings(text).Split('\n');
                    lineCount = lines.Length;
                    diffBuilder.Append("\ndiff --git a/").Append(untracked).Append(" b/").Append(untracked).Append('\n')
                        .Append("new file mode 100644\n--- /dev/null\n+++ b/").Append(untracked).Append('\n')
                        .Append("@@ -0,0 +1,").Append(lineCount).Append(" @@\n");
                    foreach (var l in lines)
                        diffBuilder.Append('+').Append(l).Append('\n');
                }
            }
            catch
            {
            }

            files.Add(new ProjectBoardDiffFileDto
            {
                Path = untracked,
                Status = "added",
                Added = lineCount,
                Removed = 0
            });
        }

        return new ProjectBoardTaskDiffDto
        {
            WorktreePath = worktreePath,
            DiffText = diffBuilder.ToString().Trim(),
            Files = files
        };
    }

    private static bool IsGitCheckout(string dir)
    {
        var dotGit = Path.Combine(dir, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static (bool Success, string Stdout, string Stderr) TryRunGit(string workingDir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            if (proc is null) return (false, string.Empty, "Failed to start git.");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15_000);
            return (proc.HasExited && proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
