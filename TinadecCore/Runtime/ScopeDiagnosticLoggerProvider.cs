using System.Text;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Runtime;

/// <summary>Bounded diagnostic side channel. Run facts remain in data and are never rotated.</summary>
public sealed class ScopeDiagnosticLoggerProvider : ILoggerProvider
{
    private readonly IScopeStorageLocations _scope;
    private readonly string _path;
    private readonly object _gate = new();
    private long _rotation = 10L * 1024 * 1024, _capacity = 200L * 1024 * 1024;
    private FileStream? _stream;
    private long _currentBytes;
    private long _knownTotalBytes;
    private bool _budgetInitialized;
    private bool _disposed;

    public ScopeDiagnosticLoggerProvider(IScopeStorageLocations scope)
    {
        _scope = scope;
        _path = Path.Combine(scope.Logs, "core.log");
        var config = Path.Combine(scope.Config, "logging.toml");
        ScopeConfigurationInitialization.Ensure(scope, "logging", () => "version = 1\n[logging]\nrotation_bytes = 10485760\ntotal_bytes = 209715200\n");
        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(config))!;
        if (StorageScopeInitializer.Value(model, "logging") is TomlTable logging)
        {
            _rotation = Convert.ToInt64(StorageScopeInitializer.Value(logging, "rotation_bytes") ?? _rotation);
            _capacity = Convert.ToInt64(StorageScopeInitializer.Value(logging, "total_bytes") ?? _capacity);
            if (_rotation < 4096 || _capacity < _rotation) throw new InvalidDataException("logging.toml requires total_bytes >= rotation_bytes >= 4096.");
        }
    }
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }
    private void Write(string category, LogLevel level, string text)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var line = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} {level} {category}: {text}\n");
                if (line.Length > _rotation / 2) line = line.AsSpan(0, (int)Math.Min(_rotation / 2, int.MaxValue)).ToArray();

                var rotated = false;
                if (_stream is null)
                {
                    StorageScopePaths.RejectLinks(_scope.Root, _path);
                    Directory.CreateDirectory(_scope.Logs);
                    if (File.Exists(_path))
                    {
                        _currentBytes = new FileInfo(_path).Length;
                        if (_currentBytes + line.Length > _rotation)
                        {
                            File.Move(_path, _path + "." + DateTimeOffset.UtcNow.UtcTicks + "-" + Guid.NewGuid().ToString("N"));
                            _currentBytes = 0;
                            rotated = true;
                        }
                    }
                    else
                    {
                        _currentBytes = 0;
                    }
                    _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                }
                else if (_currentBytes + line.Length > _rotation)
                {
                    _stream.Dispose();
                    _stream = null;
                    StorageScopePaths.RejectLinks(_scope.Root, _path);
                    if (File.Exists(_path))
                    {
                        File.Move(_path, _path + "." + DateTimeOffset.UtcNow.UtcTicks + "-" + Guid.NewGuid().ToString("N"));
                    }
                    _currentBytes = 0;
                    _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    rotated = true;
                }

                _stream.Write(line);
                _stream.Flush();
                _currentBytes += line.Length;
                _knownTotalBytes += line.Length;

                // Walk the logs directory on first write, rotation, or whenever known size exceeds budget.
                if (!_budgetInitialized || rotated || _knownTotalBytes > _capacity)
                {
                    _budgetInitialized = true;
                    var files = StorageMaintenanceService.OwnedFiles(_scope.Root, _scope.Logs, CancellationToken.None);
                    var total = files.Sum(x => x.Length);
                    foreach (var file in files.Where(x => IsRotatedFile(x.Name)).OrderBy(x => x.LastWriteTimeUtc))
                    {
                        if (total <= _capacity) break;
                        var size = file.Length; File.Delete(file.FullName); total -= size;
                    }
                    _knownTotalBytes = total <= _capacity ? total : _capacity;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _stream?.Dispose();
                _stream = null;
                /* logging cannot fail an operation */
            }
        }
    }
    internal static bool IsRotatedFile(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        @"(?:\.log\.\d+(?:-[a-fA-F0-9]{32})?|\.\d+-\d+\.log)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private sealed class Logger(ScopeDiagnosticLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) owner.Write(category, logLevel, formatter(state, exception) + (exception is null ? "" : "\n" + exception)); }
    }
}
