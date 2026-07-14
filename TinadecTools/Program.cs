// 主循环

using System.Text.Json;
using NLog;
using TinadecTools.Abstractions;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Windows;
using TinadecTools.Tools.FileRW;
using TinadecTools.Tools.Mcp;

// ── internal sandbox modes ──────────────────────────────────────────────────

if (OperatingSystem.IsWindows() && WindowsSandboxSetup.IsSetupMode(args))
    return WindowsSandboxSetup.RunSetup();

if (OperatingSystem.IsWindows() && WindowsSandboxRunner.IsRunnerMode(args))
    return WindowsSandboxRunner.RunRunner();

var logger = LogManager.GetCurrentClassLogger();
FileToolRuntime.InitializeWorkspace();

GeneratedToolRegistry.RegisterAll();

try
{
    while (true)
    {
        var line = await Console.In.ReadLineAsync();
        if (line is null)
            break;

        ToolCallRequest<JsonElement>? req = null;
        try
        {
            req = JsonSerializer.Deserialize(line, ToolCallJsonContext.Default.ToolCallRequestJsonElement)
                ?? throw new JsonException("Tool call request was null.");
            var resp = await ToolRegistry.DispatchAsync(req);
            lock (Console.Out)
            {
                Console.WriteLine(JsonSerializer.Serialize(resp, ToolCallJsonContext.Default.ToolCallResponseJsonElement));
            }

            logger.Debug("处理完毕工具调用{id},工具类型为{type}", req.ToolCallId, req.ToolId);
        }
        catch (Exception ex)
        {
            var error = new ToolCallErrorResponse
            {
                CallId = req?.ToolCallId ?? -1,
                IsSuccess = false,
                Error = ex.Message
            };
            lock (Console.Out)
            {
                Console.WriteLine(JsonSerializer.Serialize(error, ToolCallJsonContext.Default.ToolCallErrorResponse));
            }

            logger.Warn("工具调用流程出错，错误为{ex}", ex);
        }
    }
}
finally
{
    await McpRuntime.DisposeAsync();
}

return 0;
