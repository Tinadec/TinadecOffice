// 主循环

using System.Text.Json;
using NLog;
using TinadecTools.Abstractions;
using TinadecTools.Tools.Demo;

var logger = LogManager.GetCurrentClassLogger();

GeneratedToolRegistry.RegisterAll();
ToolRegistry.Register(new StatefulTool("[stateful]"));

while (true)
{
    string? line = await Console.In.ReadLineAsync();
    if (line is null)
        break;

    try
    {
        var req = JsonSerializer.Deserialize(line, ToolCallJsonContext.Default.ToolCallRequestJsonElement)!;
        var resp = await ToolRegistry.DispatchAsync(req);
        lock (Console.Out)
            Console.WriteLine(JsonSerializer.Serialize(resp, ToolCallJsonContext.Default.ToolCallResponseJsonElement));
        logger.Debug("处理完毕工具调用{id},工具类型为{type}", req.ToolCallId, req.ToolId);
    }
    catch (Exception ex)
    {
        var error = new ToolCallErrorResponse
        {
            CallId = -1,
            IsSuccess = false,
            Error = ex.Message
        };
        lock (Console.Out)
            Console.WriteLine(JsonSerializer.Serialize(error, ToolCallJsonContext.Default.ToolCallErrorResponse));
        logger.Warn("工具调用流程出错，错误为{ex}", ex);
    }
}
