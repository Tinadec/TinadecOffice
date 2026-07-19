using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinadecTools.Runtime.Sandbox;

// ── domain models ─────────────────────────────────────────────────────────────

internal enum SandboxResetScope { Workspace, Machine }

internal sealed class SandboxPermissions
{
    public List<string> ReadPaths { get; set; } = new();
    public List<string> WritePaths { get; set; } = new();
    public List<string> EnvironmentVariableNames { get; set; } = new();
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
    [JsonPropertyName("working_directory")] public string WorkingDirectory { get; set; } = string.Empty;
    [JsonPropertyName("stdin")] public string? Stdin { get; set; }
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; set; } = 30_000;
    [JsonPropertyName("environment")] public Dictionary<string, string>? Environment { get; set; }
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
    Task ResetAsync(SandboxResetScope scope, CancellationToken ct);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SandboxPolicyFile))]
[JsonSerializable(typeof(SandboxRunnerRequest))]
[JsonSerializable(typeof(SandboxRunnerResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SandboxJsonContext : JsonSerializerContext { }
