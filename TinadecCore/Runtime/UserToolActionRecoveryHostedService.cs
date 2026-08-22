using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Runtime;

/// <summary>Runs the idempotent durable user-action recovery pass once per host start.</summary>
internal sealed class UserToolActionRecoveryHostedService : BackgroundService
{
    private readonly IUserToolActionRecovery _recovery;
    private readonly ILogger<UserToolActionRecoveryHostedService> _logger;

    public UserToolActionRecoveryHostedService(
        IUserToolActionRecovery recovery,
        ILogger<UserToolActionRecoveryHostedService> logger)
    {
        _recovery = recovery;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _recovery.RecoverAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "User tool action recovery scan failed.");
        }
    }
}
