using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.TinaChat;

namespace TinadecCore.Runtime;

/// <summary>
/// Runs the two TinaChat follow-ups a host owes: the wake queue a committed message leaves behind,
/// and the outcome announcement an admitted handoff leaves behind. Both are durable rows, so this
/// loop is only a scheduler — a host that stops mid-turn resumes the work after restart, and a
/// disabled drain leaves the queues intact rather than dropping them.
/// </summary>
public sealed class TinaChatWakeService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TinaChatWakeOptions _options;
    private readonly ILogger<TinaChatWakeService> _logger;

    public TinaChatWakeService(
        IServiceProvider services,
        ILogger<TinaChatWakeService> logger,
        IOptions<TinaChatWakeOptions>? options = null)
    {
        _services = services;
        _logger = logger;
        _options = options?.Value ?? new TinaChatWakeOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WakeDrainEnabled)
        {
            _logger.TryLogInformation("TinaChat turn drain is disabled; owed turns and outcomes stay queued.");
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.WakeIntervalSeconds, 1, 3600));
        var batch = Math.Clamp(_options.WakeBatchSize, 1, 20);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunPassAsync(batch, stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Public for deterministic tests: one drain pass, both follow-ups, failures contained.</summary>
    public async Task<int> RunPassAsync(int batch, CancellationToken ct = default)
    {
        var handled = 0;
        try
        {
            if (_services.GetService<ITinaChatWakeProcessor>() is { } wakes)
                handled += await wakes.ProcessPendingWakesAsync(batch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.TryLogWarning(ex, "TinaChat wake drain pass failed.");
        }
        try
        {
            handled += await CollectExecutionOutcomesAsync(batch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.TryLogWarning(ex, "TinaChat execution outcome pass failed.");
        }
        return handled;
    }

    /// <summary>
    /// Posts an outcome only once its run is genuinely terminal. A run still waiting on a human
    /// decision is not late, it is parked, so it is left alone rather than announced.
    /// </summary>
    private async Task<int> CollectExecutionOutcomesAsync(int batch, CancellationToken ct)
    {
        if (_services.GetService<ITinaChatExecutionResults>() is not { } results) return 0;
        if (_services.GetService<ILifecycleManager>() is not { } lifecycle) return 0;
        var posted = 0;
        foreach (var execution in await results.ListOpenExecutionsAsync(batch, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await lifecycle.GetRunStateAsync(execution.RunId.ToString("N"), ct).ConfigureAwait(false);
                var terminal = state.Status switch
                {
                    RunStatus.Completed => "completed",
                    RunStatus.Failed => "failed",
                    RunStatus.Cancelled => "cancelled",
                    _ => null
                };
                if (terminal is null) continue;
                var outcome = new TinaChatRunOutcome(terminal, state.Summary, state.TerminalErrorCategory);
                if (await results.RecordResultAsync(execution.Id, outcome, ct).ConfigureAwait(false) == TinaChatExecutionResultOutcome.Posted) posted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.TryLogWarning(ex, "Could not report the outcome of TinaChat execution {ExecutionId}.", execution.Id);
            }
        }
        return posted;
    }
}
