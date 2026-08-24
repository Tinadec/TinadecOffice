namespace TinadecTools.Runtime.Sandbox;

/// <summary>
/// Validates the command request at both sides of the host/runner boundary.
/// The runner receives JSON from another process, so it must not rely on the
/// validation performed by <see cref="CommandSandboxRuntime"/>.
/// </summary>
internal static class SandboxRequestValidator
{
    internal const int MinTimeoutMs = 1;
    internal const int MaxTimeoutMs = 1_800_000;

    internal static void Validate(
        string? executable,
        IReadOnlyList<string>? arguments,
        string? workingDirectory,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("executable must not be empty.", nameof(executable));
        if (executable.Contains('\0'))
            throw new ArgumentException("executable must not contain NUL.", nameof(executable));

        if (arguments is null)
            throw new ArgumentException("arguments must not be null.", nameof(arguments));
        foreach (var argument in arguments)
        {
            if (argument is null)
                throw new ArgumentException("arguments must not contain null entries.", nameof(arguments));
            if (argument.Contains('\0'))
                throw new ArgumentException("arguments must not contain NUL.", nameof(arguments));
        }

        if (timeoutMs is < MinTimeoutMs or > MaxTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), $"timeout_ms must be between {MinTimeoutMs} and {MaxTimeoutMs}.");

        var fullWorkingDirectory = SandboxPaths.ValidateWorkingDirectory(workingDirectory ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fullWorkingDirectory))
            throw new UnauthorizedAccessException("working_directory must be inside the workspace root.");

        if (environment is null) return;
        foreach (var pair in environment)
        {
            if (!SandboxEnvironment.IsEnvironmentVariableNameValid(pair.Key))
                throw new ArgumentException($"Invalid environment variable name '{pair.Key}'.", nameof(environment));
            if (pair.Value is null || pair.Value.Contains('\0'))
                throw new ArgumentException($"Invalid value for environment variable '{pair.Key}'.", nameof(environment));
        }
    }
}
