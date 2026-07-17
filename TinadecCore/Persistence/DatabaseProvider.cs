namespace TinadecCore.Persistence;

/// <summary>
/// Supported persistence backends. Default for local desktop is <see cref="Sqlite"/>.
/// </summary>
public enum DatabaseProvider
{
    Sqlite = 0,
    PostgreSql = 1
}
