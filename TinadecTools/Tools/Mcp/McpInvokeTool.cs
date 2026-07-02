using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Mcp;

public static class McpInvokeTool
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [ToolFunction("mcp_invoke", RequiresApproval = true)]
    public static async ValueTask<McpInvokeResponse> HandleAsync(McpInvokeParams args, CancellationToken cancellationToken)
    {
        try
        {
            var server = await McpRuntime.Repository.GetRequiredAsync(args.ServerId, cancellationToken).ConfigureAwait(false);
            var result = await McpRuntime.ClientPool.InvokeAsync(server, args.ToolName, args.Arguments, cancellationToken).ConfigureAwait(false);

            return new McpInvokeResponse
            {
                Success = true,
                ServerId = server.Id,
                ToolName = args.ToolName,
                Result = result
            };
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "mcp_invoke failed for server {serverId} tool {toolName}", args.ServerId, args.ToolName);
            return new McpInvokeResponse
            {
                Success = false,
                Error = ex.Message,
                ServerId = args.ServerId,
                ToolName = args.ToolName
            };
        }
    }
}
