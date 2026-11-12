using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Persistence;

/// <summary>
/// Configuration problems observed while the host was starting.
///
/// Startup records them instead of aborting the process: the API still has to answer
/// <c>/api/v1/health</c>, <c>/api/v1/readiness</c> and the per-scope availability rows, because those
/// are the only places a client can learn which document is broken and which file to repair. A
/// single malformed <c>agents.toml</c> used to abort Core before it ever bound a port.
/// </summary>
public sealed class StartupConfigurationState
{
    private readonly object _gate = new();
    private IReadOnlyList<ConfigurationDiagnostic> _diagnostics = [];

    /// <summary>Recorded diagnostics; empty when every document validated.</summary>
    public IReadOnlyList<ConfigurationDiagnostic> Diagnostics
    {
        get { lock (_gate) return _diagnostics; }
    }

    public bool IsBroken => Diagnostics.Count > 0;

    /// <summary>
    /// Startup failures that must not abort the process. Everything else (a missing database, a
    /// disk error) still fails fast exactly as before.
    /// </summary>
    public static bool IsRecoverable(Exception error) => error is ConfigurationDocumentException;

    public void Record(string stage, Exception error)
        => Record(stage,
            error is ConfigurationDocumentException document ? document.Code : "startup_failure",
            error.Message,
            (error as ConfigurationDocumentException)?.Diagnostics);

    public void Record(string stage, string code, string message, IEnumerable<ConfigurationDiagnostic>? diagnostics = null)
    {
        lock (_gate)
        {
            var combined = new List<ConfigurationDiagnostic>(_diagnostics)
            {
                new(code, "[" + stage + "] " + message, "error")
            };
            if (diagnostics is not null) combined.AddRange(diagnostics);
            _diagnostics = combined;
        }
    }
}
