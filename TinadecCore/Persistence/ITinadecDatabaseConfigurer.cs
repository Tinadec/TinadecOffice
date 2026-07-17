using Microsoft.EntityFrameworkCore;

namespace TinadecCore.Persistence;

/// <summary>
/// Applies the active Tinadec database provider to an EF Core <see cref="DbContextOptionsBuilder"/>.
/// Business modules call this when registering their own DbContext types.
/// </summary>
public interface ITinadecDatabaseConfigurer
{
    /// <summary>
    /// Configures EF Core to use SQLite or PostgreSQL based on <see cref="TinadecPersistenceOptions"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Persistence is disabled or connection is not configured.</exception>
    void Configure(DbContextOptionsBuilder options);
}
