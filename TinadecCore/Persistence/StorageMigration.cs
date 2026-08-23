using Microsoft.Extensions.Logging;

namespace TinadecCore.Persistence;

/// <summary>Implemented by business modules; Persistence coordinates but owns no schema.</summary>
public interface IStorageMigrationParticipant
{
    Task MigrateAsync(CancellationToken cancellationToken = default);
}

public interface IStorageMigrationRunner
{
    Task RunAsync(CancellationToken cancellationToken = default);
}

internal sealed class StorageMigrationRunner : IStorageMigrationRunner
{
    private readonly IEnumerable<IStorageMigrationParticipant> _participants;
    private readonly Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions> _options;
    private readonly ILogger<StorageMigrationRunner> _logger;

    public StorageMigrationRunner(
        IEnumerable<IStorageMigrationParticipant> participants,
        Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions> options,
        ILogger<StorageMigrationRunner> logger)
    {
        _participants = participants;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled || (options.Provider == DatabaseProvider.PostgreSql && !options.ApplyMigrationsOnStartup))
        {
            return;
        }

        var participants = _participants.ToList();
        foreach (var participant in participants)
        {
            await participant.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        _logger.LogInformation("Storage migrations ensured for {Count} context(s) ({Provider}).", participants.Count, options.Provider);
    }
}
