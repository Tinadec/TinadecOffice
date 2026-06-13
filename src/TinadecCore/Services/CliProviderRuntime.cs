using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed partial class CliProviderRuntime : IModelProviderRuntime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);
    private readonly TimeSpan _timeout;
    private readonly CoreStore? _store;

    public CliProviderRuntime()
        : this(DefaultTimeout, null)
    {
    }

    public CliProviderRuntime(TimeSpan timeout, CoreStore? store = null)
    {
        _timeout = timeout <= TimeSpan.Zero ? DefaultTimeout : timeout;
        _store = store;
    }

    public string Id => "cli-provider";

    public bool CanHandle(ResolvedModelInvocationContextDto context)
    {
        return string.Equals(context.ConnectionKind, "cli", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ModelInvocationResultDto> GenerateAsync(
        ResolvedModelInvocationContextDto context,
        string? apiKey,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ModelToolSpecDto>? tools = null)
    {
        var validationFailure = ValidateConfiguration(context);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var provider = context.Provider!;
        var sessionId = messages.FirstOrDefault()?.SessionId ?? "sessionless";
        var runId = $"run_{Guid.NewGuid():N}";
        var arguments = BuildArguments(provider, context, sessionId, runId);
        var stdin = BuildStdinPayload(context, messages, sessionId, runId);

        var execution = await ProviderPolicyHelpers.ExecuteAsync(
            context.ProviderInstanceId,
            executionToken => RunProcessAsync(
                provider.BinaryPath!,
                provider.HomePath!,
                arguments,
                stdin,
                executionToken),
            exception => ProviderErrorMapper.FromException(context.ProviderInstanceId, exception),
            ProviderExecutionPolicy.SingleAttempt(),
            _store,
            cancellationToken);

        if (!execution.Succeeded)
        {
            return CreateFailure(
                context,
                execution.Failure!,
                null);
        }

        var processResult = execution.Value!;
        if (processResult.ExitCode == 0)
        {
            var response = NormalizeResponse(context, processResult.Stdout, sessionId, runId);
            return new ModelInvocationResultDto(
                "executed",
                response.TextContent,
                context,
                false,
                Id);
        }

        var failure = ProviderErrorMapper.FromCliExitCode(context.ProviderInstanceId, processResult.ExitCode);
        if (failure.Retryable && _store is not null)
        {
            _store.RecordModelProviderFailure(context.ProviderInstanceId, failure.Category, DateTimeOffset.UtcNow);
        }

        return CreateFailure(
            context,
            failure,
            BuildFailureMessage(processResult.Stderr, apiKey));
    }

    private ModelInvocationResultDto? ValidateConfiguration(ResolvedModelInvocationContextDto context)
    {
        if (context.Provider is null)
        {
            return CreateInvalidRequest(context, "CLI provider instance is missing.");
        }

        if (string.IsNullOrWhiteSpace(context.Provider.BinaryPath))
        {
            return CreateInvalidRequest(context, "CLI provider executable path is required.");
        }

        if (!File.Exists(context.Provider.BinaryPath))
        {
            return CreateInvalidRequest(context, "CLI provider executable path does not exist.");
        }

        if (string.IsNullOrWhiteSpace(context.Provider.HomePath))
        {
            return CreateInvalidRequest(context, "CLI provider workspace path is required.");
        }

        if (!Directory.Exists(context.Provider.HomePath))
        {
            return CreateInvalidRequest(context, "CLI provider workspace path does not exist.");
        }

        return null;
    }

    private static ModelInvocationResultDto CreateInvalidRequest(ResolvedModelInvocationContextDto context, string message)
    {
        var failure = ProviderErrorMapper.FromCliExitCode(context.ProviderInstanceId, 2);
        return CreateFailure(context, failure, message);
    }

    private static ModelInvocationResultDto CreateFailure(
        ResolvedModelInvocationContextDto context,
        ProviderFailureDetails failure,
        string? detail)
    {
        var content = string.IsNullOrWhiteSpace(detail)
            ? failure.SafeMessage
            : $"{failure.SafeMessage} {detail}";

        return new ModelInvocationResultDto(
            "failed",
            content,
            context,
            false,
            "cli-provider",
            failure.Category,
            failure.Retryable,
            failure.StatusCode,
            failure.ExitCode,
            content,
            failure.ProviderId);
    }

    private static IReadOnlyList<string> BuildArguments(
        ModelProviderInstanceDto provider,
        ResolvedModelInvocationContextDto context,
        string sessionId,
        string runId)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(provider.LaunchArgs))
        {
            arguments.AddRange(SplitCommandLine(provider.LaunchArgs));
        }

        arguments.Add("--model");
        arguments.Add(context.EffectiveModel);
        arguments.Add("--session-id");
        arguments.Add(sessionId);
        arguments.Add("--run-id");
        arguments.Add(runId);
        arguments.Add("--provider-instance-id");
        arguments.Add(context.ProviderInstanceId);
        return arguments;
    }

    private static string BuildStdinPayload(
        ResolvedModelInvocationContextDto context,
        IReadOnlyList<MessageDto> messages,
        string sessionId,
        string runId)
    {
        var payload = new
        {
            provider_instance_id = context.ProviderInstanceId,
            purpose = context.Purpose,
            model = context.EffectiveModel,
            session_id = sessionId,
            run_id = runId,
            messages = messages.Select(message => new
            {
                id = message.Id,
                session_id = message.SessionId,
                role = message.Role,
                content = message.Content,
                created_at = message.CreatedAt
            })
        };

        return JsonSerializer.Serialize(payload, TinadecJson.Options);
    }

    private async Task<CliProcessResult> RunProcessAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string stdin,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("CLI provider process did not start.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            throw new TimeoutException("CLI provider process timed out.");
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new CliProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ModelInvocationResponseDto NormalizeResponse(
        ResolvedModelInvocationContextDto context,
        string stdout,
        string sessionId,
        string runId)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length > 0)
        {
            try
            {
                var response = JsonSerializer.Deserialize<ModelInvocationResponseDto>(trimmed, TinadecJson.Options);
                if (response is not null)
                {
                    return response;
                }
            }
            catch (JsonException)
            {
            }
        }

        var content = string.IsNullOrWhiteSpace(trimmed)
            ? "The CLI model returned an empty response."
            : trimmed;

        return new ModelInvocationResponseDto(
            content,
            new ModelUsageDto(0, 0, 0),
            ModelFinishReason.Stop,
            new ProviderMetadataDto(
                context.ProviderInstanceId,
                context.EffectiveModel,
                "codex-cli",
                new Dictionary<string, object?>
                {
                    ["session_id"] = sessionId,
                    ["run_id"] = runId
                }),
            null,
            null,
            null);
    }

    private static string? BuildFailureMessage(string stderr, string? apiKey)
    {
        var redacted = RedactSecrets(stderr, apiKey).Trim();
        if (string.IsNullOrWhiteSpace(redacted))
        {
            return null;
        }

        return $"CLI stderr: {redacted}";
    }

    private static string RedactSecrets(string value, string? apiKey)
    {
        var redacted = value;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            redacted = redacted.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
        }

        redacted = SecretLikeTokenRegex().Replace(redacted, "$1[REDACTED]");
        redacted = BearerTokenRegex().Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    private static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        foreach (var character in commandLine)
        {
            if ((character == '"' || character == '\'') && (!inQuotes || quoteChar == character))
            {
                inQuotes = !inQuotes;
                quoteChar = inQuotes ? character : '\0';
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                AddArgument(result, current);
                continue;
            }

            current.Append(character);
        }

        AddArgument(result, current);
        return result;
    }

    private static void AddArgument(List<string> arguments, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        arguments.Add(current.ToString());
        current.Clear();
    }

    [GeneratedRegex(@"(?i)\b((?:api[_-]?key|token|secret|password)\s*[=:]\s*)\S+")]
    private static partial Regex SecretLikeTokenRegex();

    [GeneratedRegex(@"(?i)\b(bearer\s+)\S+")]
    private static partial Regex BearerTokenRegex();

    private sealed record CliProcessResult(int ExitCode, string Stdout, string Stderr);
}
