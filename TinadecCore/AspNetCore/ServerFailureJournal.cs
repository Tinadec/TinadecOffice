using System.Collections.Concurrent;

namespace TinadecCore.AspNetCore;

/// <summary>
/// One 5xx the exception handler turned into a RFC 9457 body. The response is deliberately not the
/// only copy: a 500 tells the caller "an unexpected error occurred", which is the correct amount to
/// say to a client and the wrong amount to say to whoever has to fix it.
/// </summary>
public sealed record ServerFailure(
    string Method,
    string Path,
    int Status,
    string Code,
    string ExceptionType,
    string ExceptionMessage,
    string? RootType,
    string? RootMessage,
    string TraceId,
    DateTimeOffset Timestamp);

/// <summary>
/// In-process ring of recent 5xx failures, readable through DI and never through HTTP.
/// <para>
/// The wire stays opaque on purpose: exception type names and driver messages describe internals that
/// a caller has no need to select on, and `docs/security.md` keeps that class of detail off external
/// responses. What a test host needs is the same fact through a different door, so a
/// <c>WebApplicationFactory</c> can resolve this and name the cause in its own failure message.
/// </para>
/// <para>
/// Only 5xx is recorded. A 4xx already carries its reason in the body — storing it here would make
/// the ring a record of ordinary rejections and push the interesting entries out of view.
/// </para>
/// </summary>
public sealed class ServerFailureJournal
{
    /// <summary>Enough history to survive a parallel test collection, small enough to never matter.</summary>
    public const int MaxEntries = 64;

    private readonly ConcurrentQueue<ServerFailure> _entries = [];

    public void Record(HttpContext context, Exception? exception, int status, string code)
    {
        if (exception is null)
            return;

        // The outermost type is usually the framework's wrapper; the cause is the one with a name
        // worth reading. Both are kept because "SqliteException inside IOException" is itself a fact.
        var root = exception;
        while (root.InnerException is { } inner)
            root = inner;

        Add(new ServerFailure(
            context.Request.Method,
            context.Request.Path.ToString(),
            status,
            code,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            root.GetType().FullName ?? root.GetType().Name,
            root.Message,
            context.TraceIdentifier,
            DateTimeOffset.UtcNow));
    }

    public void Add(ServerFailure failure)
    {
        _entries.Enqueue(failure);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    /// <summary>Oldest first, so the last entry is the one that just happened.</summary>
    public IReadOnlyList<ServerFailure> Recent() => [.. _entries];
}
