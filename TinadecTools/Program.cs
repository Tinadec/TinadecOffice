// 主循环

using System.Text.Json;
using System.Text.Json.Nodes;
using NLog;
using TinadecTools.Abstractions;

var logger = LogManager.GetCurrentClassLogger();

while (true)
{

    string? line = await Console.In.ReadLineAsync();
    if (line is null)
        break;

    _ = Task.Run(async () =>
    {
        try
        {
            var req = JsonSerializer.Deserialize<ToolCallRequest<JsonElement>>(line)!;
            // TODO：搞定中间的东西
            var res = new JsonElement();
            var resp = new ToolCallResponse<JsonElement> { CallId = req.ToolCallId, IsSuccess = true, Response = res };
            lock (Console.Out) Console.WriteLine(JsonSerializer.Serialize(resp));
            logger.Debug("处理完毕工具调用{id},工具类型为{type}",req.ToolCallId,req.ToolId);
        }
        catch (Exception ex)
        {
            lock (Console.Out)
                Console.WriteLine(JsonSerializer.Serialize(new { call_id = -1, success = false, error = ex.Message }));
            logger.Warn("工具调用流程出错，错误为{ex}",ex);
        }
    });
}
