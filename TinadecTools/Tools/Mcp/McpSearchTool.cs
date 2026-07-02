using NLog;
using TinadecTools.Abstractions;

namespace TinadecTools.Tools.Mcp;

public static class McpSearchTool
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [ToolFunction("mcp_search")]
    public static async ValueTask<McpSearchResponse> HandleAsync(McpSearchParams args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
            return new McpSearchResponse();

        var terms = args.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return new McpSearchResponse();

        var limit = Math.Clamp(args.Limit, 1, 100);
        var response = new McpSearchResponse();
        var servers = await McpRuntime.Repository.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var server in servers)
        {
            IReadOnlyList<McpToolSummary> tools;
            try
            {
                tools = await McpRuntime.ClientPool.ListToolsAsync(server, args.IncludeSchema, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "mcp_search failed for server {serverId}", server.Id);
                continue;
            }

            foreach (var tool in tools)
            {
                var score = Score(tool, terms);
                if (score <= 0)
                    continue;

                response.Results.Add(new McpSearchResult
                {
                    ServerId = server.Id,
                    ServerName = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name,
                    Score = score,
                    Tool = tool
                });
            }
        }

        response.Results = response.Results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.ServerId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return response;
    }

    private static int Score(McpToolSummary tool, IReadOnlyList<string> terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            score += ScoreField(tool.Name, term, 10);
            if (!string.IsNullOrWhiteSpace(tool.Description))
                score += ScoreField(tool.Description, term, 4);
        }

        return score;
    }

    private static int ScoreField(string value, string term, int weight)
    {
        if (value.Equals(term, StringComparison.OrdinalIgnoreCase))
            return weight * 4;

        if (value.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return weight * 2;

        return value.Contains(term, StringComparison.OrdinalIgnoreCase) ? weight : 0;
    }
}
