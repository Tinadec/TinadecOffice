using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Lifecycle;

/// <summary>
/// Startup recovery for orphaned runs. After a host restart no in-memory lease or feed
/// exists, so a non-terminal run can never finish on its own. Recovery fails those runs,
/// completes their open turns, and emits a replayable <c>run.recovered</c> audit event.
/// This is state-consistency recovery only; execution is not resumed.
/// </summary>
public sealed class RunRecoveryHostedService : BackgroundService
{
    private readonly ILifecycleManager _lifecycle;
    private readonly IServiceProvider _services;
    private readonly ILogger<RunRecoveryHostedService> _logger;

    public RunRecoveryHostedService(ILifecycleManager lifecycle, IServiceProvider services, ILogger<RunRecoveryHostedService> logger)
    {
        _lifecycle = lifecycle;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var orphans = await _lifecycle.ListNonTerminalRunsAsync(stoppingToken).ConfigureAwait(false);
            foreach (var run in orphans)
            {
                try
                {
                    if (!Guid.TryParse(run.RunId, out var runId)) continue;
                    if (run.Status is RunStatus.AwaitingApproval or RunStatus.AwaitingDelegate or RunStatus.AwaitingUser)
                    {
                        // Waiting runs are durable user/delegate decision points,
                        // not orphaned work. Their checkpoint is resumed by the
                        // decision endpoint after an explicit authorization fact.
                        continue;
                    }
                    await _lifecycle.SetRunStatusAsync(run.RunId, "failed", "Recovered after host restart.", stoppingToken).ConfigureAwait(false);
                    await _lifecycle.AppendEventAsync(runId, "run.recovered",
                        new { run_id = run.RunId, reason = "host_restart" },
                        "Run recovered as failed after a host restart.", "warning").ConfigureAwait(false);
                    if (run.TurnId is { } turnIdText && Guid.TryParse(turnIdText, out var turnId)
                        && Guid.TryParse(run.SessionId, out var sessionId)
                        && _services.GetService<IConversationStore>() is { } conversations)
                    {
                        var revision = await conversations.GetContextRevisionAsync(sessionId, stoppingToken).ConfigureAwait(false);
                        await conversations.CompleteTurnAsync(turnId, runId, null, revision, "failed", stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not recover orphaned run {RunId}", run.RunId);
                }
            }
            if (orphans.Count != 0)
            {
                _logger.LogInformation("Recovered {Count} orphaned run(s) after restart.", orphans.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown before the scan finished.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run recovery scan failed.");
        }
    }
}
