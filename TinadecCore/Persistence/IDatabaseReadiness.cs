namespace TinadecCore.Persistence;

/// <summary>
/// Non-throwing database readiness probe for Core readiness receipts.
/// </summary>
public interface IDatabaseReadiness
{
    Task<DatabaseReadinessResult> ProbeAsync(CancellationToken cancellationToken = default);
}
