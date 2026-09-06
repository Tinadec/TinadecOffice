using Microsoft.Extensions.Logging;

namespace TinadecCore.Abstractions;

/// <summary>
/// Logging is a side channel: durable state is authoritative. A failing log
/// provider (e.g. the Windows Event Log when the process lacks rights to
/// create an event source) must never change a run's outcome, so every
/// runtime-path log write goes through these swallow-failure wrappers.
/// </summary>
public static class SafeLogExtensions
{
    public static void TryLogWarning(this ILogger? logger, Exception? exception, string message, params object?[] args)
        => Write(logger, LogLevel.Warning, exception, message, args);

    public static void TryLogError(this ILogger? logger, Exception? exception, string message, params object?[] args)
        => Write(logger, LogLevel.Error, exception, message, args);

    public static void TryLogInformation(this ILogger? logger, Exception? exception, string message, params object?[] args)
        => Write(logger, LogLevel.Information, exception, message, args);

    public static void TryLogDebug(this ILogger? logger, Exception? exception, string message, params object?[] args)
        => Write(logger, LogLevel.Debug, exception, message, args);

    public static void TryLogWarning(this ILogger? logger, string message, params object?[] args)
        => Write(logger, LogLevel.Warning, null, message, args);

    public static void TryLogError(this ILogger? logger, string message, params object?[] args)
        => Write(logger, LogLevel.Error, null, message, args);

    public static void TryLogInformation(this ILogger? logger, string message, params object?[] args)
        => Write(logger, LogLevel.Information, null, message, args);

    public static void TryLogDebug(this ILogger? logger, string message, params object?[] args)
        => Write(logger, LogLevel.Debug, null, message, args);

    private static void Write(ILogger? logger, LogLevel level, Exception? exception, string message, object?[] args)
    {
        if (logger is null) return;
        try { logger.Log(level, exception, message, args); }
        catch { /* logging is advisory; durable state remains authoritative */ }
    }
}
