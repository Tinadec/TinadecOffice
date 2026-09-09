using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;

namespace TinadecCore.Runtime;

/// <summary>
/// Single orchestration point for durable recovery (plan §4.3 item 5). One hosted
/// service, two ordered startup passes, one audit and log surface:
///
///   Pass 1 — orphan run scan: non-terminal runs that cannot finish on their own
///            after a host restart are failed with a replayable `run.recovered`
///            audit event and their open turns are closed. Awaiting decision
///            states (awaiting_approval/awaiting_delegate/awaiting_user) are
///            protected (RecoveryPolicy) and skipped.
///   Pass 2 — user tool action recovery: claimed-but-unfinished user actions are
///            parked for an explicit recovery decision (never auto-replayed).
///
/// Steady-state ownership stays where execution lives: the engine lease scan
/// picks up lease-expired runs every scan interval under the same
/// <see cref="RecoveryPolicy"/> (awaiting protection + admission grace), and
/// approval park expiry is a decision-time rule inside the approval coordinator —
/// both consume the shared policy instead of local copies.
/// </summary>
public sealed class RecoveryCoordinator : BackgroundService
{
    private readonly ILifecycleManager _lifecycle;
    private readonly StorageLifecycleService _storage;
    private readonly IServiceProvider _services;
    private readonly IUserToolActionRecovery _userToolActionRecovery;
    private readonly ToolApprovalCoordinator _approvals;
    private readonly TinadecApprovalOptions _approvalOptions;
    private readonly ILogger<RecoveryCoordinator> _logger;

    public RecoveryCoordinator(
        ILifecycleManager lifecycle,
        StorageLifecycleService storage,
        IServiceProvider services,
        IUserToolActionRecovery userToolActionRecovery,
        ToolApprovalCoordinator approvals,
        ILogger<RecoveryCoordinator> logger,
        IOptions<TinadecApprovalOptions>? approvalOptions = null)
    {
        _lifecycle = lifecycle;
        _storage = storage;
        _services = services;
        _userToolActionRecovery = userToolActionRecovery;
        _approvals = approvals;
        _approvalOptions = approvalOptions?.Value ?? new TinadecApprovalOptions();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunStartupPassesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown before the passes finished.
        }
        catch (Exception ex)
        {
            _logger.TryLogError(ex, "Recovery coordinator startup passes failed.");
        }

        // Approval expiry has no other observer: TryStartAsync only evaluates the
        // decision window when a run happens to resume, and the recovery scans
        // skip awaiting_* runs by design. Sweep at startup and then periodically
        // so a parked run escalates to awaiting_user instead of waiting forever.
        if (!_approvalOptions.ExpirySweepEnabled) return;
        await SweepApprovalExpiryAsync(stoppingToken).ConfigureAwait(false);
        var interval = TimeSpan.FromSeconds(Math.Clamp(_approvalOptions.ExpirySweepIntervalSeconds, 5, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            await SweepApprovalExpiryAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One expiry sweep pass. Public for deterministic test invocation, mirroring
    /// <see cref="RunStartupPassesAsync"/>. Live runs are only enqueued — the
    /// engine's resume re-enters TryStartAsync, which owns the park transition —
    /// while undrivable approvals were already expired by the coordinator.
    /// </summary>
    public async Task<ApprovalExpirySweepResult> SweepApprovalExpiryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var sweep = await _approvals.SweepExpiredPendingApprovalsAsync(cancellationToken).ConfigureAwait(false);
            if (_services.GetService<IFullDuplexRunEngine>() is { } engine)
            {
                foreach (var runId in sweep.RunIdsToWake)
                {
                    await engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
                }
            }
            if (sweep.RunIdsToWake.Count > 0 || sweep.OrphanedExpiryCount > 0)
            {
                _logger.TryLogInformation("Approval expiry sweep: {WakeCount} run(s) woken, {OrphanCount} undrivable approval(s) expired.",
                    sweep.RunIdsToWake.Count, sweep.OrphanedExpiryCount);
            }
            return sweep;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ApprovalExpirySweepResult([], 0);
        }
        catch (Exception ex)
        {
            _logger.TryLogWarning(ex, "Approval expiry sweep failed.");
            return new ApprovalExpirySweepResult([], 0);
        }
    }

    /// <summary>Public for deterministic test and tooling invocation.</summary>
    public async Task RunStartupPassesAsync(CancellationToken cancellationToken = default)
    {
        await RecoverOrphanRunsAsync(cancellationToken).ConfigureAwait(false);
        await RecoverUserToolActionsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverOrphanRunsAsync(CancellationToken cancellationToken)
    {
        var recovered = 0;
        try
        {
            var orphans = await _lifecycle.ListNonTerminalRunsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var run in orphans)
            {
                try
                {
                    if (!Guid.TryParse(run.RunId, out var runId)) continue;
                    if (RecoveryPolicy.IsProtected(run.Status)) continue;
                    // A run with a checkpoint can be resumed by the engine lease
                    // scan once its lease lapses — failing it here would race the
                    // resume (plan §1.2: the two rule sets used to disagree). Only
                    // checkpoint-less runs are converged to a failed terminal.
                    var checkpoint = await _storage.GetCurrentRunCheckpointAsync(runId, cancellationToken).ConfigureAwait(false);
                    if (checkpoint is not null) continue;                    await _lifecycle.SetRunStatusAsync(run.RunId, "failed", "Recovered after host restart.", cancellationToken).ConfigureAwait(false);
                    await _lifecycle.AppendEventAsync(runId, "run.recovered",
                        new { run_id = run.RunId, reason = "host_restart" },
                        "Run recovered as failed after a host restart.", "warning",
                        idempotencyKey: $"run:{runId}:event:run.recovered").ConfigureAwait(false);
                    if (run.TurnId is { } turnIdText && Guid.TryParse(turnIdText, out var turnId)
                        && Guid.TryParse(run.SessionId, out var sessionId)
                        && _services.GetService<IConversationStore>() is { } conversations)
                    {
                        var revision = await conversations.GetContextRevisionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                        await conversations.CompleteTurnAsync(turnId, runId, null, revision, "failed", cancellationToken).ConfigureAwait(false);
                    }
                    recovered++;
                }
                catch (Exception ex)
                {
                    _logger.TryLogWarning(ex, "Could not recover orphaned run {RunId}", run.RunId);
                }
            }
            if (recovered > 0)
            {
                _logger.TryLogInformation("Recovered {Count} orphaned run(s) after restart.", recovered);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown before the scan finished.
        }
        catch (Exception ex)
        {
            _logger.TryLogWarning(ex, "Run recovery scan failed.");
        }
    }

    private async Task RecoverUserToolActionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _userToolActionRecovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.TryLogWarning(ex, "User tool action recovery scan failed.");
        }
    }
}
