using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace TinadecTools.Runtime.Sandbox;

// ── domain models ─────────────────────────────────────────────────────────────

internal enum SandboxResetScope { Workspace, Machine }

internal sealed class SandboxPermissions
{
    public List<string> ReadPaths { get; set; } = new();
    public List<string> WritePaths { get; set; } = new();
    public List<string> EnvironmentVariableNames { get; set; } = new();
    public List<string> ProtectedPaths { get; set; } = new();
    public List<string> ReadExceptions { get; set; } = new();
    /// <summary>
    /// Paths inside a granted write root that stay read-only. Derived from the workspace on
    /// every launch, never persisted: the policy file's schema is fail-closed on unknown fields.
    /// </summary>
    public List<string> WriteDeniedPaths { get; set; } = new();
    public string? StorageId { get; set; }
    public string? StorageRoot { get; set; }
    public bool StorageWrite { get; set; }
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
}

internal sealed class SandboxPolicyFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("read_paths")] public List<string> ReadPaths { get; set; } = new();
    [JsonPropertyName("write_paths")] public List<string> WritePaths { get; set; } = new();
    [JsonPropertyName("environment_variables")] public List<string> EnvironmentVariables { get; set; } = new();
}

// ── embedded protocol (host <-> sandbox-runner) ──────────────────────────────

internal sealed class SandboxRunnerRequest
{
    [JsonPropertyName("executable")] public string Executable { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public List<string> Arguments { get; set; } = new();
    /// <summary>
    /// The whole command line the child parses itself, as one raw tail assigned to
    /// <see cref="ProcessStartInfo.Arguments"/>. Only cmd.exe needs it: .NET escapes
    /// <see cref="Arguments"/> with MSVCRT rules (<c>"</c> to <c>\"</c>) and cmd has no
    /// backslash escaping, so every quoted command is corrupted on the argv path.
    /// Mutually exclusive with <see cref="Arguments"/>; a POSIX command always uses argv.
    /// </summary>
    [JsonPropertyName("argument_string")] public string? ArgumentString { get; set; }
    [JsonPropertyName("working_directory")] public string WorkingDirectory { get; set; } = string.Empty;
    [JsonPropertyName("stdin")] public string? Stdin { get; set; }
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; set; } = 30_000;
    [JsonPropertyName("environment")] public Dictionary<string, string>? Environment { get; set; }

    /// <summary>
    /// The single place that decides argv versus a raw command-line tail.
    /// <see cref="ProcessStartInfo"/> rejects both being set, so callers must not branch on it themselves.
    /// </summary>
    public void ApplyCommandLine(ProcessStartInfo psi)
    {
        if (ArgumentString is not null)
        {
            psi.Arguments = ArgumentString;
            return;
        }
        foreach (var argument in Arguments)
            psi.ArgumentList.Add(argument);
    }
}

internal sealed class SandboxRunnerResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("exit_code")] public int ExitCode { get; set; }
    [JsonPropertyName("stdout")] public string Stdout { get; set; } = string.Empty;
    [JsonPropertyName("stderr")] public string Stderr { get; set; } = string.Empty;
    [JsonPropertyName("timed_out")] public bool TimedOut { get; set; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("stdout_truncated")] public bool StdoutTruncated { get; set; }
    [JsonPropertyName("stderr_truncated")] public bool StderrTruncated { get; set; }
}

/// <summary>A provider-owned process whose lifetime remains under sandbox cleanup.</summary>
internal sealed class SandboxStreamingProcess(Process process, IDisposable cleanup) : IDisposable
{
    public Process Process { get; } = process;
    public IDisposable Cleanup { get; } = cleanup;
    public void Dispose() => Cleanup.Dispose();
}

// ── backend interface ────────────────────────────────────────────────────────

internal interface ISandboxBackend
{
    bool IsSupported { get; }
    bool IsInitialized { get; }
    Task EnsureSetupAsync(CancellationToken ct);
    Task<SandboxRunnerResponse> ExecuteAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        bool persistGrants,
        CancellationToken ct);

    /// <summary>
    /// Starts a process without waiting for exit. Backends that cannot keep ACLs,
    /// process groups and credentials alive for the session fail closed through
    /// this default implementation.
    /// </summary>
    Task<SandboxStreamingProcess> StartStreamingAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        CancellationToken ct) =>
        throw new PlatformNotSupportedException("A streaming sandbox backend is not configured on this platform.");

    Task ResetAsync(SandboxResetScope scope, CancellationToken ct);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SandboxPolicyFile))]
[JsonSerializable(typeof(SandboxRunnerRequest))]
[JsonSerializable(typeof(SandboxRunnerResponse))]
[JsonSerializable(typeof(Posix.LinuxSandboxPayload))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SandboxJsonContext : JsonSerializerContext { }
