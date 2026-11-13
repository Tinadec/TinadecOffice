using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Persistence;

/// <summary>
/// Creates a context without attaching the configuration projection.
///
/// Coordinated provider migrations must be able to create the schema even when a
/// configuration document is invalid: attaching a projection reads and validates every
/// document in the scope, so one broken <c>agents.toml</c> would otherwise abort startup
/// before any table exists. Migration participants prefer this factory when it exists and
/// fall back to <see cref="IDbContextFactory{TContext}"/> for contexts that have no
/// configuration projection.
/// </summary>
public interface IRawDbContextFactory<TContext> where TContext : DbContext
{
    Task<TContext> CreateRawDbContextAsync(CancellationToken cancellationToken = default);
}
