using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentGraph;
using TinadecCore.Lifecycle;

namespace TinadecCore.Runtime;

/// <summary>
/// Governed ACP <c>session/request_permission</c> bridge (#36).
/// <list type="bullet">
/// <item>Auto-approves read-only or file-write operations whose <see cref="AcpPermissionCheckRequest.AffectedPaths"/>
/// canonically resolve strictly inside <see cref="AcpPermissionCheckRequest.WorkingDirectory"/> (blocking <c>..</c>
/// traversal and <c>.git/</c> hook escapes) AND match <see cref="AcpPermissionCheckRequest.WriteScope"/> when declared.</item>
/// <item>Bridges out-of-scope, destructive (<c>delete</c>), or shell/command (<c>execute</c>/<c>shell</c>) requests
/// into TinadecCore's durable approval gate (<see cref="IToolApprovalCoordinator"/> + <see cref="IToolApprovalGate"/>)
/// when bound to a live run, or fails closed immediately when no interactive approval gate exists.</item>
/// </list>
/// </summary>
public sealed class CoreAcpApprovalBridge : IAcpApprovalBridge
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CoreAcpApprovalBridge>? _logger;

    public CoreAcpApprovalBridge(IServiceProvider services, ILogger<CoreAcpApprovalBridge>? logger = null)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<AcpPermissionDecision> EvaluateOrWaitForApprovalAsync(
        AcpPermissionCheckRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var allowOption = SelectAllowOption(request.AvailableOptionKinds);
        var rejectOption = SelectRejectOption(request.AvailableOptionKinds);

        var evaluation = EvaluateContainmentPolicy(request);
        if (evaluation.AutoApproved)
        {
            return new AcpPermissionDecision(
                Approved: true,
                Reason: evaluation.Reason,
                SelectedOptionKind: allowOption);
        }

        // Attempt to bridge into TinadecCore's durable interactive approval gate if bound to a live run.
        var interactiveDecision = await TryWaitOnCoreApprovalGateAsync(request, evaluation.Reason, allowOption, rejectOption, ct)
            .ConfigureAwait(false);
        if (interactiveDecision is not null)
        {
            return interactiveDecision;
        }

        // Headless test mode or unbound/non-interactive session: fail closed without hanging.
        _logger?.LogInformation(
            "Refusing ACP permission request (session={SessionId}, run={RunId}, teammate={Teammate}, kind={Kind}): {Reason}",
            request.SessionId,
            request.RunId ?? string.Empty,
            request.TeammateHandle ?? string.Empty,
            request.Kind ?? string.Empty,
            evaluation.Reason);

        return new AcpPermissionDecision(
            Approved: false,
            Reason: evaluation.Reason,
            SelectedOptionKind: rejectOption);
    }

    internal static (bool AutoApproved, string Reason) EvaluateContainmentPolicy(AcpPermissionCheckRequest request)
    {
        var kind = NormalizeKind(request.Kind, request.Title);

        if (kind is "execute" or "shell")
        {
            return (false, "Shell or subprocess execution from an ACP harness requires explicit approval.");
        }

        if (kind is "delete")
        {
            return (false, "Destructive file deletion from an ACP harness requires explicit approval.");
        }

        if (kind == "think" && request.AffectedPaths.Count == 0)
        {
            return (true, "Pure reasoning step requires no workspace mutation.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return (false, "ACP session has no governed WorkingDirectory boundary; refusing automatic approval.");
        }

        string canonicalRoot;
        try
        {
            canonicalRoot = WorkspacePathSpelling.Canonical(request.WorkingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, $"ACP WorkingDirectory '{request.WorkingDirectory}' is invalid.");
        }

        if (kind is "read" or "search")
        {
            if (request.AffectedPaths.Count == 0)
            {
                return (true, $"Read/search within governed working directory '{canonicalRoot}'.");
            }

            foreach (var rawPath in request.AffectedPaths)
            {
                if (!TryResolveStrictlyInsideRoot(canonicalRoot, rawPath, out _, out var pathError))
                {
                    return (false, $"ACP read path escaped WorkingDirectory: {pathError}");
                }
            }

            return (true, $"All {request.AffectedPaths.Count} read path(s) are strictly inside '{canonicalRoot}'.");
        }

        // Mutating / file-write / move / patch / unspecified tool kinds:
        if (request.AffectedPaths.Count == 0)
        {
            return (false, $"ACP '{kind}' permission request declared no affected file paths; cannot verify workspace containment.");
        }

        var relativePaths = new List<string>(request.AffectedPaths.Count);
        foreach (var rawPath in request.AffectedPaths)
        {
            if (!TryResolveStrictlyInsideRoot(canonicalRoot, rawPath, out var relativePath, out var pathError))
            {
                return (false, $"ACP write path escaped WorkingDirectory: {pathError}");
            }

            if (IsGitMetadataPath(relativePath))
            {
                return (false, $"ACP write to repository metadata '{relativePath}' is forbidden without explicit approval.");
            }

            relativePaths.Add(relativePath);
        }

        var violations = WorktreeIntegratorService.EvaluateWriteScopeViolations(
            string.IsNullOrWhiteSpace(request.TeammateHandle) ? "acp" : request.TeammateHandle!,
            relativePaths,
            request.WriteScope);

        if (violations.Count > 0)
        {
            return (false, $"ACP write violated declared write_scope: {string.Join("; ", violations)}");
        }

        return (true, $"Auto-approved in-scope ACP '{kind}' across {relativePaths.Count} path(s) within '{canonicalRoot}'.");
    }

    internal static bool TryResolveStrictlyInsideRoot(
        string canonicalRoot,
        string rawCandidate,
        out string normalizedRelativePath,
        out string error)
    {
        normalizedRelativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            error = "empty path";
            return false;
        }

        string combined;
        try
        {
            var trimmed = rawCandidate.Trim();
            combined = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(canonicalRoot, trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"invalid path '{rawCandidate}'";
            return false;
        }

        string canonicalCandidate;
        try
        {
            canonicalCandidate = WorkspacePathSpelling.Canonical(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"unresolvable path '{rawCandidate}'";
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(canonicalCandidate, canonicalRoot, comparison))
        {
            normalizedRelativePath = ".";
            error = string.Empty;
            return true;
        }

        var rootWithSep = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;

        if (!canonicalCandidate.StartsWith(rootWithSep, comparison))
        {
            error = $"'{rawCandidate}' resolves to '{canonicalCandidate}', outside '{canonicalRoot}'";
            return false;
        }

        var rel = Path.GetRelativePath(canonicalRoot, canonicalCandidate).Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(rel)
            || rel.Equals("..", StringComparison.Ordinal)
            || rel.StartsWith("../", StringComparison.Ordinal))
        {
            error = $"'{rawCandidate}' traverses outside '{canonicalRoot}'";
            return false;
        }

        normalizedRelativePath = rel;
        error = string.Empty;
        return true;
    }

    private async Task<AcpPermissionDecision?> TryWaitOnCoreApprovalGateAsync(
        AcpPermissionCheckRequest request,
        string gateReason,
        string allowOption,
        string rejectOption,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RunId) || !Guid.TryParse(request.RunId, out var runGuid))
            return null;

        var lifecycle = _services.GetService<ILifecycleManager>();
        var coordinator = _services.GetService<IToolApprovalCoordinator>();
        var lifecycleDb = _services.GetService<IDbContextFactory<LifecycleDbContext>>();
        var gate = _services.GetService<IToolApprovalGate>();

        if (lifecycle is null || coordinator is null || (lifecycleDb is null && gate is null))
            return null;

        RunState runState;
        try
        {
            runState = await lifecycle.GetRunStateAsync(runGuid.ToString("N"), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(runState.SessionId)
            || runState.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
        {
            return null;
        }

        var toolLabel = $"acp:{NormalizeKind(request.Kind, request.Title)}";
        var summaryBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.TeammateHandle))
            summaryBuilder.Append('[').Append(request.TeammateHandle).Append("] ");
        summaryBuilder.Append(string.IsNullOrWhiteSpace(request.Title) ? toolLabel : request.Title!.Trim());
        if (request.AffectedPaths.Count > 0)
            summaryBuilder.Append(" (paths: ").Append(string.Join(", ", request.AffectedPaths.Take(8))).Append(')');
        summaryBuilder.Append(" — ").Append(gateReason);
        var summary = summaryBuilder.ToString();

        var hashPayload = $"{request.RunId}|{request.TeammateHandle}|{request.ToolCallId}|{toolLabel}|{string.Join(",", request.AffectedPaths)}|{request.RawParametersJson}";
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashPayload))).ToLowerInvariant();

        Guid approvalId;
        try
        {
            approvalId = await coordinator.CreateToolApprovalAsync(
                runState.SessionId,
                runGuid.ToString("N"),
                taskId: string.Empty,
                toolId: toolLabel,
                parametersHash: requestHash,
                summary: summary,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Could not persist ACP approval request for run {RunId}; failing closed.", runGuid);
            return null;
        }

        try
        {
            await lifecycle.AppendEventAsync(
                runGuid,
                "approval.requested",
                new
                {
                    approval_id = approvalId,
                    tool_id = toolLabel,
                    teammate_handle = request.TeammateHandle,
                    affected_paths = request.AffectedPaths,
                    summary,
                },
                summary,
                severity: "warning",
                approvalId: approvalId,
                toolId: toolLabel,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Failed to append approval.requested event for ACP approval {ApprovalId}.", approvalId);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var tasks = new List<Task<string>>(2);
            if (gate is not null)
                tasks.Add(gate.WaitAsync(approvalId, linkedCts.Token));
            if (lifecycleDb is not null)
                tasks.Add(PollDurableApprovalDecisionAsync(lifecycleDb, approvalId, linkedCts.Token));

            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            var outcome = await completed.ConfigureAwait(false);
            var approved = string.Equals(outcome, "approved", StringComparison.OrdinalIgnoreCase);

            return new AcpPermissionDecision(
                Approved: approved,
                Reason: approved
                    ? $"Approved via Core approval gate ({approvalId:N})."
                    : $"Rejected or expired via Core approval gate ({outcome}).",
                SelectedOptionKind: approved ? allowOption : rejectOption);
        }
        finally
        {
            try { linkedCts.Cancel(); } catch { }
            gate?.Cancel(approvalId);
        }
    }

    private static async Task<string> PollDurableApprovalDecisionAsync(
        IDbContextFactory<LifecycleDbContext> dbFactory,
        Guid approvalId,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var status = await db.ApprovalRequests.AsNoTracking()
                    .Where(x => x.Id == approvalId)
                    .Select(x => new { x.Status, x.ExpiresAt })
                    .SingleOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (status is null)
                    return "cancelled";

                if (!string.Equals(status.Status, "pending", StringComparison.OrdinalIgnoreCase))
                    return status.Status;

                if (status.ExpiresAt <= DateTimeOffset.UtcNow)
                    return "expired";
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return "cancelled";
    }

    private static bool IsGitMetadataPath(string normalizedRelativePath) =>
        string.Equals(normalizedRelativePath, ".git", StringComparison.OrdinalIgnoreCase)
        || normalizedRelativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
        || normalizedRelativePath.Contains("/.git/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeKind(string? kind, string? title)
    {
        var k = kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (k is "execute" or "shell" or "bash" or "terminal" or "command" or "cmd" or "powershell" or "pwsh")
            return "execute";
        if (k is "delete" or "remove" or "unlink")
            return "delete";
        if (k is "read" or "view" or "cat" or "open")
            return "read";
        if (k is "search" or "grep" or "glob" or "find" or "list")
            return "search";
        if (k is "think")
            return "think";
        if (k is "edit" or "write" or "create" or "patch" or "replace" or "move")
            return "write";

        var t = title?.Trim().ToLowerInvariant() ?? string.Empty;
        if (t.StartsWith("run ", StringComparison.Ordinal)
            || t.StartsWith("exec", StringComparison.Ordinal)
            || t.StartsWith("bash", StringComparison.Ordinal)
            || t.StartsWith("shell", StringComparison.Ordinal)
            || t.Contains("terminal", StringComparison.Ordinal))
        {
            return "execute";
        }
        if (t.StartsWith("delete ", StringComparison.Ordinal) || t.StartsWith("remove ", StringComparison.Ordinal))
        {
            return "delete";
        }

        return string.IsNullOrEmpty(k) ? "write" : k;
    }

    private static string SelectAllowOption(IReadOnlyList<string>? available)
    {
        if (available is { Count: > 0 })
        {
            if (available.Contains("allow_once", StringComparer.OrdinalIgnoreCase)) return "allow_once";
            if (available.Contains("allow_always", StringComparer.OrdinalIgnoreCase)) return "allow_always";
        }
        return "allow_once";
    }

    private static string SelectRejectOption(IReadOnlyList<string>? available)
    {
        if (available is { Count: > 0 })
        {
            if (available.Contains("reject_once", StringComparer.OrdinalIgnoreCase)) return "reject_once";
            if (available.Contains("reject_always", StringComparer.OrdinalIgnoreCase)) return "reject_always";
        }
        return "reject_once";
    }
}
