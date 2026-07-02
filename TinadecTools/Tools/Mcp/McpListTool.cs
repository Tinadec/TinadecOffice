using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Mcp;

public static class McpListTool
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [ToolFunction("mcp_list")]
    public static async ValueTask<McpListResponse> HandleAsync(McpListParams args, CancellationToken cancellationToken)
    {
        var servers = await McpRuntime.Repository.ListAsync(cancellationToken).ConfigureAwait(false);
        var response = new McpListResponse { ConfigPath = McpRuntime.Repository.ConfigPath };

        foreach (var server in servers)
        {
            var item = new McpServerToolList
            {
                Id = server.Id,
                Name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name
            };

            try
            {
                item.Tools = (await McpRuntime.ClientPool.ListToolsAsync(server, args.IncludeSchema, cancellationToken).ConfigureAwait(false)).ToList();
                item.Status = "connected";
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "mcp_list failed for server {serverId}", server.Id);
                item.Status = "error";
                item.Error = ex.Message;
            }

            response.Servers.Add(item);
        }

        return response;
    }
}
