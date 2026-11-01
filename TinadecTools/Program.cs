// 主循环

using System.Text;
using TinadecTools.Abstractions;
using TinadecTools.Runtime;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Windows;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Mcp;
using TinadecTools.Tools.Browser;

// ── internal sandbox modes ──────────────────────────────────────────────────

if (OperatingSystem.IsWindows() && WindowsSandboxSetup.IsSetupMode(args))
    return WindowsSandboxSetup.RunSetup(args);

if (OperatingSystem.IsWindows() && WindowsSandboxRunner.IsRunnerMode(args))
    return WindowsSandboxRunner.RunRunner(args);

// The Linux sandbox's own child half: this process installs Landlock, sets its group, and
// execve's the commanded binary. It runs before any workspace snapshot, registry
// registration, or console reconfiguration, because none of that may happen in a process
// that is about to become the user's command.
if (OperatingSystem.IsLinux() && TinadecTools.Runtime.Sandbox.Posix.LinuxSandboxLauncher.IsMode(args))
    return TinadecTools.Runtime.Sandbox.Posix.LinuxSandboxLauncher.Run(args);

// The wire protocol is BOM-free UTF-8; without this, a zh-CN Windows console
// defaults stdin/stdout to GBK and Core cannot deserialize the JSON lines.
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

FileToolRuntime.InitializeWorkspace();

GeneratedToolRegistry.RegisterAll();
TinadecTools.Tools.Command.ShellToolRegistration.Register();
TinadecTools.Tools.Web.WebFetchTool.RegisterControl();
BrowserToolRegistration.Register();
ToolHostControls.Register();

try
{
    await ToolDispatchLoop.RunAsync(Console.In, Console.Out);
}
finally
{
    await McpRuntime.DisposeAsync();
}

return 0;
