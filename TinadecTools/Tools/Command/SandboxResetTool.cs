using System.Text.Json.Serialization;
using TinadecTools.Abstractions;
using TinadecTools.Runtime.Sandbox;

namespace TinadecTools.Tools.Command;

public sealed class SandboxResetParams
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "workspace";
}

public sealed class SandboxResetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SandboxResetParams))]
[JsonSerializable(typeof(SandboxResetResponse))]
internal partial class SandboxResetToolJsonContext : JsonSerializerContext;

public static class SandboxResetTool
{
    [ToolFunction("sandbox_reset", RequiresApproval = true)]
    public static async ValueTask<SandboxResetResponse> HandleAsync(
        SandboxResetParams args,
        CancellationToken cancellationToken)
    {
        var scope = string.Equals(args.Scope, "machine", StringComparison.OrdinalIgnoreCase)
            ? SandboxResetScope.Machine
            : SandboxResetScope.Workspace;

        try
        {
            var backend = CommandSandboxRuntime.GetBackend();
            await backend.ResetAsync(scope, cancellationToken).ConfigureAwait(false);
            return new SandboxResetResponse { Success = true, Scope = scope.ToString().ToLowerInvariant() };
        }
        catch (Exception ex)
        {
            return new SandboxResetResponse { Success = false, Scope = scope.ToString().ToLowerInvariant(), Error = ex.Message };
        }
    }
}