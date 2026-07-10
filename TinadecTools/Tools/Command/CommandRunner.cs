using System.Text.Json.Serialization;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;
using TinadecTools.Runtime.Sandbox;

namespace TinadecTools.Tools.Command;

// ── 请求 / 响应 ────────────────────────────────────────────────────────────────

public sealed class CommandRunParams
{
    [JsonPropertyName("executable")]
    public string Executable { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    [JsonPropertyName("working_directory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("stdin")]
    public string? Stdin { get; set; }

    /// <summary>超时毫秒数，合法范围 1..1_800_000，默认 30 秒</summary>
    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; } = 30_000;

    [JsonPropertyName("additional_read_paths")]
    public List<string>? AdditionalReadPaths { get; set; }

    [JsonPropertyName("additional_write_paths")]
    public List<string>? AdditionalWritePaths { get; set; }

    [JsonPropertyName("environment_variable_names")]
    public List<string>? EnvironmentVariableNames { get; set; }

    [JsonPropertyName("persist_grants")]
    public bool PersistGrants { get; set; } = false;
}

public sealed class CommandRunResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }

    [JsonPropertyName("stdout")]
    public string Stdout { get; set; } = string.Empty;

    [JsonPropertyName("stderr")]
    public string Stderr { get; set; } = string.Empty;

    [JsonPropertyName("timed_out")]
    public bool TimedOut { get; set; }

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }

    [JsonPropertyName("sandbox_error")]
    public string? SandboxError { get; set; }

    [JsonPropertyName("stdout_truncated")]
    public bool StdoutTruncated { get; set; }

    [JsonPropertyName("stderr_truncated")]
    public bool StderrTruncated { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CommandRunParams))]
[JsonSerializable(typeof(CommandRunResponse))]
[JsonSerializable(typeof(List<string>))]
internal partial class CommandRunnerJsonContext : JsonSerializerContext;

// ── 工具入口 ──────────────────────────────────────────────────────────────────

public static class CommandRunner
{
    [ToolFunction("command_run", RequiresApproval = true)]
    public static async ValueTask<CommandRunResponse> HandleAsync(
        CommandRunParams args,
        CancellationToken cancellationToken)
    {
        var workingDir = args.WorkingDirectory
            ?? TinadecTools.Tools.FileRW.WorkspacePathResolver.WorkspaceRoot;

        var requestedPermissions = CommandSandboxRuntime.BuildPermissions(
            args.AdditionalReadPaths,
            args.AdditionalWritePaths,
            args.EnvironmentVariableNames);

        if (args.PersistGrants)
            SandboxPolicyStore.MergeAndPersist(requestedPermissions);

        var permissions = CommandSandboxRuntime.MergeWithPolicy(requestedPermissions);

        try
        {
            var result = await CommandSandboxRuntime.ExecuteSandboxedAsync(
                args.Executable,
                args.Arguments,
                workingDir,
                args.Stdin,
                args.TimeoutMs,
                permissions,
                persistGrants: false,
                cancellationToken).ConfigureAwait(false);

            return new CommandRunResponse
            {
                Success = result.Success,
                ExitCode = result.ExitCode,
                Stdout = result.Stdout,
                Stderr = result.Stderr,
                TimedOut = result.TimedOut,
                DurationMs = result.DurationMs,
                SandboxError = result.Error,
                StdoutTruncated = result.StdoutTruncated,
                StderrTruncated = result.StderrTruncated
            };
        }
        catch (Exception ex)
        {
            return new CommandRunResponse
            {
                Success = false,
                SandboxError = ex.Message
            };
        }
    }
}
