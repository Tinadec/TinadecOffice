using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

public sealed class ScopeLoggingTests
{
    [Fact]
    public void CombinedBudgetRotatesAndRemovesOnlyRecognizedClosedLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinadec-log-budget", Guid.NewGuid().ToString("N"));
        var scope = new StorageScopeDescriptor("test", "project", root);
        Directory.CreateDirectory(scope.Config); Directory.CreateDirectory(scope.Logs);
        try
        {
            File.WriteAllText(Path.Combine(scope.Config, "logging.toml"), "version = 1\n[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");
            var rotatedHost = Path.Combine(scope.Logs, "electron.1723456789012-1.log");
            File.WriteAllText(rotatedHost, new string('h', 5000)); File.SetLastWriteTimeUtc(rotatedHost, DateTime.UtcNow.AddHours(-1));
            var diagnostic = Path.Combine(scope.Logs, "user-diagnostic.txt"); File.WriteAllText(diagnostic, "keep");
            using var provider = new ScopeDiagnosticLoggerProvider(scope);
            var logger = provider.CreateLogger("StorageTest");
            for (var i = 0; i < 12; i++) logger.LogInformation("{Message}", new string('a', 1000));
            Assert.False(File.Exists(rotatedHost)); Assert.Equal("keep", File.ReadAllText(diagnostic));
            Assert.All(Directory.GetFiles(scope.Logs).Where(file => Path.GetFileName(file).StartsWith("core.log")),
                file => Assert.InRange(new FileInfo(file).Length, 1, 4096));
            Assert.InRange(Directory.GetFiles(scope.Logs).Sum(file => new FileInfo(file).Length), 1, 8192);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DisposedProviderDoesNotReopenLogFileOnLateWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinadec-log-dispose", Guid.NewGuid().ToString("N"));
        var scope = new StorageScopeDescriptor("test", "project", root);
        Directory.CreateDirectory(scope.Config);
        Directory.CreateDirectory(scope.Logs);
        try
        {
            File.WriteAllText(Path.Combine(scope.Config, "logging.toml"), "version = 1\n[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n");
            var provider = new ScopeDiagnosticLoggerProvider(scope);
            var logger = provider.CreateLogger("DisposeTest");
            logger.LogInformation("before dispose");
            provider.Dispose();
            logger.LogInformation("after dispose should be ignored");

            var logPath = Path.Combine(scope.Logs, "core.log");
            var contents = File.ReadAllText(logPath);
            Assert.Contains("before dispose", contents);
            Assert.DoesNotContain("after dispose should be ignored", contents);

            // File handle must remain completely released on Windows after Dispose().
            using var exclusive = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.CanRead);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
