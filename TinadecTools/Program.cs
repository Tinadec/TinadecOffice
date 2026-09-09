// 主循环

using System.Text;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Windows;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Mcp;

// ── internal sandbox modes ──────────────────────────────────────────────────

if (OperatingSystem.IsWindows() && WindowsSandboxSetup.IsSetupMode(args))
    return WindowsSandboxSetup.RunSetup();

if (OperatingSystem.IsWindows() && WindowsSandboxRunner.IsRunnerMode(args))
    return WindowsSandboxRunner.RunRunner();

// The wire protocol is BOM-free UTF-8; without this, a zh-CN Windows console
// defaults stdin/stdout to GBK and Core cannot deserialize the JSON lines.
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

FileToolRuntime.InitializeWorkspace();

GeneratedToolRegistry.RegisterAll();
TinadecTools.Tools.Command.ShellToolRegistration.Register();

try
{
    await ToolDispatchLoop.RunAsync(Console.In, Console.Out);
}
finally
{
    await McpRuntime.DisposeAsync();
}

return 0;
