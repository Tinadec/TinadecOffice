using System.Text.Json.Serialization;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;

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

    /// <summary>超时毫秒数，&lt;= 0 表示不设超时</summary>
    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; } = 30_000;
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
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CommandRunParams))]
[JsonSerializable(typeof(CommandRunResponse))]
[JsonSerializable(typeof(List<string>))]
internal partial class CommandRunnerJsonContext : JsonSerializerContext;

// ── 工具入口 ──────────────────────────────────────────────────────────────────

// ponytail: 单个进程直调，无进程池/队列。stdin 循环本身已并发 dispatch 多个调用，
// 需要长驻/流式命令时再加专门的 job 抽象。
public static class CommandRunner
{
    [ToolFunction("command_run", RequiresApproval = true)]
    public static async ValueTask<CommandRunResponse> HandleAsync(
        CommandRunParams args,
        CancellationToken cancellationToken)
    {
        var result = await TerminalRunner.RunAsync(
            args.Executable,
            args.Arguments,
            args.WorkingDirectory,
            args.Stdin,
            args.TimeoutMs,
            cancellationToken).ConfigureAwait(false);

        return new CommandRunResponse
        {
            Success = result.Success,
            ExitCode = result.ExitCode,
            Stdout = result.Stdout,
            Stderr = result.Stderr,
            TimedOut = result.TimedOut,
            DurationMs = result.DurationMs
        };
    }
}
