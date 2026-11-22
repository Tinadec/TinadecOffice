using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Companion;

/// <summary>
/// Drives persistent Bot due-schedules and bounded autonomy ticks on a quiet background cadence.
/// </summary>
public sealed class CompanionAutonomyWorker(
    CompanionAutonomyService autonomy,
    CompanionPlanningService planning,
    TimeProvider timeProvider,
    ILogger<CompanionAutonomyWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Companion autonomy and schedule worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PassInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await planning.RunDueSchedulesAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Companion schedule evaluation pass failed.");
            }

            IReadOnlyList<Guid> due;
            try
            {
                due = await autonomy.DueCompanionIdsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Companion autonomy pass could not read due companions.");
                continue;
            }

            if (due.Count == 0)
            {
                logger.LogDebug("Companion autonomy pass found no due companion.");
                continue;
            }

            foreach (var companionId in due)
            {
                try
                {
                    await autonomy.TickAsync(companionId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Companion autonomy tick for {CompanionId} failed.", companionId);
                }
            }
        }
    }
}
