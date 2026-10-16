using System.Text.Json;
using TinadecTools.Abstractions;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Tools.Mcp;
using TinadecTools.Tools.Search;

namespace TinadecTools.Runtime;

/// <summary>Host-owned control channels. Neither control appears in the model tool manifest.</summary>
internal static class ToolHostControls
{
    internal const string ReleaseExecutionContextId = "#release_execution_context";
    internal const string CapabilitiesId = "#capabilities";

    public static void Register()
    {
        ToolRegistry.Register(ReleaseExecutionContextId, ReleaseAsync, requiresApproval: false,
            mutatesWorkspace: false, retrySafety: "safe");
        ToolRegistry.Register(CapabilitiesId, CapabilitiesAsync, requiresApproval: false,
            mutatesWorkspace: false, retrySafety: "safe");
        ManagedMcpProgramControl.Register();
    }

    private static async ValueTask<ToolCallResponse<JsonElement>> ReleaseAsync(
        ToolCallRequest<JsonElement> request, CancellationToken cancellationToken)
    {
        if (request.Params.ValueKind != JsonValueKind.Object
            || !request.Params.TryGetProperty("run_id", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidOperationException("#release_execution_context requires a non-empty run_id.");
        await McpRuntime.ClientPool.ReleaseRunAsync(id.GetString()!).ConfigureAwait(false);
        return Response(request.ToolCallId, writer =>
        {
            writer.WriteStartObject(); writer.WriteBoolean("released", true); writer.WriteEndObject();
        });
    }

    private static ValueTask<ToolCallResponse<JsonElement>> CapabilitiesAsync(
        ToolCallRequest<JsonElement> request, CancellationToken cancellationToken)
    {
        var rgPath = RipgrepRunner.ResolveRgPath();
        var rgAvailable = File.Exists(rgPath);
        var backend = CommandSandboxRuntime.GetBackend();
        return ValueTask.FromResult(Response(request.ToolCallId, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("ripgrep");
            writer.WriteBoolean("available", rgAvailable);
            writer.WriteString("path", rgPath);
            // reason_code is what a client words; reason stays the English diagnostic.
            if (!rgAvailable)
            {
                writer.WriteString("reason_code", "not_found");
                writer.WriteString("reason", "Ripgrep executable was not found at the configured or resolved path.");
            }
            writer.WriteEndObject();
            writer.WriteStartArray("shells");
            foreach (var id in new[] { "auto", "cmd", "powershell", "pwsh", "bash" })
            {
                var executable = id switch
                {
                    "auto" => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                    "cmd" when OperatingSystem.IsWindows() => "cmd.exe",
                    "powershell" when OperatingSystem.IsWindows() => "powershell.exe",
                    "pwsh" => OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh",
                    "bash" => OperatingSystem.IsWindows() ? "bash.exe" : "/bin/bash",
                    _ => null
                };
                var path = FindExecutable(executable);
                writer.WriteStartObject(); writer.WriteString("id", id);
                writer.WriteBoolean("available", path is not null);
                if (path is not null) writer.WriteString("path", path);
                else
                {
                    writer.WriteString("reason_code", executable is null ? "unsupported_platform" : "not_in_path");
                    writer.WriteString("reason", executable is null ? "Unsupported on this platform." : "Executable was not found in PATH.");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("sandbox");
            writer.WriteBoolean("supported", backend.IsSupported);
            writer.WriteBoolean("initialized", backend.IsInitialized);
            writer.WriteBoolean("frozen_grants", ToolExecutionContext.Current is not null);
            writer.WriteEndObject();
            writer.WriteStartObject("web");
            writer.WriteBoolean("supported", true); writer.WriteBoolean("address_guard", true);
            writer.WriteEndObject(); writer.WriteEndObject();
        }));
    }

    private static string? FindExecutable(string? executable)
    {
        if (executable is null) return null;
        if (Path.IsPathFullyQualified(executable)) return File.Exists(executable) ? executable : null;
        if (OperatingSystem.IsWindows())
        {
            var systemPath = Path.Combine(Environment.SystemDirectory, executable);
            if (File.Exists(systemPath)) return systemPath;
        }
        foreach (var entry in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var candidate = Path.Combine(entry.Trim(), executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static ToolCallResponse<JsonElement> Response(long callId, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        using var document = JsonDocument.Parse(stream.ToArray());
        return new ToolCallResponse<JsonElement> { CallId = callId, IsSuccess = true, Response = document.RootElement.Clone() };
    }
}
