using System.Text.Json.Serialization;
using TinadecTools.Abstractions;
using TinadecTools.Runtime.Sandbox;

namespace TinadecTools.Tools.Command;

public sealed class SandboxStatusParams { }

public sealed class SandboxStatusResponse
{
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    [JsonPropertyName("policy_configured")]
    public bool PolicyConfigured { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SandboxStatusParams))]
[JsonSerializable(typeof(SandboxStatusResponse))]
internal partial class SandboxStatusToolJsonContext : JsonSerializerContext;

public static class SandboxStatusTool
{
    [ToolFunction("sandbox_status")]
    public static ValueTask<SandboxStatusResponse> HandleAsync(
        SandboxStatusParams args,
        CancellationToken cancellationToken)
    {
        var backend = CommandSandboxRuntime.GetBackend();
        var policy = SandboxPolicyStore.Load();
        return ValueTask.FromResult(new SandboxStatusResponse
        {
            Supported = backend.IsSupported,
            Initialized = backend.IsInitialized,
            PolicyConfigured = policy.ReadPaths.Count > 0
                || policy.WritePaths.Count > 0
                || policy.EnvironmentVariables.Count > 0
        });
    }
}
