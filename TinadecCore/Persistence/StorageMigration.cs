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

    public StorageMigrationRunner(
        IEnumerable<IStorageMigrationParticipant> participants,
        Microsoft.Extensions.Options.IOptions<TinadecPersistenceOptions> options)
    {
        _participants = participants;
        _options = options;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled || (options.Provider == DatabaseProvider.PostgreSql && !options.ApplyMigrationsOnStartup))
        {
            return;
        }

        foreach (var participant in _participants)
        {
            await participant.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
