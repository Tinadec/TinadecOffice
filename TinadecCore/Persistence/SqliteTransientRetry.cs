using Microsoft.Data.Sqlite;

namespace TinadecCore.Persistence;

/// <summary>
/// Bounded retry for pure read projections on SQLite.
///
/// Microsoft.Data.Sqlite does not set busy_timeout, so a writer holding the lock makes a reader
/// fail immediately rather than wait. A database that has never completed a write transaction is
/// still in DELETE journal mode, where a writer blocks readers too; once EF's first write
/// transaction flips it to WAL, readers stop blocking on writers and the remaining contention is
/// WAL recovery and write-write. Either way the failure is transient and the read is worth
/// repeating.
///
/// Deliberately not an EF ExecutionStrategy: EF's OnFirstExecution guard throws for any user
/// transaction once MaxRetryCount is above zero, which would break every existing transaction
/// site regardless of whether a lock conflict actually happened.
///
/// The delegate must only read. It must create its own DbContext on every attempt so a failure
/// during connection initialization is covered as well, and it must not write, touch files or
/// call anything outside the process. There is no void overload, so a write path cannot use this
/// by accident.
/// </summary>
public static class SqliteTransientRetry
{
    public const int MaxAttempts = 3;

    // Masked to the primary code: 261 (BUSY_RECOVERY) and 517 (BUSY_SNAPSHOT) are the same
    // contention as 5 and 6, and CI has actually hit 261. A bare "is 5 or 6" misses them.
    internal static bool IsTransient(SqliteException exception) => (exception.SqliteErrorCode & 0xFF) is 5 or 6;

    public static Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        RetryAsync(read, cancellationToken);

    /// <summary>
    /// Bounded retry for a delegate that owns one database transaction end to end: it creates its
    /// own context, opens the transaction, writes, saves and commits. Retrying re-runs the whole
    /// delegate, so it is only safe when a rolled-back attempt leaves nothing behind — no file
    /// writes, no external calls, and no random identifier that feeds a side effect outside the
    /// transaction. A Guid used solely for a row created inside the transaction is fine: the
    /// rollback discards the row and the next attempt's Guid leaves no orphan.
    ///
    /// Sites that already have their own retry loop must be converted to this helper, not wrapped
    /// in it, or the attempts multiply.
    /// </summary>
    public static Task<T> WriteInTransactionAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken) =>
        RetryAsync(write, cancellationToken, IsTransientWrite);

    public static async Task WriteInTransactionAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken) =>
        await WriteInTransactionAsync(async ct => { await write(ct).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);

    private static Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RetryAsync(operation, cancellationToken, static exception => exception is SqliteException sqlite && IsTransient(sqlite));

    private static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, Func<Exception, bool> transient)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await operation(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (transient(ex) && attempt < MaxAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // SaveChanges wraps a provider failure in DbUpdateException, so on a write the transient code
    // sits on an inner exception. A unique-violation (19) is not transient and must not be retried.
    // Internal because a caller with its own duplicate-key compare-and-set must ask the same
    // question: catching every DbUpdateException as "someone else won" mislabels a lock conflict
    // as a duplicate, and returns a receipt that a rolled-back attempt never wrote.
    internal static bool IsTransientWrite(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SqliteException sqlite && IsTransient(sqlite)) return true;
        return false;
    }
}