using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Logging is a side channel: a throwing logger provider (e.g. the Windows
/// Event Log without source-creation rights) must never propagate out of a
/// TryLog* call or change durable state.
/// </summary>
public sealed class SafeLoggingTests
{
    private sealed class ThrowingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

        public void Dispose() { }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("The event source could not be created.");
    }

    [Fact]
    public void TryLog_Extensions_SwallowProviderFailures()
    {
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new ThrowingProvider()));
        var logger = factory.CreateLogger("TinadecCore.DmaEA.FullDuplexRunEngine");
        var exception = new InvalidOperationException("boom");

        logger.TryLogWarning(exception, "warning {Id}", 1);
        logger.TryLogError(exception, "error {Id}", 1);
        logger.TryLogInformation(exception, "info {Id}", 1);
        logger.TryLogDebug(exception, "debug {Id}", 1);
        logger.TryLogWarning("plain warning {Id}", 1);
        logger.TryLogError("plain error {Id}", 1);
        logger.TryLogInformation("plain info {Id}", 1);
        logger.TryLogDebug("plain debug {Id}", 1);
        logger.TryLogWarning(null, "null exception warning");
    }

    [Fact]
    public void TryLog_Extensions_TolerateNullLogger()
    {
        ILogger? logger = null;

        logger.TryLogWarning(new InvalidOperationException("boom"), "no receiver");
        logger.TryLogError("no receiver");
    }
}
