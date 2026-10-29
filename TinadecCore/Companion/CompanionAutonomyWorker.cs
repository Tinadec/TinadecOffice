using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TinadecCore.Companion;

/// <summary>
/// Drives autonomy on a quiet schedule. Every pass asks the service which companions are due
/// (enabled, not paused, interval elapsed, budget left) and ticks them one by one; one failed
/// companion never stops the others. Nothing is logged above Debug for an idle pass, and shutdown
/// cancels the wait and the tick, so a closed switch stays closed and a closed host writes nothing.
///
/// A scope instance of this module runs in the user scope and in each project scope. Companions
/// only live in the user scope (see the storage-scope middleware), so a project pass finds an
/// empty table and is a no-op rather than a second ledger.
/// </summary>
public sealed class CompanionAutonomyWorker(
    CompanionAutonomyService autonomy,
    TimeProvider timeProvider,
    ILogger<CompanionAutonomyWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Companion autonomy worker started.");
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
